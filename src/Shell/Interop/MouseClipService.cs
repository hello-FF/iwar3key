using System;
using System.Windows.Threading;
using IWar3Key.Shell.Core;

namespace IWar3Key.Shell.Interop
{
    public sealed class MouseClipService : IDisposable
    {
        private readonly Func<IntPtr> _windowProvider;
        private readonly Func<bool> _enabledProvider;
        private readonly Logger _log;
        private readonly DispatcherTimer _timer;
        private bool _clipped;

        public MouseClipService(Func<IntPtr> windowProvider, Func<bool> enabledProvider, Logger log)
        {
            _windowProvider = windowProvider;
            _enabledProvider = enabledProvider;
            _log = log;
            _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
            _timer.Tick += (s, e) => Tick();
            _timer.Start();
        }

        public void Dispose()
        {
            _timer.Stop();
            Release();
        }

        private void Tick()
        {
            IntPtr hwnd = _windowProvider();
            bool shouldClip =
                _enabledProvider() &&
                hwnd != IntPtr.Zero &&
                NativeMethods.IsWindow(hwnd) &&
                !NativeMethods.IsIconic(hwnd) &&
                NativeMethods.GetForegroundWindow() == hwnd;

            if (!shouldClip)
            {
                Release();
                return;
            }

            if (!NativeMethods.GetClientRect(hwnd, out NativeMethods.RECT client) || client.Width <= 0 || client.Height <= 0)
            {
                Release();
                return;
            }

            var topLeft = new NativeMethods.POINT { X = client.Left, Y = client.Top };
            var bottomRight = new NativeMethods.POINT { X = client.Right, Y = client.Bottom };
            if (!NativeMethods.ClientToScreen(hwnd, ref topLeft) ||
                !NativeMethods.ClientToScreen(hwnd, ref bottomRight))
            {
                Release();
                return;
            }

            var rect = new NativeMethods.RECT
            {
                Left = topLeft.X,
                Top = topLeft.Y,
                Right = bottomRight.X,
                Bottom = bottomRight.Y
            };

            if (NativeMethods.ClipCursor(ref rect))
            {
                _clipped = true;
            }
        }

        private void Release()
        {
            if (!_clipped)
            {
                return;
            }

            NativeMethods.ClipCursor(IntPtr.Zero);
            _clipped = false;
            _log.Info("已释放鼠标裁剪。");
        }
    }
}
