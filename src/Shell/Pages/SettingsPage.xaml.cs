using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Web.Script.Serialization;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using IWar3Key.Shell.Core;
using IWar3Key.Shell.Interop;

namespace IWar3Key.Shell.Pages
{
    public partial class SettingsPage : UserControl
    {
        private readonly ShellContext _ctx;

        public SettingsPage(ShellContext context)
        {
            _ctx = context;
            InitializeComponent();
            DataContext = context.Config;
        }

        private void BrowseGame_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new OpenFileDialog
            {
                Title = "选择 Warcraft III 的 war3.exe",
                Filter = "war3.exe|war3.exe|可执行文件 (*.exe)|*.exe",
                CheckFileExists = true
            };

            if (dialog.ShowDialog() == true)
            {
                _ctx.Config.Game.Path = dialog.FileName;
                _ctx.Status("已设置游戏路径: " + dialog.FileName);
            }
        }

        private void OpenDataDir_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = "explorer.exe",
                    Arguments = "\"" + _ctx.Paths.DataDirectory + "\"",
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                _ctx.Status("打开配置目录失败: " + ex.Message);
            }
        }

        private void Export_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new SaveFileDialog
            {
                Title = "导出配置",
                FileName = "iwar3key-settings.json",
                Filter = "JSON 配置 (*.json)|*.json"
            };

            if (dialog.ShowDialog() != true)
            {
                return;
            }

            try
            {
                _ctx.Store.SaveNow();
                File.Copy(_ctx.Paths.SettingsFile, dialog.FileName, true);
                _ctx.Status("配置已导出: " + dialog.FileName);
            }
            catch (Exception ex)
            {
                _ctx.Log.Error("导出配置失败", ex);
                _ctx.Status("导出配置失败: " + ex.Message);
            }
        }

        private void Import_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new OpenFileDialog
            {
                Title = "导入配置",
                Filter = "JSON 配置 (*.json)|*.json",
                CheckFileExists = true
            };

            if (dialog.ShowDialog() != true)
            {
                return;
            }

            try
            {
                var serializer = new JavaScriptSerializer { MaxJsonLength = 32 * 1024 * 1024 };
                serializer.Deserialize<AppConfig>(File.ReadAllText(dialog.FileName, Encoding.UTF8));
            }
            catch (Exception ex)
            {
                _ctx.Status("配置文件无效: " + ex.Message);
                return;
            }

            MessageBoxResult confirm = MessageBox.Show(
                "导入会覆盖当前设置。导入后需要重启 iWar3Key 才能完全生效，是否继续？",
                "导入配置",
                MessageBoxButton.OKCancel,
                MessageBoxImage.Warning);
            if (confirm != MessageBoxResult.OK)
            {
                return;
            }

            try
            {
                File.Copy(dialog.FileName, _ctx.Paths.SettingsFile, true);
                _ctx.Status("配置已导入，请重启 iWar3Key。");
            }
            catch (Exception ex)
            {
                _ctx.Log.Error("导入配置失败", ex);
                _ctx.Status("导入配置失败: " + ex.Message);
            }
        }

        private void RestoreRegistry_Click(object sender, RoutedEventArgs e)
        {
            MessageBoxResult confirm = MessageBox.Show(
                "将把 Warcraft III 的 Video 分辨率键值恢复为启动工具前备份的值，是否继续？",
                "恢复分辨率注册表",
                MessageBoxButton.OKCancel,
                MessageBoxImage.Question);
            if (confirm != MessageBoxResult.OK)
            {
                return;
            }

            var registry = new VideoRegistry(_ctx.Paths, _ctx.Log);
            bool restored = registry.RestoreNow();
            _ctx.Status(restored ? "已恢复分辨率注册表。" : "没有可恢复的分辨率备份。");
        }
    }
}
