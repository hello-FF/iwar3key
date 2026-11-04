using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Web.Script.Serialization;
using Microsoft.Win32;
using IWar3Key.Shell.Core;

namespace IWar3Key.Shell.Interop
{
    public sealed class VideoBackup
    {
        public bool HadWidth { get; set; }
        public bool HadHeight { get; set; }
        public bool HadColorDepth { get; set; }
        public int Width { get; set; }
        public int Height { get; set; }
        public int ColorDepth { get; set; }
        public int AppliedWidth { get; set; }
        public int AppliedHeight { get; set; }
        public bool Applied { get; set; }
        public bool Restored { get; set; }
        public string AppliedAt { get; set; }
    }

    public sealed class VideoRegistry
    {
        private const string VideoKeyPath = @"Software\Blizzard Entertainment\Warcraft III\Video";
        private readonly AppPaths _paths;
        private readonly Logger _log;
        private readonly JavaScriptSerializer _json = new JavaScriptSerializer();

        public VideoRegistry(AppPaths paths, Logger log)
        {
            _paths = paths;
            _log = log;
        }

        public void Apply(int width, int height)
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.CreateSubKey(VideoKeyPath))
                {
                    if (key == null)
                    {
                        _log.Warn("无法打开 Warcraft III Video 注册表键。");
                        return;
                    }

                    VideoBackup backup = LoadBackup();
                    bool gameRunning = IsGameRunning();

                    if (backup != null && backup.Applied && !backup.Restored)
                    {
                        if (!gameRunning)
                        {
                            RestoreInternal(backup, true);
                        }
                        else
                        {
                            _log.Warn("上次分辨率备份尚未恢复，本次保留原始值。");
                        }
                    }
                    else
                    {
                        backup = new VideoBackup
                        {
                            HadWidth = key.GetValue("reswidth") != null,
                            HadHeight = key.GetValue("resheight") != null,
                            HadColorDepth = key.GetValue("colordepth") != null,
                            Width = ReadInt(key, "reswidth"),
                            Height = ReadInt(key, "resheight"),
                            ColorDepth = ReadInt(key, "colordepth")
                        };
                    }

                    backup.Applied = true;
                    backup.Restored = false;
                    backup.AppliedWidth = width;
                    backup.AppliedHeight = height;
                    backup.AppliedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
                    SaveBackup(backup);

                    key.SetValue("reswidth", width, RegistryValueKind.DWord);
                    key.SetValue("resheight", height, RegistryValueKind.DWord);
                    key.SetValue("colordepth", 32, RegistryValueKind.DWord);
                    _log.Info(string.Format("已写入游戏分辨率 {0}x{1}，原值已备份。", width, height));
                }
            }
            catch (Exception ex)
            {
                _log.Error("写入游戏分辨率失败", ex);
            }
        }

        public bool RestoreIfPending(bool onlyIfNoGame)
        {
            VideoBackup backup = LoadBackup();
            if (backup == null || !backup.Applied || backup.Restored)
            {
                return false;
            }

            if (onlyIfNoGame && IsGameRunning())
            {
                _log.Info("游戏仍在运行，分辨率备份留到下次启动恢复。");
                return false;
            }

            return RestoreInternal(backup, true);
        }

        public bool RestoreNow()
        {
            VideoBackup backup = LoadBackup();
            if (backup == null || !backup.Applied || backup.Restored)
            {
                return false;
            }

            return RestoreInternal(backup, true);
        }

        private bool RestoreInternal(VideoBackup backup, bool markRestored)
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(VideoKeyPath, true))
                {
                    if (key == null)
                    {
                        return false;
                    }

                    int currentWidth = ReadInt(key, "reswidth");
                    int currentHeight = ReadInt(key, "resheight");
                    bool currentMatchesApplied =
                        (key.GetValue("reswidth") == null || currentWidth == backup.AppliedWidth) &&
                        (key.GetValue("resheight") == null || currentHeight == backup.AppliedHeight);

                    if (!currentMatchesApplied)
                    {
                        _log.Warn("游戏内分辨率已被改变，保留当前值，不覆盖用户设置。");
                    }
                    else
                    {
                        RestoreValue(key, "reswidth", backup.HadWidth, backup.Width);
                        RestoreValue(key, "resheight", backup.HadHeight, backup.Height);
                        RestoreValue(key, "colordepth", backup.HadColorDepth, backup.ColorDepth);
                        _log.Info("已恢复游戏分辨率注册表。");
                    }
                }

                backup.Restored = true;
                SaveBackup(backup);
                return true;
            }
            catch (Exception ex)
            {
                _log.Error("恢复游戏分辨率失败", ex);
                return false;
            }
        }

        private static void RestoreValue(RegistryKey key, string name, bool had, int value)
        {
            if (had)
            {
                key.SetValue(name, value, RegistryValueKind.DWord);
            }
            else
            {
                key.DeleteValue(name, false);
            }
        }

        private static int ReadInt(RegistryKey key, string name)
        {
            object value = key.GetValue(name);
            if (value == null)
            {
                return 0;
            }

            try
            {
                return Convert.ToInt32(value);
            }
            catch
            {
                return 0;
            }
        }

        private VideoBackup LoadBackup()
        {
            try
            {
                if (!File.Exists(_paths.RegistryBackupFile))
                {
                    return null;
                }

                return _json.Deserialize<VideoBackup>(File.ReadAllText(_paths.RegistryBackupFile, Encoding.UTF8));
            }
            catch (Exception ex)
            {
                _log.Error("读取分辨率备份失败", ex);
                return null;
            }
        }

        private void SaveBackup(VideoBackup backup)
        {
            try
            {
                File.WriteAllText(
                    _paths.RegistryBackupFile,
                    _json.Serialize(backup),
                    new UTF8Encoding(false));
            }
            catch (Exception ex)
            {
                _log.Error("保存分辨率备份失败", ex);
            }
        }

        private static bool IsGameRunning()
        {
            try
            {
                foreach (Process process in Process.GetProcessesByName("war3"))
                {
                    process.Dispose();
                    return true;
                }
            }
            catch
            {
                // Ignore.
            }

            return false;
        }
    }
}
