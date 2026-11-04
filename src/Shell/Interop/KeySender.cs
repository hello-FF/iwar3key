using System;
using System.Collections.Generic;
using IWar3Key.Shell.Core;

namespace IWar3Key.Shell.Interop
{
    public static class KeySender
    {
        public static bool SendChord(string chordText)
        {
            if (!HotkeyUtil.TryParse(chordText, out HotkeyChord chord))
            {
                return false;
            }

            var inputs = new List<NativeMethods.INPUT>(8);
            var modifiers = new List<int>(4);
            if ((chord.Modifiers & HotkeyModifiers.Control) != 0) modifiers.Add(NativeMethods.VK_CONTROL);
            if ((chord.Modifiers & HotkeyModifiers.Alt) != 0) modifiers.Add(NativeMethods.VK_MENU);
            if ((chord.Modifiers & HotkeyModifiers.Shift) != 0) modifiers.Add(NativeMethods.VK_SHIFT);
            if ((chord.Modifiers & HotkeyModifiers.Win) != 0) modifiers.Add(NativeMethods.VK_LWIN);

            foreach (int modifier in modifiers)
            {
                inputs.Add(KeyInput(modifier, false));
            }

            inputs.Add(KeyInput(chord.VirtualKey, false));
            inputs.Add(KeyInput(chord.VirtualKey, true));

            for (int i = modifiers.Count - 1; i >= 0; i--)
            {
                inputs.Add(KeyInput(modifiers[i], true));
            }

            return Send(inputs);
        }

        public static bool SendKeyPress(int virtualKey)
        {
            return Send(new List<NativeMethods.INPUT>
            {
                KeyInput(virtualKey, false),
                KeyInput(virtualKey, true)
            });
        }

        public static bool SendItemKey(int slot)
        {
            switch (slot)
            {
                case 1: return SendKeyPress(0x67);
                case 2: return SendKeyPress(0x68);
                case 3: return SendKeyPress(0x64);
                case 4: return SendKeyPress(0x65);
                case 5: return SendKeyPress(0x61);
                case 6: return SendKeyPress(0x62);
                default: return false;
            }
        }

        private static NativeMethods.INPUT KeyInput(int virtualKey, bool keyUp)
        {
            uint flags = keyUp ? NativeMethods.KEYEVENTF_KEYUP : 0;
            if (virtualKey == NativeMethods.VK_LWIN || virtualKey == NativeMethods.VK_RWIN)
            {
                flags |= 0x0001; // KEYEVENTF_EXTENDEDKEY
            }

            return new NativeMethods.INPUT
            {
                type = NativeMethods.INPUT_KEYBOARD,
                U = new NativeMethods.InputUnion
                {
                    ki = new NativeMethods.KEYBDINPUT
                    {
                        wVk = (ushort)virtualKey,
                        wScan = 0,
                        dwFlags = flags,
                        time = 0,
                        dwExtraInfo = new UIntPtr(KeyboardHook.InjectionMarkerValue)
                    }
                }
            };
        }

        private static bool Send(List<NativeMethods.INPUT> inputs)
        {
            if (inputs.Count == 0)
            {
                return false;
            }

            uint sent = NativeMethods.SendInput((uint)inputs.Count, inputs.ToArray(), System.Runtime.InteropServices.Marshal.SizeOf(typeof(NativeMethods.INPUT)));
            return sent == inputs.Count;
        }
    }
}
