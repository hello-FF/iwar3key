using System;
using IWar3Key.Shell.Interop;

namespace IWar3Key.Shell.Core
{
    public sealed class ShellContext
    {
        public ShellContext(AppPaths paths, Logger log, ConfigStore store, ProfileCatalog profiles)
        {
            Paths = paths;
            Log = log;
            Store = store;
            Profiles = profiles;
        }

        public AppPaths Paths { get; }

        public Logger Log { get; }

        public ConfigStore Store { get; }

        public ProfileCatalog Profiles { get; }

        public AppConfig Config => Store.Config;

        public GameSession Session { get; set; }

        public event Action<string> StatusChanged;

        public void Status(string message)
        {
            StatusChanged?.Invoke(message);
        }
    }
}
