using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using IWar3Key.Shell.Core;
using IWar3Key.Shell.Interop;

namespace IWar3Key.Shell
{
    public partial class App : Application
    {
        private const string SingleInstanceMutexName = @"Local\iWar3Key.Shell";
        private static Mutex _singleInstanceMutex;

        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            bool createdNew = true;
            try
            {
                _singleInstanceMutex = new Mutex(false, SingleInstanceMutexName, out createdNew);
            }
            catch (UnauthorizedAccessException)
            {
                // Never block startup because another user/session owns the
                // name; the normal same-user case below still works.
                createdNew = true;
            }

            if (!createdNew)
            {
                _singleInstanceMutex?.Dispose();
                _singleInstanceMutex = null;
                MessageBox.Show(
                    "iWar3Key 已经启动，请勿重复运行。",
                    "iWar3Key",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
                Shutdown(0);
                return;
            }

            DispatcherUnhandledException += OnDispatcherUnhandledException;
            TaskScheduler.UnobservedTaskException += (s, args) => args.SetObserved();
            AppDomain.CurrentDomain.UnhandledException += (s, args) =>
            {
                if (args.ExceptionObject is Exception exception)
                {
                    WriteCrashLog("appdomain", exception);
                }
            };

            try
            {
                var paths = new AppPaths();
                var log = new Logger(paths);
                var store = new ConfigStore(paths, log);
                store.Load();

                var profiles = new ProfileCatalog();
                profiles.Load(paths.ProfilesDirectory, log);

                var context = new ShellContext(paths, log, store, profiles);
                context.Session = new GameSession(paths, store, profiles, log, ParsePreferredPid(e.Args));

                var window = new MainWindow(context);
                MainWindow = window;
                window.Show();
                log.Info("iWar3Key 启动。数据目录: " + paths.DataDirectory);
            }
            catch (Exception ex)
            {
                WriteCrashLog("startup", ex);
                MessageBox.Show(ex.ToString(), "iWar3Key 启动失败", MessageBoxButton.OK, MessageBoxImage.Error);
                Shutdown(1);
            }
        }

        protected override void OnExit(ExitEventArgs e)
        {
            if (_singleInstanceMutex != null)
            {
                _singleInstanceMutex.Dispose();
                _singleInstanceMutex = null;
            }

            base.OnExit(e);
        }

        private static int? ParsePreferredPid(string[] args)
        {
            if (args == null)
            {
                return null;
            }

            for (int i = 0; i + 1 < args.Length; i++)
            {
                if (string.Equals(args[i], "--pid", StringComparison.OrdinalIgnoreCase) &&
                    int.TryParse(args[i + 1], out int pid) &&
                    pid > 0)
                {
                    return pid;
                }
            }

            return null;
        }

        private static void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
        {
            WriteCrashLog("dispatcher", e.Exception);
            MessageBox.Show(e.Exception.ToString(), "iWar3Key 未处理异常", MessageBoxButton.OK, MessageBoxImage.Error);
            e.Handled = true;
        }

        private static void WriteCrashLog(string bucket, Exception exception)
        {
            try
            {
                string path = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "crash-" + bucket + ".log");
                System.IO.File.AppendAllText(
                    path,
                    DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + Environment.NewLine + exception + Environment.NewLine,
                    System.Text.Encoding.UTF8);
            }
            catch
            {
                // Diagnostics must not mask the original failure.
            }
        }
    }
}
