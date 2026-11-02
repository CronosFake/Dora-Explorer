using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace DoraExplorer.App.Controls;

public partial class CenterToolbarButton
{
    public static readonly DependencyProperty LabelProperty =
        DependencyProperty.Register(nameof(Label), typeof(string), typeof(CenterToolbarButton), new PropertyMetadata(string.Empty));

    public static readonly DependencyProperty HintProperty =
        DependencyProperty.Register(nameof(Hint), typeof(string), typeof(CenterToolbarButton), new PropertyMetadata(null));

    public static readonly DependencyProperty IconGlyphProperty =
        DependencyProperty.Register(nameof(IconGlyph), typeof(string), typeof(CenterToolbarButton), new PropertyMetadata(string.Empty));

    public static readonly DependencyProperty IconBackgroundProperty =
        DependencyProperty.Register(nameof(IconBackground), typeof(Brush), typeof(CenterToolbarButton), new PropertyMetadata(Brushes.Transparent));

    public static readonly DependencyProperty IconForegroundProperty =
        DependencyProperty.Register(nameof(IconForeground), typeof(Brush), typeof(CenterToolbarButton), new PropertyMetadata(Brushes.Black));

    public static readonly DependencyProperty CommandProperty =
        DependencyProperty.Register(nameof(Command), typeof(ICommand), typeof(CenterToolbarButton), new PropertyMetadata(null));

    public static readonly DependencyProperty IsMenuOpenProperty =
        DependencyProperty.Register(nameof(IsMenuOpen), typeof(bool), typeof(CenterToolbarButton), new PropertyMetadata(false));

    public static readonly DependencyProperty ShowServiceStatusProperty =
        DependencyProperty.Register(nameof(ShowServiceStatus), typeof(bool), typeof(CenterToolbarButton), new PropertyMetadata(false));

    public static readonly DependencyProperty IsServiceActiveProperty =
        DependencyProperty.Register(nameof(IsServiceActive), typeof(bool), typeof(CenterToolbarButton), new PropertyMetadata(false));

    public static readonly RoutedEvent MenuClickEvent =
        EventManager.RegisterRoutedEvent(nameof(MenuClick), RoutingStrategy.Bubble, typeof(RoutedEventHandler), typeof(CenterToolbarButton));

    public CenterToolbarButton()
    {
        InitializeComponent();
    }

    public event RoutedEventHandler MenuClick
    {
        add => AddHandler(MenuClickEvent, value);
        remove => RemoveHandler(MenuClickEvent, value);
    }

    public bool OpensMenu { get; set; }

    public bool IsMenuOpen
    {
        get => (bool)GetValue(IsMenuOpenProperty);
        set => SetValue(IsMenuOpenProperty, value);
    }

    public bool ShowServiceStatus
    {
        get => (bool)GetValue(ShowServiceStatusProperty);
        set => SetValue(ShowServiceStatusProperty, value);
    }

    public bool IsServiceActive
    {
        get => (bool)GetValue(IsServiceActiveProperty);
        set => SetValue(IsServiceActiveProperty, value);
    }

    public ICommand? Command
    {
        get => (ICommand?)GetValue(CommandProperty);
        set => SetValue(CommandProperty, value);
    }

    public string Label
    {
        get => (string)GetValue(LabelProperty);
        set => SetValue(LabelProperty, value);
    }

    /// <summary>Texto da dica de ferramenta (inclui o atalho de teclado, quando houver).</summary>
    public string? Hint
    {
        get => (string?)GetValue(HintProperty);
        set => SetValue(HintProperty, value);
    }

    public string IconGlyph
    {
        get => (string)GetValue(IconGlyphProperty);
        set => SetValue(IconGlyphProperty, value);
    }

    public Brush IconBackground
    {
        get => (Brush)GetValue(IconBackgroundProperty);
        set => SetValue(IconBackgroundProperty, value);
    }

    public Brush IconForeground
    {
        get => (Brush)GetValue(IconForegroundProperty);
        set => SetValue(IconForegroundProperty, value);
    }

    private void Button_Click(object sender, RoutedEventArgs e)
    {
        if (OpensMenu)
        {
            RaiseEvent(new RoutedEventArgs(MenuClickEvent, this));
            e.Handled = true;
        }
    }
}
