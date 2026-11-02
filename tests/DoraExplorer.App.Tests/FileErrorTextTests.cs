using DoraExplorer.App.Infrastructure;
using FluentAssertions;
using Xunit;

namespace DoraExplorer.App.Tests;

/// <summary>
/// As mensagens de falha ao mexer em arquivos aparecem para o usuário: precisam estar em português, sem o prefixo
/// <c>\\?\</c> do .NET, e as de "acesso negado" precisam ser reconhecidas para oferecer reiniciar como administrador.
/// </summary>
public sealed class FileErrorTextTests : IDisposable
{
    private readonly string _work = Directory.CreateDirectory(
        Path.Combine(Path.GetTempPath(), "dora-errtext-" + Guid.NewGuid().ToString("N"))).FullName;

    public void Dispose() => FileOperationTests.DeleteQuietly(_work);

    private static string Marked(string text) => text + FileErrorText.AccessDeniedMarker;

    [Theory]
    [InlineData(@"\\?\C:\Intel\GfxCPLBatchFiles", @"C:\Intel\GfxCPLBatchFiles")]
    [InlineData(@"\\?\UNC\servidor\pasta\arquivo.txt", @"\\servidor\pasta\arquivo.txt")]
    [InlineData(@"C:\Intel", @"C:\Intel")]
    [InlineData("", "")]
    public void The_long_path_prefix_is_not_shown(string path, string expected) =>
        FileErrorText.DisplayPath(path).Should().Be(expected);

    [Fact]
    public void Access_denied_names_the_inner_path_in_portuguese_and_is_marked()
    {
        var exception = new UnauthorizedAccessException(@"Access to the path '\\?\C:\Intel\GfxCPLBatchFiles' is denied.");

        var text = FileErrorText.Describe(exception, @"C:\Intel");

        text.Should().Be(Marked(@"Intel: acesso negado a 'C:\Intel\GfxCPLBatchFiles'."));
        FileErrorText.IsAccessDenied(text).Should().BeTrue();
        FileErrorText.WithoutMarker(text).Should().Be(@"Intel: acesso negado a 'C:\Intel\GfxCPLBatchFiles'.");
    }

    [Fact]
    public void The_same_text_comes_out_when_the_runtime_message_is_already_in_portuguese()
    {
        var exception = new UnauthorizedAccessException(@"O acesso ao caminho '\\?\C:\inetpub' foi negado.");

        FileErrorText.Describe(exception, @"C:\inetpub").Should().Be(Marked(@"inetpub: acesso negado a 'C:\inetpub'."));
    }

    // Directory.Delete(recursive) avisa a falta de permissão com um IOException comum (não UnauthorizedAccessException), e o único
    // sinal é a frase da mensagem. Foi o que apareceu em inglês, com \\?\ e sem oferecer administrador, ao excluir C:\inetpub.
    [Theory]
    [InlineData(@"Access to the path '\\?\C:\inetpub' is denied.")]
    [InlineData(@"O acesso ao caminho '\\?\C:\inetpub' foi negado.")]
    public void A_plain_IO_exception_that_says_the_path_is_denied_is_access_denied(string runtimeMessage)
    {
        var text = FileErrorText.Describe(new IOException(runtimeMessage), @"C:\inetpub");

        text.Should().Be(Marked(@"inetpub: acesso negado a 'C:\inetpub'."));
        FileErrorText.IsAccessDenied(text).Should().BeTrue();
    }

    [Fact]
    public void A_plain_IO_exception_about_something_else_is_not_taken_for_access_denied()
    {
        var text = FileErrorText.Describe(new IOException("O disco não respondeu."), @"C:\Dados\a.txt");

        FileErrorText.IsAccessDenied(text).Should().BeFalse();
        text.Should().Contain("não foi possível concluir a operação");
    }

    [Fact]
    public void Without_a_path_in_the_message_the_item_itself_is_named()
    {
        var exception = new UnauthorizedAccessException("Acesso negado.");

        FileErrorText.Describe(exception, @"C:\Dados\relatorio.txt")
            .Should().Be(Marked(@"relatorio.txt: acesso negado a 'C:\Dados\relatorio.txt'."));
    }

    [Fact]
    public void A_read_only_file_is_explained_and_is_not_offered_elevation()
    {
        var file = Path.Combine(_work, "so-leitura.txt");
        File.WriteAllText(file, "x");
        File.SetAttributes(file, FileAttributes.ReadOnly);
        try
        {
            var exception = new UnauthorizedAccessException($"Access to the path '{file}' is denied.");

            var text = FileErrorText.Describe(exception, file);

            text.Should().Contain("somente leitura");
            FileErrorText.IsAccessDenied(text).Should().BeFalse("ser administrador não remove o atributo somente leitura");
        }
        finally
        {
            File.SetAttributes(file, FileAttributes.Normal);
        }
    }

    [Theory]
    [InlineData(unchecked((int)0x80070020), "está em uso por outro programa")]
    [InlineData(unchecked((int)0x80070021), "está em uso por outro programa")]
    [InlineData(unchecked((int)0x80070070), "não há espaço livre no disco")]
    [InlineData(unchecked((int)0x80070091), "não está vazia")]
    [InlineData(unchecked((int)0x800700B7), "já existe um item com esse nome")]
    public void Common_io_failures_are_explained_in_portuguese_and_are_not_access_denied(int hresult, string expected)
    {
        var exception = new IOException(@"The process cannot access the file 'C:\Dados\a.txt' because it is being used by another process.", hresult);

        var text = FileErrorText.Describe(exception, @"C:\Dados\a.txt");

        text.Should().StartWith("a.txt: ").And.Contain(expected);
        text.Should().NotContain("The process", "a mensagem do .NET está em inglês");
        FileErrorText.IsAccessDenied(text).Should().BeFalse();
    }

    [Fact]
    public void A_path_that_is_too_long_is_explained()
    {
        FileErrorText.Describe(new PathTooLongException(), @"C:\Dados\x").Should().Be("x: o caminho é longo demais.");
    }

    [Fact]
    public void A_missing_item_is_explained()
    {
        FileErrorText.Describe(new FileNotFoundException("Could not find file", @"C:\Dados\sumiu.txt"), @"C:\Dados")
            .Should().Be(@"Dados: 'C:\Dados\sumiu.txt' não existe mais.");

        FileErrorText.Describe(new DirectoryNotFoundException(@"Could not find a part of the path 'C:\Dados\pasta\'."), @"C:\Dados\pasta")
            .Should().Be(@"pasta: 'C:\Dados\pasta' não existe mais.");
    }

    [Fact]
    public void Anything_else_keeps_the_original_text_after_a_portuguese_lead_in()
    {
        var text = FileErrorText.Describe(new InvalidOperationException("algo inesperado"), @"C:\Dados\a.txt");

        text.Should().Be("a.txt: não foi possível concluir a operação (algo inesperado).");
        FileErrorText.IsAccessDenied(text).Should().BeFalse();
    }

    [Fact]
    public void Access_denied_texts_built_without_an_exception_are_marked_too()
    {
        FileErrorText.AccessDenied(@"C:\Intel").Should().Be(Marked(@"Intel: acesso negado a 'C:\Intel'."));
        FileErrorText.AccessDenied(@"C:\Intel", @"C:\Intel\Sub").Should().Be(Marked(@"Intel: acesso negado a 'C:\Intel\Sub'."));
        FileErrorText.ListingDenied(@"C:\System Volume Information")
            .Should().Be(Marked(@"Sem permissão para abrir 'C:\System Volume Information'."));
    }

    [Fact]
    public void The_marker_survives_when_several_failures_are_joined_and_is_removed_for_display()
    {
        var joined = "Alguns itens não puderam ser excluídos:" + Environment.NewLine +
                     Marked("a: acesso negado a 'C:\\a'.") + Environment.NewLine +
                     "b: está em uso.";

        FileErrorText.IsAccessDenied(joined).Should().BeTrue();
        FileErrorText.WithoutMarker(joined).Should().NotContain(FileErrorText.AccessDeniedMarker.ToString());
        FileErrorText.IsAccessDenied("texto normal").Should().BeFalse();
    }

    [Theory]
    [InlineData(5, true)]     // ERROR_ACCESS_DENIED
    [InlineData(0x78, true)]  // DE_ACCESSDENIEDSRC (SHFileOperation)
    [InlineData(32, false)]   // ERROR_SHARING_VIOLATION
    [InlineData(0, false)]
    [InlineData(0x75, false)] // DE_OPCANCELLED
    public void Shell_and_win32_codes_that_mean_access_denied_are_recognized(int code, bool expected) =>
        FileErrorText.IsAccessDeniedCode(code).Should().Be(expected);
}
