using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using IWar3Key.Shell.Core;

namespace IWar3Key.Shell.Pages
{
    public partial class GameSettingsPage : UserControl
    {
        private sealed class ResolutionOption
        {
            public int Width;
            public int Height;
            public string Label;
            public bool Custom;

            public override string ToString()
            {
                return Label;
            }
        }

        private readonly ShellContext _ctx;
        private readonly List<ResolutionOption> _options;
        private bool _loading = true;

        public GameSettingsPage(ShellContext context)
        {
            _ctx = context;
            InitializeComponent();
            DataContext = context.Config;

            _options = new List<ResolutionOption>
            {
                new ResolutionOption { Width = 1024, Height = 768, Label = "1024 × 768" },
                new ResolutionOption { Width = 1280, Height = 960, Label = "1280 × 960" },
                new ResolutionOption { Width = 1366, Height = 768, Label = "1366 × 768" },
                new ResolutionOption { Width = 1600, Height = 900, Label = "1600 × 900" },
                new ResolutionOption { Width = 1920, Height = 1080, Label = "1920 × 1080" },
                new ResolutionOption { Custom = true, Label = "自定义…" }
            };

            ResolutionCombo.ItemsSource = _options;
            ResolutionOption match = _options.FirstOrDefault(o => !o.Custom &&
                                                                 o.Width == _ctx.Config.Window.Width &&
                                                                 o.Height == _ctx.Config.Window.Height);
            if (match == null)
            {
                match = _options.First(o => o.Custom);
                _ctx.Config.Window.CustomResolution = true;
            }

            ResolutionCombo.SelectedItem = match;
            CustomResRow.Visibility = match.Custom ? Visibility.Visible : Visibility.Collapsed;
            _loading = false;
        }

        private void ResolutionCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_loading || !(ResolutionCombo.SelectedItem is ResolutionOption option))
            {
                return;
            }

            if (option.Custom)
            {
                _ctx.Config.Window.CustomResolution = true;
                CustomResRow.Visibility = Visibility.Visible;
                return;
            }

            _ctx.Config.Window.CustomResolution = false;
            _ctx.Config.Window.Width = option.Width;
            _ctx.Config.Window.Height = option.Height;
            CustomResRow.Visibility = Visibility.Collapsed;
        }

    }
}
