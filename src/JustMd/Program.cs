using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Windows.Forms;
using JustMd.Core;
using JustMd.Core.Tabs;
using Microsoft.Web.WebView2.Core;

namespace JustMd
{
    internal static class Program
    {
        public const string AppName = "JustMd";
        public const string WebView2DownloadUrl = "https://developer.microsoft.com/microsoft-edge/webview2/";

        [STAThread]
        private static void Main(string[] args)
        {
            // Every argument is a file. Resolve relative paths here, against this process's
            // working directory, before they are handed to another instance.
            List<string> files = args.Select(DocumentPaths.Normalize).OfType<string>().ToList();

            using SingleInstance instance = SingleInstance.Create();
            if (!instance.IsPrimary)
            {
                if (instance.TrySendToPrimary(files))
                {
                    return;
                }

                // The primary did not answer: run as a normal window rather than lose the files.
                instance.TryBecomePrimary();
            }

            // Before MainForm, which reads settings and creates the WebView2 folder.
            AppPaths.MigrateLegacyDataDirectory();

            ApplicationConfiguration.Initialize();
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
            Application.ThreadException += OnThreadException;

            if (!IsWebViewRuntimeAvailable())
            {
                ShowWebViewRuntimeMissing(null);
                return;
            }

            var form = new MainForm(files);
            instance.StartServer(form.ReceiveFilesFromOtherInstance);
            Application.Run(form);
        }

        private static bool IsWebViewRuntimeAvailable()
        {
            try
            {
                return !string.IsNullOrEmpty(CoreWebView2Environment.GetAvailableBrowserVersionString());
            }
            catch (WebView2RuntimeNotFoundException)
            {
                return false;
            }
            catch (Exception ex) when (ex is DllNotFoundException || ex is BadImageFormatException)
            {
                // The loader DLL itself is missing or the wrong architecture; let the form report it.
                return true;
            }
        }

        public static void ShowWebViewRuntimeMissing(IWin32Window? owner)
        {
            DialogResult answer = MessageBox.Show(
                owner,
                "JustMd needs the Microsoft Edge WebView2 Runtime, which was not found on this PC.\n\n" +
                "It is free and usually already part of Windows 10 and 11.\n\n" +
                "Open the Microsoft download page now?",
                AppName,
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Warning,
                MessageBoxDefaultButton.Button1);

            if (answer == DialogResult.Yes)
            {
                TryOpenWithShell(WebView2DownloadUrl);
            }
        }

        /// <summary>Opens a URL that the app itself trusts (not document content) with the shell.</summary>
        public static bool TryOpenWithShell(string url)
        {
            try
            {
                using (Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }))
                {
                }

                return true;
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception || ex is InvalidOperationException)
            {
                return false;
            }
        }

        private static void OnThreadException(object sender, ThreadExceptionEventArgs e)
        {
            MessageBox.Show(
                "Something went wrong:\n\n" + e.Exception.Message,
                AppName,
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        }
    }
}
