using System;
using System.Collections.Generic;
using IWar3Key.Shell.Core;

namespace IWar3Key.Shell.Interop
{
    public sealed class WindowStyler
    {
        private const int War3MaxWindowHeight = 1023;

        private sealed class Snapshot
        {
            public uint Style;
            public uint ExStyle;
            public NativeMethods.RECT Rect;
        }

        private readonly Dictionary<IntPtr, Snapshot> _snapshots = new Dictionary<IntPtr, Snapshot>();

        public void Apply(IntPtr hwnd, WindowOptions options, Logger log)
        {
            if (hwnd == IntPtr.Zero || !NativeMethods.IsWindow(hwnd))
            {
                return;
            }

            if (!options.Enabled)
            {
                Restore(hwnd);
                return;
            }

            Snapshot snapshot = EnsureSnapshot(hwnd);

            uint style = snapshot.Style;
            if (options.Borderless)
            {
                style &= ~(NativeMethods.WS_CAPTION |
                           NativeMethods.WS_THICKFRAME |
                           NativeMethods.WS_SYSMENU |
                           NativeMethods.WS_MINIMIZEBOX |
                           NativeMethods.WS_MAXIMIZEBOX);
                style |= NativeMethods.WS_POPUP | NativeMethods.WS_VISIBLE;
            }

            uint exStyle = snapshot.ExStyle;
            if (options.AlwaysOnTop)
            {
                exStyle |= NativeMethods.WS_EX_TOPMOST;
            }
            else
            {
                exStyle &= ~NativeMethods.WS_EX_TOPMOST;
            }

            // Window.Width/Height are outer-frame dimensions, matching the
            // way U9/魔兽's windowed "resolution" behaves. The client area is
            // smaller by the non-client frame (16x39 at 100% DPI).
            int requestedWidth = Math.Max(1, options.Width);
            int requestedHeight = Math.Max(1, options.Height);
            int windowWidth = requestedWidth;
            // War3's windowed mode keeps a 1023px outer-height ceiling (the
            // game's own startup window and U9's 1920x1080 preset both land
            // here). Borderless windows have no non-client frame, so they keep
            // the requested client height.
            int windowHeight = requestedHeight;
            if (!options.Borderless)
            {
                windowHeight = Math.Min(requestedHeight, War3MaxWindowHeight);
            }

            if (windowHeight != requestedHeight)
            {
                log.Info(string.Format(
                    "外框高度 {0} 超过 War3 窗口上限 {1}，按 {2} 应用。",
                    requestedHeight,
                    War3MaxWindowHeight,
                    windowHeight));
            }

            int cappedWidth = windowWidth;
            int cappedHeight = windowHeight;
            NativeMethods.GetWindowRect(hwnd, out NativeMethods.RECT current);
            int x = current.Left;
            int y = current.Top;
            ClampToMonitor(hwnd, ref x, ref y, ref windowWidth, ref windowHeight);

            if (windowWidth != cappedWidth || windowHeight != cappedHeight)
            {
                log.Info(string.Format(
                    "请求的外框尺寸 {0}x{1} 超过桌面工作区，按 {2}x{3} 应用。",
                    cappedWidth,
                    cappedHeight,
                    windowWidth,
                    windowHeight));
            }

            // A 1920x1080 config now caps to the game's own 1920x1023 outer
            // size. Avoid SetWindowPos/SetStyle entirely when the window is
            // already in the requested state; even a same-size frame change
            // during 1.20e startup can crash the game.
            bool styleMatches =
                NativeMethods.Style(hwnd) == style &&
                NativeMethods.ExStyle(hwnd) == exStyle;
            bool geometryMatches =
                current.Left == x && current.Top == y &&
                current.Width == windowWidth && current.Height == windowHeight;
            if (styleMatches && geometryMatches)
            {
                NativeMethods.GetClientRect(hwnd, out NativeMethods.RECT noopClient);
                log.Info(string.Format(
                    "窗口无需调整: 外框 {0}x{1}, 客户区 {2}x{3}, 无边框={4}, 置顶={5}",
                    current.Width,
                    current.Height,
                    noopClient.Width,
                    noopClient.Height,
                    options.Borderless,
                    options.AlwaysOnTop));
                return;
            }

            NativeMethods.SetStyle(hwnd, style);
            NativeMethods.SetExStyle(hwnd, exStyle);

            IntPtr insertAfter = options.AlwaysOnTop ? NativeMethods.HWND_TOPMOST : NativeMethods.HWND_NOTOPMOST;
            bool moved = NativeMethods.SetWindowPos(
                hwnd,
                insertAfter,
                x,
                y,
                windowWidth,
                windowHeight,
                NativeMethods.SWP_FRAMECHANGED | NativeMethods.SWP_SHOWWINDOW | NativeMethods.SWP_NOACTIVATE);

            if (!moved)
            {
                log.Warn("调整游戏窗口失败，Win32 错误 " + System.Runtime.InteropServices.Marshal.GetLastWin32Error());
                return;
            }

            NativeMethods.GetWindowRect(hwnd, out NativeMethods.RECT actualWindow);
            NativeMethods.GetClientRect(hwnd, out NativeMethods.RECT actualClient);
            if (actualWindow.Width != windowWidth || actualWindow.Height != windowHeight)
            {
                log.Warn(string.Format(
                    "游戏窗口外框与请求值不一致：请求 {0}x{1}，实际 {2}x{3}，客户区 {4}x{5}。可能受桌面工作区限制。",
                    windowWidth,
                    windowHeight,
                    actualWindow.Width,
                    actualWindow.Height,
                    actualClient.Width,
                    actualClient.Height));
            }
            else
            {
                log.Info(string.Format(
                    "窗口已应用: 外框 {0}x{1}, 客户区 {2}x{3}, 无边框={4}, 置顶={5}",
                    actualWindow.Width,
                    actualWindow.Height,
                    actualClient.Width,
                    actualClient.Height,
                    options.Borderless,
                    options.AlwaysOnTop));
            }
        }

        // Apply only borderless/always-on-top immediately, without changing
        // the window size. This makes a requested borderless mode visible at
        // attach time; the delayed ApplyWindow() still handles the final
        // outer size after the main menu has laid out.
        public void ApplyStyle(IntPtr hwnd, WindowOptions options, Logger log)
        {
            if (hwnd == IntPtr.Zero || !NativeMethods.IsWindow(hwnd) || !options.Enabled)
            {
                return;
            }

            if (!options.Borderless && !options.AlwaysOnTop)
            {
                return;
            }

            Snapshot snapshot = EnsureSnapshot(hwnd);
            uint style = snapshot.Style;
            if (options.Borderless)
            {
                style &= ~(NativeMethods.WS_CAPTION |
                           NativeMethods.WS_THICKFRAME |
                           NativeMethods.WS_SYSMENU |
                           NativeMethods.WS_MINIMIZEBOX |
                           NativeMethods.WS_MAXIMIZEBOX);
                style |= NativeMethods.WS_POPUP | NativeMethods.WS_VISIBLE;
            }

            uint exStyle = snapshot.ExStyle;
            if (options.AlwaysOnTop)
            {
                exStyle |= NativeMethods.WS_EX_TOPMOST;
            }
            else
            {
                exStyle &= ~NativeMethods.WS_EX_TOPMOST;
            }

            if (NativeMethods.Style(hwnd) == style && NativeMethods.ExStyle(hwnd) == exStyle)
            {
                return;
            }

            NativeMethods.SetStyle(hwnd, style);
            NativeMethods.SetExStyle(hwnd, exStyle);
            NativeMethods.GetWindowRect(hwnd, out NativeMethods.RECT current);
            IntPtr insertAfter = options.AlwaysOnTop ? NativeMethods.HWND_TOPMOST : NativeMethods.HWND_NOTOPMOST;
            if (!NativeMethods.SetWindowPos(
                    hwnd,
                    insertAfter,
                    current.Left,
                    current.Top,
                    current.Width,
                    current.Height,
                    NativeMethods.SWP_FRAMECHANGED | NativeMethods.SWP_NOACTIVATE))
            {
                log.Warn("应用窗口样式失败，Win32 错误 " + System.Runtime.InteropServices.Marshal.GetLastWin32Error());
                return;
            }

            log.Info(string.Format("窗口样式已应用: 无边框={0}, 置顶={1}", options.Borderless, options.AlwaysOnTop));
        }

        private Snapshot EnsureSnapshot(IntPtr hwnd)
        {
            if (!_snapshots.TryGetValue(hwnd, out Snapshot snapshot))
            {
                snapshot = new Snapshot
                {
                    Style = NativeMethods.Style(hwnd),
                    ExStyle = NativeMethods.ExStyle(hwnd)
                };
                NativeMethods.GetWindowRect(hwnd, out snapshot.Rect);
                _snapshots[hwnd] = snapshot;
            }

            return snapshot;
        }

        public void Restore(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero || !NativeMethods.IsWindow(hwnd))
            {
                return;
            }

            if (!_snapshots.TryGetValue(hwnd, out Snapshot snapshot))
            {
                return;
            }

            NativeMethods.SetStyle(hwnd, snapshot.Style);
            NativeMethods.SetExStyle(hwnd, snapshot.ExStyle);
            NativeMethods.SetWindowPos(
                hwnd,
                NativeMethods.HWND_NOTOPMOST,
                snapshot.Rect.Left,
                snapshot.Rect.Top,
                snapshot.Rect.Width,
                snapshot.Rect.Height,
                NativeMethods.SWP_FRAMECHANGED | NativeMethods.SWP_NOACTIVATE);
            _snapshots.Remove(hwnd);
        }

        public void Forget(IntPtr hwnd)
        {
            if (hwnd != IntPtr.Zero)
            {
                _snapshots.Remove(hwnd);
            }
        }

        private static void ClampToMonitor(
            IntPtr hwnd, ref int x, ref int y, ref int width, ref int height)
        {
            IntPtr monitor = NativeMethods.MonitorFromWindow(hwnd, NativeMethods.MONITOR_DEFAULTTONEAREST);
            if (monitor == IntPtr.Zero)
            {
                return;
            }

            var info = new NativeMethods.MONITORINFO { cbSize = System.Runtime.InteropServices.Marshal.SizeOf(typeof(NativeMethods.MONITORINFO)) };
            if (!NativeMethods.GetMonitorInfoW(monitor, ref info))
            {
                return;
            }

            NativeMethods.RECT work = info.rcWork;
            width = Math.Max(1, Math.Min(width, work.Width));
            height = Math.Max(1, Math.Min(height, work.Height));
            if (x < work.Left || y < work.Top || x + width > work.Right || y + height > work.Bottom)
            {
                x = work.Left + Math.Max(0, (work.Width - width) / 2);
                y = work.Top + Math.Max(0, (work.Height - height) / 2);
            }
        }
    }
}
