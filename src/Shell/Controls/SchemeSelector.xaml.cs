using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using IWar3Key.Shell.Core;

namespace IWar3Key.Shell.Controls
{
    public partial class SchemeSelector : UserControl
    {
        private readonly ShellContext _ctx;
        private readonly ConfigStore _store;
        private List<SchemeRow> _rows = new List<SchemeRow>();
        private SchemeRow _editingRow;
        private string _editingOriginalName;
        private Window _hostWindow;

        public SchemeSelector(ShellContext context)
        {
            _ctx = context ?? throw new ArgumentNullException(nameof(context));
            _store = context.Store;
            InitializeComponent();
            DropDown.CustomPopupPlacementCallback = PlacePopup;
            _store.SchemesChanged += RefreshRows;
            Loaded += SchemeSelector_Loaded;
            Unloaded += SchemeSelector_Unloaded;
            RefreshRows();
        }

        private void SchemeSelector_Loaded(object sender, RoutedEventArgs e)
        {
            Window window = Window.GetWindow(this);
            if (window == null || ReferenceEquals(window, _hostWindow))
            {
                return;
            }

            if (_hostWindow != null)
            {
                _hostWindow.PreviewMouseDown -= HostWindow_PreviewMouseDown;
            }

            _hostWindow = window;
            window.PreviewMouseDown += HostWindow_PreviewMouseDown;
        }

        private void SchemeSelector_Unloaded(object sender, RoutedEventArgs e)
        {
            if (_hostWindow != null)
            {
                _hostWindow.PreviewMouseDown -= HostWindow_PreviewMouseDown;
                _hostWindow = null;
            }
        }

        private void HostWindow_PreviewMouseDown(object sender, MouseButtonEventArgs e)
        {
            // StaysOpen=true so the trigger can close the popup itself; clicks
            // elsewhere in the shell close it here without consuming the click.
            if (!DropDown.IsOpen)
            {
                return;
            }

            var source = e.OriginalSource as DependencyObject;
            if (source == null)
            {
                ClosePopup();
                return;
            }

            if (source == Trigger || Trigger.IsAncestorOf(source))
            {
                return;
            }

            // A Popup has its own presentation source, but its input still
            // reaches this preview handler. Do not treat popup clicks as
            // outside clicks, or rename/action buttons would close the menu
            // before their own click handler runs.
            if (DropDown.Child is DependencyObject popupChild &&
                IsDescendantOf(source, popupChild))
            {
                return;
            }

            ClosePopup();
        }

        private static bool IsDescendantOf(DependencyObject node, DependencyObject ancestor)
        {
            while (node != null)
            {
                if (ReferenceEquals(node, ancestor))
                {
                    return true;
                }

                node = node is Visual
                    ? VisualTreeHelper.GetParent(node)
                    : LogicalTreeHelper.GetParent(node);
            }

            return false;
        }

        private void RefreshRows()
        {
            _editingRow = null;
            IReadOnlyList<SchemeInfo> schemes = _store.GetSchemes();
            var rows = new List<SchemeRow>(schemes.Count);
            bool canDelete = schemes.Count > 1;

            foreach (SchemeInfo scheme in schemes)
            {
                rows.Add(new SchemeRow
                {
                    Id = scheme.Id,
                    Name = scheme.Name,
                    IsActive = scheme.IsActive,
                    CanDelete = canDelete
                });
            }

            _rows = rows;
            RowsHost.ItemsSource = rows;

            string activeName = _store.ActiveSchemeName;
            CurrentName.Text = string.IsNullOrWhiteSpace(activeName) ? "—" : activeName;
            NewButton.IsEnabled = schemes.Count < ConfigStore.MaxSchemes;
            NewButton.ToolTip = NewButton.IsEnabled
                ? "新建空白方案"
                : "最多 " + ConfigStore.MaxSchemes + " 个方案";
        }

        private CustomPopupPlacement[] PlacePopup(Size popupSize, Size targetSize, Point offset)
        {
            return new[]
            {
                new CustomPopupPlacement(
                    new Point(targetSize.Width - popupSize.Width, targetSize.Height + 6),
                    PopupPrimaryAxis.Horizontal)
            };
        }

        private void Trigger_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            // StaysOpen=false closes the popup on this same click, which would
            // otherwise toggle the trigger back on immediately. Close here and
            // suppress the toggle.
            if (!DropDown.IsOpen)
            {
                return;
            }

            ClosePopup();
            e.Handled = true;
        }

        private void DropDown_Closed(object sender, EventArgs e)
        {
            Trigger.IsChecked = false;
            CommitEditing();
        }

        private void SchemeSelector_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Escape && DropDown.IsOpen && _editingRow == null)
            {
                ClosePopup();
                e.Handled = true;
            }
        }

        private void NameButton_Click(object sender, RoutedEventArgs e)
        {
            SchemeRow row = RowOf(sender);
            if (row == null)
            {
                return;
            }

            string id = row.Id;
            string name = row.Name;
            bool active = row.IsActive;
            CommitEditing();

            if (!active && _store.SwitchScheme(id))
            {
                _ctx.Status("已切换方案：" + name);
            }

            ClosePopup();
        }

        private void RenameButton_Click(object sender, RoutedEventArgs e)
        {
            SchemeRow row = RowOf(sender);
            if (row == null)
            {
                return;
            }

            string id = row.Id;
            CommitEditing();
            row = FindRow(id);
            if (row == null)
            {
                return;
            }

            _editingRow = row;
            _editingOriginalName = row.Name;
            row.IsEditing = true;
        }

        private void CopyButton_Click(object sender, RoutedEventArgs e)
        {
            SchemeRow row = RowOf(sender);
            if (row == null)
            {
                return;
            }

            string id = row.Id;
            CommitEditing();
            SchemeSnapshot copy = _store.DuplicateScheme(id);
            if (copy == null)
            {
                _ctx.Status("最多 " + ConfigStore.MaxSchemes + " 个方案");
                return;
            }

            _ctx.Status("已复制为「" + copy.Name + "」");
        }

        private void DeleteButton_Click(object sender, RoutedEventArgs e)
        {
            SchemeRow row = RowOf(sender);
            if (row == null || !row.CanDelete)
            {
                return;
            }

            string id = row.Id;
            CommitEditing();
            row = FindRow(id);
            if (row == null || _store.GetSchemes().Count <= 1)
            {
                return;
            }

            string name = row.Name;
            bool wasActive = row.IsActive;
            Window owner = Window.GetWindow(this);
            string message = "删除方案「" + name + "」？删除后不可恢复。";
            MessageBoxResult result = owner == null
                ? MessageBox.Show(message, "删除方案", MessageBoxButton.OKCancel, MessageBoxImage.Warning)
                : MessageBox.Show(owner, message, "删除方案", MessageBoxButton.OKCancel, MessageBoxImage.Warning);
            if (result != MessageBoxResult.OK)
            {
                return;
            }

            if (_store.DeleteScheme(id))
            {
                _ctx.Status(wasActive
                    ? "已删除「" + name + "」，切换到「" + _store.ActiveSchemeName + "」"
                    : "已删除「" + name + "」");
            }
        }

        private void NewButton_Click(object sender, RoutedEventArgs e)
        {
            CommitEditing();
            SchemeSnapshot scheme = _store.CreateScheme();
            if (scheme == null)
            {
                _ctx.Status("最多 " + ConfigStore.MaxSchemes + " 个方案");
                return;
            }

            _ctx.Status("已新建空白方案「" + scheme.Name + "」并切换");
            ClosePopup();
        }

        private void NameEditor_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
        {
            if (!(sender is TextBox editor) || !editor.IsVisible)
            {
                return;
            }

            Dispatcher.BeginInvoke(new Action(() =>
            {
                editor.Focus();
                editor.SelectAll();
            }), DispatcherPriority.Input);
        }

        private void NameEditor_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                e.Handled = true;
                CommitEditing();
            }
            else if (e.Key == Key.Escape)
            {
                e.Handled = true;
                CancelEditing();
            }
        }

        private void CommitEditing()
        {
            SchemeRow row = _editingRow;
            if (row == null)
            {
                return;
            }

            _editingRow = null;
            string requested = (row.Name ?? string.Empty).Trim();
            string final = _store.RenameScheme(row.Id, requested);
            if (final == null)
            {
                row.Name = _editingOriginalName;
                row.IsEditing = false;
                _ctx.Status("方案名不能为空");
                return;
            }

            row.Name = final;
            row.IsEditing = false;
            _ctx.Status(!string.Equals(final, requested, StringComparison.Ordinal)
                ? "名称重复，已改为「" + final + "」"
                : "已重命名为「" + final + "」");
        }

        private void CancelEditing()
        {
            SchemeRow row = _editingRow;
            if (row == null)
            {
                return;
            }

            _editingRow = null;
            row.Name = _editingOriginalName;
            row.IsEditing = false;
        }

        private void ClosePopup()
        {
            Trigger.IsChecked = false;
            if (DropDown.IsOpen)
            {
                DropDown.IsOpen = false;
            }
        }

        private SchemeRow FindRow(string id)
        {
            return _rows.FirstOrDefault(
                row => string.Equals(row.Id, id, StringComparison.Ordinal));
        }

        private static SchemeRow RowOf(object sender)
        {
            return (sender as FrameworkElement)?.Tag as SchemeRow;
        }

        private sealed class SchemeRow : ObservableObject
        {
            private string _name;
            private bool _isActive;
            private bool _isEditing;

            public string Id { get; set; }

            public string Name
            {
                get => _name;
                set => Set(ref _name, value);
            }

            public bool IsActive
            {
                get => _isActive;
                set => Set(ref _isActive, value);
            }

            public bool IsEditing
            {
                get => _isEditing;
                set => Set(ref _isEditing, value);
            }

            public bool CanDelete { get; set; }

            public string DeleteTip => CanDelete ? "删除" : "至少保留一个方案";
        }
    }
}
