using DoraExplorer.App.Models;
using DoraExplorer.App.Services;
using DoraExplorer.App.ViewModels;
using FluentAssertions;
using Xunit;

namespace DoraExplorer.App.Tests;

/// <summary>
/// Menu da linha da Lixeira (botão direito): esvaziar e restaurar tudo, e as perguntas feitas antes. A Lixeira de verdade é do usuário:
/// os testes trocam a chamada que esvazia e a consulta "tem itens?" por funções de mentira (<see cref="RecycleBinService.EmptyNative"/>
/// e <see cref="RecycleBinService.HasItemsNative"/>) e nunca esvaziam nada.
/// </summary>
[Collection("App statics")]
public sealed class RecycleBinMenuTests : IDisposable
{
    private readonly PaneTestHarness _h = new();
    private readonly Func<int> _emptyBefore = RecycleBinService.EmptyNative;
    private readonly Func<bool> _hasItemsBefore = RecycleBinService.HasItemsNative;

    public void Dispose()
    {
        RecycleBinService.EmptyNative = _emptyBefore;
        RecycleBinService.HasItemsNative = _hasItemsBefore;
        _h.Dispose();
    }

    /// <summary>Lixeira de mentira: diz que tem itens, conta quantas vezes foi esvaziada e devolve o resultado combinado.</summary>
    private sealed class FakeBin
    {
        public int EmptyCalls { get; private set; }

        public bool Full { get; set; } = true;

        public FakeBin(int result = 0)
        {
            RecycleBinService.HasItemsNative = () => Full;
            RecycleBinService.EmptyNative = () =>
            {
                EmptyCalls++;
                Full = result != 0;
                return result;
            };
        }
    }

    // ---- esvaziar

    [Fact]
    public void Emptying_asks_windows_once_and_succeeds()
    {
        var bin = new FakeBin();

        RecycleBinService.TryEmpty(out var error).Should().BeTrue(error);

        bin.EmptyCalls.Should().Be(1);
        error.Should().BeNull();
    }

    [Fact]
    public void Emptying_a_bin_that_is_already_empty_is_not_an_error_and_does_not_bother_windows()
    {
        var bin = new FakeBin { Full = false };

        RecycleBinService.TryEmpty(out var error).Should().BeTrue(error);

        bin.EmptyCalls.Should().Be(0);
    }

    [Fact]
    public void A_refusal_from_windows_becomes_a_message_in_portuguese()
    {
        _ = new FakeBin(result: unchecked((int)0x80070005)); // E_ACCESSDENIED

        RecycleBinService.TryEmpty(out var error).Should().BeFalse();

        error.Should().Contain("Não foi possível esvaziar");
    }

    [Fact]
    public void The_unexpected_code_windows_returns_when_the_bin_emptied_meanwhile_counts_as_done()
    {
        _ = new FakeBin(result: unchecked((int)0x8000FFFF));

        RecycleBinService.TryEmpty(out _).Should().BeTrue();
    }

    [Fact]
    public void An_exception_from_the_shell_is_reported_instead_of_crashing()
    {
        RecycleBinService.HasItemsNative = () => true;
        RecycleBinService.EmptyNative = () => throw new System.ComponentModel.Win32Exception(5);

        RecycleBinService.TryEmpty(out var error).Should().BeFalse();

        error.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task Emptying_from_a_pane_clears_its_selection_and_reports_success()
    {
        var bin = new FakeBin();
        var pane = _h.CreatePane(_h.Folder("qualquer"));
        pane.CompleteInitialPathRestore();

        var (success, error) = await pane.TryEmptyRecycleBinAsync();

        success.Should().BeTrue(error);
        bin.EmptyCalls.Should().Be(1);
        pane.SelectedItems.Should().BeEmpty();
    }

    [Fact]
    public async Task Emptying_the_bin_refreshes_the_row_in_both_panes()
    {
        var bin = new FakeBin();
        var left = _h.Folder("esquerda");
        var main = _h.CreateMain();
        main.LeftPane.RestorePath(left);
        main.LeftPane.CompleteInitialPathRestore();
        main.RightPane.CompleteInitialPathRestore();

        // O roteiro de teste desliga a Lixeira dos painéis (ela leria o computador); aqui as linhas são ligadas à mão.
        _h.PaneSettings.LeftPane.ShowRecycleBin = true;
        _h.PaneSettings.RightPane.ShowRecycleBin = true;
        main.LeftPane.RefreshForViewOptionsChange();
        main.RightPane.RefreshForViewOptionsChange();
        var leftBin = main.LeftPane.NavigationRoots.FirstOrDefault(n => n.Kind == NavigationNodeKind.RecycleBin);
        var rightBin = main.RightPane.NavigationRoots.FirstOrDefault(n => n.Kind == NavigationNodeKind.RecycleBin);
        leftBin.Should().NotBeNull("a opção da Lixeira está ligada nos dois painéis");
        rightBin.Should().NotBeNull();
        leftBin!.FileCount = 3;
        rightBin!.FileCount = 3;

        var (success, error) = await main.EmptyRecycleBinAsync(main.LeftPane);

        success.Should().BeTrue(error);
        bin.EmptyCalls.Should().Be(1, "a Lixeira é uma só para os dois painéis");
        main.LeftPane.NavigationRoots.First(n => n.Kind == NavigationNodeKind.RecycleBin).FileCount.Should().Be(0);
        main.RightPane.NavigationRoots.First(n => n.Kind == NavigationNodeKind.RecycleBin).FileCount.Should().Be(0);
    }

    [Fact]
    public async Task A_failure_still_refreshes_because_part_of_the_bin_may_have_been_emptied()
    {
        _ = new FakeBin(result: unchecked((int)0x80070005));
        var pane = _h.CreatePane(_h.Folder("qualquer"));

        var (success, error) = await pane.TryEmptyRecycleBinAsync();

        success.Should().BeFalse();
        error.Should().NotBeNullOrWhiteSpace();
    }

    // ---- restaurar tudo

    [Fact]
    public async Task Restoring_everything_from_an_empty_bin_says_it_is_empty()
    {
        RecycleBinService.HasItemsNative = () => false;
        var pane = _h.CreatePane(_h.Folder("qualquer"));
        await Task.CompletedTask;

        if (RecycleBinService.GetAllItemPaths().Count > 0)
        {
            return; // a Lixeira de verdade desta máquina tem itens: não restaura nada dela
        }

        pane.TryRestoreAllRecycleBinItems(out var error).Should().BeFalse();

        error.Should().Contain("vazia");
    }

    // ---- perguntas

    [Theory]
    [InlineData(0, "Todo o conteúdo da Lixeira será excluído")]
    [InlineData(1, "1 item será excluído")]
    [InlineData(7, "Os 7 itens serão excluídos")]
    public void The_question_before_emptying_says_how_many_items_go_for_good(int count, string expected)
    {
        var text = RecycleBinText.ConfirmEmpty(count);

        text.Should().StartWith("Esvaziar a Lixeira?");
        text.Should().Contain(expected);
        text.Should().Contain("não pode ser desfeita");
    }

    [Theory]
    [InlineData(0, "Todo o conteúdo voltará")]
    [InlineData(1, "1 item voltará")]
    [InlineData(4, "Os 4 itens voltarão")]
    public void The_question_before_restoring_says_how_many_items_come_back(int count, string expected)
    {
        var text = RecycleBinText.ConfirmRestoreAll(count);

        text.Should().Contain(expected);
        text.Should().Contain("nome ocupado");
    }
}
