using System.Windows;
using System.Windows.Input;
using DoraExplorer.App.Models;

namespace DoraExplorer.App.Views.Dialogs;

public partial class SearchDialog : Window
{
    public SearchDialog(string rootPath)
    {
        InitializeComponent();
        RootPathText.Text = rootPath;
    }

    public string SearchPattern => PatternTextBox.Text;

    public bool SearchRecursive => RecursiveCheckBox.IsChecked == true;

    public event Action<SearchResultItem>? ResultSelected;

    public void SetResults(IEnumerable<SearchResultItem> results)
    {
        ResultsList.ItemsSource = results.ToList();
        StatusText.Text = $"{ResultsList.Items.Count} resultado(s)";
    }

    private void Search_Click(object sender, RoutedEventArgs e)
    {
        SearchRequested?.Invoke(this, EventArgs.Empty);
    }

    public event EventHandler? SearchRequested;

    private void ResultsList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (ResultsList.SelectedItem is SearchResultItem item)
        {
            ResultSelected?.Invoke(item);
            DialogResult = true;
        }
    }

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }
}
