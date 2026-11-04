using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using IWar3Key.Shell.Core;

namespace IWar3Key.Shell.Controls
{
    public sealed class KeyCaptureButton : Button
    {
        public static readonly DependencyProperty ChordProperty = DependencyProperty.Register(
            nameof(Chord),
            typeof(string),
            typeof(KeyCaptureButton),
            new FrameworkPropertyMetadata(
                null,
                FrameworkPropertyMetadataOptions.BindsTwoWayByDefault,
                OnChordChanged));

        public static readonly DependencyProperty PlaceholderProperty = DependencyProperty.Register(
            nameof(Placeholder),
            typeof(string),
            typeof(KeyCaptureButton),
            new FrameworkPropertyMetadata("点击录入"));

        private bool _capturing;

        public KeyCaptureButton()
        {
            Focusable = true;
            UpdateDisplay();

            var clearItem = new MenuItem { Header = "清除" };
            clearItem.Click += (s, e) =>
            {
                StopCapture();
                Chord = null;
                UpdateDisplay();
            };
            ContextMenu = new ContextMenu();
            ContextMenu.Items.Add(clearItem);
        }

        public string Chord
        {
            get => (string)GetValue(ChordProperty);
            set => SetValue(ChordProperty, value);
        }

        public string Placeholder
        {
            get => (string)GetValue(PlaceholderProperty);
            set => SetValue(PlaceholderProperty, value);
        }

        protected override void OnClick()
        {
            base.OnClick();
            StartCapture();
        }

        protected override void OnPreviewKeyDown(KeyEventArgs e)
        {
            if (!_capturing)
            {
                base.OnPreviewKeyDown(e);
                return;
            }

            e.Handled = true;
            Key key = e.Key == Key.System ? e.SystemKey : e.Key;
            if (key == Key.Escape)
            {
                StopCapture();
                return;
            }

            if (IsModifierKey(key) || key == Key.None)
            {
                return;
            }

            int virtualKey = KeyInterop.VirtualKeyFromKey(key);
            if (virtualKey == 0)
            {
                return;
            }

            ModifierKeys modifiers = Keyboard.Modifiers;
            HotkeyModifiers hotkeyModifiers = HotkeyModifiers.None;
            if ((modifiers & ModifierKeys.Control) != 0) hotkeyModifiers |= HotkeyModifiers.Control;
            if ((modifiers & ModifierKeys.Alt) != 0) hotkeyModifiers |= HotkeyModifiers.Alt;
            if ((modifiers & ModifierKeys.Shift) != 0) hotkeyModifiers |= HotkeyModifiers.Shift;
            if ((modifiers & ModifierKeys.Windows) != 0) hotkeyModifiers |= HotkeyModifiers.Win;

            Chord = HotkeyUtil.Format(virtualKey, hotkeyModifiers);
            StopCapture();
        }

        protected override void OnPreviewKeyUp(KeyEventArgs e)
        {
            if (_capturing)
            {
                e.Handled = true;
                return;
            }

            base.OnPreviewKeyUp(e);
        }

        protected override void OnLostKeyboardFocus(KeyboardFocusChangedEventArgs e)
        {
            base.OnLostKeyboardFocus(e);
            if (_capturing)
            {
                StopCapture();
            }
        }

        private void StartCapture()
        {
            if (_capturing)
            {
                return;
            }

            _capturing = true;
            Content = "按下按键";
            ToolTip = "请按下按键，Esc 取消";
            Focus();
            Keyboard.Focus(this);
        }

        private void StopCapture()
        {
            if (!_capturing)
            {
                return;
            }

            _capturing = false;
            UpdateDisplay();
        }

        private void UpdateDisplay()
        {
            if (_capturing)
            {
                return;
            }

            Content = string.IsNullOrWhiteSpace(Chord) ? Placeholder : HotkeyUtil.Display(Chord);
            ToolTip = string.IsNullOrWhiteSpace(Chord) ? null : HotkeyUtil.Display(Chord);
        }

        private static void OnChordChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            ((KeyCaptureButton)d).UpdateDisplay();
        }

        private static bool IsModifierKey(Key key)
        {
            switch (key)
            {
                case Key.LeftCtrl:
                case Key.RightCtrl:
                case Key.LeftAlt:
                case Key.RightAlt:
                case Key.LeftShift:
                case Key.RightShift:
                case Key.LWin:
                case Key.RWin:
                case Key.System:
                    return true;
                default:
                    return false;
            }
        }
    }
}
