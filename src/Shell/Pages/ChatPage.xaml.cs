using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using IWar3Key.Shell.Core;

namespace IWar3Key.Shell.Pages
{
    public partial class ChatPage : UserControl
    {
        private const int MaxMacros = 9;

        private readonly ShellContext _ctx;

        public ChatPage(ShellContext context)
        {
            _ctx = context;
            InitializeComponent();
            DataContext = context.Config;

            _ctx.Config.Chat.Macros.CollectionChanged += (s, e) => UpdateAddMacroButton();
            UpdateAddMacroButton();
        }

        private void UpdateAddMacroButton()
        {
            AddMacroButton.IsEnabled = _ctx.Config.Chat.Macros.Count < MaxMacros;
        }

        private void AddMacro_Click(object sender, RoutedEventArgs e)
        {
            if (_ctx.Config.Chat.Macros.Count >= MaxMacros)
            {
                _ctx.Status("快捷喊话最多 9 个");
                return;
            }

            int slot = 1;
            while (slot <= MaxMacros && _ctx.Config.Chat.Macros.Any(
                       m => string.Equals((m.Binding ?? string.Empty).Trim(),
                                          "Alt+" + slot, StringComparison.OrdinalIgnoreCase)))
            {
                slot++;
            }

            _ctx.Config.Chat.Macros.Add(new ChatMacro
            {
                Id = Guid.NewGuid().ToString("N").Substring(0, 8),
                Binding = "Alt+" + slot,
                Channel = "all",
                Text = string.Empty,
                Enabled = true
            });
        }

        private void DeleteMacro_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.Tag is ChatMacro macro)
            {
                _ctx.Config.Chat.Macros.Remove(macro);
            }
        }

    }
}
