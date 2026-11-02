using DoraExplorer.App.Models;
using DoraExplorer.App.ViewModels;
using FluentAssertions;
using Xunit;

namespace DoraExplorer.App.Tests;

/// <summary>
/// Janela principal: os dois painéis, qual está ativo, a área de transferência de copiar e colar entre os painéis, as regras
/// que recusam transferências sem sentido (para si mesmo, pasta dentro dela mesma) e a ordenação que vale para os dois lados.
/// </summary>
[Collection("App statics")]
public sealed class MainWindowViewModelTests : IDisposable
{
    private readonly PaneTestHarness _h = new();

    public void Dispose() => _h.Dispose();

    /// <summary>Janela com o painel esquerdo em <paramref name="left"/> e o direito em <paramref name="right"/>.</summary>
    private MainWindowViewModel Open(string left, string right)
    {
        var main = _h.CreateMain();
        main.LeftPane.RestorePath(left);
        main.RightPane.RestorePath(right);
        main.LeftPane.CompleteInitialPathRestore();
        main.RightPane.CompleteInitialPathRestore();
        return main;
    }

    private static void Select(FilePaneViewModel pane, string path)
    {
        pane.NavigateToPath(path);
        pane.SetSingleItemSelection(PaneTestHarness.Find(pane, path)!);
    }

    [Fact]
    public void The_left_pane_starts_active_and_the_right_one_is_the_destination()
    {
        var main = _h.CreateMain();

        main.IsLeftPaneActive.Should().BeTrue();
        main.ActivePane.Should().BeSameAs(main.LeftPane);
        main.SourcePane.Should().BeSameAs(main.LeftPane);
        main.DestinationPane.Should().BeSameAs(main.RightPane);
        main.OppositePane(main.LeftPane).Should().BeSameAs(main.RightPane);
        main.OppositePane(main.RightPane).Should().BeSameAs(main.LeftPane);
    }

    [Fact]
    public void Activating_the_right_pane_swaps_source_and_destination_and_tells_the_screen()
    {
        var main = _h.CreateMain();
        var changed = new List<string?>();
        main.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        main.IsLeftPaneActive = false;

        main.ActivePane.Should().BeSameAs(main.RightPane);
        main.SourcePane.Should().BeSameAs(main.RightPane);
        main.DestinationPane.Should().BeSameAs(main.LeftPane);
        changed.Should().Contain([nameof(MainWindowViewModel.ActivePane), nameof(MainWindowViewModel.SourcePane), nameof(MainWindowViewModel.DestinationPane)]);
    }

    [Fact]
    public void The_pane_with_a_selection_is_the_one_used_and_the_active_one_wins_a_tie()
    {
        var left = _h.Folder("esquerda");
        var right = _h.Folder("direita");
        var a = PaneTestHarness.File(left, "a.txt");
        var b = PaneTestHarness.File(right, "b.txt");
        var main = Open(left, right);
        foreach (var pane in new[] { main.LeftPane, main.RightPane })
        {
            // Sem item marcado e sem pasta aberta como "seleção" (a pasta atual também conta como seleção do painel).
            pane.ClearItemSelection();
            pane.SelectedNode = null;
        }

        main.GetPaneWithSelection().Should().BeNull();

        Select(main.RightPane, b);
        main.GetPaneWithSelection().Should().BeSameAs(main.RightPane);

        Select(main.LeftPane, a);
        main.IsLeftPaneActive = false;
        main.GetPaneWithSelection().Should().BeSameAs(main.RightPane, "com seleção nos dois lados vale o painel ativo");
        main.IsLeftPaneActive = true;
        main.GetPaneWithSelection().Should().BeSameAs(main.LeftPane);
    }

    [Fact]
    public void The_chosen_sort_order_applies_to_both_panes()
    {
        var left = _h.Folder("esquerda");
        var right = _h.Folder("direita");
        foreach (var name in new[] { "a.txt", "b.txt" })
        {
            PaneTestHarness.File(left, name);
            PaneTestHarness.File(right, name);
        }

        var main = Open(left, right);
        var descending = new TreeSortOptions(FoldersFirst: true, Field: TreeSortField.Name, Ascending: false);

        main.ApplyGlobalSortOptions(descending);

        main.GlobalSortOptions.Should().Be(descending);
        PaneTestHarness.Eventually(() =>
                PaneTestHarness.Find(main.LeftPane, left)!.Children.Select(c => c.Name).SequenceEqual(["b.txt", "a.txt"]) &&
                PaneTestHarness.Find(main.RightPane, right)!.Children.Select(c => c.Name).SequenceEqual(["b.txt", "a.txt"]))
            .Should().BeTrue("os dois lados são reordenados");
        PaneTestHarness.Find(main.LeftPane, left)!.Children.Select(c => c.Name).Should().Equal("b.txt", "a.txt");
        PaneTestHarness.Find(main.RightPane, right)!.Children.Select(c => c.Name).Should().Equal("b.txt", "a.txt");
    }

    [Fact]
    public void Transfers_are_validated_before_anything_is_touched()
    {
        var left = _h.Folder("esquerda");
        var right = _h.Folder("direita");
        var inside = Directory.CreateDirectory(Path.Combine(left, "dentro")).FullName;
        var file = PaneTestHarness.File(left, "a.txt");
        var main = Open(left, right);

        main.TryValidateTransfer([], right, out var none).Should().BeFalse();
        main.TryValidateTransfer([file], "  ", out var noDestination).Should().BeFalse();
        main.TryValidateTransfer([file], file, out var toItself).Should().BeFalse();
        main.TryValidateTransfer([left], inside, out var intoItself).Should().BeFalse();
        main.TryValidateTransfer([file], left, out var alreadyThere).Should().BeFalse();

        none.Should().Contain("Selecione");
        noDestination.Should().Contain("destino");
        toItself.Should().Contain("si próprio");
        intoItself.Should().Contain("dentro de si mesma");
        alreadyThere.Should().Contain("já está");
        main.TryValidateTransfer([file], right, out var fine).Should().BeTrue();
        fine.Should().BeNull();
    }

    [Fact]
    public void A_transfer_takes_the_selection_of_the_active_pane_to_the_folder_of_the_other()
    {
        var left = _h.Folder("esquerda");
        var right = _h.Folder("direita");
        var file = PaneTestHarness.File(left, "a.txt");
        var main = Open(left, right);
        Select(main.LeftPane, file);

        main.TryResolveTransfer(out var source, out var destination, out var sourcePath, out var destinationDirectory, out var error)
            .Should().BeTrue(error);

        source.Should().BeSameAs(main.LeftPane);
        destination.Should().BeSameAs(main.RightPane);
        sourcePath.Should().Be(file);
        destinationDirectory.Should().Be(right);
    }

    [Fact]
    public void A_transfer_without_a_selection_says_what_is_missing()
    {
        var main = Open(_h.Folder("esquerda"), _h.Folder("direita"));
        main.LeftPane.ClearItemSelection();
        main.LeftPane.SelectedNode = null;

        main.TryResolveTransfer(out _, out _, out _, out _, out var error).Should().BeFalse();

        error.Should().Contain("Selecione");
    }

    [Fact]
    public void Staging_without_a_selection_names_the_action_that_was_asked_for()
    {
        var main = Open(_h.Folder("esquerda"), _h.Folder("direita"));
        main.LeftPane.ClearItemSelection();
        main.LeftPane.SelectedNode = null;

        main.TryStageTransfer(main.LeftPane, FileTransferOperation.Copy, out var copyError).Should().BeFalse();
        main.TryStageTransfer(main.LeftPane, FileTransferOperation.Move, out var moveError).Should().BeFalse();

        copyError.Should().Contain("copiar");
        moveError.Should().Contain("cortar");
        main.HasTransferClipboard.Should().BeFalse();
    }

    [Fact]
    public async Task Pasting_with_nothing_staged_reports_it()
    {
        var main = Open(_h.Folder("esquerda"), _h.Folder("direita"));

        var outcome = await main.TryPasteTransferAsync();

        outcome.Success.Should().BeFalse();
        outcome.ErrorMessage.Should().Contain("Nenhum item");
    }

    [Fact]
    public async Task Copy_and_paste_puts_a_copy_in_the_other_pane_and_keeps_the_clipboard()
    {
        var left = _h.Folder("esquerda");
        var right = _h.Folder("direita");
        var file = PaneTestHarness.File(left, "a.txt", "conteudo");
        var main = Open(left, right);
        Select(main.LeftPane, file);

        main.TryStageTransfer(main.LeftPane, FileTransferOperation.Copy, out var stageError).Should().BeTrue(stageError);
        var outcome = await main.TryPasteTransferAsync();

        outcome.Success.Should().BeTrue(outcome.ErrorMessage);
        File.ReadAllText(Path.Combine(right, "a.txt")).Should().Be("conteudo");
        File.Exists(file).Should().BeTrue("copiar não tira do lugar");
        main.HasTransferClipboard.Should().BeTrue("dá para colar de novo");
    }

    [Fact]
    public async Task Cut_and_paste_moves_the_file_and_empties_the_clipboard()
    {
        var left = _h.Folder("esquerda");
        var right = _h.Folder("direita");
        var file = PaneTestHarness.File(left, "a.txt", "conteudo");
        var main = Open(left, right);
        Select(main.LeftPane, file);

        main.TryStageTransfer(main.LeftPane, FileTransferOperation.Move, out var stageError).Should().BeTrue(stageError);
        var outcome = await main.TryPasteTransferAsync();

        outcome.Success.Should().BeTrue(outcome.ErrorMessage);
        File.Exists(file).Should().BeFalse();
        File.ReadAllText(Path.Combine(right, "a.txt")).Should().Be("conteudo");
        main.HasTransferClipboard.Should().BeFalse("depois de mover não há mais o que colar");
    }

    [Fact]
    public async Task Dropping_onto_the_same_folder_is_refused_without_touching_the_file()
    {
        var left = _h.Folder("esquerda");
        var right = _h.Folder("direita");
        var file = PaneTestHarness.File(left, "a.txt");
        var main = Open(left, right);

        var outcome = await main.ExecuteDropTransferAsync(main.LeftPane, [file], main.LeftPane, left, move: true);

        outcome.Success.Should().BeFalse();
        outcome.ErrorMessage.Should().Contain("já está");
        File.Exists(file).Should().BeTrue();
    }

    [Fact]
    public async Task Dropping_files_on_the_other_pane_copies_or_moves_them_there()
    {
        var left = _h.Folder("esquerda");
        var right = _h.Folder("direita");
        var toCopy = PaneTestHarness.File(left, "copiar.txt", "c");
        var toMove = PaneTestHarness.File(left, "mover.txt", "m");
        var main = Open(left, right);

        var copied = await main.ExecuteDropTransferAsync(main.LeftPane, [toCopy], main.RightPane, right, move: false);
        var moved = await main.ExecuteDropTransferAsync(main.LeftPane, [toMove], main.RightPane, right, move: true);

        copied.Success.Should().BeTrue(copied.ErrorMessage);
        moved.Success.Should().BeTrue(moved.ErrorMessage);
        File.Exists(toCopy).Should().BeTrue();
        File.Exists(toMove).Should().BeFalse();
        File.ReadAllText(Path.Combine(right, "copiar.txt")).Should().Be("c");
        File.ReadAllText(Path.Combine(right, "mover.txt")).Should().Be("m");
    }

    // ---- depois de soltar numa pasta que aparece como linha da árvore (não a que está aberta no painel), os DOIS painéis precisam
    // ----- mostrar a mudança: a pasta que ganhou conteúdo ganha a seta de expandir, a que perdeu o último item a perde.

    private (MainWindowViewModel Main, string Source, string Destination, string File) TwoPanesShowingTheSameFolders()
    {
        var root = _h.Folder("raiz");
        var source = Directory.CreateDirectory(Path.Combine(root, "origem")).FullName;
        var destination = Directory.CreateDirectory(Path.Combine(root, "destino")).FullName;
        var other = Directory.CreateDirectory(Path.Combine(root, "outra")).FullName;
        var file = PaneTestHarness.File(source, "a.txt");
        // Esquerda aberta em "origem", direita aberta em "outra": nenhuma tem "destino" como pasta aberta, só como linha da árvore.
        return (Open(source, other), source, destination, file);
    }

    private static bool HasArrow(FilePaneViewModel pane, string folder) =>
        PaneTestHarness.Find(pane, folder) is { } node && node.Children.Any();

    [Fact]
    public async Task A_folder_that_was_empty_gets_its_expand_arrow_in_both_panes_after_a_drop_between_them()
    {
        var (main, _, destination, file) = TwoPanesShowingTheSameFolders();
        HasArrow(main.LeftPane, destination).Should().BeFalse("a pasta de destino começa vazia");
        HasArrow(main.RightPane, destination).Should().BeFalse();

        var outcome = await main.ExecuteDropTransferAsync(main.LeftPane, [file], main.RightPane, destination, move: true);

        outcome.Success.Should().BeTrue(outcome.ErrorMessage);
        HasArrow(main.LeftPane, destination).Should().BeTrue("o painel de origem recarrega a pasta de destino");
        HasArrow(main.RightPane, destination).Should().BeTrue("o painel de destino também: soltar na linha não pode deixá-la sem a seta");
    }

    [Fact]
    public async Task A_folder_that_was_empty_gets_its_expand_arrow_in_both_panes_after_a_drop_from_another_program()
    {
        var (main, _, destination, _) = TwoPanesShowingTheSameFolders();
        var outside = PaneTestHarness.File(_h.Folder("fora"), "do-explorer.txt");

        var outcome = await main.ExecuteDropTransferAsync(null, [outside], main.RightPane, destination, move: false);

        outcome.Success.Should().BeTrue(outcome.ErrorMessage);
        HasArrow(main.RightPane, destination).Should().BeTrue();
        HasArrow(main.LeftPane, destination).Should().BeTrue("o outro painel também mostra essa pasta");
    }

    [Fact]
    public async Task The_folder_a_file_was_moved_out_of_loses_its_arrow_in_both_panes()
    {
        var (main, source, destination, file) = TwoPanesShowingTheSameFolders();
        HasArrow(main.RightPane, source).Should().BeTrue("a pasta de origem tem o arquivo que vai sair");

        await main.ExecuteDropTransferAsync(main.LeftPane, [file], main.RightPane, destination, move: true);

        HasArrow(main.LeftPane, source).Should().BeFalse();
        HasArrow(main.RightPane, source).Should().BeFalse("o painel de destino também mostrava essa pasta e ficava com a seta velha");
    }

    [Fact]
    public void The_menu_option_for_locked_drives_flips_and_is_saved()
    {
        var main = _h.CreateMain();
        main.ShowLockedDrives.Should().BeTrue("o padrão é mostrar as unidades bloqueadas");

        main.ToggleShowLockedDrivesCommand.Execute(null);
        main.ShowLockedDrives.Should().BeFalse();
        _h.ViewSettings.Options.ShowLockedDrives.Should().BeFalse();
        new Services.AppSettingsService(Path.Combine(_h.Root, "settings.json")).LoadExplorerViewOptions().ShowLockedDrives.Should().BeFalse();

        main.ToggleShowLockedDrivesCommand.Execute(null);
        main.ShowLockedDrives.Should().BeTrue();
    }

    [Fact]
    public void Checking_always_administrator_asks_for_access_right_away_and_keeps_the_check_on()
    {
        _h.Elevation.RestartSucceeds = true;
        var main = _h.CreateMain();
        main.StartAsAdministrator.Should().BeFalse("o padrão é não pedir administrador");
        var notified = new List<string?>();
        main.PropertyChanged += (_, e) => notified.Add(e.PropertyName);

        main.ToggleStartAsAdministratorCommand.Execute(null);

        _h.Elevation.RestartRequests.Should().Be(1, "marcar pede o acesso de administrador imediatamente");
        main.StartAsAdministrator.Should().BeTrue();
        notified.Should().Contain(nameof(MainWindowViewModel.StartAsAdministrator));
        new Services.AppSettingsService(Path.Combine(_h.Root, "settings.json")).LoadStartAsAdministrator().Should().BeTrue();
    }

    [Fact]
    public void Refusing_the_windows_confirmation_puts_the_check_back_to_off_without_a_message()
    {
        _h.Elevation.RestartSucceeds = false;
        var main = _h.CreateMain();
        var messages = new List<string>();
        main.MessageRequested += (_, message) => messages.Add(message);
        var notified = new List<string?>();
        main.PropertyChanged += (_, e) => notified.Add(e.PropertyName);

        main.ToggleStartAsAdministratorCommand.Execute(null);

        main.StartAsAdministrator.Should().BeFalse();
        messages.Should().BeEmpty("recusar o aviso do Windows é uma escolha do usuário");
        notified.Should().Contain(nameof(MainWindowViewModel.StartAsAdministrator),
            "o clique já tinha marcado o item; é a notificação que o desmarca de volta");
    }

    [Fact]
    public void A_failure_to_restart_as_administrator_shows_the_reason()
    {
        _h.Elevation.RestartSucceeds = false;
        _h.Elevation.RestartError = "Não foi possível reiniciar como administrador.";
        var main = _h.CreateMain();
        var messages = new List<string>();
        main.MessageRequested += (_, message) => messages.Add(message);

        main.ToggleStartAsAdministratorCommand.Execute(null);

        main.StartAsAdministrator.Should().BeFalse();
        messages.Should().Equal("Não foi possível reiniciar como administrador.");
    }

    [Fact]
    public void Unchecking_always_administrator_only_saves_the_choice()
    {
        _h.Elevation.IsElevated = true;
        var main = _h.CreateMain();
        main.ToggleStartAsAdministratorCommand.Execute(null);
        main.StartAsAdministrator.Should().BeTrue();

        main.ToggleStartAsAdministratorCommand.Execute(null);

        main.StartAsAdministrator.Should().BeFalse();
        _h.Elevation.RestartRequests.Should().Be(0, "já era administrador: nada a pedir");
        new Services.AppSettingsService(Path.Combine(_h.Root, "settings.json")).LoadStartAsAdministrator().Should().BeFalse();
    }

    [Fact]
    public void Panes_open_where_they_were_left_when_remembering_the_path_is_on_and_the_folder_still_exists()
    {
        var left = _h.Folder("esquerda");
        var right = _h.Folder("direita");
        _h.Settings.SaveRememberPathSettings(isEnabled: true, left, right);
        _h.RememberPath.Load();

        var main = _h.CreateMain();

        main.RememberPath.Should().BeTrue();
        main.LeftPane.CurrentPath.Should().Be(left);
        main.RightPane.CurrentPath.Should().Be(right);
    }

    [Fact]
    public void A_remembered_folder_that_no_longer_exists_falls_back_to_a_drive_instead_of_failing()
    {
        var left = Path.Combine(_h.Data, "sumiu-esquerda");
        var right = _h.Folder("direita");
        _h.Settings.SaveRememberPathSettings(isEnabled: true, left, right);
        _h.RememberPath.Load();

        var main = _h.CreateMain();

        main.RightPane.CurrentPath.Should().Be(right);
        main.LeftPane.CurrentPath.Should().NotBe(left);
        Directory.Exists(main.LeftPane.CurrentPath).Should().BeTrue("volta para uma pasta que existe");
    }

    [Fact]
    public void Without_remembering_the_panes_do_not_restore_a_path()
    {
        var left = _h.Folder("esquerda");
        _h.Settings.SaveRememberPathSettings(isEnabled: false, left, left);
        _h.RememberPath.Load();

        var main = _h.CreateMain();

        main.RememberPath.Should().BeFalse();
        main.LeftPane.CurrentPath.Should().NotBe(left);
    }
}
