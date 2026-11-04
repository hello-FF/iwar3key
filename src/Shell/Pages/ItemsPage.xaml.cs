using System.Windows;
using System.Windows.Controls;
using IWar3Key.Shell.Core;

namespace IWar3Key.Shell.Pages
{
    public partial class ItemsPage : UserControl
    {
        private readonly ShellContext _ctx;

        public ItemsPage(ShellContext context)
        {
            _ctx = context;
            InitializeComponent();
            DataContext = context.Config;
        }

        private void QuickAll_Click(object sender, RoutedEventArgs e)
        {
            bool value = (sender as Button)?.Tag as string == "on";
            foreach (ItemSlot item in _ctx.Config.Input.Items)
            {
                item.QuickCast = value;
            }
        }

        private void ClearItem_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.Tag is ItemSlot item)
            {
                item.Key = null;
                item.QuickCast = false;
            }
        }
    }
}
