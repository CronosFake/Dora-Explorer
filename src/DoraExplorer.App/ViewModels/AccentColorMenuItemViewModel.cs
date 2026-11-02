using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DoraExplorer.App.Models;

namespace DoraExplorer.App.ViewModels;

public partial class AccentColorMenuItemViewModel : ObservableObject
{
    private readonly Action<AccentColorMenuItemViewModel> _onSelected;

    public AccentColorMenuItemViewModel(
        AccentColorOption option,
        Action<AccentColorMenuItemViewModel> onSelected)
    {
        Option = option;
        _onSelected = onSelected;

        var swatch = new SolidColorBrush(option.Color);
        swatch.Freeze();
        Swatch = swatch;
    }

    public AccentColorOption Option { get; }

    public string Name => Option.Name;

    public Brush Swatch { get; }

    [ObservableProperty]
    private bool _isSelected;

    [RelayCommand]
    private void Select()
    {
        _onSelected(this);
    }
}
