using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Threading;
using System.Threading.Tasks;
using JustMd.Core.Tabs;

namespace JustMd
{
    /// <summary>
    /// Keeps one window per user session. The first process owns a named mutex and listens on a
    /// named pipe that only the current user can open; later processes hand their file paths to
    /// it and exit. If the primary does not answer, the caller simply runs as its own window.
    /// </summary>
    internal sealed class SingleInstance : IDisposable
    {
        private static readonly TimeSpan ClientTimeout = TimeSpan.FromSeconds(6);
        private static readonly TimeSpan ServerReadTimeout = TimeSpan.FromSeconds(5);

        private readonly Mutex _mutex;
        private readonly string _pipeName;
        private CancellationTokenSource? _serverCts;

        private SingleInstance(Mutex mutex, string pipeName, bool isPrimary)
        {
            _mutex = mutex;
            _pipeName = pipeName;
            IsPrimary = isPrimary;
        }

        public bool IsPrimary { get; private set; }

        public static SingleInstance Create()
        {
            string user = WindowsIdentity.GetCurrent().User?.Value ?? Environment.UserName;
            int session = Process.GetCurrentProcess().SessionId;
            var mutex = new Mutex(initiallyOwned: false, InstanceProtocol.MutexName(user, session));
            var instance = new SingleInstance(mutex, InstanceProtocol.PipeName(user, session), isPrimary: false);
            instance.IsPrimary = instance.TryAcquireMutex();
            return instance;
        }

        /// <summary>True while a 1.0.0 process (which shipped as JustMdViewer) runs in this user's session.</summary>
        public static bool IsLegacyInstanceRunning()
        {
            string user = WindowsIdentity.GetCurrent().User?.Value ?? Environment.UserName;
            int session = Process.GetCurrentProcess().SessionId;
            try
            {
                if (Mutex.TryOpenExisting(InstanceProtocol.LegacyMutexName(user, session), out Mutex? mutex))
                {
                    mutex.Dispose();
                    return true;
                }

                return false;
            }
            catch (UnauthorizedAccessException)
            {
                return true; // the mutex exists, we just may not open it
            }
            catch (Exception ex) when (ex is WaitHandleCannotBeOpenedException || ex is IOException || ex is ArgumentException)
            {
                return false;
            }
        }

        /// <summary>After a failed hand-off: take over if the previous primary has gone away.</summary>
        public void TryBecomePrimary()
        {
            if (!IsPrimary)
            {
                IsPrimary = TryAcquireMutex();
            }
        }

        /// <summary>
        /// Sends <paramref name="files"/> (absolute paths; may be empty to just activate the window)
        /// to the primary. Retries while the primary is still starting. True when it accepted them.
        /// </summary>
        public bool TrySendToPrimary(IReadOnlyList<string> files)
        {
            try
            {
                return Task.Run(() => SendAsync(files)).GetAwaiter().GetResult();
            }
            catch (Exception ex) when (ex is IOException || ex is TimeoutException || ex is OperationCanceledException
                                       || ex is UnauthorizedAccessException || ex is ArgumentException)
            {
                return false;
            }
        }

        /// <summary>Starts listening. <paramref name="onFiles"/> runs on a thread-pool thread.</summary>
        public void StartServer(Action<IReadOnlyList<string>> onFiles)
        {
            if (!IsPrimary || _serverCts != null)
            {
                return;
            }

            _serverCts = new CancellationTokenSource();
            CancellationToken token = _serverCts.Token;
            _ = Task.Run(() => ServeAsync(onFiles, token));
        }

        public void Dispose()
        {
            _serverCts?.Cancel();
            _serverCts?.Dispose();
            _mutex.Dispose(); // ownership is released by the OS when the process exits
        }

        private bool TryAcquireMutex()
        {
            try
            {
                return _mutex.WaitOne(0);
            }
            catch (AbandonedMutexException)
            {
                return true; // previous primary crashed; we own it now
            }
        }

        private async Task<bool> SendAsync(IReadOnlyList<string> files)
        {
            byte[] frame = InstanceProtocol.Encode(files);
            using var cts = new CancellationTokenSource(ClientTimeout);
            using var client = new NamedPipeClientStream(".", _pipeName, PipeDirection.InOut,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);

            // ConnectAsync keeps retrying until the server exists or the timeout expires, which
            // covers a primary that is still starting (Explorer launching N processes at once).
            await client.ConnectAsync(cts.Token).ConfigureAwait(false);

            if (GetNamedPipeServerProcessId(client.SafePipeHandle.DangerousGetHandle(), out uint serverPid))
            {
                AllowSetForegroundWindow(serverPid);
            }

            await client.WriteAsync(frame, cts.Token).ConfigureAwait(false);
            await client.FlushAsync(cts.Token).ConfigureAwait(false);

            byte[] ack = new byte[1];
            int read = await client.ReadAsync(ack, cts.Token).ConfigureAwait(false);
            return read == 1 && ack[0] == InstanceProtocol.Accepted;
        }

        private async Task ServeAsync(Action<IReadOnlyList<string>> onFiles, CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                NamedPipeServerStream? server = null;
                try
                {
                    server = new NamedPipeServerStream(_pipeName, PipeDirection.InOut,
                        NamedPipeServerStream.MaxAllowedServerInstances, PipeTransmissionMode.Byte,
                        PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                    await server.WaitForConnectionAsync(token).ConfigureAwait(false);

                    NamedPipeServerStream connected = server;
                    server = null;
                    _ = Task.Run(() => HandleClientAsync(connected, onFiles, token));
                }
                catch (OperationCanceledException)
                {
                    server?.Dispose();
                    return;
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                {
                    server?.Dispose();
                    try
                    {
                        await Task.Delay(500, token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        return;
                    }
                }
            }
        }

        private static async Task HandleClientAsync(NamedPipeServerStream server, Action<IReadOnlyList<string>> onFiles, CancellationToken token)
        {
            using (server)
            {
                try
                {
                    using var cts = CancellationTokenSource.CreateLinkedTokenSource(token);
                    cts.CancelAfter(ServerReadTimeout);

                    byte[]? body = await InstanceProtocol.ReadFrameAsync(server, cts.Token).ConfigureAwait(false);
                    IReadOnlyList<string>? files = InstanceProtocol.Parse(body);
                    byte answer = files == null ? InstanceProtocol.Rejected : InstanceProtocol.Accepted;
                    await server.WriteAsync(new[] { answer }, cts.Token).ConfigureAwait(false);
                    await server.FlushAsync(cts.Token).ConfigureAwait(false);

                    if (files != null)
                    {
                        onFiles(files);
                    }
                }
                catch (Exception ex) when (ex is IOException || ex is OperationCanceledException || ex is ObjectDisposedException)
                {
                    // Client went away or stalled; nothing to do.
                }
            }
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetNamedPipeServerProcessId(IntPtr pipe, out uint serverProcessId);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool AllowSetForegroundWindow(uint processId);
    }
}
