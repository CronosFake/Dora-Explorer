using System.Windows;
using DoraExplorer.App.Infrastructure;
using FluentAssertions;
using Xunit;

namespace DoraExplorer.App.Tests;

/// <summary>
/// Arrastar itens para outro programa (Explorer, navegador, área de trabalho) só pode copiar: mover tiraria o arquivo do lugar sem o
/// usuário ter pedido. Entre os dois painéis do app continua valendo copiar e mover. O arrasto de verdade, com o mouse saindo da
/// janela, só se confere no app real (<c>scripts\dev\ui-capture.ps1 -Scenario dragdrop</c>).
/// </summary>
public sealed class DragPolicyTests
{
    [Fact]
    public void Dragging_to_another_program_only_allows_copying()
    {
        var effects = DragPolicy.AllowedEffects(cursorOverOwnWindow: false);

        effects.Should().Be(DragDropEffects.Copy);
        effects.HasFlag(DragDropEffects.Move).Should().BeFalse("o outro programa nem pode escolher mover");
        effects.HasFlag(DragDropEffects.Link).Should().BeFalse();
    }

    [Fact]
    public void Dragging_between_the_two_panes_of_the_app_still_allows_copy_and_move()
    {
        DragPolicy.AllowedEffects(cursorOverOwnWindow: true).Should().Be(DragDropEffects.Copy | DragDropEffects.Move);
    }

    [Fact]
    public void The_cursor_check_runs_without_a_window_and_never_finds_the_test_process_under_it()
    {
        // O processo de teste não tem janela, então nada sob o cursor é dele. Também prova que as chamadas ao Windows funcionam.
        DragPolicy.IsCursorOverOwnWindow().Should().BeFalse();
        _ = DragPolicy.IsLeftButtonDown();
    }
}
