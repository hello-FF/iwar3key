using System;
using System.IO;

namespace IWar3Key.Shell.Core
{
    public sealed class AppPaths
    {
        public AppPaths()
        {
            BaseDirectory = AppDomain.CurrentDomain.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);
            DataDirectory = ResolveDataDirectory();
            LogDirectory = Path.Combine(DataDirectory, "logs");
            BackupDirectory = Path.Combine(DataDirectory, "backups");
            Directory.CreateDirectory(LogDirectory);
            Directory.CreateDirectory(BackupDirectory);
        }

        public string BaseDirectory { get; }

        public string DataDirectory { get; }

        public string LogDirectory { get; }

        public string BackupDirectory { get; }

        public string SettingsFile => Path.Combine(DataDirectory, "settings.json");

        public string RegistryBackupFile => Path.Combine(DataDirectory, "registry-backup.json");

        public string AgentDllPath => Path.Combine(BaseDirectory, "agent", "iwar3key32.dll");

        public string ProfilesDirectory => Path.Combine(BaseDirectory, "profiles");

        private string ResolveDataDirectory()
        {
            string portable = Path.Combine(BaseDirectory, "data");
            try
            {
                Directory.CreateDirectory(portable);
                string probe = Path.Combine(portable, ".write-test");
                File.WriteAllText(probe, string.Empty);
                File.Delete(probe);
                return portable;
            }
            catch
            {
                string appData = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "iWar3Key");
                Directory.CreateDirectory(appData);
                return appData;
            }
        }
    }
}
