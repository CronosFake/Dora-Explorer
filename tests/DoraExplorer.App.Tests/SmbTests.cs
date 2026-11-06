using DoraExplorer.App.Models;
using DoraExplorer.App.Services;
using FluentAssertions;
using SMBLibrary;
using Xunit;

namespace DoraExplorer.App.Tests;

/// <summary>Contrato com a SMBLibrary: o programa usa sobrecargas internas dela e não pode perdê-las sem ninguém ver.</summary>
public sealed class SmbLibraryContractTests
{
    [Fact]
    public void The_internal_overloads_that_take_a_port_still_exist()
    {
        SmbLibraryAccess.SupportsCustomPorts.Should().BeTrue(
            "sem elas o cliente e o servidor SMB só funcionariam na porta 445 (o Windows costuma já usá-la)");
    }

    [Fact]
    public void The_package_is_pinned_to_the_exact_version_the_contract_was_checked_against()
    {
        var project = File.ReadAllText(ShareTestSupport.LocateSource(@"src\DoraExplorer.App\DoraExplorer.App.csproj"));

        project.Should().Contain("<PackageReference Include=\"SMBLibrary\" Version=\"[1.5.8.1]\" />",
            "atualizar o pacote exige rever SmbLibraryAccess e rodar estes testes de novo");
    }
}

public sealed class SmbHelperTests
{
    [Theory]
    [InlineData("", "", "")]
    [InlineData("ana", "", "ana")]
    [InlineData("  ana  ", "", "ana")]
    [InlineData("EMPRESA\\ana", "EMPRESA", "ana")]
    [InlineData("ana@empresa.com", "", "ana@empresa.com")]
    [InlineData("\\ana", "", "\\ana")]
    public void The_smb_account_may_carry_a_domain(string account, string domain, string user)
    {
        SmbRemoteSession.SplitAccount(account).Should().Be((domain, user));
    }

    [Fact]
    public void A_missing_account_is_an_empty_user_without_domain() =>
        SmbRemoteSession.SplitAccount(null).Should().Be((string.Empty, string.Empty));

    [Theory]
    [InlineData("/", null, "")]
    [InlineData("", null, "")]
    [InlineData("/Fotos", "Fotos", "")]
    [InlineData("/Fotos/", "Fotos", "")]
    [InlineData("/Fotos/2024/a.jpg", "Fotos", "2024\\a.jpg")]
    [InlineData("\\Fotos\\2024", "Fotos", "2024")]
    public void The_first_part_of_the_path_is_the_share(string path, string? share, string inside)
    {
        SmbRemoteSession.SplitShare(path).Should().Be((share, inside));
    }

    [Theory]
    [InlineData("*", "qualquer.txt", true)]
    [InlineData("*.*", "semextensao", true)]
    [InlineData("", "x", true)]
    [InlineData("ARQUIVO.TXT", "arquivo.txt", true)]
    [InlineData("arquivo.txt", "outro.txt", false)]
    [InlineData("*.txt", "A.TXT", true)]
    [InlineData("*.txt", "a.txt.bak", false)]
    [InlineData("a?c", "abc", true)]
    [InlineData("a?c", "ac", false)]
    [InlineData("rel*rio*.pdf", "relatório-2024.pdf", true)]
    [InlineData("rel*rio*.pdf", "relat.pdf", false)]
    [InlineData("*a*b*", "xxaxxbxx", true)]
    [InlineData("*a*b*", "xxbxxaxx", false)]
    public void Directory_search_patterns_follow_the_dos_wildcards(string pattern, string name, bool expected)
    {
        SmbWildcard.IsMatch(pattern, name).Should().Be(expected);
    }

    [Theory]
    [InlineData(NTStatus.STATUS_LOGON_FAILURE, "usuário ou senha")]
    [InlineData(NTStatus.STATUS_ACCESS_DENIED, "acesso negado")]
    [InlineData(NTStatus.STATUS_BAD_NETWORK_NAME, "compartilhamento não existe")]
    [InlineData(NTStatus.STATUS_MEDIA_WRITE_PROTECTED, "somente leitura")]
    [InlineData(NTStatus.STATUS_DISK_FULL, "sem espaço")]
    [InlineData(NTStatus.STATUS_OBJECT_NAME_COLLISION, "já existe")]
    public void Smb_status_codes_are_described_in_portuguese(NTStatus status, string expected)
    {
        SmbLibraryAccess.Describe(status).Should().Contain(expected);
    }
}

public sealed class SmbServerTests : IDisposable
{
    private const string User = "dora";
    private const string Password = "s3nha-forte";

    private readonly string _root = ShareTestSupport.NewFolder("dora-smb-root-");
    private readonly string _outside = ShareTestSupport.NewFolder("dora-smb-out-");
    private readonly NetworkServerService _service = new(new NetworkServerLogStore());
    private int _port;

    public void Dispose()
    {
        _service.Dispose();
        ShareTestSupport.DeleteWhenReleased(_root);
        ShareTestSupport.DeleteWhenReleased(_outside);
    }

    private string Share => "/" + ShareNames.FromFolder(_root);

    private void Start(bool readOnly = false)
    {
        _port = ShareTestSupport.FreeTcpPort();
        _service.TryStart(
            new NetworkConnectionInfo
            {
                Protocol = NetworkProtocol.Smb,
                Host = "127.0.0.1",
                Port = _port,
                Username = User,
                Password = Password,
                RemotePath = _root,
                ReadOnly = readOnly
            },
            out var error).Should().BeTrue(error);
    }

    private SmbRemoteSession Open(string user = User, string password = Password) =>
        SmbRemoteSession.Open(new NetworkConnectionInfo
        {
            Protocol = NetworkProtocol.Smb,
            Host = "127.0.0.1",
            Port = _port,
            Username = user,
            Password = password
        });

    [Fact]
    public void A_client_can_list_download_create_rename_and_delete()
    {
        Start();
        File.WriteAllText(Path.Combine(_root, "existente.txt"), "ola mundo");
        Directory.CreateDirectory(Path.Combine(_root, "pasta"));
        using var session = Open();

        session.List("/").Should().ContainSingle().Which.Name.Should().Be(ShareNames.FromFolder(_root));
        session.Stat("/")!.IsDirectory.Should().BeTrue();
        session.Stat(Share)!.IsDirectory.Should().BeTrue();

        var names = session.List(Share).ToDictionary(e => e.Name);
        names.Keys.Should().BeEquivalentTo("existente.txt", "pasta");
        names["existente.txt"].Length.Should().Be(9);
        names["pasta"].IsDirectory.Should().BeTrue();
        names["existente.txt"].Modified!.Value.Should().BeCloseTo(File.GetLastWriteTime(Path.Combine(_root, "existente.txt")), TimeSpan.FromSeconds(2));

        using var download = new MemoryStream();
        session.Download(Share + "/existente.txt", download, _ => { }, CancellationToken.None);
        System.Text.Encoding.UTF8.GetString(download.ToArray()).Should().Be("ola mundo");

        session.CreateDirectory(Share + "/nova");
        Directory.Exists(Path.Combine(_root, "nova")).Should().BeTrue();
        session.Upload(new MemoryStream([1, 2, 3, 4]), Share + "/nova/dados.bin", _ => { }, CancellationToken.None);
        File.ReadAllBytes(Path.Combine(_root, "nova", "dados.bin")).Should().Equal(1, 2, 3, 4);

        session.Rename(Share + "/nova/dados.bin", Share + "/nova/renomeado.bin");
        File.Exists(Path.Combine(_root, "nova", "dados.bin")).Should().BeFalse();
        File.Exists(Path.Combine(_root, "nova", "renomeado.bin")).Should().BeTrue();

        session.Stat(Share + "/nova/renomeado.bin")!.Length.Should().Be(4);
        session.Stat(Share + "/nova/nao-existe.bin").Should().BeNull();

        session.DeleteFile(Share + "/nova/renomeado.bin");
        session.DeleteEmptyDirectory(Share + "/nova");
        Directory.Exists(Path.Combine(_root, "nova")).Should().BeFalse();
    }

    [Fact]
    public void A_file_of_several_megabytes_round_trips_in_many_blocks_with_progress()
    {
        Start();
        var content = ShareTestSupport.Pattern(3_500_000);
        using var session = Open();

        var uploaded = new List<long>();
        session.Upload(new MemoryStream(content), Share + "/grande.bin", uploaded.Add, CancellationToken.None);
        File.ReadAllBytes(Path.Combine(_root, "grande.bin")).Should().Equal(content);
        uploaded.Should().BeInAscendingOrder();
        uploaded[^1].Should().Be(content.Length);

        using var download = new MemoryStream();
        var downloaded = new List<long>();
        session.Download(Share + "/grande.bin", download, downloaded.Add, CancellationToken.None);
        download.ToArray().Should().Equal(content);
        downloaded[^1].Should().Be(content.Length);
    }

    [Fact]
    public void Uploading_over_a_longer_file_leaves_only_the_new_content_and_an_empty_file_works()
    {
        Start();
        File.WriteAllBytes(Path.Combine(_root, "a.bin"), ShareTestSupport.Pattern(50_000));
        using var session = Open();

        session.Upload(new MemoryStream([9, 9, 9]), Share + "/a.bin", _ => { }, CancellationToken.None);
        File.ReadAllBytes(Path.Combine(_root, "a.bin")).Should().Equal(9, 9, 9);

        session.Upload(new MemoryStream(), Share + "/vazio.txt", _ => { }, CancellationToken.None);
        new FileInfo(Path.Combine(_root, "vazio.txt")).Length.Should().Be(0);
        using var download = new MemoryStream();
        session.Download(Share + "/vazio.txt", download, _ => { }, CancellationToken.None);
        download.Length.Should().Be(0);
    }

    [Fact]
    public void A_folder_with_hundreds_of_entries_is_listed_completely()
    {
        Start();
        for (var i = 0; i < 700; i++)
        {
            File.WriteAllText(Path.Combine(_root, $"arquivo-com-nome-bem-longo-para-ocupar-bytes-{i:D4}.txt"), "x");
        }

        using var session = Open();

        var names = session.List(Share).Select(e => e.Name).ToList();

        names.Should().HaveCount(700);
        names.Distinct().Should().HaveCount(700);
    }

    [Fact]
    public void An_empty_shared_folder_lists_as_empty()
    {
        Start();
        using var session = Open();

        session.List(Share).Should().BeEmpty();
    }

    [Fact]
    public void Cancelling_a_download_stops_it_and_reports_the_cancellation()
    {
        Start();
        File.WriteAllBytes(Path.Combine(_root, "grande.bin"), ShareTestSupport.Pattern(2_000_000));
        using var session = Open();
        using var cancellation = new CancellationTokenSource();

        var act = () => session.Download(Share + "/grande.bin", new MemoryStream(), bytes =>
        {
            if (bytes > 100_000)
            {
                cancellation.Cancel();
            }
        }, cancellation.Token);

        act.Should().Throw<OperationCanceledException>();
    }

    // ---- Login ---------------------------------------------------------------------------------

    [Theory]
    [InlineData("dora", "senha-errada")]
    [InlineData("outro", "s3nha-forte")]
    [InlineData("", "")]
    public void A_wrong_user_or_password_is_refused_with_a_clear_message(string user, string password)
    {
        Start();

        var act = () => Open(user, password);

        act.Should().Throw<IOException>().WithMessage("*127.0.0.1*");
    }

    [Fact]
    public void The_user_name_is_not_case_sensitive_but_the_password_is()
    {
        Start();

        using (Open("DORA", Password))
        {
        }

        var act = () => Open(User, Password.ToUpperInvariant());
        act.Should().Throw<IOException>().WithMessage("*usuário ou senha*");
    }

    [Fact]
    public void A_server_that_nobody_listens_to_is_reported_as_unreachable()
    {
        _port = ShareTestSupport.FreeTcpPort();

        var act = () => Open();

        act.Should().Throw<IOException>().WithMessage("*Não foi possível ligar*");
    }

    // ---- Somente leitura e segurança -------------------------------------------------------------

    [Fact]
    public void A_read_only_server_serves_reads_and_refuses_every_change()
    {
        File.WriteAllText(Path.Combine(_root, "fixo.txt"), "conteudo");
        Directory.CreateDirectory(Path.Combine(_root, "pasta"));
        Start(readOnly: true);
        using var session = Open();

        using var download = new MemoryStream();
        session.Download(Share + "/fixo.txt", download, _ => { }, CancellationToken.None);
        download.Length.Should().Be(8);

        void Refused(Action change) => change.Should().Throw<IOException>().WithMessage("*somente leitura*");

        Refused(() => session.Upload(new MemoryStream([1]), Share + "/novo.txt", _ => { }, CancellationToken.None));
        Refused(() => session.Upload(new MemoryStream([1]), Share + "/fixo.txt", _ => { }, CancellationToken.None));
        Refused(() => session.CreateDirectory(Share + "/nova"));
        Refused(() => session.DeleteFile(Share + "/fixo.txt"));
        Refused(() => session.DeleteEmptyDirectory(Share + "/pasta"));
        Refused(() => session.Rename(Share + "/fixo.txt", Share + "/outro.txt"));

        File.ReadAllText(Path.Combine(_root, "fixo.txt")).Should().Be("conteudo");
        Directory.GetFileSystemEntries(_root).Should().HaveCount(2);
    }

    [Fact]
    public void Paths_with_dot_dot_cannot_reach_outside_the_shared_folder()
    {
        Start();
        File.WriteAllText(Path.Combine(_outside, "segredo.txt"), "segredo");
        using var session = Open();
        var outsideName = Path.GetFileName(_outside);

        var read = () => session.Download(Share + "/../../" + outsideName + "/segredo.txt", new MemoryStream(), _ => { }, CancellationToken.None);
        read.Should().Throw<IOException>();

        // ".." acima da raiz fica na raiz: o arquivo vai para dentro da pasta compartilhada, nunca para fora.
        session.Upload(new MemoryStream([1]), Share + "/../evil.txt", _ => { }, CancellationToken.None);
        File.Exists(Path.Combine(Path.GetDirectoryName(_root)!, "evil.txt")).Should().BeFalse();
        File.Exists(Path.Combine(_root, "evil.txt")).Should().BeTrue();
        Directory.GetFileSystemEntries(_outside).Should().Equal(Path.Combine(_outside, "segredo.txt"));
    }

    [Theory]
    [InlineData("CON")]
    [InlineData("nul.txt")]
    [InlineData("termina-com-ponto.")]
    public void Reserved_and_ambiguous_names_are_refused_and_nothing_is_created(string name)
    {
        Start();
        using var session = Open();

        var act = () => session.Upload(new MemoryStream([1]), Share + "/" + name, _ => { }, CancellationToken.None);

        act.Should().Throw<IOException>();
        Directory.GetFileSystemEntries(_root).Should().BeEmpty();
    }

    [Fact]
    public void A_link_that_points_outside_is_hidden_and_cannot_be_followed()
    {
        Start();
        File.WriteAllText(Path.Combine(_outside, "segredo.txt"), "segredo");
        ShareTestSupport.CreateFolderLink(Path.Combine(_root, "atalho"), _outside);
        File.WriteAllText(Path.Combine(_root, "normal.txt"), "ok");
        using var session = Open();

        session.List(Share).Select(e => e.Name).Should().Equal("normal.txt");
        var list = () => session.List(Share + "/atalho");
        list.Should().Throw<IOException>();
        var read = () => session.Download(Share + "/atalho/segredo.txt", new MemoryStream(), _ => { }, CancellationToken.None);
        read.Should().Throw<IOException>();
        var write = () => session.Upload(new MemoryStream([1]), Share + "/atalho/novo.txt", _ => { }, CancellationToken.None);
        write.Should().Throw<IOException>();
        File.Exists(Path.Combine(_outside, "novo.txt")).Should().BeFalse();
    }

    [Fact]
    public void A_folder_that_is_not_empty_cannot_be_deleted()
    {
        Start();
        Directory.CreateDirectory(Path.Combine(_root, "cheia"));
        File.WriteAllText(Path.Combine(_root, "cheia", "x.txt"), "x");
        using var session = Open();

        var act = () => session.DeleteEmptyDirectory(Share + "/cheia");

        act.Should().Throw<IOException>();
        File.Exists(Path.Combine(_root, "cheia", "x.txt")).Should().BeTrue();
    }

    [Fact]
    public void Creating_a_folder_that_already_exists_fails()
    {
        Start();
        Directory.CreateDirectory(Path.Combine(_root, "existe"));
        using var session = Open();

        var act = () => session.CreateDirectory(Share + "/existe");

        act.Should().Throw<IOException>().WithMessage("*já existe*");
    }

    [Fact]
    public void A_share_that_does_not_exist_is_reported()
    {
        Start();
        using var session = Open();

        session.Stat("/nao-existe").Should().BeNull();
        var act = () => session.List("/nao-existe");
        act.Should().Throw<IOException>().WithMessage("*não existe*");
    }

    // ---- Serviço de servidor ---------------------------------------------------------------------

    [Fact]
    public void The_smb_server_reports_its_share_and_refuses_a_second_start()
    {
        _port = ShareTestSupport.FreeTcpPort();
        var logs = new NetworkServerLogStore();
        using var service = new NetworkServerService(logs);
        var config = new NetworkConnectionInfo
        {
            Protocol = NetworkProtocol.Smb, Host = "127.0.0.1", Port = _port, Username = User, Password = Password, RemotePath = _root
        };

        service.TryStart(config, out var error).Should().BeTrue(error);
        logs.GetText(NetworkProtocol.Smb).Should().Contain("\\\\127.0.0.1\\" + ShareNames.FromFolder(_root));
        logs.GetText(NetworkProtocol.Smb).Should().Contain("SMB 1");

        service.TryStart(config, out error).Should().BeFalse();
        error.Should().NotBeNullOrEmpty();

        service.StopServer(NetworkProtocol.Smb);
        service.IsRunning(NetworkProtocol.Smb).Should().BeFalse();
    }
}
