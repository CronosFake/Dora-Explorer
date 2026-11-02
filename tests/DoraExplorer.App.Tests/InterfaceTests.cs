using System.Globalization;
using System.Windows.Media;
using DoraExplorer.App.Models;
using DoraExplorer.App.Services;
using DoraExplorer.App.ViewModels;
using FluentAssertions;
using Xunit;

namespace DoraExplorer.App.Tests;

public sealed class StorageUsageTests
{
    [Theory]
    [InlineData(0.0, StorageUsageLevel.Normal)]
    [InlineData(0.50, StorageUsageLevel.Normal)]
    [InlineData(0.7999, StorageUsageLevel.Normal)]
    [InlineData(0.80, StorageUsageLevel.Warning)]
    [InlineData(0.8999, StorageUsageLevel.Warning)]
    [InlineData(0.90, StorageUsageLevel.Critical)]
    [InlineData(1.0, StorageUsageLevel.Critical)]
    public void Usage_ratio_maps_to_a_level(double ratio, StorageUsageLevel expected)
    {
        StorageUsage.Classify(ratio).Should().Be(expected);
    }

    [Fact]
    public void Drive_node_exposes_its_level()
    {
        var node = new NavigationTreeNodeViewModel("C:", NavigationNodeKind.Drive) { StorageUsedRatio = 0.93 };

        node.StorageLevel.Should().Be(StorageUsageLevel.Critical);
    }
}

public sealed class DeniedFolderRowTests
{
    [Fact]
    public void A_folder_the_user_may_not_list_swaps_its_counts_for_a_lock_on_the_row()
    {
        var node = new NavigationTreeNodeViewModel("trancada", NavigationNodeKind.Directory, @"C:\Dados\trancada");
        node.ShowDirectoryCounts.Should().BeTrue("uma pasta comum mostra as contagens");
        var changed = new List<string?>();
        node.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        node.ListingDenied = true;

        node.ShowDirectoryCounts.Should().BeFalse("0 pastas e 0 arquivos seria mentira: o Windows não deixou contar");
        changed.Should().Contain(nameof(NavigationTreeNodeViewModel.ListingDenied));
        changed.Should().Contain(nameof(NavigationTreeNodeViewModel.ShowDirectoryCounts));
    }
}

public sealed class SelectionStatusFormatterTests
{
    [Fact]
    public void Nothing_selected_has_no_text()
    {
        SelectionStatusFormatter.Format([], null).Should().BeEmpty();
    }

    [Fact]
    public void Folder_shows_name_and_counts_with_correct_plurals()
    {
        var many = new NavigationTreeNodeViewModel("Windows", NavigationNodeKind.Directory) { FolderCount = 79, FileCount = 33 };
        var one = new NavigationTreeNodeViewModel("Intel", NavigationNodeKind.Directory) { FolderCount = 1, FileCount = 1 };
        var none = new NavigationTreeNodeViewModel("PerfLogs", NavigationNodeKind.Directory) { FolderCount = 0, FileCount = 0 };

        SelectionStatusFormatter.Format([], many).Should().Be("Windows · 79 pastas · 33 arquivos");
        SelectionStatusFormatter.Format([], one).Should().Be("Intel · 1 pasta · 1 arquivo");
        SelectionStatusFormatter.Format([], none).Should().Be("PerfLogs · 0 pastas · 0 arquivos");
    }

    [Fact]
    public void A_folder_the_user_may_not_list_says_so_instead_of_showing_zero_folders_and_files()
    {
        var locked = new NavigationTreeNodeViewModel("PerfLogs", NavigationNodeKind.Directory) { FolderCount = 0, FileCount = 0, ListingDenied = true };

        SelectionStatusFormatter.Format([], locked).Should().Be("PerfLogs · sem permissão para abrir");
        SelectionStatusFormatter.Format([locked], null).Should().Be("PerfLogs · sem permissão para abrir");

        locked.ListingDenied = false;
        SelectionStatusFormatter.Format([], locked).Should().Be("PerfLogs · 0 pastas · 0 arquivos", "sem a marca a pasta volta a mostrar as contagens");
    }

    [Fact]
    public void File_shows_name_size_and_date()
    {
        var file = new NavigationTreeNodeViewModel("DumpStack.log", NavigationNodeKind.File)
        {
            SizeInBytes = 12 * 1024,
            LastModified = new DateTime(2026, 10, 7, 12, 49, 0)
        };

        var text = SelectionStatusFormatter.Format([file], null);

        text.Should().StartWith("DumpStack.log · 12 KB · ");
        text.Should().Contain("12:49");
    }

    [Fact]
    public void Drive_shows_name_and_free_space_or_the_reason_it_is_unavailable()
    {
        var ready = new NavigationTreeNodeViewModel("C:", NavigationNodeKind.Drive) { StorageSummary = "Livre 221,8GB/439,1GB" };
        var locked = new NavigationTreeNodeViewModel("E:", NavigationNodeKind.Drive) { StorageSummary = "Bloqueada (BitLocker)" };

        SelectionStatusFormatter.Format([], ready).Should().Be("C: · Livre 221,8GB/439,1GB");
        SelectionStatusFormatter.Format([], locked).Should().Be("E: · Bloqueada (BitLocker)");
    }

    [Fact]
    public void Multiple_selection_shows_count_and_total_size()
    {
        var first = new NavigationTreeNodeViewModel("a.bin", NavigationNodeKind.File) { SizeInBytes = 1024 };
        var second = new NavigationTreeNodeViewModel("b.bin", NavigationNodeKind.File) { SizeInBytes = 3 * 1024 };
        var folder = new NavigationTreeNodeViewModel("pasta", NavigationNodeKind.Directory);

        SelectionStatusFormatter.Format([first, second], null).Should().Be("2 itens selecionados · 4 KB");
        SelectionStatusFormatter.Format([folder, folder], null).Should().Be("2 itens selecionados");
    }

    [Fact]
    public void Placeholders_never_show_text()
    {
        var placeholder = new NavigationTreeNodeViewModel(string.Empty, NavigationNodeKind.Directory) { IsPlaceholder = true };

        SelectionStatusFormatter.Format([], placeholder).Should().BeEmpty();
    }
}

public sealed class ThemeUsageColorsTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Both_themes_define_distinct_usage_colors(bool dark)
    {
        var theme = ExplorerThemeResources.Create(dark, Color.FromRgb(0xFF, 0x77, 0x00));

        var normal = ((SolidColorBrush)theme["UsageNormalBrush"]).Color;
        var warning = ((SolidColorBrush)theme["UsageWarningBrush"]).Color;
        var critical = ((SolidColorBrush)theme["UsageCriticalBrush"]).Color;

        theme.Contains("UsageTrackBrush").Should().BeTrue();
        new[] { normal, warning, critical }.Distinct().Should().HaveCount(3);
    }

    [Theory]
    [InlineData(0xFF, 0x77, 0x00)] // laranja: o padrão do app
    [InlineData(0xF4, 0x43, 0x36)] // vermelho
    [InlineData(0x00, 0x78, 0xD4)] // azul
    public void Normal_usage_follows_the_accent_while_the_alert_colors_stay_fixed(byte r, byte g, byte b)
    {
        var accent = Color.FromRgb(r, g, b);
        var otherAccent = Color.FromRgb(0x10, 0x9D, 0x58);

        foreach (var dark in new[] { true, false })
        {
            var theme = ExplorerThemeResources.Create(dark, accent);
            var other = ExplorerThemeResources.Create(dark, otherAccent);

            ((SolidColorBrush)theme["UsageNormalBrush"]).Color.Should().NotBe(((SolidColorBrush)other["UsageNormalBrush"]).Color);
            ((SolidColorBrush)theme["UsageWarningBrush"]).Color.Should().Be(((SolidColorBrush)other["UsageWarningBrush"]).Color);
            ((SolidColorBrush)theme["UsageCriticalBrush"]).Color.Should().Be(((SolidColorBrush)other["UsageCriticalBrush"]).Color);
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Scrollbar_and_hierarchy_lines_follow_the_accent(bool dark)
    {
        var first = ExplorerThemeResources.Create(dark, Color.FromRgb(0xF4, 0x43, 0x36));
        var second = ExplorerThemeResources.Create(dark, Color.FromRgb(0x00, 0x78, 0xD4));

        foreach (var key in new[] { "TreeHierarchyLineBrush", "ScrollBarThumbBrush" })
        {
            ((SolidColorBrush)first[key]).Color.Should().NotBe(((SolidColorBrush)second[key]).Color, key);
        }
    }

    [Fact]
    public void Storage_summary_text_also_signals_the_alert_level()
    {
        var xaml = File.ReadAllText(LocateSource(@"src\DoraExplorer.App\Controls\FilePaneControl.xaml"), System.Text.Encoding.UTF8);

        // Uma ocorrência é da barra; a outra, do texto "Livre ..." ao lado dela.
        System.Text.RegularExpressions.Regex.Matches(xaml, "UsageCriticalBrush").Should().HaveCount(2,
            "com destaque vermelho a barra crítica sozinha não se distingue da normal");
    }

    [Fact]
    public void Tree_items_declare_an_accessible_name()
    {
        var themeXaml = File.ReadAllText(LocateSource(@"src\DoraExplorer.App\Resources\Theme.xaml"), System.Text.Encoding.UTF8);

        themeXaml.Should().Contain("AutomationProperties.Name",
            "sem isso leitores de tela leem o nome da classe do ViewModel");
        themeXaml.IndexOf("NavigationTreeItemStyle", StringComparison.Ordinal).Should().BeLessThan(
            themeXaml.IndexOf("AutomationProperties.Name", StringComparison.Ordinal));
    }

    private static string LocateSource(string relativePath)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, relativePath);
            if (File.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        throw new FileNotFoundException($"Não encontrei {relativePath} a partir de {AppContext.BaseDirectory}");
    }
}

public sealed class ApplicationNameTests
{
    [Theory]
    [InlineData("Notepad.exe", "Notepad", @"C:\x\Notepad.exe", "Notepad")]                  // Bloco de Notas da Loja
    [InlineData("NOTEPAD.EXE", null, @"C:\x\Notepad.exe", "NOTEPAD")]
    [InlineData("Notepad++", "Notepad++", @"C:\Program Files\Notepad++\notepad++.exe", "Notepad++")]
    [InlineData("Bloco de notas", "Sistema operacional Microsoft Windows", @"C:\Windows\notepad.exe", "Bloco de notas")]
    [InlineData("", "Produto X", @"C:\x\app.exe", "Produto X")]
    [InlineData("   ", "Produto.exe", @"C:\x\app.exe", "Produto")]
    [InlineData(null, null, @"C:\x\meu app.exe", "meu app")]
    [InlineData(".exe", null, @"C:\x\sozinho.exe", "sozinho")]
    public void Application_names_never_show_the_executable_extension(string? description, string? product, string path, string expected)
    {
        FileAssociationService.PickDisplayName(description, product, path).Should().Be(expected);
    }

    [Theory]
    [InlineData("Microsoft.WindowsNotepad_8wekyb3d8bbwe!App", true)]
    [InlineData("Microsoft.ZuneMusic_8wekyb3d8bbwe!Microsoft.ZuneMusic", true)]
    [InlineData("firefox.exe", false)]
    [InlineData(@"C:\Program Files\App!\app.exe", false)]
    [InlineData("AppXabc123", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Packaged_app_ids_are_told_apart_from_programs_and_paths(string? name, bool expected)
    {
        FileAssociationService.IsPackagedAppId(name).Should().Be(expected);
    }

    [Fact]
    public void A_raw_executable_name_from_the_shell_is_replaced_by_the_description_of_the_program()
    {
        var listed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        FileAssociationService.ChooseHandlerDisplayName("firefox", "Firefox", listed).Should().Be("Firefox");
        FileAssociationService.ChooseHandlerDisplayName("WINWORD", "Microsoft Word", listed).Should().Be("Microsoft Word");
    }

    [Fact]
    public void A_friendly_shell_name_is_kept_when_the_description_is_missing_the_same_or_already_listed()
    {
        var listed = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Notepad" };

        FileAssociationService.ChooseHandlerDisplayName("Bloco de notas", null, listed).Should().Be("Bloco de notas");
        FileAssociationService.ChooseHandlerDisplayName("Bloco de notas", "  ", listed).Should().Be("Bloco de notas");
        FileAssociationService.ChooseHandlerDisplayName("Fotos", "Fotos", listed).Should().Be("Fotos");
        FileAssociationService.ChooseHandlerDisplayName("npp", "Notepad", listed).Should().Be("npp");
    }
}

public sealed class DefaultApplicationOverridesTests : IDisposable
{
    private readonly string _dir = Directory.CreateDirectory(
        Path.Combine(Path.GetTempPath(), "dora-defapp-" + Guid.NewGuid().ToString("N"))).FullName;

    public void Dispose()
    {
        FileAssociationService.Install(new DefaultApplicationOverrides()); // não deixa a escolha de um teste valendo nos outros
        FileOperationTests.DeleteQuietly(_dir);
    }

    [Theory]
    [InlineData(".txt", ".txt")]
    [InlineData("TXT", ".txt")]
    [InlineData("  .Log ", ".log")]
    [InlineData(null, null)]
    [InlineData("", null)]
    [InlineData(".", null)]
    public void Extensions_are_normalized_so_upper_and_lower_case_are_the_same(string? input, string? expected)
    {
        DefaultApplicationOverrides.Normalize(input).Should().Be(expected);
    }

    [Fact]
    public void Choosing_and_clearing_an_application_is_saved_and_announced()
    {
        var saved = new List<(string Extension, string Name, string? Path)>();
        var removed = new List<string>();
        var changed = new List<string>();
        var overrides = new DefaultApplicationOverrides(save: (e, n, p) => saved.Add((e, n, p)), remove: removed.Add);
        overrides.Changed += (_, extension) => changed.Add(extension);

        overrides.Set("TXT", "Notepad++", @"C:\np\notepad++.exe");
        overrides.TryGet(".txt", out var entry).Should().BeTrue();
        entry.DisplayName.Should().Be("Notepad++");

        overrides.Clear(".txt").Should().BeTrue();
        overrides.Clear(".txt").Should().BeFalse("já tinha sido removido");

        saved.Should().Equal((".txt", "Notepad++", @"C:\np\notepad++.exe"));
        removed.Should().Equal(".txt");
        changed.Should().Equal(".txt", ".txt");
        overrides.TryGet(".txt", out _).Should().BeFalse();
    }

    [Fact]
    public void Saved_choices_are_loaded_back_with_the_settings_service()
    {
        var path = Path.Combine(_dir, "settings.json");
        var service = new AppSettingsService(path);
        service.SaveDefaultApplication(".txt", "Notepad++", @"C:\np\notepad++.exe");
        service.SaveDefaultApplication(".md", "Firefox", null);
        service.RemoveDefaultApplication(".md");

        var reloaded = new AppSettingsService(path).LoadDefaultApplications();

        reloaded.Should().ContainSingle();
        reloaded[".txt"].DisplayName.Should().Be("Notepad++");
        reloaded[".txt"].ExecutablePath.Should().Be(@"C:\np\notepad++.exe");

        // Reabrir o programa: a escolha volta a valer.
        var overrides = new DefaultApplicationOverrides(reloaded);
        overrides.TryGet("TXT", out var entry).Should().BeTrue();
        entry.DisplayName.Should().Be("Notepad++");
    }

    [Fact]
    public void Old_settings_without_chosen_applications_load_as_empty()
    {
        var path = Path.Combine(_dir, "antigo.json");
        File.WriteAllText(path, """{ "ThemeMode": 1 }""");

        new AppSettingsService(path).LoadDefaultApplications().Should().BeEmpty();
    }

    [Fact]
    public void A_chosen_application_that_no_longer_exists_is_ignored_in_favor_of_the_windows_default()
    {
        var existing = Path.Combine(_dir, "editor.exe");
        File.WriteAllText(existing, "x");
        var notifications = new List<string>();
        FileAssociationService.OverridesChanged += (_, extension) => notifications.Add(extension);
        FileAssociationService.Install(new DefaultApplicationOverrides());

        FileAssociationService.SetUserOverride(@"C:\docs\a.txt", existing, "Meu Editor");
        FileAssociationService.HasUserOverride(@"C:\docs\b.TXT").Should().BeTrue();
        FileAssociationService.GetDefaultApplicationName(@"C:\docs\b.txt").Should().Be("Meu Editor");
        FileAssociationService.TryGetUserOverrideExecutable(".txt", out var executable).Should().BeTrue();
        executable.Should().Be(existing);
        notifications.Should().Contain(".txt");

        File.Delete(existing); // o programa foi desinstalado
        FileAssociationService.HasUserOverride(@"C:\docs\b.txt").Should().BeFalse();
        FileAssociationService.TryGetUserOverrideExecutable(".txt", out _).Should().BeFalse();
        FileAssociationService.GetDefaultApplicationName(@"C:\docs\b.txt").Should().NotBe("Meu Editor");
    }

    [Fact]
    public void Only_programs_the_system_can_run_are_accepted_as_applications()
    {
        FileAssociationService.IsLaunchable(@"C:\x\editor.EXE").Should().BeTrue();
        FileAssociationService.IsLaunchable(@"C:\x\script.cmd").Should().BeTrue();
        FileAssociationService.IsLaunchable(@"C:\x\documento.txt").Should().BeFalse();
    }
}