using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using DoraExplorer.App.Infrastructure;
using DoraExplorer.App.Services;
using DoraExplorer.App.ViewModels;
using DoraExplorer.App.Views.Dialogs;

namespace DoraExplorer.App;

public partial class MainWindow
{
    public MainWindow(MainWindowViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        Closing += MainWindow_Closing;
        viewModel.MessageRequested += (_, message) => MessageDialog.Show(this, message);
        Loaded += (_, _) => ViewModel.OnWindowLoaded();
        LeftPaneControl.PaneActivated += (_, _) => ViewModel.IsLeftPaneActive = true;
        RightPaneControl.PaneActivated += (_, _) => ViewModel.IsLeftPaneActive = false;
    }

    private bool _closeConfirmed;

    private MainWindowViewModel ViewModel => (MainWindowViewModel)DataContext;

    /// <summary>
    /// Sair no meio de copiar, mover ou compactar deixaria o arquivo de destino pela metade: pergunta, cancela
    /// (o trabalho apaga o que ficou) e só então fecha.
    /// </summary>
    private void MainWindow_Closing(object? sender, CancelEventArgs e)
    {
        if (!_closeConfirmed && OperationTracker.Shared.HasActive)
        {
            e.Cancel = true;
            _ = ConfirmCloseDuringOperationAsync();
            return;
        }

        ViewModel.PersistPanePaths();
    }

    private async Task ConfirmCloseDuringOperationAsync()
    {
        if (!MessageDialog.Confirm(
                this,
                "Há uma operação em andamento (copiar, mover ou compactar).\n\nCancelar a operação e sair? O que ficou pela metade será apagado.",
                "Dora Explorer"))
        {
            return;
        }

        var finished = await OperationTracker.Shared.CancelAllAsync(TimeSpan.FromSeconds(8));
        if (!finished)
        {
            AppLogService.Warning("Encerrando antes de a operação cancelada terminar a limpeza.");
        }

        // Deixa a janela de progresso se fechar sozinha antes de fechar a principal.
        await Dispatcher.InvokeAsync(static () => { }, DispatcherPriority.ApplicationIdle);

        _closeConfirmed = true;
        Close();
    }

    private void LeftPane_MouseDown(object sender, MouseButtonEventArgs e)
    {
        ViewModel.IsLeftPaneActive = true;
    }

    private void RightPane_MouseDown(object sender, MouseButtonEventArgs e)
    {
        ViewModel.IsLeftPaneActive = false;
    }

    private void About_Click(object sender, RoutedEventArgs e) => AboutDialog.Show(this);
}
