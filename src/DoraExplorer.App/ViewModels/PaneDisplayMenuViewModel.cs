using CommunityToolkit.Mvvm.ComponentModel;
using DoraExplorer.App.Models;

namespace DoraExplorer.App.ViewModels;

public partial class PaneDisplayMenuViewModel : ObservableObject
{
    [ObservableProperty]
    private bool _showFavorites;

    [ObservableProperty]
    private bool _showRecycleBin;

    [ObservableProperty]
    private bool _showFtp;

    [ObservableProperty]
    private bool _showSftp;

    [ObservableProperty]
    private bool _showScp;

    [ObservableProperty]
    private bool _showTftp;

    [ObservableProperty]
    private bool _showSmb;

    [ObservableProperty]
    private bool _showNfs;

    public void LoadFrom(PaneDisplayOptions options)
    {
        ShowFavorites = options.ShowFavorites;
        ShowRecycleBin = options.ShowRecycleBin;
        ShowFtp = options.ShowFtp;
        ShowSftp = options.ShowSftp;
        ShowScp = options.ShowScp;
        ShowTftp = options.ShowTftp;
        ShowSmb = options.ShowSmb;
        ShowNfs = options.ShowNfs;
    }

    public bool DiffersFrom(PaneDisplayOptions options) =>
        ShowFavorites != options.ShowFavorites
            || ShowRecycleBin != options.ShowRecycleBin
            || ShowFtp != options.ShowFtp
            || ShowSftp != options.ShowSftp
            || ShowScp != options.ShowScp
            || ShowTftp != options.ShowTftp
            || ShowSmb != options.ShowSmb
            || ShowNfs != options.ShowNfs;

    public void ApplyTo(PaneDisplayOptions options)
    {
        options.ShowFavorites = ShowFavorites;
        options.ShowRecycleBin = ShowRecycleBin;
        options.ShowFtp = ShowFtp;
        options.ShowSftp = ShowSftp;
        options.ShowScp = ShowScp;
        options.ShowTftp = ShowTftp;
        options.ShowSmb = ShowSmb;
        options.ShowNfs = ShowNfs;
    }
}
