using System.Windows;
using System.Windows.Controls;

namespace DoraExplorer.App.Controls;

/// <summary>
/// Constantes e metadados de hierarquia partilhados entre o template da árvore e o desenho das linhas.
/// </summary>
public static class TreeViewHierarchy
{
    public const double IndentSize = 19.0;
    public const double GlobalMarqueeGutterWidth = 20.0;
    public const double ExpanderSize = 16.0;
    public const double ExpanderRightMargin = 4.0;
    public const double LineOverlap = 16.0;

    public static Thickness MultiSelectHighlightMargin =>
        new(-(ExpanderSize + ExpanderRightMargin), -ItemBorderTopPadding, -4, -ItemBorderBottomPadding);

    /// <summary>Margem inferior dos nós de navegação raiz (FilePaneControl HierarchicalDataTemplate).</summary>
    public const double ItemsPresenterGap = 10.0;
    public const double RowMinHeight = 22.0;

    public const double HeaderBandHeight = 28.0;

    /// <summary>Espessura da borda do ItemBorder: empurra o conteúdo (seta/ícone) para dentro em X e Y.</summary>
    public const double ItemBorderThickness = 1.0;
    public const double ItemBorderTopPadding = 1.0;
    public const double ItemBorderBottomPadding = 1.0;

    /// <summary>
    /// Deslocamento horizontal do conteúdo a partir da origem do desenho das guias
    /// (borda esquerda do ItemBorder; o padding esquerdo do ItemBorder é 0).
    /// </summary>
    public const double ContentLeftInset = ItemBorderThickness;

    /// <summary>Deslocamento vertical do conteúdo a partir do topo da faixa (borda + padding superior).</summary>
    public const double ContentTopInset = ItemBorderThickness + ItemBorderTopPadding;

    /// <summary>Altura da faixa onde ícone, seta e linhas são desenhados (sem metadata), incluindo bordas.</summary>
    public const double HeaderRowHeight =
        ItemBorderThickness + ItemBorderTopPadding + HeaderBandHeight + ItemBorderBottomPadding + ItemBorderThickness;

    /// <summary>Centro vertical da seta e dos conectores horizontais (alinhado ao centro real da seta/ícone).</summary>
    public const double ConnectorY = ContentTopInset + HeaderBandHeight / 2.0;

    /// <summary>Margem superior da seta para alinhar ao <see cref="ConnectorY"/>.</summary>
    public static double ExpanderTopMargin => ConnectorY - ContentTopInset - ExpanderSize / 2.0;

    public static Thickness ExpanderMargin => new(0, ExpanderTopMargin, ExpanderRightMargin, 0);

    /// <summary>Tronco virtual à esquerda do primeiro nível indentado.</summary>
    public static double RootSpineX => ContentLeftInset + ExpanderSize / 2.0;

    /// <summary>Centro X da seta a uma dada profundidade (donde desce a linha vertical guia).</summary>
    public static double SpineX(int depth) =>
        depth * IndentSize + ContentLeftInset + ExpanderSize / 2.0;

    /// <summary>Borda esquerda da seta (onde o ramo horizontal toca as pastas).</summary>
    public static double ExpanderLeftX(int depth) =>
        depth * IndentSize + ContentLeftInset;

    /// <summary>Borda esquerda do ícone (onde o ramo horizontal toca ficheiros, que não têm seta).</summary>
    public static double IconLeftX(int depth) =>
        ExpanderLeftX(depth) + ExpanderSize + ExpanderRightMargin;

    public const double MetadataFontSize = 9.0;
    public const double MetadataDateWidth = 76.0;
    public const double MetadataTimeWidth = 44.0;
    public const double MetadataSizeWidth = 72.0;
    public const double MetadataColumnGap = 8.0;

    public const double MetadataFooterWidth =
        MetadataDateWidth + MetadataColumnGap + MetadataTimeWidth + MetadataColumnGap + MetadataSizeWidth;

    public static readonly DependencyProperty DepthProperty =
        DependencyProperty.RegisterAttached(
            "Depth",
            typeof(int),
            typeof(TreeViewHierarchy),
            new PropertyMetadata(0, OnHierarchyMetadataChanged));

    public static readonly DependencyProperty IsLastSiblingProperty =
        DependencyProperty.RegisterAttached(
            "IsLastSibling",
            typeof(bool),
            typeof(TreeViewHierarchy),
            new PropertyMetadata(false, OnHierarchyMetadataChanged));

    public static readonly DependencyProperty IsFirstSiblingProperty =
        DependencyProperty.RegisterAttached(
            "IsFirstSibling",
            typeof(bool),
            typeof(TreeViewHierarchy),
            new PropertyMetadata(false, OnHierarchyMetadataChanged));

    public static readonly DependencyProperty AncestorContinuationsProperty =
        DependencyProperty.RegisterAttached(
            "AncestorContinuations",
            typeof(string),
            typeof(TreeViewHierarchy),
            new PropertyMetadata(string.Empty, OnHierarchyMetadataChanged));

    public static readonly DependencyProperty IsLeafNodeProperty =
        DependencyProperty.RegisterAttached(
            "IsLeafNode",
            typeof(bool),
            typeof(TreeViewHierarchy),
            new PropertyMetadata(false, OnHierarchyMetadataChanged));

    public static readonly DependencyProperty ItemContentMarginProperty =
        DependencyProperty.RegisterAttached(
            "ItemContentMargin",
            typeof(Thickness),
            typeof(TreeViewHierarchy),
            new PropertyMetadata(new Thickness(0)));

    public static Thickness GetItemContentMargin(DependencyObject element) =>
        (Thickness)element.GetValue(ItemContentMarginProperty);

    public static void SetItemContentMargin(DependencyObject element, Thickness value) =>
        element.SetValue(ItemContentMarginProperty, value);

    public static int GetDepth(DependencyObject element) => (int)element.GetValue(DepthProperty);

    public static void SetDepth(DependencyObject element, int value) => element.SetValue(DepthProperty, value);

    public static bool GetIsLastSibling(DependencyObject element) => (bool)element.GetValue(IsLastSiblingProperty);

    public static void SetIsLastSibling(DependencyObject element, bool value) =>
        element.SetValue(IsLastSiblingProperty, value);

    public static bool GetIsFirstSibling(DependencyObject element) =>
        (bool)element.GetValue(IsFirstSiblingProperty);

    public static void SetIsFirstSibling(DependencyObject element, bool value) =>
        element.SetValue(IsFirstSiblingProperty, value);

    public static string GetAncestorContinuations(DependencyObject element) =>
        (string)element.GetValue(AncestorContinuationsProperty);

    public static void SetAncestorContinuations(DependencyObject element, string value) =>
        element.SetValue(AncestorContinuationsProperty, value);

    public static bool GetIsLeafNode(DependencyObject element) => (bool)element.GetValue(IsLeafNodeProperty);

    public static void SetIsLeafNode(DependencyObject element, bool value) =>
        element.SetValue(IsLeafNodeProperty, value);

    private static void OnHierarchyMetadataChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not TreeViewItem item)
        {
            return;
        }

        item.InvalidateVisual();
        item.InvalidateMeasure();
        InvalidateGuides(item);
    }

    private static void InvalidateGuides(TreeViewItem item)
    {
        if (item.Template is null)
        {
            return;
        }

        if (item.Template.FindName("HierarchyGuides", item) is TreeHierarchyGuides guides)
        {
            guides.InvalidateVisual();
            guides.InvalidateMeasure();
        }
    }

    /// <summary>
    /// Recalcula os metadados de hierarquia percorrendo os <b>dados</b> (ViewModels), e não os
    /// containers. Isto torna o cálculo independente da virtualização: os valores são gravados no
    /// ViewModel e chegam ao container (mesmo reciclado) por binding. Como os metadados são
    /// estruturais (a posição entre irmãos não muda com expansão; a continuação das colunas
    /// ancestrais depende de <see cref="ViewModels.NavigationTreeNodeViewModel.IsExpanded"/> no
    /// último irmão) e as escritas são idempotentes (só notificam quando o valor muda),
    /// reexecutar isto é barato e seguro.
    /// </summary>
    public static void UpdateTree(ItemsControl root)
    {
        var roots = new List<ViewModels.NavigationTreeNodeViewModel>();
        foreach (var item in root.Items)
        {
            if (item is ViewModels.NavigationTreeNodeViewModel node)
            {
                roots.Add(node);
            }
        }

        UpdateLevel(roots, 0, string.Empty);
    }

    private static void UpdateLevel(
        IReadOnlyList<ViewModels.NavigationTreeNodeViewModel> siblings,
        int depth,
        string ancestorContinuations)
    {
        var visibleSiblings = new List<ViewModels.NavigationTreeNodeViewModel>();
        foreach (var node in siblings)
        {
            if (!node.IsPlaceholder)
            {
                visibleSiblings.Add(node);
            }
        }

        for (var i = 0; i < visibleSiblings.Count; i++)
        {
            var node = visibleSiblings[i];
            var isLast = i == visibleSiblings.Count - 1;
            node.SetHierarchyMetadata(depth, isLast, ancestorContinuations);

            if (node.Children.Count > 0)
            {
                // Último irmão colapsado: sem irmãos abaixo → '0' (a coluna do pai termina no "L").
                // Último irmão expandido: a coluna do pai deve continuar nos descendentes visíveis,
                // tal como acontece para irmãos que não são o último.
                var continuationChar = isLast && !node.IsExpanded ? '0' : '1';
                var childContinuations = ancestorContinuations + continuationChar;
                UpdateLevel(node.Children, depth + 1, childContinuations);
            }
        }
    }
}
