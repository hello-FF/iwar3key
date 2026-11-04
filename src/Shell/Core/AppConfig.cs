using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Web.Script.Serialization;

namespace IWar3Key.Shell.Core
{
    public sealed class AppConfig : ObservableObject
    {
        public GameOptions Game { get; set; } = new GameOptions();

        public WindowOptions Window { get; set; } = new WindowOptions();

        public CameraOptions Camera { get; set; } = new CameraOptions();

        public StatusBarOptions StatusBar { get; set; } = new StatusBarOptions();

        public InputOptions Input { get; set; } = new InputOptions();

        public ChatOptions Chat { get; set; } = new ChatOptions();

        // The root Input/Chat objects are the active scheme's live working
        // copy. Inactive schemes live in Schemes.Others, so each scheme is
        // stored exactly once in settings.json.
        public SchemeLibrary Schemes { get; set; } = new SchemeLibrary();

        public void Normalize()
        {
            Game = Game ?? new GameOptions();
            Window = Window ?? new WindowOptions();
            Camera = Camera ?? new CameraOptions();
            StatusBar = StatusBar ?? new StatusBarOptions();
            Input = Input ?? new InputOptions();
            Chat = Chat ?? new ChatOptions();
            Schemes = Schemes ?? new SchemeLibrary();

            StatusBar.Normalize();
            Input.Normalize();
            Chat.Normalize();
            Schemes.Normalize();
            Window.Normalize();
            Camera.Normalize();

            if (string.IsNullOrWhiteSpace(Game.ApplyHotkey))
            {
                Game.ApplyHotkey = "Ctrl+Alt+W";
            }

            if (string.IsNullOrWhiteSpace(Game.PanicHotkey))
            {
                Game.PanicHotkey = "Ctrl+Alt+F12";
            }
        }
    }

    public sealed class GameOptions : ObservableObject
    {
        private string _path = string.Empty;
        private bool _autoApply = true;
        private string _applyHotkey = "Ctrl+Alt+W";
        private string _panicHotkey = "Ctrl+Alt+F12";
        private bool _minimizeToTray;

        public string Path
        {
            get => _path;
            set => Set(ref _path, value);
        }

        public bool AutoApply
        {
            get => _autoApply;
            set => Set(ref _autoApply, value);
        }

        public string ApplyHotkey
        {
            get => _applyHotkey;
            set => Set(ref _applyHotkey, value);
        }

        public string PanicHotkey
        {
            get => _panicHotkey;
            set => Set(ref _panicHotkey, value);
        }

        public bool MinimizeToTray
        {
            get => _minimizeToTray;
            set => Set(ref _minimizeToTray, value);
        }
    }

    public sealed class WindowOptions : ObservableObject
    {
        private bool _enabled = true;
        private bool _borderless;
        private bool _alwaysOnTop;
        private bool _clipMouse = true;
        private int _width = 1920;
        private int _height = 1080;
        private bool _customResolution;

        public bool Enabled
        {
            get => _enabled;
            set => Set(ref _enabled, value);
        }

        public bool Borderless
        {
            get => _borderless;
            set => Set(ref _borderless, value);
        }

        public bool AlwaysOnTop
        {
            get => _alwaysOnTop;
            set => Set(ref _alwaysOnTop, value);
        }

        public bool ClipMouse
        {
            get => _clipMouse;
            set => Set(ref _clipMouse, value);
        }

        public int Width
        {
            get => _width;
            set => Set(ref _width, value);
        }

        public int Height
        {
            get => _height;
            set => Set(ref _height, value);
        }

        public bool CustomResolution
        {
            get => _customResolution;
            set => Set(ref _customResolution, value);
        }

        public void Normalize()
        {
            if (Width < 640)
            {
                Width = 640;
            }

            if (Height < 480)
            {
                Height = 480;
            }

            // War3 windowed mode is effectively capped at 1920x1023 outer.
            // Keep the saved profile within the supported U9-like range so a
            // stale 2560x1440 value cannot keep exercising the unsupported
            // window-size path.
            if (Width > 1920)
            {
                Width = 1920;
            }

            if (Height > 1080)
            {
                Height = 1080;
            }
        }
    }

    public sealed class CameraOptions : ObservableObject
    {
        private bool _enabled;
        private int _maxDistance = 3000;
        private string _zoomOutHotkey = "Alt+PageUp";
        private string _zoomInHotkey = "Alt+PageDown";

        public bool Enabled
        {
            get => _enabled;
            set => Set(ref _enabled, value);
        }

        public int MaxDistance
        {
            get => _maxDistance;
            set => Set(ref _maxDistance, value);
        }

        public string ZoomOutHotkey
        {
            get => _zoomOutHotkey;
            set => Set(ref _zoomOutHotkey, value);
        }

        public string ZoomInHotkey
        {
            get => _zoomInHotkey;
            set => Set(ref _zoomInHotkey, value);
        }

        public void Normalize()
        {
            if (MaxDistance < 1650)
            {
                MaxDistance = 1650;
            }
            else if (MaxDistance > 3000)
            {
                MaxDistance = 3000;
            }

            if (!HotkeyUtil.TryParse(ZoomOutHotkey, out _))
            {
                ZoomOutHotkey = "Alt+PageUp";
            }

            if (!HotkeyUtil.TryParse(ZoomInHotkey, out _))
            {
                ZoomInHotkey = "Alt+PageDown";
            }
        }
    }

    public sealed class StatusBarOptions : ObservableObject
    {
        private bool _healthEnabled;
        private bool _friendlyMana;
        private bool _enemyMana;

        // Always show health bars for every unit, regardless of relation.
        public bool HealthEnabled
        {
            get => _healthEnabled;
            set
            {
                if (Set(ref _healthEnabled, value))
                {
                    if (!value)
                    {
                        FriendlyMana = false;
                        EnemyMana = false;
                    }
                    OnPropertyChanged(nameof(ManaEnabled));
                }
            }
        }

        // Mana bars depend on the health bar being shown, so the two mana
        // switches are only usable while HealthEnabled is on.
        public bool ManaEnabled => _healthEnabled;

        // Mana bar below the health bar. Friendly includes the local player's
        // own units; enemy covers everyone else.
        public bool FriendlyMana
        {
            get => _friendlyMana;
            set => Set(ref _friendlyMana, _healthEnabled && value);
        }

        public bool EnemyMana
        {
            get => _enemyMana;
            set => Set(ref _enemyMana, _healthEnabled && value);
        }

        public void Normalize()
        {
            if (!_healthEnabled)
            {
                _friendlyMana = false;
                _enemyMana = false;
            }
        }
    }

    public sealed class InputOptions : ObservableObject
    {
        private bool _enabled = true;
        private bool _itemsEnabled;
        private bool _skillsEnabled;
        private bool _mappingEnabled;

        public bool Enabled
        {
            get => _enabled;
            set => Set(ref _enabled, value);
        }

        public bool ItemsEnabled
        {
            get => _itemsEnabled;
            set => Set(ref _itemsEnabled, value);
        }

        public bool SkillsEnabled
        {
            get => _skillsEnabled;
            set => Set(ref _skillsEnabled, value);
        }

        public bool MappingEnabled
        {
            get => _mappingEnabled;
            set => Set(ref _mappingEnabled, value);
        }

        public ObservableCollection<ItemSlot> Items { get; set; } = new ObservableCollection<ItemSlot>();

        public ObservableCollection<SkillSlot> Skills { get; set; } = new ObservableCollection<SkillSlot>();

        public ObservableCollection<KeyMapping> Mappings { get; set; } = new ObservableCollection<KeyMapping>();

        public void Normalize()
        {
            Items = Items ?? new ObservableCollection<ItemSlot>();
            Skills = Skills ?? new ObservableCollection<SkillSlot>();
            Mappings = Mappings ?? new ObservableCollection<KeyMapping>();

            while (Items.Count < 6)
            {
                Items.Add(new ItemSlot { SlotNumber = Items.Count + 1 });
            }

            while (Skills.Count < 12)
            {
                Skills.Add(new SkillSlot { SlotNumber = Skills.Count + 1 });
            }

            while (Items.Count > 6)
            {
                Items.RemoveAt(Items.Count - 1);
            }

            while (Skills.Count > 12)
            {
                Skills.RemoveAt(Skills.Count - 1);
            }

            for (int i = 0; i < Items.Count; i++)
            {
                Items[i].SlotNumber = i + 1;
            }

            for (int i = 0; i < Skills.Count; i++)
            {
                Skills[i].SlotNumber = i + 1;
            }

        }
    }

    public sealed class ItemSlot : ObservableObject
    {
        private int _slotNumber;
        private string _key;
        private bool _quickCast;

        public int SlotNumber
        {
            get => _slotNumber;
            set
            {
                if (Set(ref _slotNumber, value))
                {
                    OnPropertyChanged(nameof(DisplayName));
                    OnPropertyChanged(nameof(NativeLabel));
                }
            }
        }

        public string Key
        {
            get => _key;
            set => Set(ref _key, value);
        }

        public bool QuickCast
        {
            get => _quickCast;
            set => Set(ref _quickCast, value);
        }

        [ScriptIgnore]
        public string DisplayName => "物品 " + SlotNumber;

        [ScriptIgnore]
        public string NativeLabel
        {
            get
            {
                switch (SlotNumber)
                {
                    case 1: return "Num 7";
                    case 2: return "Num 8";
                    case 3: return "Num 4";
                    case 4: return "Num 5";
                    case 5: return "Num 1";
                    case 6: return "Num 2";
                    default: return string.Empty;
                }
            }
        }
    }

    public sealed class SkillSlot : ObservableObject
    {
        private int _slotNumber;
        private string _key;
        private bool _quickCast;

        public int SlotNumber
        {
            get => _slotNumber;
            set
            {
                if (Set(ref _slotNumber, value))
                {
                    OnPropertyChanged(nameof(PositionHint));
                }
            }
        }

        public string Key
        {
            get => _key;
            set => Set(ref _key, value);
        }

        public bool QuickCast
        {
            get => _quickCast;
            set => Set(ref _quickCast, value);
        }

        [ScriptIgnore]
        public string PositionHint
        {
            get
            {
                int index = SlotNumber - 1;
                int row = index / 4;
                int column = index % 4 + 1;
                string rowName = row == 0 ? "顶排" : row == 1 ? "中排" : "底排";
                return "命令卡" + rowName + " · 第 " + column + " 列";
            }
        }
    }

    public sealed class KeyMapping : ObservableObject
    {
        private string _from;
        private string _to;
        private bool _enabled = true;

        // Native key in the game. Pressing To sends this key.
        public string From
        {
            get => _from;
            set => Set(ref _from, value);
        }

        // Physical key after remapping.
        public string To
        {
            get => _to;
            set => Set(ref _to, value);
        }

        public bool Enabled
        {
            get => _enabled;
            set => Set(ref _enabled, value);
        }
    }

    public sealed class ChatOptions : ObservableObject
    {
        private int _sendDelayMs = 80;
        private int _cooldownMs = 400;
        private bool _antiSpam = true;

        public bool AntiSpam
        {
            get => _antiSpam;
            set => Set(ref _antiSpam, value);
        }

        public int SendDelayMs
        {
            get => _sendDelayMs;
            set => Set(ref _sendDelayMs, value);
        }

        public int CooldownMs
        {
            get => _cooldownMs;
            set => Set(ref _cooldownMs, value);
        }

        public ObservableCollection<ChatMacro> Macros { get; set; } = new ObservableCollection<ChatMacro>();

        public void Normalize()
        {
            Macros = Macros ?? new ObservableCollection<ChatMacro>();
            if (SendDelayMs < 20)
            {
                SendDelayMs = 20;
            }
            else if (SendDelayMs > 500)
            {
                SendDelayMs = 500;
            }

            if (CooldownMs < 0)
            {
                CooldownMs = 0;
            }
        }
    }

    public sealed class ChatMacro : ObservableObject
    {
        private string _id;
        private string _binding;
        private string _channel = "all";
        private string _text;
        private bool _enabled = true;

        public string Id
        {
            get => _id;
            set => Set(ref _id, value);
        }

        public string Binding
        {
            get => _binding;
            set => Set(ref _binding, value);
        }

        public string Channel
        {
            get => _channel;
            set => Set(ref _channel, value ?? "all");
        }

        public string Text
        {
            get => _text;
            set => Set(ref _text, value);
        }

        public bool Enabled
        {
            get => _enabled;
            set => Set(ref _enabled, value);
        }
    }

    public sealed class SchemeLibrary
    {
        public string ActiveId { get; set; }

        public string ActiveName { get; set; }

        // Display and switch order for all schemes, including the active one.
        public List<string> Order { get; set; } = new List<string>();

        // Inactive schemes. The active scheme is stored in the root Input/Chat.
        public List<SchemeSnapshot> Others { get; set; } = new List<SchemeSnapshot>();

        public void Normalize()
        {
            Order = Order ?? new List<string>();
            Others = Others ?? new List<SchemeSnapshot>();
            Others.RemoveAll(s => s == null);

            foreach (SchemeSnapshot scheme in Others)
            {
                scheme.Input = scheme.Input ?? new InputOptions();
                scheme.Chat = scheme.Chat ?? new ChatOptions();
                scheme.Input.Normalize();
                scheme.Chat.Normalize();
            }
        }
    }

    public sealed class SchemeSnapshot
    {
        public string Id { get; set; }

        public string Name { get; set; }

        public InputOptions Input { get; set; } = new InputOptions();

        public ChatOptions Chat { get; set; } = new ChatOptions();
    }

    public sealed class SchemeInfo
    {
        public SchemeInfo(string id, string name, bool isActive)
        {
            Id = id;
            Name = name;
            IsActive = isActive;
        }

        public string Id { get; }

        public string Name { get; }

        public bool IsActive { get; }
    }
}
