using System;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;

namespace SteamSmart.Desktop
{
    internal static class Program
    {
        public static bool AutoStart { get; private set; }
        private const string MutexName = @"Local\SteamSmartUI";
        private const string ShowEventName = @"Local\SteamSmartUIShow";

        [STAThread]
        private static int Main(string[] args)
        {
            AppContext.SetSwitch("Switch.System.Windows.DoNotScaleForDpiChanges", false);
            AutoStart = true;
            bool stopOnly = false;
            string dataDirectory = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "support", "SteamSmart");
            try
            {
                for (int index = 0; index < args.Length; index++)
                {
                    if (args[index] == "--stop") stopOnly = true;
                    else if (args[index] == "--no-start") AutoStart = false;
                    else if (args[index] == "--data-dir" && index + 1 < args.Length)
                    {
                        dataDirectory = Path.GetFullPath(args[++index]);
                    }
                    else throw new ArgumentException("启动参数不正确。可用参数：--stop、--no-start、--data-dir 路径。");
                }
                if (stopOnly) return Stop(dataDirectory);
                return Run(dataDirectory);
            }
            catch (Exception error)
            {
                ShowError(error);
                return 1;
            }
        }

        private static int Stop(string dataDirectory)
        {
            string engineDirectory = Bundle.EnsureExtracted();
            using (var controller = new RuntimeController(engineDirectory, dataDirectory))
            {
                controller.StopAsync().GetAwaiter().GetResult();
            }
            return 0;
        }

        private static int Run(string dataDirectory)
        {
            using (var mutex = new Mutex(false, MutexName))
            {
                bool ownsMutex;
                try { ownsMutex = mutex.WaitOne(0); }
                catch (AbandonedMutexException) { ownsMutex = true; }
                if (!ownsMutex)
                {
                    SignalExistingWindow();
                    return 0;
                }
                try
                {
                    using (var showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName))
                    {
                        string engineDirectory = Bundle.EnsureExtracted();
                        var controller = new RuntimeController(engineDirectory, dataDirectory);
                        bool normalClose = false;
                        try
                        {
                            var application = new Application();
                            application.ShutdownMode = ShutdownMode.OnMainWindowClose;
                            var window = new MainWindow(controller);
                            var showTimer = new DispatcherTimer();
                            showTimer.Interval = TimeSpan.FromMilliseconds(300);
                            showTimer.Tick += delegate
                            {
                                if (showEvent.WaitOne(0)) window.ShowFromTray();
                            };
                            application.MainWindow = window;
                            showTimer.Start();
                            try
                            {
                                application.Run(window);
                                normalClose = true;
                            }
                            finally { showTimer.Stop(); }
                        }
                        finally
                        {
                            // MainWindow awaits ShutdownAsync before its normal close.
                            // An initialization failure still needs a complete recovery.
                            if (!normalClose)
                            {
                                Task.Run(async delegate
                                {
                                    await controller.ShutdownAsync().ConfigureAwait(false);
                                }).GetAwaiter().GetResult();
                            }
                            controller.Dispose();
                        }
                    }
                }
                finally { mutex.ReleaseMutex(); }
            }
            return 0;
        }

        private static void SignalExistingWindow()
        {
            for (int attempt = 0; attempt < 8; attempt++)
            {
                try
                {
                    using (var handle = EventWaitHandle.OpenExisting(ShowEventName))
                    {
                        handle.Set();
                        return;
                    }
                }
                catch (WaitHandleCannotBeOpenedException) { Thread.Sleep(75); }
            }
            MessageBox.Show("Steam 自动优选已经在运行，请查看窗口或系统托盘。", "Steam 自动优选",
                MessageBoxButton.OK, MessageBoxImage.Information);
        }

        private static void ShowError(Exception error)
        {
            string message = error.GetBaseException().Message;
            if (String.IsNullOrWhiteSpace(message)) message = "程序启动失败，请重新打开。";
            MessageBox.Show(message, "Steam 自动优选", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }
}
