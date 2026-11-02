using System.Reflection;
using System.Windows;
using System.Windows.Input;

namespace DoraExplorer.App.Views.Dialogs;

public partial class AboutDialog : Window
{
    private static AboutDialog? _openInstance;
    private Window? _owner;
    private bool _closingFromOwner;

    public AboutDialog()
    {
        InitializeComponent();
        VersionText.Text = $"Versão {GetDisplayVersion()}";
    }

    public static void Show(Window owner)
    {
        if (_openInstance is not null)
        {
            _openInstance.Activate();
            return;
        }

        var dialog = new AboutDialog();
        DialogChrome.Apply(dialog, owner);
        dialog.AttachOwnerClose(owner);
        _openInstance = dialog;
        dialog.Closed += (_, _) =>
        {
            dialog.DetachOwnerClose();
            if (ReferenceEquals(_openInstance, dialog))
            {
                _openInstance = null;
            }
        };
        dialog.Show();
    }

    private void AttachOwnerClose(Window owner)
    {
        _owner = owner;
        owner.PreviewMouseDown += Owner_PreviewInput;
        owner.PreviewStylusDown += Owner_PreviewInput;
        owner.PreviewKeyDown += Owner_PreviewInput;
    }

    private void DetachOwnerClose()
    {
        if (_owner is null)
        {
            return;
        }

        _owner.PreviewMouseDown -= Owner_PreviewInput;
        _owner.PreviewStylusDown -= Owner_PreviewInput;
        _owner.PreviewKeyDown -= Owner_PreviewInput;
        _owner = null;
    }

    private void Owner_PreviewInput(object sender, EventArgs e)
    {
        if (_closingFromOwner || !IsVisible)
        {
            return;
        }

        _closingFromOwner = true;
        Close();
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key is Key.Escape or Key.Enter or Key.Space)
        {
            Close();
            e.Handled = true;
        }
    }

    private void Ok_Click(object sender, RoutedEventArgs e) => Close();

    private static string GetDisplayVersion()
    {
        var assembly = typeof(App).Assembly;
        var informational = assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion;

        if (!string.IsNullOrWhiteSpace(informational))
        {
            var plusIndex = informational.IndexOf('+', StringComparison.Ordinal);
            return plusIndex > 0 ? informational[..plusIndex] : informational;
        }

        return assembly.GetName().Version?.ToString(3) ?? "1.0.0";
    }
}
