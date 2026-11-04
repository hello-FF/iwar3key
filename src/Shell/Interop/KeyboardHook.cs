using System;
using System.Runtime.InteropServices;
using IWar3Key.Shell.Core;

namespace IWar3Key.Shell.Interop
{
    public sealed class HookKeyEvent
    {
        public int VirtualKey;
        public uint ScanCode;
        public bool IsDown;
        public bool IsInjected;
        public HotkeyModifiers Modifiers;
    }

    /// <summary>
    /// Installs WH_KEYBOARD_LL on the calling thread. In this application that is the
    /// WPF UI thread, which already pumps messages, so no private hook thread is needed.
    /// </summary>
    public sealed class KeyboardHook : IDisposable
    {
        internal const ulong InjectionMarkerValue = 0x1A2B3C4D;

        private readonly Func<HookKeyEvent, bool> _handler;
        private readonly Action<Exception> _onError;
        private NativeMethods.HookProc _proc;
        private IntPtr _hook;
        private HotkeyModifiers _modifiers;
        private bool _disposed;

        public KeyboardHook(Func<HookKeyEvent, bool> handler, Action<Exception> onError)
        {
            _handler = handler;
            _onError = onError;
        }

        public bool IsRunning => _hook != IntPtr.Zero && !_disposed;

        public bool Start()
        {
            if (_disposed || _hook != IntPtr.Zero)
            {
                return _hook != IntPtr.Zero;
            }

            InitializeModifiers();
            _proc = HookCallback;
            _hook = NativeMethods.SetWindowsHookExW(
                NativeMethods.WH_KEYBOARD_LL,
                _proc,
                NativeMethods.GetModuleHandleW(null),
                0);

            if (_hook == IntPtr.Zero)
            {
                int error = Marshal.GetLastWin32Error();
                _onError?.Invoke(new InvalidOperationException("安装键盘钩子失败，Win32 错误 " + error));
                return false;
            }

            return true;
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            if (_hook != IntPtr.Zero)
            {
                NativeMethods.UnhookWindowsHookEx(_hook);
                _hook = IntPtr.Zero;
            }
        }

        private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode >= 0)
            {
                int message = (int)wParam;
                bool isDown = message == NativeMethods.WM_KEYDOWN || message == NativeMethods.WM_SYSKEYDOWN;
                bool isUp = message == NativeMethods.WM_KEYUP || message == NativeMethods.WM_SYSKEYUP;
                if (isDown || isUp)
                {
                    NativeMethods.KBDLLHOOKSTRUCT data =
                        Marshal.PtrToStructure<NativeMethods.KBDLLHOOKSTRUCT>(lParam);
                    int vk = (int)data.vkCode;
                    UpdateModifiers(vk, isDown);

                    bool injected = (data.flags & NativeMethods.LLKHF_INJECTED) != 0 ||
                                    data.dwExtraInfo.ToUInt64() == InjectionMarkerValue;

                    var evt = new HookKeyEvent
                    {
                        VirtualKey = vk,
                        ScanCode = data.scanCode,
                        IsDown = isDown,
                        IsInjected = injected,
                        Modifiers = _modifiers
                    };

                    try
                    {
                        if (_handler != null && _handler(evt))
                        {
                            return new IntPtr(1);
                        }
                    }
                    catch (Exception ex)
                    {
                        _onError?.Invoke(ex);
                    }
                }
            }

            return NativeMethods.CallNextHookEx(_hook, nCode, wParam, lParam);
        }

        private void InitializeModifiers()
        {
            _modifiers = HotkeyModifiers.None;
            if (IsKeyDown(NativeMethods.VK_CONTROL) || IsKeyDown(NativeMethods.VK_LCONTROL) || IsKeyDown(NativeMethods.VK_RCONTROL))
            {
                _modifiers |= HotkeyModifiers.Control;
            }

            if (IsKeyDown(NativeMethods.VK_MENU) || IsKeyDown(NativeMethods.VK_LMENU) || IsKeyDown(NativeMethods.VK_RMENU))
            {
                _modifiers |= HotkeyModifiers.Alt;
            }

            if (IsKeyDown(NativeMethods.VK_SHIFT) || IsKeyDown(NativeMethods.VK_LSHIFT) || IsKeyDown(NativeMethods.VK_RSHIFT))
            {
                _modifiers |= HotkeyModifiers.Shift;
            }

            if (IsKeyDown(NativeMethods.VK_LWIN) || IsKeyDown(NativeMethods.VK_RWIN))
            {
                _modifiers |= HotkeyModifiers.Win;
            }
        }

        private static bool IsKeyDown(int vk)
        {
            return (NativeMethods.GetAsyncKeyState(vk) & 0x8000) != 0;
        }

        private void UpdateModifiers(int vk, bool down)
        {
            switch (vk)
            {
                case NativeMethods.VK_CONTROL:
                case NativeMethods.VK_LCONTROL:
                case NativeMethods.VK_RCONTROL:
                    SetModifier(HotkeyModifiers.Control, down);
                    break;
                case NativeMethods.VK_MENU:
                case NativeMethods.VK_LMENU:
                case NativeMethods.VK_RMENU:
                    SetModifier(HotkeyModifiers.Alt, down);
                    break;
                case NativeMethods.VK_SHIFT:
                case NativeMethods.VK_LSHIFT:
                case NativeMethods.VK_RSHIFT:
                    SetModifier(HotkeyModifiers.Shift, down);
                    break;
                case NativeMethods.VK_LWIN:
                case NativeMethods.VK_RWIN:
                    SetModifier(HotkeyModifiers.Win, down);
                    break;
            }
        }

        private void SetModifier(HotkeyModifiers modifier, bool down)
        {
            if (down)
            {
                _modifiers |= modifier;
            }
            else
            {
                _modifiers &= ~modifier;
            }
        }
    }
}
