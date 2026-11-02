using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using DoraExplorer.App.Infrastructure;
using DoraExplorer.App.Models;
using DoraExplorer.App.Services;
using DoraExplorer.App.ViewModels;
using DoraExplorer.App.Views.Dialogs;

namespace DoraExplorer.App.Controls;

public partial class FilePaneControl : UserControl
{
    private const double MarqueeAutoScrollMargin = 24;
    private const double MarqueeAutoScrollStep = TreeViewHierarchy.HeaderRowHeight * 0.75;

    private bool _expansionFromClick;
    private bool _collapseFromClick;
    private bool _syncingAccordion;
    private bool _revealChildrenOnRefresh;

    // True enquanto manipulamos a árvore programaticamente (restaurar expansão, refrescar
    // hierarquia, realizar containers). Qualquer evento Expanded disparado pela binding
    // IsExpanded durante estas operações é sintético e NÃO deve reagendar o reveal, pois
    // isso incrementaria _applyExpansionVersion e cancelaria o reveal legítimo em curso.
    private bool _suppressExpansionReveal;
    private int _applyExpansionVersion;

    private bool _suppressSelectionScroll;
    private bool _lockTreeScroll;
    private double _lockedTreeScrollOffset;
    private ScrollViewer? _treeScrollViewer;

    private Point _marqueeStart;
    private bool _marqueePending;
    private bool _marqueeActive;
    private NavigationTreeNodeViewModel? _marqueeReferenceNode;
    private TreeViewItem? _marqueeClickItem;
    private NavigationTreeNodeViewModel? _marqueeClickNode;
    private Rectangle? _marqueeRectangle;
    private Point _marqueeLastRawHostPoint;
    private int _marqueeRangeEndIndex = -1;
    private DispatcherTimer? _marqueeAutoScrollTimer;
    private bool _suppressSelectionSync;
    private ContextMenu? _activeTreeContextMenu;

    private bool _itemDragPending;
    private Point _itemDragStart;
    private TreeViewItem? _itemDragClickItem;
    private NavigationTreeNodeViewModel? _itemDragClickNode;

    private static readonly DataFormat InternalDragFormat =
        DataFormats.GetDataFormat("DoraExplorer.InternalFileDrag");

    public event EventHandler? PaneActivated;

    static FilePaneControl()
    {
        // Suprime o auto-scroll do WPF que alinha o item selecionado ao topo do painel.
        // Um class handler no próprio TreeViewItem corre na origem do evento (antes de o
        // RequestBringIntoView borbulhar até ao ScrollViewer), ao contrário de um handler
        // no TreeView, que só corre depois de o ScrollViewer já ter rolado.
        EventManager.RegisterClassHandler(
            typeof(TreeViewItem),
            TreeViewItem.RequestBringIntoViewEvent,
            new RequestBringIntoViewEventHandler(OnTreeViewItemRequestBringIntoView));
    }

    public FilePaneControl()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        SizeChanged += OnPaneSizeChanged;
        NavigationTree.SizeChanged += OnNavigationTreeSizeChanged;
        NavigationTree.ItemContainerGenerator.StatusChanged += OnItemContainerGeneratorStatusChanged;
        NavigationTree.PreviewKeyDown += NavigationTree_PreviewKeyDown;
        NavigationTree.GotFocus += (_, _) => NotifyPaneActivated();
        NavigationTree.PreviewMouseWheel += NavigationTree_PreviewMouseWheel;
        ThumbnailHoverPreview.Attach(NavigationTree);
        TreeHost.AddHandler(UIElement.PreviewMouseLeftButtonDownEvent, new MouseButtonEventHandler(TreeHost_PreviewMouseLeftButtonDown), true);
        TreeHost.AddHandler(UIElement.PreviewMouseRightButtonDownEvent, new MouseButtonEventHandler(TreeHost_PreviewMouseRightButtonDown), true);
        TreeHost.AddHandler(UIElement.PreviewMouseRightButtonUpEvent, new MouseButtonEventHandler(TreeHost_PreviewMouseRightButtonUp), true);
        TreeHost.AllowDrop = true;
        TreeHost.DragOver += TreeHost_DragOver;
        TreeHost.Drop += TreeHost_Drop;
        QueryContinueDrag += OnQueryContinueDrag;
        TreeHost.SizeChanged += (_, _) => SyncMarqueeCanvasSize();
        PreviewKeyDown += OnPanePreviewKeyDown;
        PreviewMouseDown += OnPanePreviewMouseDown;
    }

    // Botões laterais do mouse: voltar e avançar, como num navegador.
    private void OnPanePreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        ICommand? command = e.ChangedButton switch
        {
            MouseButton.XButton1 => ViewModel?.GoBackCommand,
            MouseButton.XButton2 => ViewModel?.GoForwardCommand,
            _ => null
        };

        if (command is null)
        {
            return;
        }

        if (command.CanExecute(null))
        {
            command.Execute(null);
        }

        e.Handled = true;
    }

    private void OnPanePreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (ViewModel?.RenamingNode is not null)
        {
            return;
        }

        // Alt+← voltar, Alt+→ avançar, Alt+↑ subir (com Alt pressionado o WPF reporta Key.System).
        if (Keyboard.Modifiers == ModifierKeys.Alt && ViewModel is { } navigationPane)
        {
            ICommand? navigation = (e.Key == Key.System ? e.SystemKey : e.Key) switch
            {
                Key.Left => navigationPane.GoBackCommand,
                Key.Right => navigationPane.GoForwardCommand,
                Key.Up => navigationPane.GoUpCommand,
                _ => null
            };

            if (navigation is not null)
            {
                if (navigation.CanExecute(null))
                {
                    navigation.Execute(null);
                }

                e.Handled = true;
                return;
            }
        }

        if (Keyboard.Modifiers != ModifierKeys.Control)
        {
            return;
        }

        switch (e.Key)
        {
            case Key.C:
                NotifyPaneActivated();
                ExecuteStageCopy();
                e.Handled = true;
                break;
            case Key.X:
                NotifyPaneActivated();
                ExecuteStageCut();
                e.Handled = true;
                break;
            case Key.V:
                NotifyPaneActivated();
                ExecutePaste();
                e.Handled = true;
                break;
        }
    }

    private static void OnTreeViewItemRequestBringIntoView(object sender, RequestBringIntoViewEventArgs e)
    {
        if (sender is TreeViewItem item &&
            FindAncestor<FilePaneControl>(item) is { IsSearchRevealActive: true })
        {
            return;
        }

        e.Handled = true;
    }

    private static T? FindAncestor<T>(DependencyObject? current) where T : DependencyObject
    {
        while (current is not null)
        {
            if (current is T match)
            {
                return match;
            }

            current = VisualTreeHelper.GetParent(current);
        }

        return null;
    }

    public bool IsSearchRevealActive => ViewModel?.ShouldCenterNavigatedNode == true;

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        CancelMarqueeTracking();
        CancelItemDragTracking();
        StopMarqueeAutoScrollTimer();

        if (_treeScrollViewer is not null)
        {
            _treeScrollViewer.ScrollChanged -= OnTreeScrollChanged;
            _treeScrollViewer = null;
        }
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        AttachTreeScrollViewer();
        SyncMarqueeCanvasSize();
        RequestHierarchyUpdate();
        Dispatcher.BeginInvoke(() =>
        {
            RunSuppressingReveal(() =>
            {
                ApplyExpansionFromViewModel(NavigationTree);
                TreeViewHierarchy.UpdateTree(NavigationTree);
            });
        }, DispatcherPriority.Loaded);
        UpdateResponsiveMetadataLayout();
    }

    private void AttachTreeScrollViewer()
    {
        if (_treeScrollViewer is not null)
        {
            _treeScrollViewer.ScrollChanged -= OnTreeScrollChanged;
        }

        _treeScrollViewer = FindScrollViewer(NavigationTree);
        if (_treeScrollViewer is not null)
        {
            _treeScrollViewer.ScrollChanged += OnTreeScrollChanged;
            UpdateColumnHeaderInset();
        }
    }

    private void ColumnHeader_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string tag } ||
            !Enum.TryParse<TreeSortField>(tag, out var field) ||
            GetMainViewModel() is not { } main)
        {
            return;
        }

        main.ApplyGlobalSortOptions(DoraExplorer.App.Services.SortHeaderLogic.Toggle(main.GlobalSortOptions, field));
    }

    // Borda (1) e espaçamento interno (4) à direita do item da árvore: o conteúdo das linhas termina antes da borda.
    private const double RowContentRightInset = 5;

    // As colunas do cabeçalho têm de coincidir com as das linhas. O ScrollViewer da árvore reserva a faixa de
    // arrasto da seleção à esquerda e, quando visível, a barra de rolagem à direita.
    private void UpdateColumnHeaderInset()
    {
        var reserved = _treeScrollViewer is { } scroll
            ? Math.Max(0, scroll.ActualWidth - scroll.ViewportWidth)
            : TreeViewHierarchy.GlobalMarqueeGutterWidth;
        var left = TreeViewHierarchy.GlobalMarqueeGutterWidth;
        var right = Math.Max(0, reserved - left) + RowContentRightInset;
        ColumnHeaderGrid.Margin = new Thickness(left, 0, right, 0);
    }

    private void OnTreeScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        UpdateColumnHeaderInset();

        if (!_lockTreeScroll || sender is not ScrollViewer scrollViewer || e.VerticalChange == 0)
        {
            return;
        }

        var target = scrollViewer.ScrollableHeight > 0
            ? Math.Clamp(_lockedTreeScrollOffset, 0, scrollViewer.ScrollableHeight)
            : _lockedTreeScrollOffset;

        if (Math.Abs(scrollViewer.VerticalOffset - target) > 0.5)
        {
            scrollViewer.ScrollToVerticalOffset(target);
            SmoothScrollAnimator.SyncTarget(scrollViewer, target);
        }
    }

    private void NavigationTree_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (FindScrollViewer(NavigationTree) is not ScrollViewer scrollViewer)
        {
            return;
        }

        SmoothScrollAnimator.HandleMouseWheel(scrollViewer, e);
    }

    private void OnPaneSizeChanged(object sender, SizeChangedEventArgs e) =>
        UpdateResponsiveMetadataLayout();

    private void OnNavigationTreeSizeChanged(object sender, SizeChangedEventArgs e) =>
        UpdateResponsiveMetadataLayout();

    private void UpdateResponsiveMetadataLayout()
    {
        if (ViewModel is null)
        {
            return;
        }

        var width = NavigationTree.ActualWidth;
        if (width <= 0)
        {
            width = ActualWidth;
        }

        ViewModel.UpdateResponsiveLayout(width);
    }

    private void OnItemContainerGeneratorStatusChanged(object? sender, EventArgs e)
    {
        if (ViewModel?.ShouldSuppressTreeAnimations == true)
        {
            return;
        }

        RequestHierarchyUpdate();
        Dispatcher.BeginInvoke(
            () => RunSuppressingReveal(() => TreeViewHierarchy.UpdateTree(NavigationTree)),
            DispatcherPriority.Loaded);
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.OldValue is FilePaneViewModel oldViewModel)
        {
            oldViewModel.NavigationTreeRebuilt -= OnNavigationTreeRebuilt;
            oldViewModel.PaneContentRefreshed -= OnPaneContentRefreshed;
            oldViewModel.NavigationApplied -= OnNavigationApplied;
            oldViewModel.RenamingStarted -= OnRenamingStarted;
            oldViewModel.RenamingEnded -= OnRenamingEnded;
        }

        if (e.NewValue is FilePaneViewModel newViewModel)
        {
            newViewModel.NavigationTreeRebuilt += OnNavigationTreeRebuilt;
            newViewModel.PaneContentRefreshed += OnPaneContentRefreshed;
            newViewModel.NavigationApplied += OnNavigationApplied;
            newViewModel.RenamingStarted += OnRenamingStarted;
            newViewModel.RenamingEnded += OnRenamingEnded;
        }
    }

    private void OnNavigationTreeRebuilt(object? sender, EventArgs e)
    {
        RequestRefreshTree();
    }

    private void OnPaneContentRefreshed(object? sender, EventArgs e)
    {
        RequestRefreshTree();
    }

    private void OnNavigationApplied(object? sender, EventArgs e)
    {
        var node = ViewModel?.SelectedNode;
        if (node is null)
        {
            return;
        }

        TrySelectNavigatedNode(node, attempt: 0);
    }

    private void TrySelectNavigatedNode(NavigationTreeNodeViewModel node, int attempt)
    {
        var centerInPanel = ViewModel?.ShouldCenterNavigatedNode == true;
        if (centerInPanel)
        {
            ScheduleSearchReveal(node);
            return;
        }

        var maxAttempts = 6;

        Dispatcher.BeginInvoke(() =>
        {
            RunSuppressingReveal(() =>
            {
                ApplyExpansionFromViewModel(NavigationTree);
                SyncAccordionExpansion();
                TreeViewHierarchy.UpdateTree(NavigationTree);
            });

            var item = FindTreeViewItemForNode(NavigationTree, node);
            if (item is null)
            {
                if (attempt < maxAttempts)
                {
                    TrySelectNavigatedNode(node, attempt + 1);
                }

                return;
            }

            _suppressSelectionScroll = true;
            try
            {
                if (!item.IsSelected)
                {
                    item.IsSelected = true;
                }
            }
            finally
            {
                _suppressSelectionScroll = false;
            }

            if (node.IsSelectableItem)
            {
                ViewModel?.SetSingleItemSelection(node);
            }

            if (FindScrollViewer(NavigationTree) is ScrollViewer scrollViewer)
            {
                TreeScrollHelper.EnsureItemVisible(item, scrollViewer, animate: false);
            }
        }, attempt < 4 ? DispatcherPriority.Loaded : DispatcherPriority.Render);
    }

    private void ScheduleSearchReveal(NavigationTreeNodeViewModel node)
    {
        var version = ViewModel?.SearchNavigationGeneration ?? 0;
        if (version == 0 || ViewModel is null)
        {
            return;
        }

        var scrollViewer = _treeScrollViewer ?? FindScrollViewer(NavigationTree);
        if (scrollViewer is not null)
        {
            TreeScrollHelper.JumpScrollNearTarget(node, ViewModel.NavigationRoots, scrollViewer);
        }

        RunSearchRevealPass(node, version, attempt: 0);
    }

    private void RunSearchRevealPass(NavigationTreeNodeViewModel node, int version, int attempt)
    {
        const int maxAttempts = 8;
        if (!IsSearchRevealCurrent(version))
        {
            return;
        }

        var priority = attempt switch
        {
            0 => DispatcherPriority.Loaded,
            <= 2 => DispatcherPriority.Render,
            _ => DispatcherPriority.ApplicationIdle
        };

        Dispatcher.BeginInvoke(() =>
        {
            if (!IsSearchRevealCurrent(version) || ViewModel is null)
            {
                return;
            }

            RunSuppressingReveal(() =>
            {
                EnsureTreeContainersForNode(node);
                MaterializeParentFolderContainers(node);
                ApplyExpansionFromViewModel(NavigationTree);
                TreeViewHierarchy.UpdateTree(NavigationTree);
            });

            var scrollViewer = _treeScrollViewer ?? FindScrollViewer(NavigationTree);
            var item = FindTreeViewItemForNode(NavigationTree, node);

            if (scrollViewer is not null)
            {
                if (item is not null)
                {
                    SelectSearchTreeItem(item, node);
                    scrollViewer.UpdateLayout();
                    NavigationTree.UpdateLayout();
                    item.UpdateLayout();
                    TreeScrollHelper.TryCenterSearchItem(
                        item,
                        node,
                        ViewModel.NavigationRoots,
                        scrollViewer);
                }
                else
                {
                    TreeScrollHelper.JumpScrollNearTarget(node, ViewModel.NavigationRoots, scrollViewer);
                }
            }

            if (item is null && attempt < maxAttempts)
            {
                RunSearchRevealPass(node, version, attempt + 1);
                return;
            }

            ViewModel.ClearCenterNavigatedNode(version);
        }, priority);
    }

    private void SelectSearchTreeItem(TreeViewItem item, NavigationTreeNodeViewModel node)
    {
        _suppressSelectionScroll = true;
        try
        {
            if (!item.IsSelected)
            {
                item.IsSelected = true;
            }

            if (node.IsSelectableItem)
            {
                ViewModel?.SetSingleItemSelection(node);
            }
        }
        finally
        {
            _suppressSelectionScroll = false;
        }
    }

    private bool IsSearchRevealCurrent(int version) =>
        ViewModel is not null && version == ViewModel.SearchNavigationGeneration;

    private void MaterializeParentFolderContainers(NavigationTreeNodeViewModel node)
    {
        var parent = ViewModel?.GetParentNode(node);
        if (parent is null)
        {
            return;
        }

        if (FindTreeViewItemForNode(NavigationTree, parent) is TreeViewItem parentItem)
        {
            RefreshTreeViewItemChildren(parentItem);
            parentItem.UpdateLayout();
        }
    }

    private void EnsureTreeContainersForNode(NavigationTreeNodeViewModel node)
    {
        if (ViewModel is null)
        {
            return;
        }

        var path = new List<NavigationTreeNodeViewModel>();
        for (var current = node; current is not null; current = ViewModel.GetParentNode(current))
        {
            path.Add(current);
        }

        path.Reverse();

        ItemsControl parent = NavigationTree;
        foreach (var segment in path)
        {
            parent.UpdateLayout();

            TreeViewItem? treeItem = parent.ItemContainerGenerator.ContainerFromItem(segment) as TreeViewItem;
            if (treeItem is null)
            {
                parent.UpdateLayout();
                treeItem = parent.ItemContainerGenerator.ContainerFromItem(segment) as TreeViewItem;
                if (treeItem is null)
                {
                    break;
                }
            }

            if (segment.IsExpandable && segment.IsExpanded && !treeItem.IsExpanded)
            {
                treeItem.IsExpanded = true;
            }

            if (segment.IsExpandable && segment.Children.Count > 0)
            {
                treeItem.IsExpanded = true;
            }

            treeItem.UpdateLayout();
            RefreshTreeViewItemChildren(treeItem);
            treeItem.BringIntoView();
            treeItem.UpdateLayout();
            parent = treeItem;
        }
    }

    private void OnRenamingStarted(object? sender, EventArgs e)
    {
        var node = ViewModel?.RenamingNode;
        RequestRefreshTree();

        if (node is null)
        {
            return;
        }

        Dispatcher.BeginInvoke(() =>
        {
            if (FindTreeViewItemForNode(NavigationTree, node) is TreeViewItem item)
            {
                item.IsSelected = true;
                ScheduleEnsureItemVisibleIfNeeded(item);
            }
        }, DispatcherPriority.Loaded);
    }

    private FilePaneViewModel? ViewModel => DataContext as FilePaneViewModel;

    private void NavigationTreeItem_Expanded(object sender, RoutedEventArgs e)
    {
        if (FindTreeViewItem(e.OriginalSource as DependencyObject) is not TreeViewItem item ||
            item.DataContext is not NavigationTreeNodeViewModel node)
        {
            return;
        }

        if (_expansionFromClick)
        {
            _expansionFromClick = false;
            return;
        }

        // Um Expanded disparado pela binding IsExpanded durante manipulação programática
        // da árvore (restaurar expansão, realizar containers) é sintético: não deve
        // reagendar o reveal nem incrementar a versão, senão cancela o reveal legítimo.
        var synthetic = _suppressExpansionReveal
                        || _syncingAccordion
                        || ViewModel?.ShouldSuppressTreeAnimations == true;

        if (node.ChildrenLoaded)
        {
            if (!node.IsExpanded)
            {
                node.IsExpanded = true;
            }

            ViewModel?.OnNavigationNodeExpanding(node);
            if (!synthetic)
            {
                ViewModel?.OnNavigationNodeExpanded(node);
            }

            SyncAccordionExpansion();

            if (synthetic)
            {
                RequestHierarchyUpdate();
            }
            else
            {
                _revealChildrenOnRefresh = true;
                RequestRefreshExpandedItem(item);
            }

            return;
        }

        if (!node.IsExpanded)
        {
            node.IsExpanded = true;
        }

        ViewModel?.OnNavigationNodeExpanding(node);
        ViewModel?.OnNavigationNodeExpanded(node);
        if (!synthetic)
        {
            AnnounceDeniedListing(node);
        }

        SyncAccordionExpansion();

        if (synthetic)
        {
            RequestHierarchyUpdate();
        }
        else
        {
            _revealChildrenOnRefresh = true;
            RequestRefreshExpandedItem(item);
        }
    }

    private void NavigationTreeItem_Collapsed(object sender, RoutedEventArgs e)
    {
        if (FindTreeViewItem(e.OriginalSource as DependencyObject) is not TreeViewItem item ||
            item.DataContext is not NavigationTreeNodeViewModel node)
        {
            return;
        }

        if (_collapseFromClick)
        {
            return;
        }

        if (_syncingAccordion)
        {
            node.IsExpanded = false;
            return;
        }

        CancelPendingExpansionRefresh();
        TreeExpansionRevealAnimator.Reset(item);
        node.IsExpanded = false;

        // Fechar por teclado (seta) ou automação também conta: reabrir traz listagem nova, como no clique.
        ViewModel?.OnNavigationNodeCollapsed(node, userInitiated: true);
        RequestHierarchyUpdate();
    }

    private void NavigationTree_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (ViewModel?.IsRestoringInitialPath == true)
        {
            return;
        }

        if (e.NewValue is NavigationTreeNodeViewModel node)
        {
            if (!_suppressSelectionScroll && ViewModel?.IsSuppressingSelectionUpdates != true)
            {
                ViewModel?.OnNavigationNodeSelected(node);
            }

            if (!_suppressSelectionSync && !_marqueePending && !_marqueeActive && node.IsSelectableItem
                && ViewModel?.IsSuppressingSelectionUpdates != true)
            {
                SyncItemSelectionFromFocus(node);
            }

            NotifyPaneActivated();
        }
    }

    private void SyncItemSelectionFromFocus(NavigationTreeNodeViewModel node)
    {
        if (ViewModel is null)
        {
            return;
        }

        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) && ViewModel.SelectionAnchor is not null)
        {
            ViewModel.HandleShiftItemSelection(node);
            return;
        }

        ViewModel.SetSingleItemSelection(node);
    }

    private void NavigationTreeItem_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is CheckBox)
        {
            return;
        }

        var onExpander = IsClickOnExpander(e.OriginalSource as DependencyObject);
        var clickedItem = FindTreeViewItem(e.OriginalSource as DependencyObject);
        var targetNode = clickedItem?.DataContext as NavigationTreeNodeViewModel;

        // O colapso do utilizador (clique no chevron OU duplo-clique na linha) esconde o
        // ItemsHost de imediato. Intercetamos ANTES do toggle para animar a saída dos filhos.
        if ((onExpander || e.ClickCount == 2) && clickedItem is not null && targetNode is not null)
        {
            if (targetNode.Kind == NavigationNodeKind.File)
            {
                e.Handled = true;
                OpenFileNode(clickedItem, targetNode);
                return;
            }

            if (e.ClickCount == 2 &&
                targetNode.Kind is NavigationNodeKind.Ftp or NavigationNodeKind.Sftp
                    or NavigationNodeKind.Scp or NavigationNodeKind.Tftp)
            {
                e.Handled = true;
                ViewModel?.OpenNetworkClient(targetNode.Kind);
                return;
            }

            if (e.ClickCount == 2 && targetNode.IsNetworkClientProfile)
            {
                e.Handled = true;
                ViewModel?.ConnectNetworkProfile(targetNode);
                return;
            }

            if (TreeExpansionRevealAnimator.IsCollapsing(clickedItem))
            {
                // Novo clique durante o colapso: cancela a animação e mantém aberto.
                e.Handled = true;
                TreeExpansionRevealAnimator.CancelCollapse(clickedItem);
                return;
            }

            if (targetNode.IsExpandable && targetNode.IsExpanded && !_syncingAccordion)
            {
                e.Handled = true;
                BeginAnimatedCollapse(clickedItem, targetNode);
                return;
            }
        }

        if (onExpander)
        {
            return;
        }

        NotifyPaneActivated();

        if (sender is not TreeViewItem item ||
            !ReferenceEquals(clickedItem, item) ||
            item.DataContext is not NavigationTreeNodeViewModel node)
        {
            return;
        }

        if (e.Handled)
        {
            return;
        }

        e.Handled = true;

        if (!IsClickOnItemContent(e.OriginalSource as DependencyObject, item))
        {
            return;
        }

        _marqueeClickItem = item;
        _marqueeClickNode = node;

        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) && node.IsSelectableItem)
        {
            _suppressSelectionSync = true;
            try
            {
                ViewModel?.HandleShiftItemSelection(node);
                SelectExpandedNode(item, node);
            }
            finally
            {
                _suppressSelectionSync = false;
            }

            _marqueeClickItem = null;
            _marqueeClickNode = null;
            return;
        }

        BeginItemDragTracking(e.GetPosition(this), item, node);
    }

    private void TreeHost_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left || e.ClickCount > 1)
        {
            return;
        }

        if (IsClickOnExpander(e.OriginalSource as DependencyObject))
        {
            return;
        }

        var hostPoint = e.GetPosition(TreeHost);
        if (!TryGetMarqueeGutterNodeAtHost(hostPoint, out var node))
        {
            return;
        }

        NotifyPaneActivated();
        e.Handled = true;
        BeginMarqueeTracking(hostPoint, node);
        _marqueeClickItem = null;
        _marqueeClickNode = null;
    }

    private bool TryGetMarqueeGutterNodeAtHost(Point hostPoint, out NavigationTreeNodeViewModel node)
    {
        node = null!;

        if (FindSelectableNodeAtHostY(hostPoint.Y) is not NavigationTreeNodeViewModel candidate ||
            !candidate.IsSelectableItem)
        {
            return false;
        }

        if (FindTreeViewItemForNode(NavigationTree, candidate) is not TreeViewItem item)
        {
            return false;
        }

        if (item.Template?.FindName("ItemBorder", item) is not FrameworkElement itemBorder)
        {
            return false;
        }

        var rowBounds = GetItemHeaderBoundsInHost(item);
        if (hostPoint.Y < rowBounds.Top - 0.5 || hostPoint.Y > rowBounds.Bottom + 0.5)
        {
            return false;
        }

        var contentLeft = itemBorder.TransformToAncestor(TreeHost).Transform(new Point(0, 0)).X;
        if (hostPoint.X >= contentLeft - 0.5)
        {
            return false;
        }

        node = candidate;
        return true;
    }

    private void HandlePendingItemClick(TreeViewItem item, NavigationTreeNodeViewModel node)
    {
        if (node.IsExpandable)
        {
            if (node.IsExpanded)
            {
                if (!item.IsExpanded)
                {
                    _expansionFromClick = true;
                    item.IsExpanded = true;
                }

                SelectExpandedNode(item, node);
                return;
            }

            ExpandNode(item, node);
            return;
        }

        SelectExpandedNode(item, node);
    }

    private void BeginItemDragTracking(Point startPoint, TreeViewItem item, NavigationTreeNodeViewModel node)
    {
        CancelItemDragTracking();

        _itemDragPending = true;
        _itemDragStart = startPoint;
        _itemDragClickItem = item;
        _itemDragClickNode = node;

        Mouse.AddPreviewMouseMoveHandler(this, OnItemDragPreviewMouseMove);
        Mouse.AddPreviewMouseUpHandler(this, OnItemDragPreviewMouseUp);
        CaptureMouse();
    }

    private void OnItemDragPreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (!_itemDragPending || _itemDragClickNode is null || _itemDragClickItem is null)
        {
            return;
        }

        if (e.LeftButton != MouseButtonState.Pressed)
        {
            return;
        }

        var current = e.GetPosition(this);
        if (!ExceedsDragThreshold(_itemDragStart, current))
        {
            return;
        }

        var node = _itemDragClickNode;
        var item = _itemDragClickItem;

        if (!node.IsSelectableItem ||
            node.Kind is not (NavigationNodeKind.File or NavigationNodeKind.Directory or NavigationNodeKind.Archive))
        {
            FinishItemDragAsClick(item, node);
            return;
        }

        if (!node.IsItemSelected)
        {
            _suppressSelectionSync = true;
            try
            {
                ViewModel?.SetSingleItemSelection(node);
                SelectExpandedNode(item, node);
            }
            finally
            {
                _suppressSelectionSync = false;
            }
        }

        var paths = ViewModel?.GetLocalTransferableItemPaths() ?? [];
        if (paths.Count == 0)
        {
            FinishItemDragAsClick(item, node);
            return;
        }

        EndItemDragTracking(keepClickState: false);
        StartFileDragDrop(paths);
    }

    private void OnItemDragPreviewMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left || !_itemDragPending)
        {
            return;
        }

        var item = _itemDragClickItem;
        var node = _itemDragClickNode;
        EndItemDragTracking(keepClickState: false);

        if (item is not null && node is not null)
        {
            HandlePendingItemClick(item, node);
        }

        e.Handled = true;
    }

    private void FinishItemDragAsClick(TreeViewItem item, NavigationTreeNodeViewModel node)
    {
        EndItemDragTracking(keepClickState: false);
        HandlePendingItemClick(item, node);
    }

    private void EndItemDragTracking(bool keepClickState)
    {
        _itemDragPending = false;
        if (!keepClickState)
        {
            _itemDragClickItem = null;
            _itemDragClickNode = null;
            _marqueeClickItem = null;
            _marqueeClickNode = null;
        }

        Mouse.RemovePreviewMouseMoveHandler(this, OnItemDragPreviewMouseMove);
        Mouse.RemovePreviewMouseUpHandler(this, OnItemDragPreviewMouseUp);

        if (IsMouseCaptured)
        {
            ReleaseMouseCapture();
        }
    }

    private void CancelItemDragTracking()
    {
        if (!_itemDragPending && _itemDragClickItem is null)
        {
            return;
        }

        EndItemDragTracking(keepClickState: false);
    }

    private void StartFileDragDrop(IReadOnlyList<string> paths)
    {
        if (ViewModel is null || paths.Count == 0)
        {
            return;
        }

        var pathArray = paths.ToArray();
        var data = new DataObject();
        data.SetData(DataFormats.FileDrop, pathArray);
        data.SetData(InternalDragFormat.Name, ViewModel);

        var main = GetMainViewModel();
        if (main is not null)
        {
            main.InternalDropHandled = false;
        }

        DragDropEffects result;
        try
        {
            result = DragDrop.DoDragDrop(this, data, DragDropEffects.Copy | DragDropEffects.Move);
        }
        catch (Exception ex)
        {
            AppLogService.Error("Falha no drag-and-drop", ex);
            return;
        }

        if (main is null)
        {
            return;
        }

        // Drop externo (Explorer): o shell já moveu/copiou; refrescar se foi move.
        if (!main.InternalDropHandled && result == DragDropEffects.Move)
        {
            main.LeftPane.RefreshPane();
            main.RightPane.RefreshPane();
        }

        main.InternalDropHandled = false;
    }

    private void TreeHost_DragOver(object sender, DragEventArgs e)
    {
        if (!TryGetDropTarget(e, out _, out var effects))
        {
            e.Effects = DragDropEffects.None;
            e.Handled = true;
            return;
        }

        e.Effects = effects;
        e.Handled = true;
    }

    private void TreeHost_Drop(object sender, DragEventArgs e)
    {
        e.Handled = true;

        if (ViewModel is null || GetMainViewModel() is not { } main)
        {
            e.Effects = DragDropEffects.None;
            return;
        }

        if (!TryGetDropTarget(e, out var destinationDirectory, out var effects) ||
            effects == DragDropEffects.None)
        {
            e.Effects = DragDropEffects.None;
            return;
        }

        if (!TryGetDropSourcePaths(e, out var sourcePaths, out var sourcePane))
        {
            e.Effects = DragDropEffects.None;
            return;
        }

        var move = effects == DragDropEffects.Move;
        main.InternalDropHandled = true;
        NotifyPaneActivated();

        if (!main.TryValidateTransfer(sourcePaths, destinationDirectory, out var error))
        {
            e.Effects = DragDropEffects.None;
            if (!string.IsNullOrWhiteSpace(error))
            {
                ShowTransferMessage(error);
            }

            return;
        }

        // A transferência corre em segundo plano, com janela de progresso: soltar não trava mais a tela.
        e.Effects = effects;
        _ = CompleteDropAsync(main, sourcePane, sourcePaths, ViewModel, destinationDirectory, move);
    }

    private async Task CompleteDropAsync(
        MainWindowViewModel main,
        FilePaneViewModel? sourcePane,
        IReadOnlyList<string> sourcePaths,
        FilePaneViewModel destinationPane,
        string destinationDirectory,
        bool move)
    {
        try
        {
            var outcome = await main.ExecuteDropTransferAsync(
                    sourcePane, sourcePaths, destinationPane, destinationDirectory, move)
                .ConfigureAwait(true);
            ShowTransferFailure(outcome, "Não foi possível concluir a transferência.");
        }
        catch (Exception ex)
        {
            AppLogService.Error("Falha ao concluir o arrastar e soltar", ex);
            ShowTransferMessage("Não foi possível concluir a transferência.");
        }
    }

    private static void OnQueryContinueDrag(object sender, QueryContinueDragEventArgs e)
    {
        if (e.EscapePressed)
        {
            e.Action = DragAction.Cancel;
        }
    }

    private bool TryGetDropTarget(
        DragEventArgs e,
        out string destinationDirectory,
        out DragDropEffects effects)
    {
        destinationDirectory = string.Empty;
        effects = DragDropEffects.None;

        if (ViewModel is null)
        {
            return false;
        }

        if (!e.Data.GetDataPresent(DataFormats.FileDrop) &&
            !e.Data.GetDataPresent(InternalDragFormat.Name))
        {
            return false;
        }

        if (!TryGetDropSourcePaths(e, out var sourcePaths, out _))
        {
            return false;
        }

        var hostPoint = e.GetPosition(TreeHost);
        var hoverNode = FindSelectableNodeAtHostY(hostPoint.Y);

        if (!ViewModel.TryResolveDropDirectory(hoverNode, out destinationDirectory, out _))
        {
            return false;
        }

        if (GetMainViewModel() is not { } main ||
            !main.TryValidateTransfer(sourcePaths, destinationDirectory, out _))
        {
            return false;
        }

        effects = ResolveDropEffects(e, sourcePaths, destinationDirectory);
        return effects != DragDropEffects.None;
    }

    private static DragDropEffects ResolveDropEffects(
        DragEventArgs e,
        IReadOnlyList<string> sourcePaths,
        string destinationDirectory)
    {
        var allowed = e.AllowedEffects & (DragDropEffects.Copy | DragDropEffects.Move);
        if (allowed == DragDropEffects.None)
        {
            return DragDropEffects.None;
        }

        DragDropEffects preferred;
        if ((e.KeyStates & DragDropKeyStates.ControlKey) != 0)
        {
            preferred = DragDropEffects.Copy;
        }
        else if ((e.KeyStates & DragDropKeyStates.ShiftKey) != 0)
        {
            preferred = DragDropEffects.Move;
        }
        else
        {
            preferred = IsSameVolumeMove(sourcePaths, destinationDirectory)
                ? DragDropEffects.Move
                : DragDropEffects.Copy;
        }

        if ((allowed & preferred) != 0)
        {
            return preferred;
        }

        if ((allowed & DragDropEffects.Copy) != 0)
        {
            return DragDropEffects.Copy;
        }

        if ((allowed & DragDropEffects.Move) != 0)
        {
            return DragDropEffects.Move;
        }

        return DragDropEffects.None;
    }

    private static bool IsSameVolumeMove(IReadOnlyList<string> sourcePaths, string destinationDirectory)
    {
        if (sourcePaths.Count == 0)
        {
            return false;
        }

        try
        {
            var destRoot = System.IO.Path.GetPathRoot(System.IO.Path.GetFullPath(destinationDirectory));
            var sourceRoot = System.IO.Path.GetPathRoot(System.IO.Path.GetFullPath(sourcePaths[0]));
            return !string.IsNullOrEmpty(destRoot) &&
                   !string.IsNullOrEmpty(sourceRoot) &&
                   string.Equals(destRoot, sourceRoot, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ExpectedFailures.IsFileSystem(ex))
        {
            return false;
        }
    }

    private bool TryGetDropSourcePaths(
        DragEventArgs e,
        out IReadOnlyList<string> sourcePaths,
        out FilePaneViewModel? sourcePane)
    {
        sourcePaths = [];
        sourcePane = null;

        if (e.Data.GetDataPresent(InternalDragFormat.Name))
        {
            sourcePane = e.Data.GetData(InternalDragFormat.Name) as FilePaneViewModel;
        }

        if (!e.Data.GetDataPresent(DataFormats.FileDrop) ||
            e.Data.GetData(DataFormats.FileDrop) is not string[] dropped ||
            dropped.Length == 0)
        {
            return false;
        }

        sourcePaths = dropped
            .Where(FilePaneViewModel.IsLocalFileSystemPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        return sourcePaths.Count > 0;
    }

    private void TreeHost_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (!TryGetTreeItemContext(e.OriginalSource as DependencyObject, out var item, out var node))
        {
            if (IsTreeBackgroundClick(e.OriginalSource as DependencyObject))
            {
                NotifyPaneActivated();
            }

            return;
        }

        NotifyPaneActivated();

        _suppressSelectionSync = true;
        try
        {
            if (!node.IsItemSelected)
            {
                ViewModel?.SetSingleItemSelection(node);
            }

            SelectExpandedNode(item, node);
        }
        finally
        {
            _suppressSelectionSync = false;
        }
    }

    private void TreeHost_PreviewMouseRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (TryGetTreeItemContext(e.OriginalSource as DependencyObject, out var item, out var node))
        {
            if (_activeTreeContextMenu is not null)
            {
                _activeTreeContextMenu.IsOpen = false;
                _activeTreeContextMenu = null;
            }

            _activeTreeContextMenu = BuildTreeItemContextMenu(node);
            _activeTreeContextMenu.Closed += (_, _) => _activeTreeContextMenu = null;
            _activeTreeContextMenu.PlacementTarget = item;
            _activeTreeContextMenu.Placement = PlacementMode.MousePoint;
            _activeTreeContextMenu.IsOpen = true;
            e.Handled = true;
            return;
        }

        if (IsTreeBackgroundClick(e.OriginalSource as DependencyObject))
        {
            NotifyPaneActivated();
            ShowTreeBackgroundContextMenu();
            e.Handled = true;
        }
    }

    private bool TryGetTreeItemContext(
        DependencyObject? source,
        out TreeViewItem item,
        out NavigationTreeNodeViewModel node)
    {
        item = null!;
        node = null!;

        if (source is CheckBox)
        {
            return false;
        }

        if (IsClickOnExpander(source))
        {
            return false;
        }

        if (HasDedicatedContextMenu(source))
        {
            return false;
        }

        if (FindTreeViewItem(source) is not TreeViewItem treeViewItem ||
            treeViewItem.DataContext is not NavigationTreeNodeViewModel viewModel ||
            !viewModel.IsSelectableItem)
        {
            return false;
        }

        item = treeViewItem;
        node = viewModel;
        return true;
    }

    private ContextMenu BuildTreeItemContextMenu(NavigationTreeNodeViewModel node)
    {
        if (node.IsRecycleBinEntry)
        {
            return BuildRecycleBinItemContextMenu();
        }

        if (node.IsNetworkClientProfile)
        {
            return BuildNetworkClientProfileContextMenu(node);
        }

        var menu = new ContextMenu();
        var insideArchive = !string.IsNullOrWhiteSpace(node.TargetPath) &&
                            ArchivePathHelper.IsVirtualArchivePath(node.TargetPath);
        var isArchiveNode = node.Kind == NavigationNodeKind.Archive;
        var insideReadOnlyArchive = insideArchive &&
                        ArchivePathHelper.TrySplit(node.TargetPath!, out var readOnlyArchive, out _) &&
                        ArchivePathHelper.TryGetFormat(readOnlyArchive, out var readOnlyFormat) &&
                        !ArchivePathHelper.SupportsRewrite(readOnlyFormat);

        if (!insideArchive && !isArchiveNode)
        {
            AddNewFolderMenuItem(menu);
        }

        AddRefreshMenuItem(menu);

        var hasOpenableFiles = ViewModel?.HasOpenableFilesInSelection() == true;
        var addedSecondaryItems = false;

        if (isArchiveNode)
        {
            menu.Items.Add(new Separator());
            var openExternal = new MenuItem { Header = "Abrir externamente" };
            openExternal.Click += (_, _) =>
            {
                if (!string.IsNullOrWhiteSpace(node.TargetPath))
                {
                    ViewModel?.OpenArchiveExternally(node.TargetPath);
                }
            };
            menu.Items.Add(openExternal);
            addedSecondaryItems = true;
        }
        else if (node.IsFile || hasOpenableFiles)
        {
            menu.Items.Add(new Separator());
            var openItem = new MenuItem { Header = "Abrir" };
            openItem.Click += (_, _) => ViewModel?.OpenSelectedFiles();
            menu.Items.Add(openItem);
            addedSecondaryItems = true;
        }

        if (ViewModel?.GetTransferableItemPaths().Count > 0)
        {
            if (!addedSecondaryItems)
            {
                menu.Items.Add(new Separator());
            }

            var copyItem = new MenuItem { Header = "Copiar" };
            copyItem.Click += (_, _) => ExecuteImmediateCopy();
            menu.Items.Add(copyItem);

            var moveItem = new MenuItem
            {
                Header = insideReadOnlyArchive ? "Mover (copia)" : "Mover"
            };
            moveItem.Click += (_, _) => ExecuteImmediateMove();
            menu.Items.Add(moveItem);
            addedSecondaryItems = true;
        }

        if (!insideArchive &&
            !isArchiveNode &&
            GetMainViewModel()?.HasTransferClipboard == true)
        {
            if (!addedSecondaryItems)
            {
                menu.Items.Add(new Separator());
            }

            var pasteItem = new MenuItem { Header = "Colar" };
            pasteItem.Click += (_, _) => ExecutePaste();
            menu.Items.Add(pasteItem);
            addedSecondaryItems = true;
        }

        if (!node.IsFile)
        {
            MenuItem? folderAction = null;
            if (node.IsExpanded)
            {
                folderAction = new MenuItem { Header = "Recolher" };
                folderAction.Click += (_, _) => CollapseFolderFromContextMenu(node);
            }
            else if (node.IsExpandable)
            {
                folderAction = new MenuItem { Header = "Expandir" };
                folderAction.Click += (_, _) => ExpandFolderFromContextMenu(node);
            }

            if (folderAction is not null)
            {
                if (!addedSecondaryItems)
                {
                    menu.Items.Add(new Separator());
                }

                menu.Items.Add(folderAction);
                addedSecondaryItems = true;
            }
        }

        if (ViewModel?.GetDeletableItemPaths().Count > 0)
        {
            if (!addedSecondaryItems)
            {
                menu.Items.Add(new Separator());
            }

            var deleteItem = new MenuItem { Header = "Excluir" };
            deleteItem.Click += (_, _) => PromptDeleteFromContextMenu();
            menu.Items.Add(deleteItem);
        }
        else if (insideReadOnlyArchive && ViewModel?.GetTransferableItemPaths().Count > 0)
        {
            if (!addedSecondaryItems)
            {
                menu.Items.Add(new Separator());
            }

            var deleteItem = new MenuItem
            {
                Header = "Excluir",
                IsEnabled = false,
                ToolTip = "Não é possível excluir entradas de arquivos .rar."
            };
            menu.Items.Add(deleteItem);
        }

        return menu;
    }

    private ContextMenu BuildNetworkClientProfileContextMenu(NavigationTreeNodeViewModel node)
    {
        var menu = new ContextMenu();

        var connectItem = new MenuItem { Header = "Conectar" };
        connectItem.Click += (_, _) => ViewModel?.ConnectNetworkProfile(node);
        menu.Items.Add(connectItem);

        var editItem = new MenuItem { Header = "Editar" };
        editItem.Click += (_, _) => ViewModel?.EditNetworkProfile(node);
        menu.Items.Add(editItem);

        var deleteItem = new MenuItem { Header = "Excluir" };
        deleteItem.Click += (_, _) => ViewModel?.DeleteNetworkProfile(node);
        menu.Items.Add(deleteItem);

        var testItem = new MenuItem { Header = "Testar" };
        testItem.Click += (_, _) => ViewModel?.TestNetworkProfile(node);
        menu.Items.Add(testItem);

        return menu;
    }

    private ContextMenu BuildRecycleBinItemContextMenu()
    {
        var menu = new ContextMenu();
        AddRefreshMenuItem(menu);

        menu.Items.Add(new Separator());

        var restoreItem = new MenuItem { Header = "Restaurar" };
        restoreItem.Click += (_, _) => RestoreFromRecycleBinContextMenu();
        menu.Items.Add(restoreItem);

        var moveItem = new MenuItem { Header = "Mover", IsEnabled = false };
        menu.Items.Add(moveItem);

        var deleteItem = new MenuItem { Header = "Excluir permanentemente" };
        deleteItem.Click += (_, _) => PermanentlyDeleteFromRecycleBinContextMenu();
        menu.Items.Add(deleteItem);

        return menu;
    }

    private void RestoreFromRecycleBinContextMenu()
    {
        if (ViewModel is null)
        {
            return;
        }

        if (!ViewModel.TryRestoreRecycleBinItems(out var error) &&
            !string.IsNullOrWhiteSpace(error))
        {
            MessageDialog.Show(Application.Current.MainWindow, error);
        }
    }

    private void PermanentlyDeleteFromRecycleBinContextMenu()
    {
        if (ViewModel is null)
        {
            return;
        }

        var paths = ViewModel.GetRecycleBinItemPaths();
        if (paths.Count == 0)
        {
            return;
        }

        var owner = Application.Current.MainWindow;
        var message = paths.Count == 1
            ? $"Excluir permanentemente \"{System.IO.Path.GetFileName(paths[0])}\"?\n\nEsta ação não pode ser desfeita."
            : $"Excluir permanentemente {paths.Count} itens?\n\nEsta ação não pode ser desfeita.";

        if (!MessageDialog.Confirm(owner, message, "Excluir permanentemente"))
        {
            return;
        }

        if (!ViewModel.TryPermanentlyDeleteRecycleBinItems(out var error) &&
            !string.IsNullOrWhiteSpace(error))
        {
            MessageDialog.Show(owner, error);
        }
    }

    private void PromptDeleteFromContextMenu()
    {
        if (ViewModel is null)
        {
            return;
        }

        var paths = ViewModel.GetDeletableItemPaths();
        if (paths.Count == 0)
        {
            return;
        }

        var owner = Application.Current.MainWindow;
        var mode = DeleteChoiceDialog.TryChoose(owner, paths);
        if (mode is null)
        {
            return;
        }

        if (!ViewModel.TryDeleteItems(paths, mode.Value, out var error) &&
            !string.IsNullOrWhiteSpace(error))
        {
            MessageDialog.Show(owner, error, "Excluir");
        }
    }

    private void AddNewFolderMenuItem(ContextMenu menu)
    {
        var newFolderItem = new MenuItem { Header = "Nova pasta" };
        newFolderItem.Click += (_, _) => CreateNewFolderFromContextMenu();
        menu.Items.Add(newFolderItem);
    }

    private void AddRefreshMenuItem(ContextMenu menu)
    {
        var refreshItem = new MenuItem { Header = "Atualizar", InputGestureText = "F5" };
        refreshItem.Click += (_, _) => RefreshFromContextMenu();
        menu.Items.Add(refreshItem);
    }

    private void RefreshFromContextMenu()
    {
        if (ViewModel is null)
        {
            return;
        }

        if (ViewModel.SelectedItems.Count > 0)
        {
            var target = ViewModel.GetPrimarySelectedNode();
            if (target is not null)
            {
                ViewModel.RefreshNode(target);
                return;
            }
        }

        ViewModel.RefreshPane();
    }

    private void CreateNewFolderFromContextMenu()
    {
        if (ViewModel is null)
        {
            return;
        }

        if (!ViewModel.TryCreateFolderWithInlineRename(out var error))
        {
            MessageDialog.Show(
                Application.Current.MainWindow,
                error ?? "Não foi possível criar a pasta.");
        }
    }

    private void ShowTreeBackgroundContextMenu()
    {
        if (_activeTreeContextMenu is not null)
        {
            _activeTreeContextMenu.IsOpen = false;
            _activeTreeContextMenu = null;
        }

        _activeTreeContextMenu = new ContextMenu();
        AddNewFolderMenuItem(_activeTreeContextMenu);
        AddRefreshMenuItem(_activeTreeContextMenu);

        if (GetMainViewModel()?.HasTransferClipboard == true)
        {
            _activeTreeContextMenu.Items.Add(new Separator());
            var pasteItem = new MenuItem { Header = "Colar" };
            pasteItem.Click += (_, _) => ExecutePaste();
            _activeTreeContextMenu.Items.Add(pasteItem);
        }
        _activeTreeContextMenu.Closed += (_, _) => _activeTreeContextMenu = null;
        _activeTreeContextMenu.PlacementTarget = TreeHost;
        _activeTreeContextMenu.Placement = PlacementMode.MousePoint;
        _activeTreeContextMenu.IsOpen = true;
    }

    private static bool IsTreeBackgroundClick(DependencyObject? source)
    {
        if (source is null || source is CheckBox)
        {
            return false;
        }

        if (HasDedicatedContextMenu(source))
        {
            return false;
        }

        var current = source;
        while (current is not null)
        {
            if (current is TreeViewItem or ToggleButton)
            {
                return false;
            }

            current = VisualTreeHelper.GetParent(current);
        }

        return true;
    }

    private void ExpandFolderFromContextMenu(NavigationTreeNodeViewModel node)
    {
        if (FindTreeViewItemForNode(NavigationTree, node) is TreeViewItem item)
        {
            ExpandNode(item, node);
        }
    }

    private void CollapseFolderFromContextMenu(NavigationTreeNodeViewModel node)
    {
        if (FindTreeViewItemForNode(NavigationTree, node) is TreeViewItem item)
        {
            BeginAnimatedCollapse(item, node);
        }
    }

    private void OpenFileNode(TreeViewItem item, NavigationTreeNodeViewModel node)
    {
        _suppressSelectionSync = true;
        try
        {
            ViewModel?.SetSingleItemSelection(node);
            SelectExpandedNode(item, node);
        }
        finally
        {
            _suppressSelectionSync = false;
        }

        ViewModel?.OpenSelectedFiles();
    }

    private void BeginMarqueeTracking(Point startPoint, NavigationTreeNodeViewModel referenceNode)
    {
        CancelItemDragTracking();

        _marqueeStart = ClampMarqueePointToTreeHost(startPoint);
        _marqueeLastRawHostPoint = startPoint;
        _marqueePending = referenceNode.IsSelectableItem;
        _marqueeActive = false;
        _marqueeReferenceNode = referenceNode.IsSelectableItem ? referenceNode : null;
        _marqueeRangeEndIndex = -1;

        if (!_marqueePending)
        {
            return;
        }

        Mouse.AddPreviewMouseMoveHandler(this, OnMarqueePreviewMouseMove);
        Mouse.AddPreviewMouseUpHandler(this, OnMarqueePreviewMouseUp);
        CaptureMouse();
    }

    private void OnMarqueePreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (!_marqueePending && !_marqueeActive)
        {
            return;
        }

        if (e.LeftButton != MouseButtonState.Pressed)
        {
            return;
        }

        var rawPoint = e.GetPosition(TreeHost);
        _marqueeLastRawHostPoint = rawPoint;
        var clampedPoint = ClampMarqueePointToTreeHost(rawPoint);

        if (!_marqueeActive)
        {
            if (!ExceedsDragThreshold(_marqueeStart, clampedPoint))
            {
                return;
            }

            if (_marqueeReferenceNode is null)
            {
                CancelMarqueeTracking();
                return;
            }

            StartMarquee();
        }

        UpdateMarqueeAutoScroll(rawPoint);
        UpdateMarqueeVisual(clampedPoint);
        UpdateMarqueeSelection(clampedPoint, rawPoint);
        UpdateMarqueeAutoScrollTimer(rawPoint);
        e.Handled = true;
    }

    private void OnMarqueePreviewMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left)
        {
            return;
        }

        if (_marqueeActive)
        {
            var rawPoint = e.GetPosition(TreeHost);
            var clampedPoint = ClampMarqueePointToTreeHost(rawPoint);
            UpdateMarqueeSelection(clampedPoint, rawPoint);
            EndMarquee();
            e.Handled = true;
            return;
        }

        if (_marqueePending && _marqueeClickItem is not null && _marqueeClickNode is not null)
        {
            HandlePendingItemClick(_marqueeClickItem, _marqueeClickNode);
            e.Handled = true;
        }

        CancelMarqueeTracking();
    }

    private void StartMarquee()
    {
        _marqueeActive = true;
        _marqueePending = false;
        SyncMarqueeCanvasSize();
        MarqueeCanvas.Visibility = Visibility.Visible;

        _marqueeRectangle = new Rectangle
        {
            Stroke = (Brush)FindResource("TreeFocusBorderBrush"),
            StrokeThickness = 1,
            Fill = new SolidColorBrush(Color.FromArgb(40, 0, 120, 215)),
            IsHitTestVisible = false
        };
        MarqueeCanvas.Children.Add(_marqueeRectangle);

        if (_marqueeReferenceNode is not null && ViewModel is not null)
        {
            var siblings = ViewModel.GetSelectableSiblings(_marqueeReferenceNode).ToList();
            _marqueeRangeEndIndex = siblings.FindIndex(n => ReferenceEquals(n, _marqueeReferenceNode));
        }
    }

    private void UpdateMarqueeVisual(Point clampedPoint)
    {
        if (_marqueeRectangle is null)
        {
            return;
        }

        var bounds = GetTreeHostBoundsSize();
        var rect = ClampRectToBounds(MakeRect(_marqueeStart, clampedPoint), bounds);
        Canvas.SetLeft(_marqueeRectangle, rect.X);
        Canvas.SetTop(_marqueeRectangle, rect.Y);
        _marqueeRectangle.Width = rect.Width;
        _marqueeRectangle.Height = rect.Height;
    }

    private void UpdateMarqueeSelection(Point clampedPoint, Point rawHostPoint)
    {
        if (_marqueeReferenceNode is null || ViewModel is null)
        {
            return;
        }

        var marqueeRect = ClampRectToBounds(MakeRect(_marqueeStart, clampedPoint), GetTreeHostBoundsSize());
        var siblings = ViewModel.GetSelectableSiblings(_marqueeReferenceNode).ToList();
        var selected = new HashSet<NavigationTreeNodeViewModel>();

        foreach (var sibling in siblings)
        {
            if (FindTreeViewItemForNode(NavigationTree, sibling) is not TreeViewItem treeViewItem)
            {
                continue;
            }

            var bounds = GetItemHeaderBoundsInHost(treeViewItem);
            if (marqueeRect.IntersectsWith(bounds))
            {
                selected.Add(sibling);
            }
        }

        var anchorIndex = siblings.FindIndex(n => ReferenceEquals(n, _marqueeReferenceNode));
        if (anchorIndex >= 0)
        {
            var endIndex = FindSiblingIndexAtY(siblings, clampedPoint.Y);
            if (endIndex < 0)
            {
                endIndex = _marqueeRangeEndIndex >= 0 ? _marqueeRangeEndIndex : anchorIndex;
            }
            else if (IsMarqueeAutoScrollZone(rawHostPoint, out var scrollDirection))
            {
                if (scrollDirection < 0)
                {
                    _marqueeRangeEndIndex = _marqueeRangeEndIndex < 0
                        ? endIndex
                        : Math.Min(_marqueeRangeEndIndex, endIndex);
                }
                else
                {
                    _marqueeRangeEndIndex = Math.Max(_marqueeRangeEndIndex, endIndex);
                }

                endIndex = _marqueeRangeEndIndex;
            }
            else
            {
                _marqueeRangeEndIndex = endIndex;
            }

            var from = Math.Min(anchorIndex, endIndex);
            var to = Math.Max(anchorIndex, endIndex);
            for (var index = from; index <= to; index++)
            {
                selected.Add(siblings[index]);
            }
        }

        if (selected.Count == 0)
        {
            selected.Add(_marqueeReferenceNode);
        }

        _suppressSelectionSync = true;
        try
        {
            ViewModel.SelectSiblingItems(selected);
        }
        finally
        {
            _suppressSelectionSync = false;
        }
    }

    private int FindSiblingIndexAtY(IReadOnlyList<NavigationTreeNodeViewModel> siblings, double y)
    {
        var bestIndex = -1;
        var bestDistance = double.MaxValue;

        for (var index = 0; index < siblings.Count; index++)
        {
            if (FindTreeViewItemForNode(NavigationTree, siblings[index]) is not TreeViewItem treeViewItem)
            {
                continue;
            }

            var bounds = GetItemHeaderBoundsInHost(treeViewItem);
            if (y >= bounds.Top && y <= bounds.Bottom)
            {
                return index;
            }

            var distance = y < bounds.Top ? bounds.Top - y : y - bounds.Bottom;
            if (distance < bestDistance)
            {
                bestDistance = distance;
                bestIndex = index;
            }
        }

        return bestIndex;
    }

    private void EndMarquee()
    {
        _marqueeActive = false;
        _marqueePending = false;
        _marqueeRangeEndIndex = -1;
        StopMarqueeAutoScrollTimer();

        MarqueeCanvas.Children.Clear();
        MarqueeCanvas.Visibility = Visibility.Collapsed;
        _marqueeRectangle = null;

        Mouse.RemovePreviewMouseMoveHandler(this, OnMarqueePreviewMouseMove);
        Mouse.RemovePreviewMouseUpHandler(this, OnMarqueePreviewMouseUp);
        if (IsMouseCaptured)
        {
            ReleaseMouseCapture();
        }

        if (ViewModel?.SelectedItems.LastOrDefault() is NavigationTreeNodeViewModel last &&
            FindTreeViewItemForNode(NavigationTree, last) is TreeViewItem item)
        {
            _suppressSelectionSync = true;
            try
            {
                item.IsSelected = true;
                item.Focus();
            }
            finally
            {
                _suppressSelectionSync = false;
            }
        }

        _marqueeReferenceNode = null;
        _marqueeClickItem = null;
        _marqueeClickNode = null;
    }

    private void CancelMarqueeTracking()
    {
        _marqueePending = false;
        _marqueeReferenceNode = null;
        _marqueeClickItem = null;
        _marqueeClickNode = null;
        _marqueeRangeEndIndex = -1;
        StopMarqueeAutoScrollTimer();

        Mouse.RemovePreviewMouseMoveHandler(this, OnMarqueePreviewMouseMove);
        Mouse.RemovePreviewMouseUpHandler(this, OnMarqueePreviewMouseUp);

        if (IsMouseCaptured)
        {
            ReleaseMouseCapture();
        }
    }

    private void SyncMarqueeCanvasSize()
    {
        MarqueeCanvas.Width = TreeHost.ActualWidth;
        MarqueeCanvas.Height = TreeHost.ActualHeight;
    }

    private Size GetTreeHostBoundsSize() =>
        new(Math.Max(TreeHost.ActualWidth, 0), Math.Max(TreeHost.ActualHeight, 0));

    private Point ClampMarqueePointToTreeHost(Point point)
    {
        var bounds = GetTreeHostBoundsSize();
        return new Point(
            Math.Clamp(point.X, 0, bounds.Width),
            Math.Clamp(point.Y, 0, bounds.Height));
    }

    private static Rect ClampRectToBounds(Rect rect, Size bounds)
    {
        if (bounds.Width <= 0 || bounds.Height <= 0)
        {
            return Rect.Empty;
        }

        var left = Math.Clamp(rect.Left, 0, bounds.Width);
        var top = Math.Clamp(rect.Top, 0, bounds.Height);
        var right = Math.Clamp(rect.Right, 0, bounds.Width);
        var bottom = Math.Clamp(rect.Bottom, 0, bounds.Height);
        return new Rect(left, top, Math.Max(0, right - left), Math.Max(0, bottom - top));
    }

    private bool IsMarqueeAutoScrollZone(Point rawHostPoint, out int direction)
    {
        direction = 0;
        var height = TreeHost.ActualHeight;
        if (height <= 0)
        {
            return false;
        }

        if (rawHostPoint.Y < MarqueeAutoScrollMargin)
        {
            direction = -1;
            return true;
        }

        if (rawHostPoint.Y > height - MarqueeAutoScrollMargin)
        {
            direction = 1;
            return true;
        }

        return false;
    }

    private void UpdateMarqueeAutoScroll(Point rawHostPoint)
    {
        if (!IsMarqueeAutoScrollZone(rawHostPoint, out var direction))
        {
            return;
        }

        var scrollViewer = _treeScrollViewer ?? FindScrollViewer(NavigationTree);
        if (scrollViewer is null || scrollViewer.ScrollableHeight <= 0)
        {
            return;
        }

        var height = TreeHost.ActualHeight;
        var intensity = direction < 0
            ? (MarqueeAutoScrollMargin - Math.Min(rawHostPoint.Y, MarqueeAutoScrollMargin)) / MarqueeAutoScrollMargin
            : (Math.Max(rawHostPoint.Y, height - MarqueeAutoScrollMargin) - (height - MarqueeAutoScrollMargin)) /
              MarqueeAutoScrollMargin;
        intensity = Math.Clamp(intensity, 0.25, 1.0);

        var delta = direction * MarqueeAutoScrollStep * intensity;
        var newOffset = Math.Clamp(scrollViewer.VerticalOffset + delta, 0, scrollViewer.ScrollableHeight);
        if (Math.Abs(newOffset - scrollViewer.VerticalOffset) < 0.01)
        {
            return;
        }

        scrollViewer.ScrollToVerticalOffset(newOffset);
        SmoothScrollAnimator.SyncTarget(scrollViewer, newOffset);
    }

    private void EnsureMarqueeAutoScrollTimer()
    {
        if (_marqueeAutoScrollTimer is not null)
        {
            return;
        }

        _marqueeAutoScrollTimer = new DispatcherTimer(
            TimeSpan.FromMilliseconds(1000.0 / AnimationFrameRateService.Rate),
            DispatcherPriority.Input,
            OnMarqueeAutoScrollTick,
            Dispatcher);
    }

    private void UpdateMarqueeAutoScrollTimer(Point rawHostPoint)
    {
        if (!_marqueeActive)
        {
            StopMarqueeAutoScrollTimer();
            return;
        }

        if (IsMarqueeAutoScrollZone(rawHostPoint, out _))
        {
            EnsureMarqueeAutoScrollTimer();
            _marqueeAutoScrollTimer!.Start();
            return;
        }

        StopMarqueeAutoScrollTimer();
    }

    private void OnMarqueeAutoScrollTick(object? sender, EventArgs e)
    {
        if (!_marqueeActive)
        {
            StopMarqueeAutoScrollTimer();
            return;
        }

        UpdateMarqueeAutoScroll(_marqueeLastRawHostPoint);
        var clampedPoint = ClampMarqueePointToTreeHost(_marqueeLastRawHostPoint);
        UpdateMarqueeVisual(clampedPoint);
        UpdateMarqueeSelection(clampedPoint, _marqueeLastRawHostPoint);
    }

    private void StopMarqueeAutoScrollTimer() => _marqueeAutoScrollTimer?.Stop();

    private static bool ExceedsDragThreshold(Point start, Point current)
    {
        var dx = Math.Abs(current.X - start.X);
        var dy = Math.Abs(current.Y - start.Y);
        return dx >= SystemParameters.MinimumHorizontalDragDistance ||
               dy >= SystemParameters.MinimumVerticalDragDistance;
    }

    private static Rect MakeRect(Point start, Point end) =>
        new(
            Math.Min(start.X, end.X),
            Math.Min(start.Y, end.Y),
            Math.Abs(end.X - start.X),
            Math.Abs(end.Y - start.Y));

    private Rect GetItemHeaderBoundsInHost(TreeViewItem item)
    {
        if (item.Template?.FindName("ItemBorder", item) is FrameworkElement header)
        {
            var topLeft = header.TransformToAncestor(TreeHost).Transform(new Point(0, 0));
            return new Rect(topLeft.X, topLeft.Y, header.ActualWidth, header.ActualHeight);
        }

        var fallbackTopLeft = item.TransformToAncestor(TreeHost).Transform(new Point(0, 0));
        return new Rect(
            fallbackTopLeft.X,
            fallbackTopLeft.Y,
            item.ActualWidth,
            Math.Min(item.ActualHeight, TreeViewHierarchy.HeaderRowHeight));
    }

    private void SelectExpandedNode(TreeViewItem item, NavigationTreeNodeViewModel node)
    {
        var scrollViewer = _treeScrollViewer ?? FindScrollViewer(NavigationTree);
        _lockedTreeScrollOffset = scrollViewer?.VerticalOffset ?? 0;
        _lockTreeScroll = true;

        _suppressSelectionScroll = true;
        try
        {
            if (!item.IsSelected)
            {
                item.IsSelected = true;
            }
        }
        finally
        {
            _suppressSelectionScroll = false;
        }

        ViewModel?.OnNavigationNodeSelected(node);

        item.Focus();
        RestoreScrollOffset(scrollViewer, _lockedTreeScrollOffset);

        Dispatcher.BeginInvoke(() =>
        {
            RestoreScrollOffset(scrollViewer, _lockedTreeScrollOffset);
            _lockTreeScroll = false;
        }, DispatcherPriority.ApplicationIdle);
    }

    private void RestoreScrollOffset(ScrollViewer? scrollViewer, double offset)
    {
        if (scrollViewer is null)
        {
            return;
        }

        void Apply()
        {
            var clamped = scrollViewer.ScrollableHeight > 0
                ? Math.Clamp(offset, 0, scrollViewer.ScrollableHeight)
                : offset;
            scrollViewer.ScrollToVerticalOffset(clamped);
            SmoothScrollAnimator.SyncTarget(scrollViewer, clamped);
        }

        Apply();
        Dispatcher.BeginInvoke(Apply, DispatcherPriority.Loaded);
        Dispatcher.BeginInvoke(Apply, DispatcherPriority.Render);
    }

    private void ExpandNode(TreeViewItem item, NavigationTreeNodeViewModel node)
    {
        if (node.IsExpanded)
        {
            if (!item.IsExpanded)
            {
                _expansionFromClick = true;
                item.IsExpanded = true;
            }

            SelectExpandedNode(item, node);
            return;
        }

        ViewModel?.OnNavigationNodeExpanding(node, userInitiated: true);
        _expansionFromClick = true;
        node.IsExpanded = true;
        item.IsExpanded = true;
        ViewModel?.OnNavigationNodeExpanded(node);
        AnnounceDeniedListing(node);
        ViewModel?.OnNavigationNodeSelected(node);
        item.IsSelected = true;
        if (node.IsSelectableItem)
        {
            ViewModel?.SetSingleItemSelection(node);
        }
        SyncAccordionExpansion();
        _revealChildrenOnRefresh = true;
        RequestRefreshExpandedItem(item);
    }

    private void BeginAnimatedCollapse(TreeViewItem item, NavigationTreeNodeViewModel node)
    {
        CancelPendingExpansionRefresh();
        TreeExpansionRevealAnimator.CollapseChildren(item, () =>
        {
            _collapseFromClick = true;
            node.IsExpanded = false;
            item.IsExpanded = false;
            ViewModel?.OnNavigationNodeCollapsed(node, userInitiated: true);
            RequestHierarchyUpdate();
            _collapseFromClick = false;
            TreeExpansionRevealAnimator.Reset(item);
            ClampTreeScrollOffset();
        });
    }

    private void DisplayMenuButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button || button.ContextMenu is not ContextMenu menu)
        {
            return;
        }

        NotifyPaneActivated();
        menu.PlacementTarget = button;
        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Top;
        menu.IsOpen = true;
    }

    private void DisplayMenu_Opened(object sender, RoutedEventArgs e)
    {
        if (sender is ContextMenu menu)
        {
            menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Top;
            if (menu.PlacementTarget is null)
            {
                menu.PlacementTarget = DisplayMenuButton;
            }

            menu.DataContext = ViewModel;
            ViewModel?.BeginDisplayMenu();
        }
    }

    private void DisplayMenu_Closed(object sender, RoutedEventArgs e)
    {
        ViewModel?.CommitDisplayMenu();
    }

    private void DefaultAppButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button || button.ContextMenu is not ContextMenu menu)
        {
            return;
        }

        NotifyPaneActivated();
        menu.PlacementTarget = button;
        menu.Placement = PlacementMode.Top;
        menu.DataContext = button.DataContext;
        menu.IsOpen = true;
    }

    private void DefaultAppMenu_Opened(object sender, RoutedEventArgs e)
    {
        if (sender is not ContextMenu menu)
        {
            return;
        }

        menu.Placement = PlacementMode.Top;
        if (menu.PlacementTarget is null && menu.Parent is Button parentButton)
        {
            menu.PlacementTarget = parentButton;
        }

        if (menu.PlacementTarget is not FrameworkElement target ||
            target.DataContext is not NavigationTreeNodeViewModel node ||
            string.IsNullOrWhiteSpace(node.TargetPath))
        {
            return;
        }

        menu.Items.Clear();

        foreach (var app in FileAssociationService.GetAssociatedApplications(node.TargetPath))
        {
            var item = new MenuItem
            {
                Header = app.DisplayName,
                Tag = app,
                FontWeight = app.IsDefault ? FontWeights.SemiBold : FontWeights.Normal
            };

            if (app.Icon is not null)
            {
                item.Icon = new System.Windows.Controls.Image
                {
                    Source = app.Icon,
                    Width = 16,
                    Height = 16
                };
            }

            item.Click += DefaultAppMenuItem_Click;
            menu.Items.Add(item);
        }

        if (menu.Items.Count > 0)
        {
            menu.Items.Add(new Separator());
        }

        var extension = System.IO.Path.GetExtension(node.TargetPath);

        // Qualquer programa do computador, valendo só dentro do Dora Explorer (não mexe no Windows) e guardado.
        var browseItem = new MenuItem
        {
            Header = "Procurar aplicativo no computador…",
            ToolTip = $"Define o aplicativo dos arquivos {extension} só no Dora Explorer; a escolha fica guardada e não muda o Windows."
        };
        browseItem.Click += (_, _) =>
        {
            menu.IsOpen = false;
            BrowseDefaultApplication(node, extension);
        };
        menu.Items.Add(browseItem);

        if (FileAssociationService.HasUserOverride(node.TargetPath))
        {
            var resetItem = new MenuItem { Header = $"Voltar ao padrão do Windows para {extension}" };
            resetItem.Click += (_, _) =>
            {
                menu.IsOpen = false;
                FileAssociationService.ClearUserOverride(node.TargetPath);
            };
            menu.Items.Add(resetItem);
        }

        var manualItem = new MenuItem
        {
            Header = "Escolher no Windows…",
            ToolTip = "Abre a janela \"Abrir com\" do Windows; marcar \"Sempre\" muda o padrão do Windows."
        };
        manualItem.Click += (_, _) =>
        {
            menu.IsOpen = false;

            // Depois de o menu fechar: a janela do Windows é modal e precisa rodar na thread da interface.
            Dispatcher.BeginInvoke(() => ShowNativeOpenWithDialog(node), DispatcherPriority.Background);
        };
        menu.Items.Add(manualItem);
    }

    private static void BrowseDefaultApplication(NavigationTreeNodeViewModel node, string extension)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = $"Aplicativo para abrir arquivos {extension}",
            Filter = "Aplicativos (*.exe;*.com;*.bat;*.cmd)|*.exe;*.com;*.bat;*.cmd|Todos os arquivos (*.*)|*.*",
            CheckFileExists = true,
            InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles)
        };

        if (dialog.ShowDialog(System.Windows.Application.Current.MainWindow) != true)
        {
            return;
        }

        if (!FileAssociationService.IsLaunchable(dialog.FileName))
        {
            ShowTransferMessage("Escolha um programa (.exe, .com, .bat ou .cmd).");
            return;
        }

        node.SetDefaultApplication(FileAssociationService.GetDisplayNameForExecutable(dialog.FileName), dialog.FileName);
    }

    private void DefaultAppMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { Tag: AssociatedApplication app, Parent: ContextMenu menu } ||
            menu.PlacementTarget is not FrameworkElement target ||
            target.DataContext is not NavigationTreeNodeViewModel node)
        {
            return;
        }

        node.SetDefaultApplication(app.DisplayName, app.ExecutablePath);
        menu.IsOpen = false;
    }

    private static void ShowNativeOpenWithDialog(NavigationTreeNodeViewModel node)
    {
        if (string.IsNullOrWhiteSpace(node.TargetPath))
        {
            return;
        }

        var owner = System.Windows.Application.Current.MainWindow is { } window
            ? new System.Windows.Interop.WindowInteropHelper(window).EnsureHandle()
            : IntPtr.Zero;

        var success = FileOpenService.TryShowNativeOpenWithDialog(node.TargetPath, owner, out var error);

        if (success)
        {
            node.RefreshDefaultApplication();
            return;
        }

        if (!string.IsNullOrWhiteSpace(error))
        {
            System.Windows.MessageBox.Show(
                System.Windows.Application.Current.MainWindow,
                error,
                "Dora Explorer",
                System.Windows.MessageBoxButton.OK,
                System.Windows.MessageBoxImage.Warning);
        }
    }

    private bool _renameEscapePressed;

    private void OnRenamingEnded(object? sender, EventArgs e)
    {
        _renameEscapePressed = false;
        RequestRefreshTree();
    }

    private void NavigationTree_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key is Key.Up or Key.Down or Key.Left or Key.Right)
        {
            _lockTreeScroll = false;
        }

        if (ViewModel?.RenamingNode is not null)
        {
            if (e.Key == Key.Escape)
            {
                _renameEscapePressed = true;
                ViewModel.CancelRename();
                e.Handled = true;
                NavigationTree.Focus();
            }

            return;
        }

        if (e.Key is Key.Up or Key.Down)
        {
            if (TryNavigateVisible(e.Key == Key.Down ? 1 : -1))
            {
                e.Handled = true;
                return;
            }
        }

        if (e.Key == Key.Left)
        {
            if (TryNavigateLeft())
            {
                e.Handled = true;
                return;
            }
        }

        if (e.Key == Key.Right)
        {
            if (TryNavigateRight())
            {
                e.Handled = true;
                return;
            }

            // Ficheiros não expandem: sem isto o TreeView passa o foco para o botão
            // "Aplicativo padrão" dentro do item (efeito de hover indesejado).
            var current = ViewModel?.GetPrimarySelectedNode() ?? ViewModel?.SelectedNode;
            if (current is { IsFile: true })
            {
                e.Handled = true;
            }
        }

        if (e.Key == Key.Enter)
        {
            ActivateSelectedItem();
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Delete)
        {
            TryDeleteFromKeyboard(e.KeyboardDevice.Modifiers.HasFlag(ModifierKeys.Shift));
            e.Handled = true;
            return;
        }

        if (e.Key == Key.F5)
        {
            ViewModel?.RefreshPane();
            e.Handled = true;
            return;
        }

        if (e.Key == Key.F2 && Keyboard.Modifiers == ModifierKeys.None)
        {
            if (ViewModel is { } pane && !string.IsNullOrWhiteSpace(pane.GetSelectedItemPath()))
            {
                pane.BeginRenameSelected();
            }

            e.Handled = true;
            return;
        }

        if (e.Key == Key.D && Keyboard.Modifiers == ModifierKeys.Control)
        {
            if (GetMainViewModel()?.CenterToolbar.FavoriteCommand is { } favorite && favorite.CanExecute(null))
            {
                favorite.Execute(null);
            }

            e.Handled = true;
        }
    }

    private MainWindowViewModel? GetMainViewModel() =>
        Application.Current.MainWindow?.DataContext as MainWindowViewModel;

    private void ExecuteStageCopy()
    {
        if (GetMainViewModel() is not { } main || ViewModel is not { } pane)
        {
            return;
        }

        if (!main.TryStageTransfer(pane, FileTransferOperation.Copy, out var error))
        {
            ShowTransferMessage(error!);
        }
    }

    private void ExecuteStageCut()
    {
        if (GetMainViewModel() is not { } main || ViewModel is not { } pane)
        {
            return;
        }

        if (!main.TryStageTransfer(pane, FileTransferOperation.Move, out var error))
        {
            ShowTransferMessage(error!);
        }
    }

    private async void ExecutePaste()
    {
        if (GetMainViewModel() is not { } main)
        {
            return;
        }

        var outcome = await main.TryPasteTransferAsync().ConfigureAwait(true);
        ShowTransferFailure(outcome, "Não foi possível colar.");
    }

    private static void ShowTransferFailure(TransferOutcome outcome, string fallback)
    {
        if (!outcome.Success && !outcome.Cancelled)
        {
            ShowTransferMessage(outcome.ErrorMessage ?? fallback);
        }
    }

    private async void ExecuteImmediateCopy()
    {
        NotifyPaneActivated();
        if (GetMainViewModel() is not { } main || ViewModel is not { } pane)
        {
            return;
        }

        var outcome = await main.ExecuteTransferAsync(pane, move: false).ConfigureAwait(true);
        ShowTransferFailure(outcome, "Não foi possível copiar.");
    }

    private async void ExecuteImmediateMove()
    {
        NotifyPaneActivated();
        if (GetMainViewModel() is not { } main || ViewModel is not { } pane)
        {
            return;
        }

        var outcome = await main.ExecuteTransferAsync(pane, move: true).ConfigureAwait(true);
        ShowTransferFailure(outcome, "Não foi possível mover.");
    }

    // Pastas que o Windows não deixou listar e que já foram avisadas nesta sessão: o aviso não se repete a cada clique.
    private static readonly HashSet<string> DeniedListingAnnounced = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Uma pasta protegida aparece vazia; sem este aviso o usuário não sabe que falta permissão. A mensagem é a de "acesso
    /// negado" (<see cref="FileErrorText"/>), então a janela já pergunta se quer reiniciar como administrador.
    /// </summary>
    private void AnnounceDeniedListing(NavigationTreeNodeViewModel node)
    {
        var path = node.TargetPath;
        if (!node.ListingDenied || string.IsNullOrWhiteSpace(path) || !DeniedListingAnnounced.Add(path))
        {
            return;
        }

        // Depois do tratamento do clique: a janela é modal e não pode abrir no meio da abertura da pasta.
        Dispatcher.BeginInvoke(
            () => MessageDialog.Show(Application.Current.MainWindow, FileErrorText.ListingDenied(path), "Abrir pasta"),
            DispatcherPriority.Background);
    }

    private static void ShowTransferMessage(string message) =>
        MessageDialog.Show(Application.Current.MainWindow, message);

    private void TryDeleteFromKeyboard(bool permanent)
    {
        if (ViewModel is null)
        {
            return;
        }

        var paths = ViewModel.GetDeletableItemPaths();
        if (paths.Count == 0)
        {
            return;
        }

        var owner = Application.Current.MainWindow;
        var mode = permanent ? FileDeleteMode.Permanent : FileDeleteMode.RecycleBin;
        if (!DeleteChoiceDialog.ConfirmShortcut(owner, paths, mode))
        {
            return;
        }

        if (!ViewModel.TryDeleteItems(paths, mode, out var error) &&
            !string.IsNullOrWhiteSpace(error))
        {
            MessageDialog.Show(owner, error, "Excluir");
        }
    }

    private void ActivateSelectedItem()
    {
        if (ViewModel?.HasOpenableFilesInSelection() == true)
        {
            ViewModel.OpenSelectedFiles();
            return;
        }

        if (NavigationTree.SelectedItem is NavigationTreeNodeViewModel node && node.IsSelectableItem)
        {
            SyncItemSelectionFromFocus(node);
        }

        TryActivateSelectedFolder();
    }

    private void TryActivateSelectedFolder()
    {
        var node = ViewModel?.GetPrimarySelectedNode();
        if (node is null || !node.IsExpandable)
        {
            return;
        }

        if (FindTreeViewItemForNode(NavigationTree, node) is TreeViewItem item)
        {
            if (!node.IsExpanded)
            {
                ExpandNode(item, node);
            }
            else
            {
                SelectExpandedNode(item, node);
            }

            return;
        }

        if (!string.IsNullOrWhiteSpace(node.TargetPath))
        {
            ViewModel?.NavigateToPath(node.TargetPath);
        }
    }

    private bool TryNavigateVisible(int direction)
    {
        if (ViewModel is null)
        {
            return false;
        }

        var current = ViewModel.GetPrimarySelectedNode() ?? ViewModel.SelectedNode;
        if (current is null || !current.IsSelectableItem)
        {
            return false;
        }

        var visible = ViewModel.GetVisibleSelectableNodes();
        var currentIndex = visible.ToList().FindIndex(n => ReferenceEquals(n, current));
        if (currentIndex < 0)
        {
            return false;
        }

        var nextIndex = currentIndex + direction;
        if (nextIndex < 0 || nextIndex >= visible.Count)
        {
            return false;
        }

        return SelectNavigationNode(visible[nextIndex]);
    }

    private bool TryNavigateLeft()
    {
        var current = ViewModel?.GetPrimarySelectedNode() ?? ViewModel?.SelectedNode;
        if (current is null)
        {
            return false;
        }

        if (current.IsExpandable && current.IsExpanded &&
            FindTreeViewItemForNode(NavigationTree, current) is TreeViewItem expandedItem)
        {
            BeginAnimatedCollapse(expandedItem, current);
            return true;
        }

        var parent = ViewModel?.GetParentNode(current);
        if (parent is { IsSelectableItem: true })
        {
            return SelectNavigationNode(parent);
        }

        return false;
    }

    private bool TryNavigateRight()
    {
        var current = ViewModel?.GetPrimarySelectedNode() ?? ViewModel?.SelectedNode;
        if (current is null)
        {
            return false;
        }

        if (current.IsExpandable &&
            FindTreeViewItemForNode(NavigationTree, current) is TreeViewItem item)
        {
            if (!current.IsExpanded)
            {
                ExpandNode(item, current);
                return true;
            }

            var visible = ViewModel?.GetVisibleSelectableNodes().ToList() ?? [];
            var currentIndex = visible.FindIndex(n => ReferenceEquals(n, current));
            if (currentIndex >= 0 && currentIndex + 1 < visible.Count)
            {
                var next = visible[currentIndex + 1];
                if (ViewModel?.GetParentNode(next) is NavigationTreeNodeViewModel parent &&
                    ReferenceEquals(parent, current))
                {
                    return SelectNavigationNode(next);
                }
            }
        }

        return false;
    }

    private bool SelectNavigationNode(NavigationTreeNodeViewModel node)
    {
        if (FindTreeViewItemForNode(NavigationTree, node) is TreeViewItem item)
        {
            FocusNavigationItem(item, keyboardNavigation: true);
            return true;
        }

        if (string.IsNullOrWhiteSpace(node.TargetPath))
        {
            return false;
        }

        ViewModel?.NavigateToPath(node.TargetPath);
        var nodeRef = node;
        Dispatcher.BeginInvoke(() =>
        {
            if (FindTreeViewItemForNode(NavigationTree, nodeRef) is TreeViewItem retry)
            {
                FocusNavigationItem(retry, keyboardNavigation: true);
            }
        }, DispatcherPriority.Loaded);

        return true;
    }

    private void FocusNavigationItem(TreeViewItem item, bool keyboardNavigation = false)
    {
        if (!item.IsSelected)
        {
            item.IsSelected = true;
        }

        item.Focus();

        var scrollViewer = _treeScrollViewer ?? FindScrollViewer(NavigationTree);
        if (scrollViewer is null)
        {
            return;
        }

        if (keyboardNavigation)
        {
            ClampTreeScrollOffset(scrollViewer);
            TreeScrollHelper.EnsureItemVisibleForKeyboard(item, scrollViewer);
            return;
        }

        TreeScrollHelper.EnsureItemVisible(item, scrollViewer, animate: false);
    }

    private void ClampTreeScrollOffset(ScrollViewer? scrollViewer = null)
    {
        scrollViewer ??= _treeScrollViewer ?? FindScrollViewer(NavigationTree);
        if (scrollViewer is null)
        {
            return;
        }

        scrollViewer.UpdateLayout();
        if (scrollViewer.ScrollableHeight <= 0)
        {
            return;
        }

        var clamped = Math.Clamp(scrollViewer.VerticalOffset, 0, scrollViewer.ScrollableHeight);
        if (Math.Abs(scrollViewer.VerticalOffset - clamped) > 0.5)
        {
            scrollViewer.ScrollToVerticalOffset(clamped);
            SmoothScrollAnimator.SyncTarget(scrollViewer, clamped);
        }
    }

    private void RenameTextBox_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is TextBox textBox)
        {
            _renameEscapePressed = false;
            textBox.Focus();
            textBox.SelectAll();
        }
    }

    private void RenameTextBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape || ViewModel?.RenamingNode is null)
        {
            return;
        }

        _renameEscapePressed = true;
        ViewModel.CancelRename();
        e.Handled = true;

        if (sender is DependencyObject source &&
            FindTreeViewItem(source) is TreeViewItem item)
        {
            item.Focus();
        }
    }

    private void RenameTextBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            ViewModel?.CommitRename();
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Escape)
        {
            _renameEscapePressed = true;
            ViewModel?.CancelRename();
            e.Handled = true;

            if (sender is DependencyObject source &&
                FindTreeViewItem(source) is TreeViewItem item)
            {
                item.Focus();
            }
            else
            {
                NavigationTree.Focus();
            }
        }
    }

    private void RenameTextBox_LostFocus(object sender, RoutedEventArgs e)
    {
        if (_renameEscapePressed || ViewModel?.RenamingNode is null)
        {
            return;
        }

        ViewModel?.CommitRename();
    }

    private void NotifyPaneActivated() => PaneActivated?.Invoke(this, EventArgs.Empty);

    private void RequestHierarchyUpdate()
    {
        Dispatcher.BeginInvoke(
            () => RunSuppressingReveal(() => TreeViewHierarchy.UpdateTree(NavigationTree)),
            DispatcherPriority.Loaded);
        Dispatcher.BeginInvoke(
            () => RunSuppressingReveal(() => TreeViewHierarchy.UpdateTree(NavigationTree)),
            DispatcherPriority.Render);
    }

    private void UpdateHierarchyForExpandedItem(TreeViewItem item)
    {
        RunSuppressingReveal(() =>
        {
            item.UpdateLayout();
            RefreshTreeViewItemChildren(item);
            TreeViewHierarchy.UpdateTree(NavigationTree);
        });
    }

    private void CancelPendingExpansionRefresh()
    {
        _applyExpansionVersion++;
    }

    private void RequestRefreshExpandedItem(TreeViewItem item)
    {
        var version = ++_applyExpansionVersion;
        var itemRef = item;
        var revealChildren = _revealChildrenOnRefresh && ViewModel?.ShouldSuppressTreeAnimations != true;
        _revealChildrenOnRefresh = false;
        if (revealChildren)
        {
            TreeExpansionRevealAnimator.HideForReveal(itemRef);
        }

        Dispatcher.BeginInvoke(() =>
        {
            if (version != _applyExpansionVersion)
            {
                return;
            }

            UpdateHierarchyForExpandedItem(itemRef);
        }, DispatcherPriority.Loaded);

        Dispatcher.BeginInvoke(() =>
        {
            if (version != _applyExpansionVersion)
            {
                return;
            }

            RunSuppressingReveal(() =>
            {
                TreeViewHierarchy.UpdateTree(NavigationTree);
            });
        }, DispatcherPriority.Render);

        if (!revealChildren)
        {
            return;
        }

        Dispatcher.BeginInvoke(() =>
        {
            if (version != _applyExpansionVersion)
            {
                return;
            }

            RunSuppressingReveal(() =>
            {
                TreeViewHierarchy.UpdateTree(NavigationTree);
                TreeExpansionRevealAnimator.RevealChildren(itemRef, version);
                TreeExpansionRevealAnimator.ShowAfterReveal(itemRef);
            });
        }, DispatcherPriority.ApplicationIdle);
    }

    private void RequestRefreshTree()
    {
        var version = ++_applyExpansionVersion;
        var selectedNode = ViewModel?.SelectedNode;

        Dispatcher.BeginInvoke(() =>
        {
            if (version != _applyExpansionVersion)
            {
                return;
            }

            RunSuppressingReveal(() =>
            {
                ApplyExpansionFromViewModel(NavigationTree);
                SyncAccordionExpansion();
                TreeViewHierarchy.UpdateTree(NavigationTree);
            });

            if (selectedNode is not null &&
                FindTreeViewItemForNode(NavigationTree, selectedNode) is TreeViewItem item)
            {
                ScheduleEnsureItemVisibleIfNeeded(item);
            }

            RequestHierarchyUpdate();
        }, DispatcherPriority.Loaded);
    }

    private void ScheduleEnsureItemVisibleIfNeeded(TreeViewItem item)
    {
        Dispatcher.BeginInvoke(() =>
        {
            if (!item.IsVisible)
            {
                return;
            }

            if (FindScrollViewer(NavigationTree) is ScrollViewer scrollViewer)
            {
                TreeScrollHelper.EnsureItemVisible(item, scrollViewer, animate: false);
            }
        }, DispatcherPriority.ApplicationIdle);
    }

    /// <summary>
    /// Sincroniza apenas ramos que o ViewModel marcou como recolhidos (acordeão).
  /// Não reexpande o item que o utilizador acabou de recolher.
    /// </summary>
    private void RunSuppressingReveal(Action action)
    {
        var previous = _suppressExpansionReveal;
        _suppressExpansionReveal = true;
        try
        {
            action();
        }
        finally
        {
            _suppressExpansionReveal = previous;
        }
    }

    private void SyncAccordionExpansion()
    {
        _syncingAccordion = true;
        try
        {
            SyncAccordionExpansion(NavigationTree);
        }
        finally
        {
            _syncingAccordion = false;
        }
    }

    private static void ApplyExpansionFromViewModel(ItemsControl parent)
    {
        foreach (var item in TreeContainers.Realized(parent))
        {
            if (item.DataContext is NavigationTreeNodeViewModel node && node.IsExpanded)
            {
                item.IsExpanded = true;

                if (node.Children.Count > 0)
                {
                    ApplyExpansionFromViewModel(item);
                }
            }
        }
    }

    private static void SyncAccordionExpansion(ItemsControl parent)
    {
        foreach (var item in TreeContainers.Realized(parent))
        {
            if (item.DataContext is NavigationTreeNodeViewModel node)
            {
                if (!node.IsExpanded && item.IsExpanded)
                {
                    item.IsExpanded = false;
                }

                if (node.Children.Count > 0)
                {
                    SyncAccordionExpansion(item);
                }
            }
        }
    }

    // Só atualiza o layout: o laço antigo chamava ContainerFromItem para cada filho e jogava o resultado fora
    // (a chamada não cria containers), custando tempo quadrático em pastas grandes.
    private static void RefreshTreeViewItemChildren(TreeViewItem item) => item.UpdateLayout();

    private static ScrollViewer? FindScrollViewer(DependencyObject root)
    {
        if (root is ScrollViewer scrollViewer)
        {
            return scrollViewer;
        }

        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var found = FindScrollViewer(VisualTreeHelper.GetChild(root, i));
            if (found is not null)
            {
                return found;
            }
        }

        return null;
    }

    private static TreeViewItem? FindTreeViewItemForNode(ItemsControl parent, NavigationTreeNodeViewModel target)
    {
        foreach (var item in TreeContainers.Realized(parent))
        {
            if (ReferenceEquals(item.DataContext, target))
            {
                return item;
            }

            var found = FindTreeViewItemForNode(item, target);
            if (found is not null)
            {
                return found;
            }
        }

        return null;
    }

    private static bool IsClickOnExpander(DependencyObject? source)
    {
        while (source is not null)
        {
            if (source is ToggleButton)
            {
                return true;
            }

            if (source is TreeViewItem)
            {
                return false;
            }

            source = VisualTreeHelper.GetParent(source);
        }

        return false;
    }

    private static bool IsClickOnItemContent(DependencyObject? source, TreeViewItem item)
    {
        if (item.Template?.FindName("ItemBorder", item) is not FrameworkElement itemBorder)
        {
            return false;
        }

        var current = source;
        while (current is not null)
        {
            if (ReferenceEquals(current, itemBorder))
            {
                return true;
            }

            if (current is TreeViewItem)
            {
                return false;
            }

            current = VisualTreeHelper.GetParent(current);
        }

        return false;
    }

    private NavigationTreeNodeViewModel? FindSelectableNodeAtHostY(double y)
    {
        return FindBestTreeViewItemAtHostY(NavigationTree, y)?.DataContext as NavigationTreeNodeViewModel;
    }

    private TreeViewItem? FindBestTreeViewItemAtHostY(ItemsControl parent, double y)
    {
        TreeViewItem? bestItem = null;
        var bestDistance = double.MaxValue;

        foreach (var item in TreeContainers.Realized(parent))
        {
            var bounds = GetItemHeaderBoundsInHost(item);
            if (y >= bounds.Top && y <= bounds.Bottom)
            {
                if (item.IsExpanded)
                {
                    var nested = FindBestTreeViewItemAtHostY(item, y);
                    if (nested is not null)
                    {
                        return nested;
                    }
                }

                return item;
            }

            var distance = y < bounds.Top ? bounds.Top - y : y - bounds.Bottom;
            if (distance < bestDistance)
            {
                bestDistance = distance;
                bestItem = item;
            }

            if (item.IsExpanded)
            {
                var nested = FindBestTreeViewItemAtHostY(item, y);
                if (nested is not null)
                {
                    var nestedBounds = GetItemHeaderBoundsInHost(nested);
                    var nestedDistance = y < nestedBounds.Top
                        ? nestedBounds.Top - y
                        : y - nestedBounds.Bottom;
                    if (y >= nestedBounds.Top && y <= nestedBounds.Bottom ||
                        nestedDistance < bestDistance)
                    {
                        bestDistance = nestedDistance;
                        bestItem = nested;
                    }
                }
            }
        }

        return bestItem;
    }

    private static bool HasDedicatedContextMenu(DependencyObject? source)
    {
        while (source is not null)
        {
            if (source is TreeViewItem)
            {
                return false;
            }

            if (source is FrameworkElement { ContextMenu: not null })
            {
                return true;
            }

            source = VisualTreeHelper.GetParent(source);
        }

        return false;
    }

    private static TreeViewItem? FindTreeViewItem(DependencyObject? source)
    {
        while (source is not null)
        {
            if (source is TreeViewItem treeViewItem)
            {
                return treeViewItem;
            }

            source = VisualTreeHelper.GetParent(source);
        }

        return null;
    }
}
