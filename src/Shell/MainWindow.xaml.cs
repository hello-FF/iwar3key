using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Forms = System.Windows.Forms;
using IWar3Key.Shell.Controls;
using IWar3Key.Shell.Core;
using IWar3Key.Shell.Interop;
using IWar3Key.Shell.Pages;

namespace IWar3Key.Shell
{
    public partial class MainWindow : Window
    {
        private static readonly Brush IdleBrush = Frozen(0x9A, 0xA0, 0xA6);
        private static readonly Brush DetectedBrush = Frozen(0xC0, 0x8A, 0x3E);
        private static readonly Brush AttachingBrush = Frozen(0x3A, 0x46, 0x54);
        private static readonly Brush AttachedBrush = Frozen(0x3F, 0x7A, 0x56);
        private static readonly Brush ErrorBrush = Frozen(0xB1, 0x4B, 0x43);

        private readonly ShellContext _ctx;
        private readonly Dictionary<string, UserControl> _pages = new Dictionary<string, UserControl>(StringComparer.OrdinalIgnoreCase);
        private readonly DispatcherTimer _toastTimer;
        private Forms.NotifyIcon _tray;
        private Forms.ToolStripMenuItem _trayPauseItem;
        private System.Drawing.Icon _trayIcon;
        private bool _started;
        private bool _closed;
        private bool _forceClose;
        private bool _needsRestartNotified;

        public MainWindow(ShellContext context)
        {
            _ctx = context;
            InitializeComponent();
            ApplyAppIcon();
            SchemeSelectorHost.Content = new SchemeSelector(_ctx);

            AddPage("game", new GameSettingsPage(_ctx));
            AddPage("items", new ItemsPage(_ctx));
            AddPage("skills", new SkillsPage(_ctx));
            AddPage("mapping", new MappingPage(_ctx));
            AddPage("chat", new ChatPage(_ctx));
            AddPage("settings", new SettingsPage(_ctx));
            ShowPage("game");

            _toastTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2.8) };
            _toastTimer.Tick += (s, e) =>
            {
                _toastTimer.Stop();
                ToastBorder.Visibility = Visibility.Collapsed;
            };

            _ctx.StatusChanged += ShowToast;
            _ctx.Session.SnapshotChanged += OnSnapshot;

            Loaded += MainWindow_Loaded;
            SourceInitialized += MainWindow_SourceInitialized;
            Closing += MainWindow_Closing;
            Closed += MainWindow_Closed;

            if (Application.Current != null)
            {
                // Never block a Windows shutdown/logoff with the tray close.
                Application.Current.SessionEnding += (s, e) => _forceClose = true;
            }
        }

        private void AddPage(string key, UserControl page)
        {
            page.Visibility = Visibility.Collapsed;
            _pages[key] = page;
            PageHost.Children.Add(page);
        }

        private void ShowPage(string key)
        {
            if (!_pages.ContainsKey(key))
            {
                key = "game";
            }

            foreach (KeyValuePair<string, UserControl> pair in _pages)
            {
                pair.Value.Visibility = pair.Key == key ? Visibility.Visible : Visibility.Collapsed;
            }
        }

        private void MainWindow_Loaded(object sender, RoutedEventArgs e)
        {
            if (_started)
            {
                return;
            }

            _started = true;
            CreateTrayIcon();
            _ctx.Session.Start();
        }

        private void MainWindow_SourceInitialized(object sender, EventArgs e)
        {
            try
            {
                IntPtr handle = new WindowInteropHelper(this).Handle;
                int preference = NativeMethods.DWMWCP_ROUND;
                NativeMethods.DwmSetWindowAttribute(handle, NativeMethods.DWMWA_WINDOW_CORNER_PREFERENCE, ref preference, sizeof(int));
            }
            catch
            {
                // Rounded corners are cosmetic and unavailable on older Windows builds.
            }
        }

        private void MainWindow_Closing(object sender, CancelEventArgs e)
        {
            // Minimize now always goes to the taskbar. Only an explicit close
            // follows the "close to tray" setting.
            if (!_forceClose && _ctx.Config.Game.MinimizeToTray)
            {
                e.Cancel = true;
                Hide();
            }
        }

        private void OnSnapshot(SessionSnapshot snapshot)
        {
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.BeginInvoke(new Action(() => OnSnapshot(snapshot)));
                return;
            }

            VersionPillText.Text = string.IsNullOrWhiteSpace(snapshot.VersionLabel) ? "未检测" : snapshot.VersionLabel;
            VersionPill.ToolTip = string.IsNullOrWhiteSpace(snapshot.VersionText) ? null : snapshot.VersionText;
            PauseIndicator.Visibility = snapshot.Paused ? Visibility.Visible : Visibility.Collapsed;
            AttachButtonText.Text = snapshot.Attached ? "重新应用" : "应用增强";
            AttachButton.IsEnabled = snapshot.Pid != 0 && snapshot.State != SessionState.Attaching;

            string stateText;
            Brush dot;
            if (snapshot.Paused)
            {
                stateText = "已暂停";
                dot = ErrorBrush;
            }
            else
            {
                switch (snapshot.State)
                {
                    case SessionState.Detected:
                        stateText = "已检测";
                        dot = DetectedBrush;
                        break;
                    case SessionState.Attaching:
                        stateText = "附加中";
                        dot = AttachingBrush;
                        break;
                    case SessionState.Attached:
                        stateText = "已附加";
                        dot = AttachedBrush;
                        break;
                    case SessionState.Error:
                        stateText = "错误";
                        dot = ErrorBrush;
                        break;
                    default:
                        stateText = "未运行";
                        dot = IdleBrush;
                        break;
                }
            }

            RunStateText.Text = stateText;
            StateDot.Fill = dot;

            if (_trayPauseItem != null)
            {
                _trayPauseItem.Text = snapshot.Paused ? "恢复改键与喊话" : "紧急暂停";
            }

            if (snapshot.NeedsRestart && !_needsRestartNotified)
            {
                _needsRestartNotified = true;
                ShowToast("窗口尺寸未完全生效，可能需要重启游戏。");
            }
            else if (!snapshot.NeedsRestart)
            {
                _needsRestartNotified = false;
            }
        }

        private void Nav_Checked(object sender, RoutedEventArgs e)
        {
            if (sender is RadioButton radio && radio.Tag is string key && _pages.Count > 0)
            {
                ShowPage(key);
            }
        }

        private void MinimizeButton_Click(object sender, RoutedEventArgs e)
        {
            WindowState = WindowState.Minimized;
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }

        private async void AttachButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                AttachButton.IsEnabled = false;
                await _ctx.Session.AttachNowAsync();
            }
            catch (Exception ex)
            {
                _ctx.Log.Error("应用增强失败", ex);
                ShowToast("应用增强失败: " + ex.Message);
            }
            finally
            {
                AttachButton.IsEnabled = true;
            }
        }

        private void LaunchButton_Click(object sender, RoutedEventArgs e)
        {
            string path = _ctx.Config.Game.Path;
            if (string.IsNullOrWhiteSpace(path) || !System.IO.File.Exists(path))
            {
                NavSettings.IsChecked = true;
                ShowToast("请先在设置中选择 war3.exe。");
                return;
            }

            _ctx.Session.LaunchGame();
        }

        private void ShowToast(string message)
        {
            if (string.IsNullOrWhiteSpace(message))
            {
                return;
            }

            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.BeginInvoke(new Action(() => ShowToast(message)));
                return;
            }

            ToastText.Text = message;
            ToastBorder.Visibility = Visibility.Visible;
            _toastTimer.Stop();
            _toastTimer.Start();
        }

        private void ApplyAppIcon()
        {
            try
            {
                using (System.Drawing.Icon icon = AppIcon.LoadLarge())
                {
                    Icon = System.Windows.Interop.Imaging.CreateBitmapSourceFromHIcon(
                        icon.Handle,
                        Int32Rect.Empty,
                        System.Windows.Media.Imaging.BitmapSizeOptions.FromEmptyOptions());
                }
            }
            catch
            {
                // The executable icon remains the fallback.
            }
        }

        private void CreateTrayIcon()
        {
            _trayIcon = AppIcon.LoadSmall();
            _tray = new Forms.NotifyIcon
            {
                Icon = _trayIcon,
                Text = "iWar3Key",
                Visible = true
            };

            var menu = new Forms.ContextMenuStrip();
            var showItem = new Forms.ToolStripMenuItem("显示 iWar3Key");
            showItem.Click += (s, e) => ShowFromTray();
            _trayPauseItem = new Forms.ToolStripMenuItem("紧急暂停");
            _trayPauseItem.Click += (s, e) => Dispatcher.BeginInvoke(new Action(() => _ctx.Session.TogglePause()));
            var exitItem = new Forms.ToolStripMenuItem("退出");
            exitItem.Click += (s, e) => Dispatcher.BeginInvoke(new Action(() =>
            {
                _forceClose = true;
                Close();
            }));

            menu.Items.Add(showItem);
            menu.Items.Add(_trayPauseItem);
            menu.Items.Add(new Forms.ToolStripSeparator());
            menu.Items.Add(exitItem);

            _tray.ContextMenuStrip = menu;
            _tray.DoubleClick += (s, e) => ShowFromTray();
        }

        private void ShowFromTray()
        {
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.BeginInvoke(new Action(ShowFromTray));
                return;
            }

            Show();
            WindowState = WindowState.Normal;
            Activate();
        }

        private void MainWindow_Closed(object sender, EventArgs e)
        {
            if (_closed)
            {
                return;
            }

            _closed = true;

            _ctx.StatusChanged -= ShowToast;
            _ctx.Session.SnapshotChanged -= OnSnapshot;
            _toastTimer.Stop();

            if (_tray != null)
            {
                _tray.Visible = false;
                _tray.Dispose();
                _tray = null;
            }

            if (_trayIcon != null)
            {
                _trayIcon.Dispose();
                _trayIcon = null;
            }

            _ctx.Session.Dispose();
            _ctx.Store.SaveNow();
            _ctx.Log.Info("主窗口已关闭。");
        }

        private static Brush Frozen(byte r, byte g, byte b)
        {
            var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
            brush.Freeze();
            return brush;
        }
    }
}
