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

    public static readonly DependencyProperty IconDataProperty =
        DependencyProperty.Register(
            nameof(IconData),
            typeof(Geometry),
            typeof(CenterToolbarButton),
            new PropertyMetadata(null, (d, _) => ((CenterToolbarButton)d).UpdateIconKind()));

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
        UpdateIconKind();
    }

    /// <summary>Mostra o desenho vetorial quando há <see cref="IconData"/>; senão, o glifo da fonte de ícones.</summary>
    private void UpdateIconKind()
    {
        // O callback pode vir antes de InitializeComponent criar os elementos (valor definido na construção).
        if (IconText is null || IconDrawing is null)
        {
            return;
        }

        var drawn = IconData is not null;
        IconText.Visibility = drawn ? Visibility.Collapsed : Visibility.Visible;
        IconDrawing.Visibility = drawn ? Visibility.Visible : Visibility.Collapsed;
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

    /// <summary>
    /// Ícone desenhado (grade de 16x16, uma cor só, a do tema) no lugar do glifo da fonte, para o que a fonte de ícones do
    /// Windows não tem (a janela do SMB e o pinguim do NFS). Com <see cref="IconData"/> o <see cref="IconGlyph"/> é ignorado.
    /// </summary>
    public Geometry? IconData
    {
        get => (Geometry?)GetValue(IconDataProperty);
        set => SetValue(IconDataProperty, value);
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
