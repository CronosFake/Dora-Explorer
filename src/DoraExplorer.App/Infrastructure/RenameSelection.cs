namespace DoraExplorer.App.Infrastructure;

/// <summary>
/// Que parte do nome já vem selecionada ao começar a renomear, como no Explorer do Windows: o nome sem a extensão, para digitar o
/// novo nome por cima sem estragar o tipo do arquivo (<c>relatorio.final.docx</c> seleciona <c>relatorio.final</c>).
/// </summary>
internal static class RenameSelection
{
    /// <summary>
    /// Início e tamanho do trecho a selecionar. Pasta, nome sem extensão, nome que só tem a extensão (<c>.gitignore</c>) e nome
    /// terminado em ponto selecionam tudo; para arquivo, vale tudo até o último ponto.
    /// </summary>
    public static (int Start, int Length) GetRange(string name, bool isFile)
    {
        name ??= string.Empty;
        if (!isFile)
        {
            return (0, name.Length);
        }

        var dot = name.LastIndexOf('.');
        return dot <= 0 || dot == name.Length - 1
            ? (0, name.Length)
            : (0, dot);
    }
}
