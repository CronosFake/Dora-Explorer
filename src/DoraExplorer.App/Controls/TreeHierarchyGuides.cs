using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace DoraExplorer.App.Controls;

/// <summary>
/// Linhas hierárquicas estilo árvore clássica: tronco vertical à esquerda, ramo horizontal à direita até a seta.
/// </summary>
public sealed class TreeHierarchyGuides : FrameworkElement
{
    public static readonly DependencyProperty DepthProperty =
        DependencyProperty.Register(
            nameof(Depth),
            typeof(int),
            typeof(TreeHierarchyGuides),
            new FrameworkPropertyMetadata(0, FrameworkPropertyMetadataOptions.AffectsRender | FrameworkPropertyMetadataOptions.AffectsMeasure, OnHierarchyChanged));

    public static readonly DependencyProperty IsLastSiblingProperty =
        DependencyProperty.Register(
            nameof(IsLastSibling),
            typeof(bool),
            typeof(TreeHierarchyGuides),
            new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty AncestorContinuationsProperty =
        DependencyProperty.Register(
            nameof(AncestorContinuations),
            typeof(string),
            typeof(TreeHierarchyGuides),
            new FrameworkPropertyMetadata(string.Empty, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty IsNodeExpandedProperty =
        DependencyProperty.Register(
            nameof(IsNodeExpanded),
            typeof(bool),
            typeof(TreeHierarchyGuides),
            new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty HasChildrenProperty =
        DependencyProperty.Register(
            nameof(HasChildren),
            typeof(bool),
            typeof(TreeHierarchyGuides),
            new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender));

    public TreeHierarchyGuides()
    {
        SnapsToDevicePixels = true;
        UseLayoutRounding = true;
        VerticalAlignment = VerticalAlignment.Stretch;
        HorizontalAlignment = HorizontalAlignment.Left;
        ClipToBounds = false;
        IsHitTestVisible = false;
        Opacity = 1;
        // Acima do ItemBorder (ZIndex 1) para que as linhas fiquem visíveis mesmo quando o
        // item está selecionado/hover (cujo fundo opaco é desenhado pelo ItemBorder).
        // Este valor precisa ser definido no construtor: os valores locais do construtor
        // prevalecem sobre os do template para este elemento.
        Panel.SetZIndex(this, 2);
        RenderOptions.SetEdgeMode(this, EdgeMode.Aliased);
        SizeChanged += (_, _) => InvalidateVisual();
    }

    public int Depth
    {
        get => (int)GetValue(DepthProperty);
        set => SetValue(DepthProperty, value);
    }

    public bool IsLastSibling
    {
        get => (bool)GetValue(IsLastSiblingProperty);
        set => SetValue(IsLastSiblingProperty, value);
    }

    public string AncestorContinuations
    {
        get => (string)GetValue(AncestorContinuationsProperty);
        set => SetValue(AncestorContinuationsProperty, value);
    }

    public bool IsNodeExpanded
    {
        get => (bool)GetValue(IsNodeExpandedProperty);
        set => SetValue(IsNodeExpandedProperty, value);
    }

    public bool HasChildren
    {
        get => (bool)GetValue(HasChildrenProperty);
        set => SetValue(HasChildrenProperty, value);
    }

    private static void OnHierarchyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is TreeHierarchyGuides guides)
        {
            guides.InvalidateVisual();
        }
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        // Dentro do ScrollViewer com scroll horizontal a largura disponível vem como
        // infinita. Devolver 0 fazia o elemento ser arranjado com 0px e o OnRender abortava
        // no guard de ActualWidth — por isso as linhas nunca apareciam. Devolvemos uma
        // largura finita suficiente para conter todas as guias desenhadas nesta linha.
        var neededWidth = TreeViewHierarchy.IconLeftX(Math.Max(Depth, 0)) + TreeViewHierarchy.IndentSize;
        var width = double.IsPositiveInfinity(availableSize.Width) || double.IsNaN(availableSize.Width)
            ? neededWidth
            : Math.Max(availableSize.Width, neededWidth);

        var height = availableSize.Height;
        if (double.IsPositiveInfinity(height) || double.IsNaN(height) || height < TreeViewHierarchy.HeaderRowHeight)
        {
            height = TreeViewHierarchy.HeaderRowHeight;
        }

        return new Size(width, height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var height = Math.Max(finalSize.Height, TreeViewHierarchy.HeaderRowHeight);
        return new Size(finalSize.Width, height);
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        if (Depth < 0 || ActualWidth <= 0 || ActualHeight <= 0)
        {
            return;
        }

        var pen = CreatePen();
        var rowHeight = Math.Max(ActualHeight, TreeViewHierarchy.HeaderRowHeight);
        var metadataExtra = Math.Max(0, rowHeight - TreeViewHierarchy.HeaderRowHeight);
        var overlap = TreeViewHierarchy.LineOverlap + metadataExtra + TreeViewHierarchy.ItemsPresenterGap;
        var connectorY = AlignY(TreeViewHierarchy.ConnectorY);
        // A spine vertical do próprio nó deve começar por BAIXO da seta (base do expander),
        // nunca no seu centro — senão a linha sobe e atravessa o glifo da seta.
        var expanderBottomY = AlignY(TreeViewHierarchy.ConnectorY + TreeViewHierarchy.ExpanderSize / 2.0);
        // Extensão para baixo: todas as verticais param exatamente no conector da PRÓXIMA
        // linha (topo da linha + ConnectorY). Assim ligam-se ao próximo nó sem ultrapassar,
        // e no último filho a coluna termina na quina do "L" — sem sobra pendurada para baixo.
        var downExtent = rowHeight + connectorY;

        // Raiz (profundidade 0): só desce quando expandida — sem gutter, sem ramo horizontal.
        if (Depth == 0)
        {
            if (IsNodeExpanded && HasChildren)
            {
                var rootX = AlignX(TreeViewHierarchy.SpineX(0));
                DrawLine(drawingContext, pen, rootX, expanderBottomY, rootX, downExtent);
            }

            return;
        }

        // 1) Verticais dos ancestrais acima do pai (avôs, bisavôs…).
        // A coluna SpineX(level) é a spine do ancestral à profundidade `level`, de onde pendem
        // os nós de profundidade `level+1`. Essa coluna só atravessa esta linha se o ancestral
        // à profundidade `level+1` tiver um irmão abaixo — ou seja, continuation[level+1].
        // (Usar continuation[level] desenharia a coluna errada: a do próprio ancestral, que já
        // terminou no seu último filho, gerando uma linha vertical fantasma.)
        // O pai imediato (Depth-1) é tratado à parte no passo 2, por isso vamos até Depth-2.
        for (var level = 0; level <= Depth - 2; level++)
        {
            if (!ShouldContinueAtLevel(level + 1))
            {
                continue;
            }

            var x = AlignX(TreeViewHierarchy.SpineX(level));
            DrawLine(drawingContext, pen, x, -overlap, x, downExtent);
        }

        // 2) Tronco vertical do pai (coluna à esquerda do ramo horizontal deste nó).
        // Começa no topo da PRÓPRIA linha (y=0), nunca em -overlap: subir para dentro da linha
        // do pai faria a coluna atravessar o glifo da seta do pai em linhas compactas.
        // A continuidade para cima é garantida pela spine própria do pai (passo 4), que já
        // desce com folga (overlap) até dentro deste nó.
        var parentSpineX = AlignX(TreeViewHierarchy.SpineX(Depth - 1));
        if (IsLastSibling)
        {
            DrawLine(drawingContext, pen, parentSpineX, 0, parentSpineX, connectorY);
        }
        else
        {
            DrawLine(drawingContext, pen, parentSpineX, 0, parentSpineX, downExtent);
        }

        // 3) Ramo horizontal: do tronco do pai até à posição da seta — comprimento uniforme
        // para todos os nós (com ou sem filhos), parando onde a seta estaria.
        var branchEndX = AlignX(TreeViewHierarchy.ExpanderLeftX(Depth));
        DrawLine(drawingContext, pen, parentSpineX, connectorY, branchEndX, connectorY);

        // 4) Nó expandido: desce da BASE da seta até ao fundo da linha, ligando aos filhos.
        if (IsNodeExpanded && HasChildren)
        {
            var ownSpineX = AlignX(TreeViewHierarchy.SpineX(Depth));
            DrawLine(drawingContext, pen, ownSpineX, expanderBottomY, ownSpineX, downExtent);
        }
    }

    private bool ShouldContinueAtLevel(int level) =>
        level < AncestorContinuations.Length && AncestorContinuations[level] == '1';

    private Pen CreatePen()
    {
        var source = TryFindResource("TreeHierarchyLineBrush") as SolidColorBrush
            ?? new SolidColorBrush(Color.FromArgb(255, 210, 140, 55));

        var color = source.Color;
        if (color.A != 255)
        {
            color = Color.FromArgb(255, color.R, color.G, color.B);
        }

        var brush = new SolidColorBrush(color) { Opacity = 1 };
        brush.Freeze();

        var pen = new Pen(brush, 1)
        {
            StartLineCap = PenLineCap.Square,
            EndLineCap = PenLineCap.Square,
            LineJoin = PenLineJoin.Miter
        };
        pen.Freeze();
        return pen;
    }

    private static double AlignX(double value) => Math.Floor(value) + 0.5;

    private static double AlignY(double value) => Math.Floor(value) + 0.5;

    private static void DrawLine(
        DrawingContext drawingContext,
        Pen pen,
        double x1,
        double y1,
        double x2,
        double y2) =>
        drawingContext.DrawLine(pen, new Point(x1, y1), new Point(x2, y2));
}
