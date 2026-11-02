using System.Windows;
using DoraExplorer.App.Models;

namespace DoraExplorer.App.Views.Dialogs;

public partial class SortDialog : Window
{
    public SortDialog(TreeSortMode currentMode)
    {
        InitializeComponent();
        SortComboBox.SelectedIndex = (int)currentMode;
    }

    public TreeSortMode SelectedSortMode => (TreeSortMode)SortComboBox.SelectedIndex;

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }

    public static bool TrySelect(Window owner, TreeSortMode currentMode, out TreeSortMode selectedMode)
    {
        var dialog = new SortDialog(currentMode);
        DialogChrome.Apply(dialog, owner);
        if (dialog.ShowDialog() == true)
        {
            selectedMode = dialog.SelectedSortMode;
            return true;
        }

        selectedMode = currentMode;
        return false;
    }
}
