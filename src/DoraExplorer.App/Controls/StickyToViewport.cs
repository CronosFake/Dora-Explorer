using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace DoraExplorer.App.Controls;

/// <summary>Onde e com que largura um elemento "grudado" na área visível fica quando a lista rola na horizontal.</summary>
public readonly record struct StickyLayout(bool Active, double TranslateX, double Width);

/// <summary>
/// Mantém um elemento da lista (a linha de uma unidade, com a barra de espaço do disco) parado e com a largura da área visível
/// enquanto a lista rola na horizontal: ele anda para a direita o mesmo tanto que o conteúdo anda para a esquerda. O elemento é
/// arrumado por <c>HorizontalAlignment=Left</c> com <c>Width</c> explícito (com Stretch o WPF centralizaria um elemento com
/// largura explícita). Vale SEMPRE (com ou sem rolagem): ligar e desligar conforme a lista rolasse mudava a largura natural da
/// linha, e com ela a extensão rolável, que ligava e desligava de novo — a tela piscava num laço de layouts.
/// </summary>
public static class StickyToViewport
{
    public static readonly DependencyProperty IsStickyProperty = DependencyProperty.RegisterAttached(
        "IsSticky",
        typeof(bool),
        typeof(StickyToViewport),
        new PropertyMetadata(false, OnIsStickyChanged));

    public static bool GetIsSticky(DependencyObject element) => (bool)element.GetValue(IsStickyProperty);

    public static void SetIsSticky(DependencyObject element, bool value) => element.SetValue(IsStickyProperty, value);

    private sealed class State
    {
        public ScrollViewer? Scroll;
        public ScrollChangedEventHandler? Handler;
    }

    private static readonly ConditionalWeakTable<FrameworkElement, State> States = new();

    /// <summary>
    /// A conta (pura). <paramref name="currentLeft"/> é onde o elemento está agora, em relação à borda esquerda da área
    /// visível, já com a rolagem e o deslocamento atual (<paramref name="currentTranslate"/>) aplicados; daí sai onde ele
    /// ficaria sem rolagem, e a largura é o que sobra dali até a borda direita da área visível.
    /// </summary>
    public static StickyLayout Resolve(
        double viewportWidth,
        double horizontalOffset,
        double currentLeft,
        double currentTranslate)
    {
        if (viewportWidth <= 0)
        {
            return new StickyLayout(false, 0, 0);
        }

        var unscrolledLeft = currentLeft + horizontalOffset - currentTranslate;
        return new StickyLayout(true, horizontalOffset, Math.Max(0, viewportWidth - unscrolledLeft));
    }

    private static void OnIsStickyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not FrameworkElement element)
        {
            return;
        }

        element.Loaded -= OnLoaded;
        element.Unloaded -= OnUnloaded;

        if ((bool)e.NewValue)
        {
            element.Loaded += OnLoaded;
            element.Unloaded += OnUnloaded;
            if (element.IsLoaded)
            {
                Attach(element);
            }
        }
        else
        {
            Detach(element);
            Reset(element);
        }
    }

    private static void OnLoaded(object sender, RoutedEventArgs e) => Attach((FrameworkElement)sender);

    private static void OnUnloaded(object sender, RoutedEventArgs e)
    {
        var element = (FrameworkElement)sender;
        Detach(element);
        Reset(element);
    }

    private static void Attach(FrameworkElement element)
    {
        Detach(element);

        var scroll = FindScrollViewer(element);
        if (scroll is null)
        {
            return;
        }

        var state = States.GetOrCreateValue(element);
        state.Scroll = scroll;
        state.Handler = (_, args) =>
        {
            if (args.HorizontalChange != 0 || args.ViewportWidthChange != 0)
            {
                Apply(element, scroll);
            }
        };
        scroll.ScrollChanged += state.Handler;
        Apply(element, scroll);
    }

    private static void Detach(FrameworkElement element)
    {
        if (States.TryGetValue(element, out var state) && state.Scroll is not null && state.Handler is not null)
        {
            state.Scroll.ScrollChanged -= state.Handler;
            state.Scroll = null;
            state.Handler = null;
        }
    }

    private static void Apply(FrameworkElement element, ScrollViewer scroll)
    {
        var presenter = scroll.Template?.FindName("PART_ScrollContentPresenter", scroll) as FrameworkElement ?? scroll;

        double currentLeft;
        try
        {
            currentLeft = element.TranslatePoint(new Point(0, 0), presenter).X;
        }
        catch (InvalidOperationException)
        {
            return;
        }

        var translate = element.RenderTransform as TranslateTransform;
        var layout = Resolve(presenter.ActualWidth, scroll.HorizontalOffset, currentLeft, translate?.X ?? 0);
        if (!layout.Active)
        {
            Reset(element);
            return;
        }

        element.HorizontalAlignment = HorizontalAlignment.Left;
        element.Width = layout.Width;
        if (translate is null)
        {
            element.RenderTransform = new TranslateTransform(layout.TranslateX, 0);
        }
        else
        {
            translate.X = layout.TranslateX;
        }
    }

    private static void Reset(FrameworkElement element)
    {
        if (element.ReadLocalValue(FrameworkElement.WidthProperty) == DependencyProperty.UnsetValue &&
            element.RenderTransform is not TranslateTransform)
        {
            return;
        }

        element.ClearValue(FrameworkElement.WidthProperty);
        element.ClearValue(FrameworkElement.HorizontalAlignmentProperty);
        if (element.RenderTransform is TranslateTransform)
        {
            element.ClearValue(UIElement.RenderTransformProperty);
        }
    }

    private static ScrollViewer? FindScrollViewer(DependencyObject start)
    {
        for (var current = VisualTreeHelper.GetParent(start); current is not null; current = VisualTreeHelper.GetParent(current))
        {
            if (current is ScrollViewer scroll)
            {
                return scroll;
            }
        }

        return null;
    }
}
