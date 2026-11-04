using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Text;
using System.Web.Script.Serialization;
using System.Windows.Threading;
using Microsoft.Win32;

namespace IWar3Key.Shell.Core
{
    public sealed class ConfigStore
    {
        private readonly AppPaths _paths;
        private readonly Logger _log;
        private readonly JavaScriptSerializer _json = new JavaScriptSerializer { MaxJsonLength = 32 * 1024 * 1024 };
        private readonly HashSet<object> _attached = new HashSet<object>();
        private readonly DispatcherTimer _saveTimer;

        public ConfigStore(AppPaths paths, Logger log)
        {
            _paths = paths;
            _log = log;
            _saveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
            _saveTimer.Tick += (s, e) =>
            {
                _saveTimer.Stop();
                SaveNow();
            };
        }

        public const int MaxSchemes = 9;

        public const int MaxSchemeNameLength = 12;

        public AppConfig Config { get; private set; } = new AppConfig();

        public event Action<string> Saved;

        public event Action SchemesChanged;

        public void Load()
        {
            AppConfig config = null;
            bool created = false;

            if (File.Exists(_paths.SettingsFile))
            {
                try
                {
                    config = _json.Deserialize<AppConfig>(File.ReadAllText(_paths.SettingsFile, Encoding.UTF8));
                }
                catch (Exception ex)
                {
                    _log.Error("读取 settings.json 失败，尝试备份", ex);
                    config = TryLoadBackup();
                }
            }
            else
            {
                created = true;
            }

            if (config == null)
            {
                config = CreateDefaults();
                created = true;
            }

            int savedWindowWidth = config.Window == null ? 0 : config.Window.Width;
            int savedWindowHeight = config.Window == null ? 0 : config.Window.Height;
            config.Normalize();
            bool schemesMigrated = EnsureSchemes(config);
            bool windowNormalized =
                config.Window.Width != savedWindowWidth ||
                config.Window.Height != savedWindowHeight;

            if (string.IsNullOrWhiteSpace(config.Game.Path) || !File.Exists(config.Game.Path))
            {
                string detected = DetectGamePath();
                if (!string.IsNullOrWhiteSpace(detected))
                {
                    config.Game.Path = detected;
                }
            }

            Config = config;
            _attached.Clear();
            Attach(config);

            if (created || windowNormalized || schemesMigrated)
            {
                SaveNow();
            }
        }

        public void RequestSave()
        {
            _saveTimer.Stop();
            _saveTimer.Start();
        }

        public void SaveNow()
        {
            _saveTimer.Stop();
            try
            {
                Config.Window.Normalize();
                string json = _json.Serialize(Config);
                string tmp = _paths.SettingsFile + ".tmp";
                File.WriteAllText(tmp, json, new UTF8Encoding(false));

                if (File.Exists(_paths.SettingsFile))
                {
                    string backup = Path.Combine(
                        _paths.BackupDirectory,
                        "settings-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".json");
                    File.Copy(_paths.SettingsFile, backup, true);
                    PruneBackups();
                    File.Replace(tmp, _paths.SettingsFile, null);
                }
                else
                {
                    File.Move(tmp, _paths.SettingsFile);
                }

                Saved?.Invoke(DateTime.Now.ToString("HH:mm"));
            }
            catch (Exception ex)
            {
                _log.Error("保存配置失败", ex);
            }
        }

        public string ActiveSchemeName =>
            Config.Schemes == null ? string.Empty : (Config.Schemes.ActiveName ?? string.Empty);

        public IReadOnlyList<SchemeInfo> GetSchemes()
        {
            var schemes = new List<SchemeInfo>();
            SchemeLibrary library = Config.Schemes;
            if (library == null)
            {
                return schemes;
            }

            foreach (string id in library.Order)
            {
                if (string.Equals(id, library.ActiveId, StringComparison.Ordinal))
                {
                    schemes.Add(new SchemeInfo(id, library.ActiveName, true));
                    continue;
                }

                SchemeSnapshot scheme = FindOther(id);
                if (scheme != null)
                {
                    schemes.Add(new SchemeInfo(id, scheme.Name, false));
                }
            }

            return schemes;
        }

        public bool SwitchScheme(string id)
        {
            if (!ApplyScheme(id))
            {
                return false;
            }

            SaveNow();
            SchemesChanged?.Invoke();
            return true;
        }

        public SchemeSnapshot CreateScheme()
        {
            SchemeLibrary library = Config.Schemes;
            if (library == null || library.Order.Count >= MaxSchemes)
            {
                return null;
            }

            var scheme = new SchemeSnapshot
            {
                Id = NewSchemeId(library),
                Name = UniqueName("新方案", null),
                Input = new InputOptions(),
                Chat = new ChatOptions()
            };
            scheme.Input.Normalize();
            scheme.Chat.Normalize();

            library.Others.Add(scheme);
            library.Order.Add(scheme.Id);
            if (!ApplyScheme(scheme.Id))
            {
                library.Others.Remove(scheme);
                library.Order.Remove(scheme.Id);
                return null;
            }

            SaveNow();
            SchemesChanged?.Invoke();
            return scheme;
        }

        public SchemeSnapshot DuplicateScheme(string id)
        {
            SchemeLibrary library = Config.Schemes;
            if (library == null || library.Order.Count >= MaxSchemes)
            {
                return null;
            }

            SchemeSnapshot source;
            if (string.Equals(id, library.ActiveId, StringComparison.Ordinal))
            {
                source = new SchemeSnapshot
                {
                    Id = library.ActiveId,
                    Name = library.ActiveName,
                    Input = Clone(Config.Input) ?? new InputOptions(),
                    Chat = Clone(Config.Chat) ?? new ChatOptions()
                };
            }
            else
            {
                source = Clone(FindOther(id));
                if (source == null)
                {
                    return null;
                }
            }

            string sourceName = string.IsNullOrWhiteSpace(source.Name) ? "未命名方案" : source.Name.Trim();
            const string copySuffix = " 副本";
            int room = MaxSchemeNameLength - copySuffix.Length;
            if (sourceName.Length > room)
            {
                sourceName = sourceName.Substring(0, room);
            }

            var copy = new SchemeSnapshot
            {
                Id = NewSchemeId(library),
                Name = UniqueName(sourceName + copySuffix, null),
                Input = source.Input,
                Chat = source.Chat
            };
            RenewMacroIds(copy.Chat);

            int index = library.Order.IndexOf(id);
            if (index < 0)
            {
                index = library.Order.Count - 1;
            }

            library.Order.Insert(index + 1, copy.Id);
            library.Others.Add(copy);
            SaveNow();
            SchemesChanged?.Invoke();
            return copy;
        }

        public string RenameScheme(string id, string name)
        {
            SchemeLibrary library = Config.Schemes;
            if (library == null)
            {
                return null;
            }

            string trimmed = (name ?? string.Empty).Trim();
            if (trimmed.Length == 0)
            {
                return null;
            }

            if (trimmed.Length > MaxSchemeNameLength)
            {
                trimmed = trimmed.Substring(0, MaxSchemeNameLength);
            }

            string current;
            SchemeSnapshot other = null;
            if (string.Equals(id, library.ActiveId, StringComparison.Ordinal))
            {
                current = library.ActiveName;
            }
            else
            {
                other = FindOther(id);
                if (other == null)
                {
                    return null;
                }

                current = other.Name;
            }

            string final = UniqueName(trimmed, id);
            if (string.Equals(current, final, StringComparison.Ordinal))
            {
                return final;
            }

            if (other == null)
            {
                library.ActiveName = final;
            }
            else
            {
                other.Name = final;
            }

            SaveNow();
            SchemesChanged?.Invoke();
            return final;
        }

        public bool DeleteScheme(string id)
        {
            SchemeLibrary library = Config.Schemes;
            if (library == null || library.Order.Count <= 1)
            {
                return false;
            }

            bool active = string.Equals(id, library.ActiveId, StringComparison.Ordinal);
            if (!active && FindOther(id) == null)
            {
                return false;
            }

            if (active)
            {
                int index = library.Order.IndexOf(id);
                if (index < 0)
                {
                    index = 0;
                }

                int nextIndex = index == library.Order.Count - 1 ? index - 1 : index + 1;
                if (nextIndex < 0 || nextIndex >= library.Order.Count)
                {
                    return false;
                }

                if (!ApplyScheme(library.Order[nextIndex]))
                {
                    return false;
                }
            }

            library.Others.RemoveAll(
                s => s != null && string.Equals(s.Id, id, StringComparison.Ordinal));
            library.Order.RemoveAll(x => string.Equals(x, id, StringComparison.Ordinal));
            SaveNow();
            SchemesChanged?.Invoke();
            return true;
        }

        private bool ApplyScheme(string id)
        {
            SchemeLibrary library = Config.Schemes;
            if (library == null || string.IsNullOrWhiteSpace(id))
            {
                return false;
            }

            if (string.Equals(id, library.ActiveId, StringComparison.Ordinal))
            {
                return false;
            }

            SchemeSnapshot target = FindOther(id);
            if (target == null)
            {
                return false;
            }

            var previous = new SchemeSnapshot
            {
                Id = library.ActiveId,
                Name = library.ActiveName,
                Input = Clone(Config.Input),
                Chat = Clone(Config.Chat)
            };

            library.Others.Remove(target);
            library.Others.Add(previous);
            ApplyInput(Config.Input, target.Input);
            ApplyChat(Config.Chat, target.Chat);
            library.ActiveId = target.Id;
            library.ActiveName = target.Name;
            return true;
        }

        private SchemeSnapshot FindOther(string id)
        {
            SchemeLibrary library = Config.Schemes;
            if (library == null || string.IsNullOrWhiteSpace(id))
            {
                return null;
            }

            return library.Others.FirstOrDefault(
                s => s != null && string.Equals(s.Id, id, StringComparison.Ordinal));
        }

        private void ApplyInput(InputOptions target, InputOptions source)
        {
            InputOptions copy = Clone(source) ?? new InputOptions();
            copy.Normalize();

            target.Enabled = copy.Enabled;
            target.ItemsEnabled = copy.ItemsEnabled;
            target.SkillsEnabled = copy.SkillsEnabled;
            target.MappingEnabled = copy.MappingEnabled;

            target.Items.Clear();
            foreach (ItemSlot item in copy.Items)
            {
                target.Items.Add(item);
            }

            target.Skills.Clear();
            foreach (SkillSlot skill in copy.Skills)
            {
                target.Skills.Add(skill);
            }

            target.Mappings.Clear();
            foreach (KeyMapping mapping in copy.Mappings)
            {
                target.Mappings.Add(mapping);
            }

            target.Normalize();
        }

        private void ApplyChat(ChatOptions target, ChatOptions source)
        {
            ChatOptions copy = Clone(source) ?? new ChatOptions();
            copy.Normalize();

            target.AntiSpam = copy.AntiSpam;
            target.SendDelayMs = copy.SendDelayMs;
            target.CooldownMs = copy.CooldownMs;
            target.Macros.Clear();
            foreach (ChatMacro macro in copy.Macros)
            {
                target.Macros.Add(macro);
            }

            target.Normalize();
        }

        private T Clone<T>(T value) where T : class
        {
            return value == null ? null : _json.Deserialize<T>(_json.Serialize(value));
        }

        private string UniqueName(string baseName, string exceptId)
        {
            string trimmed = (baseName ?? string.Empty).Trim();
            if (trimmed.Length == 0)
            {
                trimmed = "未命名方案";
            }

            if (trimmed.Length > MaxSchemeNameLength)
            {
                trimmed = trimmed.Substring(0, MaxSchemeNameLength);
            }

            string candidate = trimmed;
            int suffix = 2;
            while (NameTaken(candidate, exceptId))
            {
                candidate = trimmed + " " + suffix;
                suffix++;
            }

            return candidate;
        }

        private bool NameTaken(string name, string exceptId)
        {
            SchemeLibrary library = Config.Schemes;
            if (library == null)
            {
                return false;
            }

            if (!string.Equals(exceptId, library.ActiveId, StringComparison.Ordinal) &&
                string.Equals(name, library.ActiveName, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            return library.Others.Any(
                s => s != null &&
                     !string.Equals(s.Id, exceptId, StringComparison.Ordinal) &&
                     string.Equals(name, s.Name, StringComparison.OrdinalIgnoreCase));
        }

        private static string NewSchemeId(SchemeLibrary library)
        {
            while (true)
            {
                string id = Guid.NewGuid().ToString("N").Substring(0, 8);
                if (string.Equals(id, library.ActiveId, StringComparison.Ordinal))
                {
                    continue;
                }

                if (library.Others.Any(
                        s => s != null && string.Equals(s.Id, id, StringComparison.Ordinal)))
                {
                    continue;
                }

                return id;
            }
        }

        private static void RenewMacroIds(ChatOptions chat)
        {
            if (chat == null || chat.Macros == null)
            {
                return;
            }

            foreach (ChatMacro macro in chat.Macros)
            {
                macro.Id = Guid.NewGuid().ToString("N").Substring(0, 8);
            }
        }

        private static bool EnsureSchemes(AppConfig config)
        {
            bool changed = false;
            SchemeLibrary library = config.Schemes;
            if (library == null)
            {
                library = new SchemeLibrary();
                config.Schemes = library;
                changed = true;
            }

            if (library.Others == null)
            {
                library.Others = new List<SchemeSnapshot>();
                changed = true;
            }

            if (library.Order == null)
            {
                library.Order = new List<string>();
                changed = true;
            }

            int before = library.Others.Count;
            library.Others.RemoveAll(s => s == null || string.IsNullOrWhiteSpace(s.Id));
            if (library.Others.Count != before)
            {
                changed = true;
            }

            var ids = new HashSet<string>(StringComparer.Ordinal);
            before = library.Others.Count;
            library.Others.RemoveAll(s => !ids.Add(s.Id));
            if (library.Others.Count != before)
            {
                changed = true;
            }

            foreach (SchemeSnapshot scheme in library.Others)
            {
                if (string.IsNullOrWhiteSpace(scheme.Name))
                {
                    scheme.Name = "未命名方案";
                    changed = true;
                }

                scheme.Input = scheme.Input ?? new InputOptions();
                scheme.Chat = scheme.Chat ?? new ChatOptions();
                scheme.Input.Normalize();
                scheme.Chat.Normalize();
            }

            if (string.IsNullOrWhiteSpace(library.ActiveId))
            {
                library.ActiveId = NewSchemeId(library);
                library.ActiveName = "方案 1";
                changed = true;
            }

            if (string.IsNullOrWhiteSpace(library.ActiveName))
            {
                library.ActiveName = "方案 1";
                changed = true;
            }

            before = library.Others.Count;
            library.Others.RemoveAll(
                s => string.Equals(s.Id, library.ActiveId, StringComparison.Ordinal));
            if (library.Others.Count != before)
            {
                changed = true;
            }

            var valid = new HashSet<string>(StringComparer.Ordinal) { library.ActiveId };
            foreach (SchemeSnapshot scheme in library.Others)
            {
                valid.Add(scheme.Id);
            }

            before = library.Order.Count;
            library.Order.RemoveAll(id => string.IsNullOrWhiteSpace(id) || !valid.Contains(id));
            if (library.Order.Count != before)
            {
                changed = true;
            }

            var seen = new HashSet<string>(StringComparer.Ordinal);
            before = library.Order.Count;
            library.Order.RemoveAll(id => !seen.Add(id));
            if (library.Order.Count != before)
            {
                changed = true;
            }

            foreach (SchemeSnapshot scheme in library.Others)
            {
                if (!library.Order.Contains(scheme.Id))
                {
                    library.Order.Add(scheme.Id);
                    changed = true;
                }
            }

            if (!library.Order.Contains(library.ActiveId))
            {
                library.Order.Insert(0, library.ActiveId);
                changed = true;
            }

            return changed;
        }

        private AppConfig CreateDefaults()
        {
            // Keep the default config deliberately empty: window enhancement
            // on, no borderless mode, item/skill/mapping features off, no
            // default keys or macros, and no hard-coded game path. Normalize()
            // only creates empty item/skill rows for the UI.
            var config = new AppConfig();
            config.Camera.Enabled = false;
            return config;
        }

        private AppConfig TryLoadBackup()
        {
            try
            {
                string latest = Directory.GetFiles(_paths.BackupDirectory, "settings-*.json")
                    .OrderByDescending(File.GetLastWriteTimeUtc)
                    .FirstOrDefault();
                if (latest == null)
                {
                    return null;
                }

                _log.Warn("从备份恢复配置: " + Path.GetFileName(latest));
                return _json.Deserialize<AppConfig>(File.ReadAllText(latest, Encoding.UTF8));
            }
            catch (Exception ex)
            {
                _log.Error("读取配置备份失败", ex);
                return null;
            }
        }

        private void PruneBackups()
        {
            try
            {
                foreach (string file in Directory.GetFiles(_paths.BackupDirectory, "settings-*.json")
                             .OrderByDescending(File.GetLastWriteTimeUtc)
                             .Skip(10))
                {
                    File.Delete(file);
                }
            }
            catch
            {
                // Best effort.
            }
        }

        private static string DetectGamePath()
        {
            var candidates = new List<string>();

            foreach (string hive in new[] { "HKCU", "HKLM" })
            {
                foreach (string sub in new[]
                         {
                             @"Software\Blizzard Entertainment\Warcraft III",
                             @"Software\WOW6432Node\Blizzard Entertainment\Warcraft III"
                         })
                {
                    try
                    {
                        using (RegistryKey key = hive == "HKCU" ? Registry.CurrentUser.OpenSubKey(sub) : Registry.LocalMachine.OpenSubKey(sub))
                        {
                            string install = key?.GetValue("InstallPath") as string;
                            if (!string.IsNullOrWhiteSpace(install))
                            {
                                candidates.Add(Path.Combine(install, "war3.exe"));
                            }
                        }
                    }
                    catch
                    {
                        // Ignore registry access failures.
                    }
                }
            }

            // Do not hard-code machine-specific install paths. Only use the
            // game's own InstallPath registry value; otherwise leave the path
            // empty and let the user choose the executable.
            return candidates.FirstOrDefault(File.Exists);
        }

        private void Attach(object obj)
        {
            if (obj == null || !_attached.Add(obj))
            {
                return;
            }

            if (obj is INotifyPropertyChanged npc)
            {
                npc.PropertyChanged += OnPropertyChanged;
            }

            if (obj is AppConfig config)
            {
                Attach(config.Game);
                Attach(config.Window);
                Attach(config.Camera);
                Attach(config.StatusBar);
                Attach(config.Input);
                Attach(config.Chat);
                AttachCollection(config.Input.Items);
                AttachCollection(config.Input.Skills);
                AttachCollection(config.Input.Mappings);
                AttachCollection(config.Chat.Macros);
            }
            else if (obj is InputOptions input)
            {
                foreach (ItemSlot item in input.Items)
                {
                    Attach(item);
                }

                foreach (SkillSlot skill in input.Skills)
                {
                    Attach(skill);
                }

                foreach (KeyMapping mapping in input.Mappings)
                {
                    Attach(mapping);
                }
            }
            else if (obj is ChatOptions chat)
            {
                foreach (ChatMacro macro in chat.Macros)
                {
                    Attach(macro);
                }
            }
        }

        private void AttachCollection<T>(System.Collections.ObjectModel.ObservableCollection<T> collection)
        {
            if (collection == null || !_attached.Add(collection))
            {
                return;
            }

            collection.CollectionChanged += OnCollectionChanged;
            foreach (T item in collection)
            {
                Attach(item);
            }
        }

        private void OnCollectionChanged(object sender, NotifyCollectionChangedEventArgs e)
        {
            if (e.NewItems != null)
            {
                foreach (object item in e.NewItems)
                {
                    Attach(item);
                }
            }

            if (e.OldItems != null)
            {
                foreach (object item in e.OldItems)
                {
                    if (item is INotifyPropertyChanged npc)
                    {
                        npc.PropertyChanged -= OnPropertyChanged;
                    }

                    _attached.Remove(item);
                }
            }

            OnPropertyChanged(sender, new PropertyChangedEventArgs("Collection"));
        }

        private void OnPropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            RequestSave();
        }
    }
}
