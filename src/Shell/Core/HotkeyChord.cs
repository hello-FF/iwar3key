using System;
using System.Collections.Generic;
using System.Globalization;

namespace IWar3Key.Shell.Core
{
    [Flags]
    public enum HotkeyModifiers
    {
        None = 0,
        Alt = 1,
        Control = 2,
        Shift = 4,
        Win = 8
    }

    public struct HotkeyChord
    {
        public int VirtualKey;
        public HotkeyModifiers Modifiers;

        public bool IsValid => VirtualKey != 0;
    }

    public static class HotkeyUtil
    {
        private static readonly Dictionary<string, int> NamedKeys =
            new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
            {
                ["Space"] = 0x20,
                ["Enter"] = 0x0D,
                ["Return"] = 0x0D,
                ["Escape"] = 0x1B,
                ["Esc"] = 0x1B,
                ["Tab"] = 0x09,
                ["Backspace"] = 0x08,
                ["Delete"] = 0x2E,
                ["Insert"] = 0x2D,
                ["Home"] = 0x24,
                ["End"] = 0x23,
                ["PageUp"] = 0x21,
                ["PageDown"] = 0x22,
                ["Up"] = 0x26,
                ["Down"] = 0x28,
                ["Left"] = 0x25,
                ["Right"] = 0x27,
                ["OemSemicolon"] = 0xBA,
                ["OemPlus"] = 0xBB,
                ["OemComma"] = 0xBC,
                ["OemMinus"] = 0xBD,
                ["OemPeriod"] = 0xBE,
                ["OemQuestion"] = 0xBF,
                ["OemTilde"] = 0xC0,
                ["OemOpenBrackets"] = 0xDB,
                ["OemPipe"] = 0xDC,
                ["OemCloseBrackets"] = 0xDD,
                ["OemQuotes"] = 0xDE,
                ["Multiply"] = 0x6A,
                ["Add"] = 0x6B,
                ["Subtract"] = 0x6D,
                ["Divide"] = 0x6F,
                ["Decimal"] = 0x6E
            };

        public static bool TryParse(string text, out HotkeyChord chord)
        {
            chord = default(HotkeyChord);
            if (string.IsNullOrWhiteSpace(text))
            {
                return false;
            }

            HotkeyModifiers modifiers = HotkeyModifiers.None;
            string[] parts = text.Split('+');
            string keyPart = null;

            foreach (string raw in parts)
            {
                string part = raw.Trim();
                if (part.Length == 0)
                {
                    continue;
                }

                if (part.Equals("Ctrl", StringComparison.OrdinalIgnoreCase) ||
                    part.Equals("Control", StringComparison.OrdinalIgnoreCase))
                {
                    modifiers |= HotkeyModifiers.Control;
                }
                else if (part.Equals("Alt", StringComparison.OrdinalIgnoreCase))
                {
                    modifiers |= HotkeyModifiers.Alt;
                }
                else if (part.Equals("Shift", StringComparison.OrdinalIgnoreCase))
                {
                    modifiers |= HotkeyModifiers.Shift;
                }
                else if (part.Equals("Win", StringComparison.OrdinalIgnoreCase) ||
                         part.Equals("Windows", StringComparison.OrdinalIgnoreCase))
                {
                    modifiers |= HotkeyModifiers.Win;
                }
                else
                {
                    keyPart = part;
                }
            }

            if (keyPart == null || !TryParseKey(keyPart, out int vk))
            {
                return false;
            }

            chord.VirtualKey = vk;
            chord.Modifiers = modifiers;
            return true;
        }

        public static bool TryParseKey(string token, out int virtualKey)
        {
            virtualKey = 0;
            if (string.IsNullOrEmpty(token))
            {
                return false;
            }

            string key = token.Trim();
            if (key.Length == 1)
            {
                char c = char.ToUpperInvariant(key[0]);
                if (c >= 'A' && c <= 'Z')
                {
                    virtualKey = c;
                    return true;
                }

                if (c >= '0' && c <= '9')
                {
                    virtualKey = 0x30 + (c - '0');
                    return true;
                }

                switch (key[0])
                {
                    case ';': virtualKey = 0xBA; return true;
                    case '=': virtualKey = 0xBB; return true;
                    case ',': virtualKey = 0xBC; return true;
                    case '-': virtualKey = 0xBD; return true;
                    case '.': virtualKey = 0xBE; return true;
                    case '/': virtualKey = 0xBF; return true;
                    case '`': virtualKey = 0xC0; return true;
                    case '[': virtualKey = 0xDB; return true;
                    case '\\': virtualKey = 0xDC; return true;
                    case ']': virtualKey = 0xDD; return true;
                    case '\'': virtualKey = 0xDE; return true;
                }
            }

            if (key.StartsWith("Num ", StringComparison.OrdinalIgnoreCase) ||
                key.StartsWith("NumPad", StringComparison.OrdinalIgnoreCase))
            {
                string number = key.StartsWith("Num ", StringComparison.OrdinalIgnoreCase) ? key.Substring(4) : key.Substring(6);
                if (number.Length == 1 && number[0] >= '0' && number[0] <= '9')
                {
                    virtualKey = 0x60 + (number[0] - '0');
                    return true;
                }
            }

            if (key.Length >= 2 && (key[0] == 'F' || key[0] == 'f') &&
                int.TryParse(key.Substring(1), NumberStyles.None, CultureInfo.InvariantCulture, out int fn) &&
                fn >= 1 && fn <= 24)
            {
                virtualKey = 0x70 + fn - 1;
                return true;
            }

            if (NamedKeys.TryGetValue(key, out virtualKey))
            {
                return true;
            }

            return false;
        }

        public static string KeyName(int virtualKey)
        {
            if (virtualKey >= 'A' && virtualKey <= 'Z')
            {
                return ((char)virtualKey).ToString();
            }

            if (virtualKey >= '0' && virtualKey <= '9')
            {
                return ((char)virtualKey).ToString();
            }

            if (virtualKey >= 0x60 && virtualKey <= 0x69)
            {
                return "Num " + (virtualKey - 0x60);
            }

            if (virtualKey >= 0x70 && virtualKey <= 0x87)
            {
                return "F" + (virtualKey - 0x70 + 1);
            }

            switch (virtualKey)
            {
                case 0x20: return "Space";
                case 0x0D: return "Enter";
                case 0x1B: return "Esc";
                case 0x09: return "Tab";
                case 0x08: return "Backspace";
                case 0x2E: return "Delete";
                case 0x2D: return "Insert";
                case 0x24: return "Home";
                case 0x23: return "End";
                case 0x21: return "PageUp";
                case 0x22: return "PageDown";
                case 0x26: return "Up";
                case 0x28: return "Down";
                case 0x25: return "Left";
                case 0x27: return "Right";
                case 0xBA: return ";";
                case 0xBB: return "=";
                case 0xBC: return ",";
                case 0xBD: return "-";
                case 0xBE: return ".";
                case 0xBF: return "/";
                case 0xC0: return "`";
                case 0xDB: return "[";
                case 0xDC: return "\\";
                case 0xDD: return "]";
                case 0xDE: return "'";
                case 0x6A: return "Multiply";
                case 0x6B: return "Add";
                case 0x6D: return "Subtract";
                case 0x6F: return "Divide";
                case 0x6E: return "Decimal";
                default:
                    return "VK" + virtualKey.ToString("X2");
            }
        }

        public static string Format(int virtualKey, HotkeyModifiers modifiers)
        {
            var parts = new List<string>();
            if ((modifiers & HotkeyModifiers.Control) != 0) parts.Add("Ctrl");
            if ((modifiers & HotkeyModifiers.Alt) != 0) parts.Add("Alt");
            if ((modifiers & HotkeyModifiers.Shift) != 0) parts.Add("Shift");
            if ((modifiers & HotkeyModifiers.Win) != 0) parts.Add("Win");
            parts.Add(KeyName(virtualKey));
            return string.Join("+", parts);
        }

        public static string Display(string canonical)
        {
            if (string.IsNullOrWhiteSpace(canonical))
            {
                return string.Empty;
            }

            string[] parts = canonical.Split('+');
            for (int i = 0; i < parts.Length; i++)
            {
                parts[i] = parts[i].Trim();
            }

            return string.Join(" + ", parts);
        }
    }
}
