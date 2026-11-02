using System.Security.AccessControl;
using DoraExplorer.App.Infrastructure;
using DoraExplorer.App.Models;
using DoraExplorer.App.Services;
using FluentAssertions;
using Xunit;

namespace DoraExplorer.App.Tests;

/// <summary>
/// Faltou permissão para excluir, renomear, criar, copiar, mover ou compactar: a mensagem precisa vir em português, sem
/// <c>\\?\</c>, e marcada como "acesso negado" para o app oferecer reiniciar como administrador. A falta de permissão é
/// criada de verdade (<see cref="AccessDenyScope"/>), não simulada.
/// </summary>
public sealed class AccessDeniedOperationTests : IDisposable
{
    private readonly string _work = Directory.CreateDirectory(
        Path.Combine(Path.GetTempPath(), "dora-denied-" + Guid.NewGuid().ToString("N"))).FullName;

    private readonly AccessDenyScope _deny = new();

    public void Dispose()
    {
        _deny.Dispose();
        FileOperationTests.DeleteQuietly(_work);
    }

    private string NewFile(string folder, string name)
    {
        var directory = Directory.CreateDirectory(Path.Combine(_work, folder)).FullName;
        var path = Path.Combine(directory, name);
        File.WriteAllText(path, "conteudo");
        return path;
    }

    private static void ShouldBePortugueseAccessDenied(string? error, string itemName)
    {
        error.Should().NotBeNull();
        error.Should().StartWith(itemName + ": acesso negado a '");
        error.Should().EndWith(FileErrorText.AccessDeniedMarker.ToString());
        error.Should().NotContain(@"\\?\").And.NotContain("Access to the path");
        FileErrorText.IsAccessDenied(error!).Should().BeTrue();
    }

    [Fact]
    public void Deleting_a_protected_file_permanently_is_denied_in_portuguese()
    {
        var file = NewFile("protegida", "dados.txt");
        _deny.MakeUndeletable(file);

        new FileOperationService().TryDeletePermanently([file], out var error).Should().BeFalse();

        ShouldBePortugueseAccessDenied(error, "dados.txt");
        File.Exists(file).Should().BeTrue();
    }

    [Fact]
    public void Deleting_a_folder_with_a_protected_file_inside_names_the_folder_and_a_path_inside_it()
    {
        var file = NewFile(Path.Combine("Intel", "GfxCPLBatchFiles"), "lote.bat");
        _deny.MakeUndeletable(file);
        var folder = Path.Combine(_work, "Intel");

        new FileOperationService().TryDeletePermanently([folder], out var error).Should().BeFalse();

        // O .NET cita a pasta apagada ou o item que falhou dentro dela; nos dois casos o caminho começa na pasta.
        ShouldBePortugueseAccessDenied(error, "Intel");
        error.Should().Contain(folder);
        Directory.Exists(folder).Should().BeTrue();
    }

    [Fact]
    public void Deleting_a_folder_that_refuses_to_be_removed_is_denied_in_portuguese_and_offers_administrator()
    {
        // Como C:\inetpub: a própria pasta não deixa ser apagada. Nesse caso o .NET não lança UnauthorizedAccessException,
        // e sim um IOException comum, e a mensagem saía em inglês, com \\?\ e sem a marca que faz o app oferecer elevação.
        var parent = Directory.CreateDirectory(Path.Combine(_work, "raiz")).FullName;
        var folder = Directory.CreateDirectory(Path.Combine(parent, "inetpub")).FullName;
        _deny.Deny(folder, FileSystemRights.Delete);
        _deny.Deny(parent, FileSystemRights.DeleteSubdirectoriesAndFiles);

        new FileOperationService().TryDeletePermanently([folder], out var error).Should().BeFalse();

        ShouldBePortugueseAccessDenied(error, "inetpub");
        error.Should().Contain(folder);
        Directory.Exists(folder).Should().BeTrue();
    }

    [Fact]
    public void Creating_a_folder_where_it_is_not_allowed_is_denied_in_portuguese()
    {
        var parent = Directory.CreateDirectory(Path.Combine(_work, "somente-leitura")).FullName;
        _deny.Deny(parent, FileSystemRights.CreateDirectories);

        new FileOperationService().TryCreateDirectory(parent, "nova", out var error).Should().BeFalse();

        ShouldBePortugueseAccessDenied(error, "nova");
        Directory.Exists(Path.Combine(parent, "nova")).Should().BeFalse();
    }

    [Fact]
    public void Renaming_a_protected_file_is_denied_in_portuguese()
    {
        var file = NewFile("renomear", "antigo.txt");
        _deny.MakeUndeletable(file);

        new FileOperationService().TryRename(file, "novo.txt", out _, out var error).Should().BeFalse();

        ShouldBePortugueseAccessDenied(error, "antigo.txt");
        File.Exists(file).Should().BeTrue();
    }

    [Fact]
    public void Copying_into_a_folder_that_refuses_new_files_is_denied_in_portuguese()
    {
        var source = NewFile("origem", "arquivo.txt");
        var destination = Directory.CreateDirectory(Path.Combine(_work, "destino")).FullName;
        _deny.Deny(destination, FileSystemRights.CreateFiles);

        new FileOperationService().TryCopy(source, destination, out var error).Should().BeFalse();

        // O Windows só diz "acesso negado": pode ser a origem ou o destino, então a mensagem cita os dois.
        error.Should().StartWith("arquivo.txt: acesso negado ao copiar '");
        error.Should().Contain(source).And.Contain(Path.Combine(destination, "arquivo.txt"));
        error.Should().EndWith(FileErrorText.AccessDeniedMarker.ToString());
        error.Should().NotContain("Access to the path");
        Directory.GetFiles(destination).Should().BeEmpty();
    }

    [Fact]
    public void Moving_a_protected_file_is_denied_in_portuguese_and_keeps_it_in_place()
    {
        var file = NewFile("mover", "fixo.txt");
        _deny.MakeUndeletable(file);
        var destination = Directory.CreateDirectory(Path.Combine(_work, "alvo")).FullName;

        new FileOperationService().TryMove(file, destination, out var error).Should().BeFalse();

        error.Should().StartWith("fixo.txt: acesso negado ao mover '");
        FileErrorText.IsAccessDenied(error!).Should().BeTrue(error);
        error.Should().NotContain("Access to the path");
        File.Exists(file).Should().BeTrue();
    }

    [Fact]
    public void Compressing_into_a_folder_that_refuses_new_files_is_denied_in_portuguese()
    {
        var source = NewFile("compactar", "dado.txt");
        var destination = Directory.CreateDirectory(Path.Combine(_work, "saida")).FullName;
        _deny.Deny(destination, FileSystemRights.CreateFiles);

        new FileOperationService().TryCompress([source], destination, ArchiveFormat.Zip, out _, out var error)
            .Should().BeFalse();

        error.Should().NotBeNull();
        FileErrorText.IsAccessDenied(error!).Should().BeTrue(error);
        error.Should().NotContain("Access to the path");
    }
}
