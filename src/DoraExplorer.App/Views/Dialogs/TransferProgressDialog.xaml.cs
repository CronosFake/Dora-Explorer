using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using DoraExplorer.App.Models;
using DoraExplorer.App.Services;

namespace DoraExplorer.App.Views.Dialogs;

/// <summary>
/// Andamento de uma cópia ou movimentação, com cancelar. Só aparece se a operação demorar (as pequenas
/// terminam antes e o usuário nem vê a janela).
/// </summary>
public partial class TransferProgressDialog : Window
{
    private readonly Action _cancel;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private bool _shown;
    private bool _closingForReal;
    private bool _cancelling;
    private double _bytesPerSecond;
    private long _lastBytes;
    private TimeSpan _lastSample;

    public TransferProgressDialog(string title, string windowTitle, Action cancel)
    {
        InitializeComponent();
        _cancel = cancel;
        Title = windowTitle;
        TitleText.Text = title;
    }

    public void ShowFor(Window owner)
    {
        DialogChrome.Apply(this, owner);
        _shown = true;
        Show();
    }

    /// <summary>Fecha a janela ao fim da operação (se chegou a aparecer).</summary>
    public void CloseWhenDone()
    {
        _closingForReal = true;
        if (_shown)
        {
            Close();
        }
    }

    public void Update(FileOperationProgress progress)
    {
        if (_closingForReal)
        {
            return;
        }

        FileText.Text = progress.CurrentFile;

        if (progress.IsTotalKnown)
        {
            Bar.IsIndeterminate = false;
            Bar.Value = progress.Fraction;
        }

        // Velocidade suavizada: a instantânea oscila demais para ser legível.
        var now = _clock.Elapsed;
        if (now - _lastSample >= TimeSpan.FromMilliseconds(500))
        {
            var instant = (progress.BytesDone - _lastBytes) / (now - _lastSample).TotalSeconds;
            _bytesPerSecond = _bytesPerSecond <= 0 ? instant : (0.7 * _bytesPerSecond) + (0.3 * instant);
            _lastBytes = progress.BytesDone;
            _lastSample = now;
        }

        if (!_cancelling)
        {
            DetailText.Text = TransferProgressFormatter.Describe(progress, Math.Max(0, _bytesPerSecond));
        }
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!_closingForReal)
        {
            e.Cancel = true; // fechar a janela cancela a operação; ela se fecha sozinha quando terminar
            RequestCancel();
        }

        base.OnClosing(e);
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => RequestCancel();

    private void RequestCancel()
    {
        if (_cancelling)
        {
            return;
        }

        _cancelling = true;
        CancelButton.IsEnabled = false;
        DetailText.Text = "Cancelando…";
        _cancel();
    }
}
