using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using System.Windows.Threading;
using DoraExplorer.App.Infrastructure;
using DoraExplorer.App.Models;
using DoraExplorer.App.Services;
using DoraExplorer.App.ViewModels;

namespace DoraExplorer.App.Controls;

/// <summary>
/// Prévia de imagens e PDFs ao deixar o mouse parado sobre o arquivo. Existe uma única dica compartilhada pela lista inteira
/// (nada é criado por linha, então a rolagem e a abertura de pastas grandes não ficam mais lentas), a miniatura é carregada
/// fora da thread da interface e a dica só aparece se houver miniatura de verdade e o mouse ainda estiver no mesmo arquivo.
/// </summary>
internal sealed class ThumbnailHoverPreview
{
    private const int PreviewPixels = 320;
    private static readonly TimeSpan ShowDelay = TimeSpan.FromMilliseconds(600);

    private readonly TreeView _tree;
    private readonly DispatcherTimer _timer;
    private readonly ToolTip _tip;
    private readonly Image _image;
    private readonly TextBlock _caption;
    private NavigationTreeNodeViewModel? _hovered;
    private NavigationTreeNodeViewModel? _requested;
    private CancellationTokenSource? _loading;

    private ThumbnailHoverPreview(TreeView tree)
    {
        _tree = tree;

        _image = new Image
        {
            MaxWidth = PreviewPixels,
            MaxHeight = PreviewPixels,
            Stretch = Stretch.Uniform,
            StretchDirection = StretchDirection.DownOnly,
            SnapsToDevicePixels = true
        };
        RenderOptions.SetBitmapScalingMode(_image, BitmapScalingMode.HighQuality);

        _caption = new TextBlock
        {
            MaxWidth = PreviewPixels,
            Margin = new Thickness(0, 6, 0, 0),
            TextTrimming = TextTrimming.CharacterEllipsis
        };

        var content = new StackPanel();
        content.Children.Add(_image);
        content.Children.Add(_caption);

        _tip = new ToolTip
        {
            Content = content,
            Padding = new Thickness(8),
            StaysOpen = true,
            IsHitTestVisible = false,
            PlacementTarget = tree,
            Placement = PlacementMode.MousePoint,
            HorizontalOffset = 16,
            VerticalOffset = 20
        };

        _timer = new DispatcherTimer(DispatcherPriority.Background, tree.Dispatcher) { Interval = ShowDelay };
        _timer.Tick += OnTimerTick;

        tree.PreviewMouseMove += OnMouseMove;
        tree.MouseLeave += (_, _) => Reset();
        tree.PreviewMouseDown += (_, _) => Reset();
        tree.PreviewMouseWheel += (_, _) => Reset();
        tree.PreviewKeyDown += (_, _) => Reset();
        tree.Unloaded += (_, _) => Reset();
    }

    /// <summary>Liga a prévia à lista; os eventos da própria lista mantêm o objeto vivo.</summary>
    public static void Attach(TreeView tree) => _ = new ThumbnailHoverPreview(tree);

    /// <summary>Só arquivos locais de tipos com miniatura; itens remotos e da lixeira não entram.</summary>
    internal static bool ShouldPreview(NavigationTreeNodeViewModel node) =>
        node.Kind == NavigationNodeKind.File &&
        !node.IsRecycleBinEntry &&
        node.TargetPath is { } path &&
        Path.IsPathFullyQualified(path) &&
        FileThumbnailService.CanLoadThumbnail(path);

    private void OnMouseMove(object sender, MouseEventArgs e)
    {
        // Arrastar, selecionar com retângulo etc.: nada de dica no meio do caminho.
        if (e.LeftButton == MouseButtonState.Pressed || e.RightButton == MouseButtonState.Pressed)
        {
            Reset();
            return;
        }

        var node = NodeUnder(e.OriginalSource as DependencyObject);
        if (ReferenceEquals(node, _hovered))
        {
            return;
        }

        Reset();
        _hovered = node;
        if (node is not null && ShouldPreview(node))
        {
            _timer.Start();
        }
    }

    private void OnTimerTick(object? sender, EventArgs e)
    {
        _timer.Stop();
        if (_hovered is not { } node)
        {
            return;
        }

        _requested = node;
        _loading?.Cancel();
        _loading?.Dispose();
        _loading = new CancellationTokenSource();
        _ = ShowAsync(node, _loading.Token);
    }

    private async Task ShowAsync(NavigationTreeNodeViewModel node, CancellationToken cancellationToken)
    {
        try
        {
            var source = await FileThumbnailService.TryLoadThumbnailAsync(node.TargetPath!, PreviewPixels, cancellationToken);

            // Enquanto carregava, o mouse pode ter ido para outro arquivo: aí a prévia não vale mais.
            if (source is null || cancellationToken.IsCancellationRequested || !ReferenceEquals(_requested, node))
            {
                return;
            }

            _image.Source = source;
            _caption.Text = node.Name;
            _tip.IsOpen = true;
        }
        catch (OperationCanceledException)
        {
            // Mouse saiu do arquivo: esperado.
        }
        catch (Exception ex)
        {
            // A prévia é um extra: um arquivo que o Windows não consegue ler não pode atrapalhar a lista.
            AppLogService.Warning($"Prévia indisponível para '{node.Name}': {ex.Message}");
        }
    }

    private void Reset()
    {
        _timer.Stop();
        _hovered = null;
        _requested = null;
        if (_loading is not null)
        {
            _loading.Cancel();
            _loading.Dispose();
            _loading = null;
        }

        if (_tip.IsOpen)
        {
            _tip.IsOpen = false;
        }

        _image.Source = null;
    }

    private static NavigationTreeNodeViewModel? NodeUnder(DependencyObject? source)
    {
        while (source is not null)
        {
            if (source is TreeViewItem item)
            {
                return item.DataContext as NavigationTreeNodeViewModel;
            }

            source = source is Visual or Visual3D ? VisualTreeHelper.GetParent(source) : LogicalTreeHelper.GetParent(source);
        }

        return null;
    }
}
