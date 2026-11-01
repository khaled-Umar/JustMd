using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace JustMd.Core.Tabs
{
    /// <summary>
    /// Wire format between a secondary process and the primary instance: a 4-byte little-endian
    /// length followed by UTF-8 JSON <c>{"files":["C:\\a.md", ...]}</c>. The primary answers with a
    /// single byte (<see cref="Accepted"/> or <see cref="Rejected"/>). Everything received is
    /// validated: size cap, file count cap, and only fully qualified ordinary paths.
    /// </summary>
    public static class InstanceProtocol
    {
        public const int MaxPayloadBytes = 256 * 1024;
        public const int MaxFiles = 256;
        public const byte Accepted = 1;
        public const byte Rejected = 0;

        /// <summary>Names for the per-user, per-session mutex and pipe.</summary>
        public static string MutexName(string userId, int sessionId) =>
            @"Local\JustMd.Instance." + Sanitize(userId) + "." + sessionId.ToString(System.Globalization.CultureInfo.InvariantCulture);

        /// <summary>The mutex a running 1.0.0 process holds. Version 1.0.0 shipped as JustMdViewer.</summary>
        public static string LegacyMutexName(string userId, int sessionId) =>
            @"Local\JustMdViewer.Instance." + Sanitize(userId) + "." + sessionId.ToString(System.Globalization.CultureInfo.InvariantCulture);

        public static string PipeName(string userId, int sessionId) =>
            "JustMd.Instance." + Sanitize(userId) + "." + sessionId.ToString(System.Globalization.CultureInfo.InvariantCulture);

        public static byte[] Encode(IEnumerable<string> files)
        {
            string json = JsonSerializer.Serialize(new Payload { Files = files.Select(f => (string?)f).ToList() });
            byte[] body = Encoding.UTF8.GetBytes(json);
            if (body.Length > MaxPayloadBytes)
            {
                throw new ArgumentException("Too many or too long paths for one message.", nameof(files));
            }

            byte[] frame = new byte[4 + body.Length];
            BitConverter.GetBytes(body.Length).CopyTo(frame, 0);
            if (!BitConverter.IsLittleEndian)
            {
                Array.Reverse(frame, 0, 4);
            }

            body.CopyTo(frame, 4);
            return frame;
        }

        /// <summary>Reads one length-prefixed frame. Returns null on a bad length or a short read.</summary>
        public static byte[]? ReadFrame(Stream stream)
        {
            byte[] header = new byte[4];
            if (!ReadExactly(stream, header))
            {
                return null;
            }

            if (!BitConverter.IsLittleEndian)
            {
                Array.Reverse(header);
            }

            int length = BitConverter.ToInt32(header, 0);
            if (length <= 0 || length > MaxPayloadBytes)
            {
                return null;
            }

            byte[] body = new byte[length];
            return ReadExactly(stream, body) ? body : null;
        }

        /// <summary>Async variant of <see cref="ReadFrame"/>; cancel via <paramref name="cancellationToken"/> to time out.</summary>
        public static async System.Threading.Tasks.Task<byte[]?> ReadFrameAsync(Stream stream, System.Threading.CancellationToken cancellationToken)
        {
            byte[] header = new byte[4];
            if (!await ReadExactlyAsync(stream, header, cancellationToken).ConfigureAwait(false))
            {
                return null;
            }

            if (!BitConverter.IsLittleEndian)
            {
                Array.Reverse(header);
            }

            int length = BitConverter.ToInt32(header, 0);
            if (length <= 0 || length > MaxPayloadBytes)
            {
                return null;
            }

            byte[] body = new byte[length];
            return await ReadExactlyAsync(stream, body, cancellationToken).ConfigureAwait(false) ? body : null;
        }

        private static async System.Threading.Tasks.Task<bool> ReadExactlyAsync(Stream stream, byte[] buffer, System.Threading.CancellationToken cancellationToken)
        {
            int offset = 0;
            while (offset < buffer.Length)
            {
                int read = await stream.ReadAsync(buffer.AsMemory(offset, buffer.Length - offset), cancellationToken).ConfigureAwait(false);
                if (read <= 0)
                {
                    return false;
                }

                offset += read;
            }

            return true;
        }

        /// <summary>Parses and validates a payload. Returns null if anything is wrong.</summary>
        public static IReadOnlyList<string>? Parse(byte[]? body)
        {
            if (body == null || body.Length == 0 || body.Length > MaxPayloadBytes)
            {
                return null;
            }

            Payload? payload;
            try
            {
                payload = JsonSerializer.Deserialize<Payload>(body);
            }
            catch (JsonException)
            {
                return null;
            }

            if (payload?.Files == null || payload.Files.Count > MaxFiles)
            {
                return null;
            }

            var result = new List<string>(payload.Files.Count);
            foreach (string? file in payload.Files)
            {
                if (!IsAcceptablePath(file))
                {
                    return null;
                }

                result.Add(file!);
            }

            return result;
        }

        /// <summary>A fully qualified, ordinary local or UNC file path (no device paths, streams or control characters).</summary>
        public static bool IsAcceptablePath(string? path)
        {
            if (string.IsNullOrEmpty(path) || path.Length > 32767 || !Path.IsPathFullyQualified(path)
                || path.Any(char.IsControl))
            {
                return false;
            }

            return LocalPaths.IsOrdinaryPath(path);
        }

        private static bool ReadExactly(Stream stream, byte[] buffer)
        {
            int offset = 0;
            while (offset < buffer.Length)
            {
                int read = stream.Read(buffer, offset, buffer.Length - offset);
                if (read <= 0)
                {
                    return false;
                }

                offset += read;
            }

            return true;
        }

        private static string Sanitize(string value)
        {
            var builder = new StringBuilder(value.Length);
            foreach (char c in value)
            {
                builder.Append(char.IsLetterOrDigit(c) || c == '-' ? c : '_');
            }

            return builder.ToString();
        }

        private sealed class Payload
        {
            [JsonPropertyName("files")]
            public List<string?>? Files { get; set; }
        }
    }
}
