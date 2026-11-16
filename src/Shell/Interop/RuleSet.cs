using IWar3Key.Shell.Core;

namespace IWar3Key.Shell.Interop
{
    internal sealed class ParsedHotkey
    {
        public bool Valid;
        public int VirtualKey;
        public HotkeyModifiers Modifiers;

        public static ParsedHotkey Parse(string text)
        {
            if (HotkeyUtil.TryParse(text, out HotkeyChord chord))
            {
                return new ParsedHotkey
                {
                    Valid = true,
                    VirtualKey = chord.VirtualKey,
                    Modifiers = chord.Modifiers
                };
            }

            return new ParsedHotkey();
        }

        public bool Matches(HookKeyEvent e)
        {
            return Valid && VirtualKey == e.VirtualKey && Modifiers == e.Modifiers;
        }

        // Shift is War3's order modifier (queued orders), not part of the
        // hotkey identity. Accept one extra held Shift so a binding configured
        // without it keeps working as Shift+key; the physical Shift still
        // reaches the game. Ctrl/Alt/Win are never ignored.
        public bool MatchesExtraShift(HookKeyEvent e)
        {
            return Valid && VirtualKey == e.VirtualKey &&
                   (Modifiers & HotkeyModifiers.Shift) == 0 &&
                   e.Modifiers == (Modifiers | HotkeyModifiers.Shift);
        }
    }

    internal sealed class MacroRule
    {
        public string Id;
        public ParsedHotkey Hotkey;
        public string Text;
        public int Channel;
        public int CooldownMs;
    }

    internal sealed class ItemRule
    {
        public ParsedHotkey Hotkey;
        public int Slot;
    }

    internal sealed class MapRule
    {
        // OriginalKey is the game's native key; NewKey is the physical key
        // the user presses. Pressing NewKey sends OriginalKey.
        public string OriginalKey;
        public ParsedHotkey NewKey;
    }

    internal sealed class SkillConvertRule
    {
        public ParsedHotkey Hotkey;
    }

    internal sealed class RuleSet
    {
        public bool ItemsEnabled;
        public bool MappingEnabled;
        public ParsedHotkey Panic = new ParsedHotkey();
        public ParsedHotkey Apply = new ParsedHotkey();
        public MacroRule[] Macros = new MacroRule[0];
        public ItemRule[] Items = new ItemRule[0];
        public MapRule[] Mappings = new MapRule[0];
        public SkillConvertRule[] SkillConverts = new SkillConvertRule[0];
    }
}
