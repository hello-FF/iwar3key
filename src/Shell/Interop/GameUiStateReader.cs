using System;

namespace IWar3Key.Shell.Interop
{
    /// <summary>
    /// Reads game-maintained UI state through the version profile.
    ///
    /// Chat: the IS_CHATTING global is maintained by the chat edit bar; non-zero
    /// means the edit bar owns keyboard input.
    ///
    /// EscMenu: the game keeps the CEscMenu object pointer at CGameUI+0x228 and
    /// stores the active UI mode object at CGameUI+0x1B4. The menu is open when
    /// the active mode is the menu object. This is the same pair KK's runtime
    /// table exposes as Address_CGameUI__m_escapeMenu and the current mode.
    ///
    /// A zero offset means the build has no verified detection; callers must
    /// then leave keys untouched rather than guessing from Enter/Esc/F10.
    /// </summary>
    internal sealed class GameUiStateReader : IDisposable
    {
        private const int CurrentModeOffset = 0x1B4;

        private readonly IntPtr _process;
        private readonly long _chatAddress;
        private readonly long _cguiGlobalAddress;
        private readonly int _escapeMenuOffset;
        private bool _disposed;

        public bool ChatAvailable =>
            !_disposed && _process != IntPtr.Zero && _chatAddress > 0;

        public bool MenuAvailable =>
            !_disposed && _process != IntPtr.Zero &&
            _cguiGlobalAddress > 0 && _escapeMenuOffset > 0;

        public long ChatAddress => _chatAddress;

        public long CguiGlobalAddress => _cguiGlobalAddress;

        public int EscapeMenuOffset => _escapeMenuOffset;

        private GameUiStateReader(
            IntPtr process,
            long chatAddress,
            long cguiGlobalAddress,
            int escapeMenuOffset)
        {
            _process = process;
            _chatAddress = chatAddress;
            _cguiGlobalAddress = cguiGlobalAddress;
            _escapeMenuOffset = escapeMenuOffset;
        }

        public static GameUiStateReader TryOpen(
            int pid,
            long gameBase,
            long chattingRva,
            long cguiGlobalRva,
            int escapeMenuOffset)
        {
            bool wantsChat = chattingRva > 0;
            bool wantsMenu = cguiGlobalRva > 0 && escapeMenuOffset > 0;
            if (pid <= 0 || (!wantsChat && !wantsMenu))
            {
                return null;
            }

            IntPtr process = NativeMethods.OpenProcess(
                NativeMethods.PROCESS_VM_READ | NativeMethods.PROCESS_QUERY_INFORMATION,
                false,
                pid);
            if (process == IntPtr.Zero)
            {
                process = NativeMethods.OpenProcess(
                    NativeMethods.PROCESS_VM_READ | NativeMethods.PROCESS_QUERY_LIMITED_INFORMATION,
                    false,
                    pid);
            }

            if (process == IntPtr.Zero)
            {
                return null;
            }

            if (gameBase <= 0)
            {
                gameBase = FindModuleBase(process, "Game.dll");
            }

            long chatAddress = wantsChat ? gameBase + chattingRva : 0;
            long cguiAddress = wantsMenu ? gameBase + cguiGlobalRva : 0;
            if (gameBase <= 0 ||
                gameBase > uint.MaxValue ||
                (wantsChat && (chatAddress <= 0 || chatAddress > uint.MaxValue)) ||
                (wantsMenu && (cguiAddress <= 0 || cguiAddress > uint.MaxValue)))
            {
                NativeMethods.CloseHandle(process);
                return null;
            }

            return new GameUiStateReader(process, chatAddress, cguiAddress, escapeMenuOffset);
        }

        private static long FindModuleBase(IntPtr process, string moduleName)
        {
            var modules = new IntPtr[1024];
            if (!NativeMethods.EnumProcessModulesEx(
                    process,
                    modules,
                    (uint)(modules.Length * IntPtr.Size),
                    out uint needed,
                    NativeMethods.LIST_MODULES_ALL))
            {
                return 0;
            }

            int count = Math.Min(modules.Length, (int)(needed / (uint)IntPtr.Size));
            var name = new System.Text.StringBuilder(260);
            for (int i = 0; i < count; i++)
            {
                name.Clear();
                if (NativeMethods.GetModuleBaseNameW(process, modules[i], name, (uint)name.Capacity) > 0 &&
                    name.ToString().Equals(moduleName, StringComparison.OrdinalIgnoreCase))
                {
                    return modules[i].ToInt64();
                }
            }

            return 0;
        }

        public bool TryReadChat(out bool chatting)
        {
            chatting = false;
            if (!ChatAvailable)
            {
                return false;
            }

            if (!TryReadUInt32(_chatAddress, out uint value))
            {
                return false;
            }

            chatting = value != 0;
            return true;
        }

        public bool TryReadMenu(out bool menuOpen)
        {
            menuOpen = false;
            if (!MenuAvailable)
            {
                return false;
            }

            if (!TryReadUInt32(_cguiGlobalAddress, out uint cgui))
            {
                return false;
            }

            // CGameUI is created when the in-game UI is built. While it is
            // absent, treat the state as unknown so callers pass keys through
            // instead of acting on a menu/lobby screen.
            if (cgui == 0)
            {
                return false;
            }

            if (!TryReadUInt32((long)cgui + _escapeMenuOffset, out uint menu))
            {
                return false;
            }

            // The menu object is created lazily on first open. If it does not
            // exist yet, the menu cannot be open.
            if (menu == 0)
            {
                return true;
            }

            if (!TryReadUInt32(cgui + CurrentModeOffset, out uint currentMode))
            {
                return false;
            }

            menuOpen = currentMode == menu;
            return true;
        }

        private bool TryReadUInt32(long address, out uint value)
        {
            value = 0;
            if (address <= 0 || address > uint.MaxValue)
            {
                return false;
            }

            if (!NativeMethods.ReadProcessMemory(
                    _process,
                    new IntPtr(address),
                    out uint readValue,
                    new IntPtr(sizeof(uint)),
                    out IntPtr read) ||
                read.ToInt64() != sizeof(uint))
            {
                return false;
            }

            value = readValue;
            return true;
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            NativeMethods.CloseHandle(_process);
        }
    }
}
