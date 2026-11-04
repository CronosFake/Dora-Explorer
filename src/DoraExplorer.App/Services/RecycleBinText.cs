namespace DoraExplorer.App.Services;

/// <summary>Textos das perguntas do menu da Lixeira, separados da tela para serem testados.</summary>
public static class RecycleBinText
{
    /// <summary>Pergunta antes de esvaziar: cita quantos itens saem para sempre (o painel sabe a contagem da linha da Lixeira).</summary>
    public static string ConfirmEmpty(int itemCount) =>
        "Esvaziar a Lixeira?\n\n" +
        itemCount switch
        {
            <= 0 => "Todo o conteúdo da Lixeira será excluído permanentemente.",
            1 => "1 item será excluído permanentemente.",
            _ => $"Os {itemCount} itens serão excluídos permanentemente."
        } +
        "\n\nEsta ação não pode ser desfeita.";

    /// <summary>Pergunta antes de devolver tudo ao lugar de origem.</summary>
    public static string ConfirmRestoreAll(int itemCount) =>
        "Restaurar todos os itens da Lixeira?\n\n" +
        itemCount switch
        {
            <= 0 => "Todo o conteúdo voltará ao lugar de onde saiu.",
            1 => "1 item voltará ao lugar de onde saiu.",
            _ => $"Os {itemCount} itens voltarão ao lugar de onde saíram."
        } +
        " Os que tiverem o nome ocupado no destino ficam na Lixeira.";
}
