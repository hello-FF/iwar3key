using System.Windows;
using System.Windows.Controls;
using IWar3Key.Shell.Core;

namespace IWar3Key.Shell.Pages
{
    public partial class MappingPage : UserControl
    {
        private readonly ShellContext _ctx;

        public MappingPage(ShellContext context)
        {
            _ctx = context;
            InitializeComponent();
            DataContext = context.Config;
        }

        private void AddMapping_Click(object sender, RoutedEventArgs e)
        {
            _ctx.Config.Input.Mappings.Add(new KeyMapping { From = string.Empty, To = string.Empty, Enabled = true });
        }

        private void DeleteMapping_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.Tag is KeyMapping mapping)
            {
                _ctx.Config.Input.Mappings.Remove(mapping);
            }
        }
    }
}
