using System.Text.RegularExpressions;
using DoraExplorer.App.Infrastructure;
using DoraExplorer.App.Models;
using DoraExplorer.App.Services;
using FluentAssertions;
using Xunit;

namespace DoraExplorer.App.Tests;

public sealed class SortHeaderLogicTests
{
    [Fact]
    public void Clicking_the_sorted_column_flips_the_direction()
    {
        var byName = new TreeSortOptions(Field: TreeSortField.Name, Ascending: true);

        var once = SortHeaderLogic.Toggle(byName, TreeSortField.Name);
        var twice = SortHeaderLogic.Toggle(once, TreeSortField.Name);

        once.Ascending.Should().BeFalse();
        twice.Ascending.Should().BeTrue();
    }

    [Theory]
    [InlineData(TreeSortField.Name, true)]
    [InlineData(TreeSortField.Extension, true)]
    [InlineData(TreeSortField.Size, false)]
    [InlineData(TreeSortField.Date, false)]
    public void Clicking_another_column_starts_in_its_natural_direction(TreeSortField target, bool ascending)
    {
        // Parte de uma coluna diferente da clicada, em direção oposta à esperada.
        var current = new TreeSortOptions(
            Field: target == TreeSortField.Name ? TreeSortField.Size : TreeSortField.Name,
            Ascending: !ascending);

        var result = SortHeaderLogic.Toggle(current, target);

        result.Field.Should().Be(target);
        result.Ascending.Should().Be(ascending);
    }

    [Fact]
    public void Clicking_a_header_keeps_folders_first_as_configured()
    {
        var current = new TreeSortOptions(FoldersFirst: false);

        SortHeaderLogic.Toggle(current, TreeSortField.Date).FoldersFirst.Should().BeFalse();
    }

    [Fact]
    public void Indicator_marks_only_the_sorted_column()
    {
        var descendingByDate = new TreeSortOptions(Field: TreeSortField.Date, Ascending: false);
        var ascendingByName = new TreeSortOptions(Field: TreeSortField.Name, Ascending: true);

        SortHeaderLogic.Indicator(descendingByDate, TreeSortField.Date).Should().Be(" ▼");
        SortHeaderLogic.Indicator(descendingByDate, TreeSortField.Name).Should().BeEmpty();
        SortHeaderLogic.Indicator(ascendingByName, TreeSortField.Name).Should().Be(" ▲");
    }
}

public sealed class NavigationHistoryTests
{
    [Fact]
    public void New_history_has_nowhere_to_go()
    {
        var history = new NavigationHistory();

        history.Current.Should().BeNull();
        history.CanGoBack.Should().BeFalse();
        history.CanGoForward.Should().BeFalse();
        history.Back().Should().BeNull();
        history.Forward().Should().BeNull();
    }

    [Fact]
    public void Back_and_forward_walk_through_the_visited_folders()
    {
        var history = new NavigationHistory();
        history.Visit(@"C:\A");
        history.Visit(@"C:\B");
        history.Visit(@"C:\C");

        history.Back().Should().Be(@"C:\B");
        history.Back().Should().Be(@"C:\A");
        history.CanGoBack.Should().BeFalse();
        history.Forward().Should().Be(@"C:\B");
        history.Forward().Should().Be(@"C:\C");
        history.CanGoForward.Should().BeFalse();
        history.Current.Should().Be(@"C:\C");
    }

    [Fact]
    public void Visiting_a_new_folder_discards_what_was_ahead()
    {
        var history = new NavigationHistory();
        history.Visit(@"C:\A");
        history.Visit(@"C:\B");
        history.Visit(@"C:\C");
        history.Back();
        history.Back();

        history.Visit(@"C:\D");

        history.CanGoForward.Should().BeFalse();
        history.Back().Should().Be(@"C:\A");
    }

    [Fact]
    public void Repeating_the_current_folder_does_not_add_an_entry()
    {
        var history = new NavigationHistory();
        history.Visit(@"C:\A");
        history.Visit(@"c:\a");
        history.Visit("   ");

        history.CanGoBack.Should().BeFalse();
        history.Current.Should().Be(@"C:\A");
    }

    [Fact]
    public void Capacity_drops_the_oldest_entries()
    {
        var history = new NavigationHistory(capacity: 3);
        foreach (var name in new[] { "A", "B", "C", "D" })
        {
            history.Visit(@"C:\" + name);
        }

        history.Back().Should().Be(@"C:\C");
        history.Back().Should().Be(@"C:\B");
        history.CanGoBack.Should().BeFalse("A saiu do histórico");
    }
}

public sealed class PathNavigationTests
{
    [Theory]
    [InlineData(@"C:\Windows\System32", @"C:\Windows")]
    [InlineData(@"C:\Windows", @"C:\")]
    [InlineData(@"D:\Pasta\", @"D:\")]
    [InlineData(@"\\servidor\compartilhamento\pasta", @"\\servidor\compartilhamento")]
    public void Local_folders_go_up_one_level(string path, string expected)
    {
        PathNavigation.GetParentPath(path).Should().Be(expected);
    }

    [Theory]
    [InlineData(@"C:\")]
    [InlineData(@"\\servidor\compartilhamento")]
    public void Roots_have_no_parent(string path)
    {
        PathNavigation.GetParentPath(path).Should().BeNull();
    }

    [Fact]
    public void Archives_open_as_folders_go_up_through_their_entries_and_out_of_the_file()
    {
        PathNavigation.GetParentPath(@"C:\dados\a.zip\pasta\sub").Should().Be(@"C:\dados\a.zip\pasta");
        PathNavigation.GetParentPath(@"C:\dados\a.zip\pasta").Should().Be(@"C:\dados\a.zip");
        PathNavigation.GetParentPath(@"C:\dados\a.zip").Should().Be(@"C:\dados");
    }

    [Fact]
    public void Remote_folders_go_up_until_the_server_root()
    {
        var deep = NetworkFileService.BuildRemotePath(NetworkProtocol.Sftp, "srv", "/a/b");
        var shallow = NetworkFileService.BuildRemotePath(NetworkProtocol.Sftp, "srv", "/a");
        var root = NetworkFileService.BuildRemotePath(NetworkProtocol.Sftp, "srv", "/");

        PathNavigation.GetParentPath(deep).Should().Be(shallow);
        PathNavigation.GetParentPath(shallow).Should().Be(root);
        PathNavigation.GetParentPath(root).Should().BeNull();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("Este computador")]
    [InlineData("Favoritos")]
    public void Special_or_empty_paths_have_no_parent(string? path)
    {
        PathNavigation.GetParentPath(path).Should().BeNull();
    }
}

public sealed class RowDensitySettingTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "dora-density-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    [Fact]
    public void Compact_rows_are_the_default_for_new_and_old_settings_files()
    {
        new ExplorerViewOptions().CompactRows.Should().BeTrue();

        Directory.CreateDirectory(_directory);
        var path = Path.Combine(_directory, "settings.json");
        File.WriteAllText(path, """{ "ExplorerView": { "ShowHiddenFiles": true } }""");

        var options = new AppSettingsService(path).LoadExplorerViewOptions();

        options.ShowHiddenFiles.Should().BeTrue();
        options.CompactRows.Should().BeTrue("arquivos antigos não têm o campo");
    }

    [Fact]
    public void Changing_the_density_is_saved_and_announced_once()
    {
        var path = Path.Combine(_directory, "settings.json");
        var settings = new AppSettingsService(path);
        var service = new ExplorerViewSettingsService(settings);
        service.Load();
        var raised = 0;
        service.RowDensityChanged += (_, _) => raised++;

        service.SetCompactRows(false);
        service.SetCompactRows(false);

        raised.Should().Be(1);
        new AppSettingsService(path).LoadExplorerViewOptions().CompactRows.Should().BeFalse();
    }
}

public sealed partial class XamlResourceTests
{
    [GeneratedRegex(@"\{StaticResource\s+([^\s{}]+)\s*\}")]
    private static partial Regex StaticReferencePattern();

    [GeneratedRegex("x:Key=\"([^\"]+)\"")]
    private static partial Regex KeyDefinitionPattern();

    // Um {StaticResource} sem chave correspondente só falha quando a tela abre; o build não avisa.
    [Fact]
    public void Every_static_resource_used_in_xaml_is_defined_somewhere()
    {
        var xamlFiles = Directory.GetFiles(FindAppSourceDirectory(), "*.xaml", SearchOption.AllDirectories);
        xamlFiles.Should().NotBeEmpty();

        var contents = xamlFiles.ToDictionary(file => file, File.ReadAllText);
        var defined = contents.Values
            .SelectMany(text => KeyDefinitionPattern().Matches(text).Select(match => match.Groups[1].Value))
            .ToHashSet(StringComparer.Ordinal);

        var missing = contents
            .SelectMany(entry => StaticReferencePattern().Matches(entry.Value)
                .Select(match => (File: Path.GetFileName(entry.Key), Key: match.Groups[1].Value)))
            .Where(reference => !defined.Contains(reference.Key))
            .Select(reference => reference.File + ": " + reference.Key)
            .Distinct()
            .ToList();

        missing.Should().BeEmpty();
    }

    private static string FindAppSourceDirectory()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            var candidate = Path.Combine(directory.FullName, "src", "DoraExplorer.App");
            if (File.Exists(Path.Combine(candidate, "DoraExplorer.App.csproj")))
            {
                return candidate;
            }
        }

        throw new DirectoryNotFoundException("Pasta src\\DoraExplorer.App não encontrada a partir de " + AppContext.BaseDirectory);
    }
}
