using System.Windows;
using System.Windows.Controls;
using IWar3Key.Shell.Core;

namespace IWar3Key.Shell.Pages
{
    public partial class SkillsPage : UserControl
    {
        private readonly ShellContext _ctx;

        public SkillsPage(ShellContext context)
        {
            _ctx = context;
            InitializeComponent();
            DataContext = context.Config;
        }

        private void QuickAll_Click(object sender, RoutedEventArgs e)
        {
            bool value = (sender as Button)?.Tag as string == "on";
            foreach (SkillSlot slot in _ctx.Config.Input.Skills)
            {
                if (!string.IsNullOrWhiteSpace(slot.Key))
                {
                    slot.QuickCast = value;
                }
            }
        }

        private void ClearSlot_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.Tag is SkillSlot slot)
            {
                slot.Key = null;
                slot.QuickCast = false;
            }
        }
    }
}
