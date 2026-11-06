using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using DoraExplorer.App.Models;
using DoraExplorer.App.Services;
using FluentAssertions;
using Xunit;

namespace DoraExplorer.App.Tests;

public sealed class NfsXdrTests
{
    [Fact]
    public void Xdr_values_round_trip_and_variable_data_is_padded_to_four_bytes()
    {
        var writer = new XdrWriter();
        writer.UInt32(7).Int32(-2).UInt64(0x0102030405060708UL).Bool(true).Opaque([1, 2, 3]).String("ação");
        var bytes = writer.ToArray();
        (bytes.Length % 4).Should().Be(0);

        var reader = new XdrReader(bytes, 0, bytes.Length);
        reader.UInt32().Should().Be(7u);
        reader.Int32().Should().Be(-2);
        reader.UInt64().Should().Be(0x0102030405060708UL);
        reader.Bool().Should().BeTrue();
        reader.Opaque(16).Should().Equal(1, 2, 3);
        reader.String(16).Should().Be("ação");
        reader.Remaining.Should().Be(0);
    }

    [Fact]
    public void Xdr_reader_refuses_fields_above_the_limit_and_truncated_messages()
    {
        var big = new XdrWriter().Opaque(new byte[100]).ToArray();
        var tooBig = () => new XdrReader(big, 0, big.Length).Opaque(99);
        tooBig.Should().Throw<XdrException>();

        // Um tamanho declarado enorme não pode virar uma alocação enorme.
        var lying = new XdrWriter().UInt32(0xFFFFFFF0).ToArray();
        var huge = () => new XdrReader(lying, 0, lying.Length).Opaque(int.MaxValue);
        huge.Should().Throw<XdrException>();

        var truncated = new XdrWriter().UInt32(10).ToArray();
        var cut = () => new XdrReader(truncated, 0, truncated.Length).Opaque(100);
        cut.Should().Throw<XdrException>();

        var shortInt = () => new XdrReader([0, 0], 0, 2).UInt32();
        shortInt.Should().Throw<XdrException>();
    }

    [Theory]
    [InlineData("", true, 0u, 0u)]
    [InlineData("   ", true, 0u, 0u)]
    [InlineData("1000", true, 1000u, 1000u)]
    [InlineData("1000:100", true, 1000u, 100u)]
    [InlineData(" 12 : 34 ", true, 12u, 34u)]
    [InlineData("nobody", false, 0u, 0u)]
    [InlineData("1:2:3", false, 0u, 0u)]
    [InlineData("-1", false, 0u, 0u)]
    [InlineData("1:", false, 0u, 0u)]
    public void The_nfs_user_is_a_uid_and_gid_pair_and_anything_else_is_refused(string text, bool valid, uint uid, uint gid)
    {
        RpcIdentity.TryParse(text, out var identity).Should().Be(valid);
        if (valid)
        {
            identity.Uid.Should().Be(uid);
            identity.Gid.Should().Be(gid);
        }
    }

    [Theory]
    [InlineData("127.0.0.1", true)]
    [InlineData("10.1.2.3", true)]
    [InlineData("172.16.0.1", true)]
    [InlineData("172.31.255.255", true)]
    [InlineData("172.32.0.1", false)]
    [InlineData("172.15.255.255", false)]
    [InlineData("192.168.1.50", true)]
    [InlineData("192.169.1.50", false)]
    [InlineData("169.254.10.10", true)]
    [InlineData("8.8.8.8", false)]
    [InlineData("203.0.113.9", false)]
    [InlineData("::1", true)]
    [InlineData("fe80::1", true)]
    [InlineData("fd00::1", true)]
    [InlineData("2001:db8::1", false)]
    [InlineData("::ffff:192.168.0.5", true)]
    [InlineData("::ffff:8.8.8.8", false)]
    public void The_nfs_server_only_serves_local_network_addresses(string address, bool expected) =>
        NetworkAddressPolicy.IsLocalNetwork(IPAddress.Parse(address)).Should().Be(expected);

    [Theory]
    [InlineData("/srv/Dados", "Dados")]
    [InlineData("C:\\Users\\ana\\Meus Arquivos\\", "MeusArquivos")]
    [InlineData("/x/a b$c", "abc")]
    [InlineData("/x/...", "compartilhado")]
    public void Share_names_come_from_the_folder_name(string folder, string expected) =>
        ShareNames.FromFolder(folder).Should().Be(expected);
}

public sealed class NfsExportResolutionTests
{
    private static List<string[]> Exports(params string[] paths) => paths.Select(NfsRemoteSession.SplitPath).ToList();

    [Fact]
    public void The_most_specific_export_covers_the_path_when_one_export_contains_another()
    {
        var exports = Exports("/srv", "/srv/dados", "/srv/dados/fotos", "/outra");

        NfsRemoteSession.LongestExport(exports, NfsRemoteSession.SplitPath("/srv/dados/fotos/2024/a.jpg"))
            .Should().Equal("srv", "dados", "fotos");
        NfsRemoteSession.LongestExport(exports, NfsRemoteSession.SplitPath("/srv/dados/doc.txt"))
            .Should().Equal("srv", "dados");
        NfsRemoteSession.LongestExport(exports, NfsRemoteSession.SplitPath("/srv/outro"))
            .Should().Equal("srv");
        NfsRemoteSession.LongestExport(exports, NfsRemoteSession.SplitPath("/outra"))
            .Should().Equal("outra");
    }

    [Fact]
    public void A_path_outside_every_export_has_no_export_and_names_must_match_whole_segments()
    {
        var exports = Exports("/srv/dados");

        NfsRemoteSession.LongestExport(exports, NfsRemoteSession.SplitPath("/srv")).Should().BeNull();
        NfsRemoteSession.LongestExport(exports, NfsRemoteSession.SplitPath("/srv/dados2/x")).Should().BeNull(
            "\"dados2\" não é \"dados\"");
        NfsRemoteSession.LongestExport(exports, NfsRemoteSession.SplitPath("/")).Should().BeNull();
    }

    [Fact]
    public void An_export_of_the_server_root_covers_everything()
    {
        NfsRemoteSession.LongestExport(Exports("/"), NfsRemoteSession.SplitPath("/qualquer/coisa")).Should().BeEmpty();
    }

    [Fact]
    public void Virtual_folders_list_the_next_segment_of_each_export_once()
    {
        var exports = Exports("/srv/dados", "/srv/fotos", "/srv/fotos/2024", "/mnt/disco", "/raiz");

        NfsRemoteSession.VirtualChildren(exports, []).Should().BeEquivalentTo("srv", "mnt", "raiz");
        NfsRemoteSession.VirtualChildren(exports, NfsRemoteSession.SplitPath("/srv")).Should().BeEquivalentTo("dados", "fotos");
        NfsRemoteSession.VirtualChildren(exports, NfsRemoteSession.SplitPath("/mnt")).Should().Equal("disco");
        NfsRemoteSession.VirtualChildren(exports, NfsRemoteSession.SplitPath("/srv/dados")).Should().BeEmpty(
            "a exportação em si é uma pasta de verdade, não virtual");
        NfsRemoteSession.VirtualChildren(exports, NfsRemoteSession.SplitPath("/nada")).Should().BeEmpty();
    }

    [Theory]
    [InlineData("/a/b", "a", "b")]
    [InlineData("a//b/", "a", "b")]
    [InlineData("\\a\\b", "a", "b")]
    public void Paths_are_split_into_segments_regardless_of_slashes(string path, string first, string second)
    {
        NfsRemoteSession.SplitPath(path).Should().Equal(first, second);
    }
}

public sealed class NfsServerTests : IDisposable
{
    private readonly string _root = ShareTestSupport.NewFolder("dora-nfs-root-");
    private readonly string _outside = ShareTestSupport.NewFolder("dora-nfs-out-");
    private readonly NetworkServerService _service = new(new NetworkServerLogStore());
    private int _port;

    public void Dispose()
    {
        _service.Dispose();
        ShareTestSupport.DeleteWhenReleased(_root);
        ShareTestSupport.DeleteWhenReleased(_outside);
    }

    private string Export => "/" + ShareNames.FromFolder(_root);

    private NetworkConnectionInfo Config(bool readOnly, int port) =>
        new()
        {
            Protocol = NetworkProtocol.Nfs,
            Host = "127.0.0.1",
            Port = port,
            RemotePath = _root,
            ReadOnly = readOnly
        };

    private void Start(bool readOnly = false)
    {
        _port = ShareTestSupport.FreeTcpPort();
        _service.TryStart(Config(readOnly, _port), out var error).Should().BeTrue(error);
    }

    private NetworkConnectionInfo Connection(string? user = null) =>
        new() { Protocol = NetworkProtocol.Nfs, Host = "127.0.0.1", Port = _port, Username = user };

    private NfsClient Client() => NfsClient.Connect("127.0.0.1", _port, RpcIdentity.Anonymous, TimeSpan.FromSeconds(10));

    // ---- Operações de arquivo, pelo cliente do programa ---------------------------------------

    [Fact]
    public void A_client_can_list_create_download_rename_and_delete()
    {
        Start();
        File.WriteAllText(Path.Combine(_root, "existente.txt"), "ola mundo");
        Directory.CreateDirectory(Path.Combine(_root, "pasta"));
        using var session = NfsRemoteSession.Open(Connection());

        var names = session.List(Export).ToDictionary(e => e.Name);
        names.Keys.Should().BeEquivalentTo("existente.txt", "pasta");
        names["existente.txt"].Length.Should().Be(9);
        names["existente.txt"].IsDirectory.Should().BeFalse();
        names["pasta"].IsDirectory.Should().BeTrue();
        names["existente.txt"].Modified!.Value.Should().BeCloseTo(File.GetLastWriteTime(Path.Combine(_root, "existente.txt")), TimeSpan.FromSeconds(2));

        using var download = new MemoryStream();
        session.Download(Export + "/existente.txt", download, _ => { }, CancellationToken.None);
        System.Text.Encoding.UTF8.GetString(download.ToArray()).Should().Be("ola mundo");

        session.CreateDirectory(Export + "/nova");
        Directory.Exists(Path.Combine(_root, "nova")).Should().BeTrue();

        session.Upload(new MemoryStream([1, 2, 3, 4]), Export + "/nova/dados.bin", _ => { }, CancellationToken.None);
        File.ReadAllBytes(Path.Combine(_root, "nova", "dados.bin")).Should().Equal(1, 2, 3, 4);

        session.Rename(Export + "/nova/dados.bin", Export + "/nova/renomeado.bin");
        File.Exists(Path.Combine(_root, "nova", "dados.bin")).Should().BeFalse();
        File.Exists(Path.Combine(_root, "nova", "renomeado.bin")).Should().BeTrue();

        session.Stat(Export + "/nova/renomeado.bin")!.Length.Should().Be(4);
        session.Stat(Export + "/nova/nao-existe.bin").Should().BeNull();

        session.DeleteFile(Export + "/nova/renomeado.bin");
        session.DeleteEmptyDirectory(Export + "/nova");
        Directory.Exists(Path.Combine(_root, "nova")).Should().BeFalse();
    }

    [Fact]
    public void A_file_of_several_megabytes_round_trips_in_many_blocks_with_progress()
    {
        Start();
        var content = ShareTestSupport.Pattern(3_500_000);
        using var session = NfsRemoteSession.Open(Connection());

        var uploaded = new List<long>();
        session.Upload(new MemoryStream(content), Export + "/grande.bin", uploaded.Add, CancellationToken.None);
        File.ReadAllBytes(Path.Combine(_root, "grande.bin")).Should().Equal(content);
        uploaded.Should().NotBeEmpty();
        uploaded.Should().BeInAscendingOrder();
        uploaded[^1].Should().Be(content.Length);

        using var download = new MemoryStream();
        var downloaded = new List<long>();
        session.Download(Export + "/grande.bin", download, downloaded.Add, CancellationToken.None);
        download.ToArray().Should().Equal(content);
        downloaded[^1].Should().Be(content.Length);
    }

    [Fact]
    public void Uploading_over_a_longer_file_leaves_only_the_new_content()
    {
        Start();
        File.WriteAllBytes(Path.Combine(_root, "a.bin"), ShareTestSupport.Pattern(50_000));
        using var session = NfsRemoteSession.Open(Connection());

        session.Upload(new MemoryStream([9, 9, 9]), Export + "/a.bin", _ => { }, CancellationToken.None);

        File.ReadAllBytes(Path.Combine(_root, "a.bin")).Should().Equal(9, 9, 9);
    }

    [Fact]
    public void An_empty_file_is_uploaded_and_downloaded()
    {
        Start();
        using var session = NfsRemoteSession.Open(Connection());

        session.Upload(new MemoryStream(), Export + "/vazio.txt", _ => { }, CancellationToken.None);
        File.Exists(Path.Combine(_root, "vazio.txt")).Should().BeTrue();
        new FileInfo(Path.Combine(_root, "vazio.txt")).Length.Should().Be(0);

        using var download = new MemoryStream();
        session.Download(Export + "/vazio.txt", download, _ => { }, CancellationToken.None);
        download.Length.Should().Be(0);
    }

    [Fact]
    public void A_folder_with_hundreds_of_entries_is_listed_completely_across_pages()
    {
        Start();
        for (var i = 0; i < 700; i++)
        {
            File.WriteAllText(Path.Combine(_root, $"arquivo-com-nome-bem-longo-para-ocupar-bytes-{i:D4}.txt"), "x");
        }

        using var session = NfsRemoteSession.Open(Connection());

        var names = session.List(Export).Select(e => e.Name).ToList();

        names.Should().HaveCount(700);
        names.Distinct().Should().HaveCount(700);
    }

    [Fact]
    public void The_plain_readdir_pages_through_a_big_folder_and_matches_readdirplus()
    {
        Start();
        for (var i = 0; i < 700; i++)
        {
            File.WriteAllText(Path.Combine(_root, $"arquivo-com-nome-bem-longo-para-ocupar-bytes-{i:D4}.txt"), "x");
        }

        Directory.CreateDirectory(Path.Combine(_root, "pasta"));
        using var client = Client();
        var root = client.Mount(Export);

        var plain = client.ReadDirectory(root, usePlus: false);
        var plus = client.ReadDirectory(root);

        plain.Should().HaveCount(701);
        plain.Select(e => e.Name).Should().BeEquivalentTo(plus.Select(e => e.Name));
        plain.Select(e => e.Name).Distinct().Should().HaveCount(701);
        plain.Should().OnlyContain(e => e.Attributes == null && e.Handle == null, "o READDIR simples só traz nomes");
        plus.Should().OnlyContain(e => e.Attributes != null && e.Handle != null);
        plain.Single(e => e.Name == "pasta").FileId.Should().Be(plus.Single(e => e.Name == "pasta").FileId, "o número do arquivo é o mesmo nos dois");
    }

    [Fact]
    public void The_plain_readdir_also_hides_the_name_of_a_link_that_points_outside()
    {
        Start();
        ShareTestSupport.CreateFolderLink(Path.Combine(_root, "atalho"), _outside);
        File.WriteAllText(Path.Combine(_root, "normal.txt"), "ok");
        using var client = Client();
        var root = client.Mount(Export);

        client.ReadDirectory(root, usePlus: false).Select(e => e.Name).Should().Equal("normal.txt");
    }

    [Fact]
    public void Cancelling_a_download_stops_it_and_reports_the_cancellation()
    {
        Start();
        File.WriteAllBytes(Path.Combine(_root, "grande.bin"), ShareTestSupport.Pattern(2_000_000));
        using var session = NfsRemoteSession.Open(Connection());
        using var cancellation = new CancellationTokenSource();

        var act = () => session.Download(Export + "/grande.bin", new MemoryStream(), bytes =>
        {
            if (bytes > 100_000)
            {
                cancellation.Cancel();
            }
        }, cancellation.Token);

        act.Should().Throw<OperationCanceledException>();
    }

    // ---- Exportações e pastas virtuais ---------------------------------------------------------

    [Fact]
    public void The_root_and_the_folders_above_the_export_are_virtual_folders()
    {
        Start();
        using var session = NfsRemoteSession.Open(Connection());

        var root = session.List("/");
        root.Should().ContainSingle().Which.Should().BeEquivalentTo(new RemoteEntry(ShareNames.FromFolder(_root), true, 0));
        session.Stat("/")!.IsDirectory.Should().BeTrue();
        session.Stat("/nao-exportado").Should().BeNull();

        var create = () => session.CreateDirectory("/solta");
        create.Should().Throw<IOException>().WithMessage("*pastas compartilhadas*");
    }

    [Fact]
    public void The_client_lists_the_exports_and_mounts_only_what_the_server_offers()
    {
        Start();
        using var client = Client();

        client.ListExports().Should().Equal(Export);

        var handle = client.Mount(Export);
        client.GetAttributes(handle).IsDirectory.Should().BeTrue();

        var missing = () => client.Mount("/outra");
        missing.Should().Throw<NfsException>().Which.Status.Should().Be(NfsStatus.NoEnt);

        // O que está acima da exportação (a raiz do disco, outra pasta) não pode ser montado.
        var parent = () => client.Mount("/");
        parent.Should().Throw<NfsException>();
        var escape = () => client.Mount(Export + "/../..");
        escape.Should().Throw<NfsException>();
    }

    [Fact]
    public void Mounting_a_subfolder_gives_a_handle_for_that_folder()
    {
        Start();
        Directory.CreateDirectory(Path.Combine(_root, "sub"));
        File.WriteAllText(Path.Combine(_root, "sub", "dentro.txt"), "x");
        using var client = Client();

        var handle = client.Mount(Export + "/sub");

        client.ReadDirectory(handle).Select(e => e.Name).Should().Equal("dentro.txt");
    }

    // ---- Segurança -----------------------------------------------------------------------------

    [Fact]
    public void Looking_up_dot_dot_from_the_root_stays_in_the_shared_folder()
    {
        Start();
        File.WriteAllText(Path.Combine(_outside, "segredo.txt"), "segredo");
        using var client = Client();
        var root = client.Mount(Export);

        var parent = client.Lookup(root, "..");

        parent.Should().NotBeNull();
        parent!.Value.Handle.Should().Equal(root, "o pai da raiz é a própria raiz");
        client.Lookup(root, "segredo.txt").Should().BeNull();
    }

    [Theory]
    [InlineData("../evil.txt")]
    [InlineData("..\\evil.txt")]
    [InlineData("sub/evil.txt")]
    [InlineData("CON")]
    [InlineData("nul.txt")]
    [InlineData("termina-com-ponto.")]
    public void Dangerous_names_are_refused_and_nothing_is_created(string name)
    {
        Start();
        using var client = Client();
        var root = client.Mount(Export);

        var act = () => client.CreateOrTruncate(root, name, 0x1A4);

        act.Should().Throw<NfsException>();
        Directory.GetFileSystemEntries(_root).Should().BeEmpty();
        Directory.GetFileSystemEntries(_outside).Should().BeEmpty();
        File.Exists(Path.Combine(Path.GetDirectoryName(_root)!, "evil.txt")).Should().BeFalse();
    }

    [Theory]
    [InlineData("C:evil.txt")]
    [InlineData("evil.txt:stream")]
    public void Drive_letters_and_alternate_streams_are_refused_where_windows_would_honour_them(string name)
    {
        if (!OperatingSystem.IsWindows())
        {
            return; // fora do Windows ":" é um caractere comum de nome; o servidor roda no Windows
        }

        Start();
        using var client = Client();
        var root = client.Mount(Export);

        var act = () => client.CreateOrTruncate(root, name, 0x1A4);

        act.Should().Throw<NfsException>();
        Directory.GetFileSystemEntries(_root).Should().BeEmpty();
    }

    [Fact]
    public void A_link_that_points_outside_is_hidden_and_cannot_be_followed()
    {
        Start();
        File.WriteAllText(Path.Combine(_outside, "segredo.txt"), "segredo");
        ShareTestSupport.CreateFolderLink(Path.Combine(_root, "atalho"), _outside);
        File.WriteAllText(Path.Combine(_root, "normal.txt"), "ok");
        using var client = Client();
        var root = client.Mount(Export);

        client.ReadDirectory(root).Select(e => e.Name).Should().Equal("normal.txt");
        var lookup = () => client.Lookup(root, "atalho");
        lookup.Should().Throw<NfsException>().Which.Status.Should().Be(NfsStatus.Access);
        var create = () => client.CreateOrTruncate(root, "atalho", 0x1A4);
        create.Should().Throw<NfsException>();
        File.ReadAllText(Path.Combine(_outside, "segredo.txt")).Should().Be("segredo");
    }

    [Fact]
    public void A_forged_or_foreign_handle_is_refused_as_stale()
    {
        Start();
        using var client = Client();
        _ = client.Mount(Export);

        var forged = () => client.GetAttributes(new byte[16]);
        forged.Should().Throw<NfsException>().Which.Status.Should().Be(NfsStatus.Stale);

        var wrongSize = () => client.GetAttributes([1, 2, 3]);
        wrongSize.Should().Throw<NfsException>().Which.Status.Should().Be(NfsStatus.Stale);
    }

    [Fact]
    public void Handles_from_a_previous_server_instance_are_stale_and_mounting_again_works()
    {
        Start();
        byte[] oldHandle;
        using (var client = Client())
        {
            oldHandle = client.Mount(Export);
        }

        // Outra porta: uma instância nova do servidor (como depois de parar e iniciar) tem uma marca nova nos identificadores.
        _service.StopServer(NetworkProtocol.Nfs);
        Start();

        using var again = Client();
        var stale = () => again.GetAttributes(oldHandle);
        stale.Should().Throw<NfsException>().Which.Status.Should().Be(NfsStatus.Stale);
        again.GetAttributes(again.Mount(Export)).IsDirectory.Should().BeTrue();
    }

    [Fact]
    public void A_read_only_server_serves_reads_and_refuses_every_change()
    {
        File.WriteAllText(Path.Combine(_root, "fixo.txt"), "conteudo");
        Directory.CreateDirectory(Path.Combine(_root, "pasta"));
        Start(readOnly: true);
        using var client = Client();
        var root = client.Mount(Export);

        using var session = NfsRemoteSession.Open(Connection());
        using var download = new MemoryStream();
        session.Download(Export + "/fixo.txt", download, _ => { }, CancellationToken.None);
        download.Length.Should().Be(8);
        (client.GetAttributes(root).Mode & 0x92).Should().Be(0u, "sem permissão de escrita nos atributos");

        void Refused(Action change) =>
            change.Should().Throw<NfsException>().Which.Status.Should().Be(NfsStatus.RoFs);

        Refused(() => client.CreateOrTruncate(root, "novo.txt", 0x1A4));
        Refused(() => client.MakeDirectory(root, "nova", 0x1ED));
        Refused(() => client.Remove(root, "fixo.txt"));
        Refused(() => client.RemoveDirectory(root, "pasta"));
        Refused(() => client.Rename(root, "fixo.txt", root, "outro.txt"));
        var file = client.Lookup(root, "fixo.txt")!.Value.Handle;
        Refused(() => client.Write(file, 0, [1, 2, 3], 3));
        Refused(() => client.SetAttributes(file, new NfsSetAttributes(Size: 0)));

        File.ReadAllText(Path.Combine(_root, "fixo.txt")).Should().Be("conteudo");
        Directory.GetFileSystemEntries(_root).Should().HaveCount(2);
    }

    [Fact]
    public void Folder_operations_report_the_right_errors()
    {
        Start();
        Directory.CreateDirectory(Path.Combine(_root, "cheia"));
        File.WriteAllText(Path.Combine(_root, "cheia", "x.txt"), "x");
        File.WriteAllText(Path.Combine(_root, "arquivo.txt"), "x");
        using var client = Client();
        var root = client.Mount(Export);

        var exists = () => client.MakeDirectory(root, "cheia", 0x1ED);
        exists.Should().Throw<NfsException>().Which.Status.Should().Be(NfsStatus.Exist);

        var notEmpty = () => client.RemoveDirectory(root, "cheia");
        notEmpty.Should().Throw<NfsException>().Which.Status.Should().Be(NfsStatus.NotEmpty);

        var removeFolder = () => client.Remove(root, "cheia");
        removeFolder.Should().Throw<NfsException>().Which.Status.Should().Be(NfsStatus.IsDir);

        var rmdirFile = () => client.RemoveDirectory(root, "arquivo.txt");
        rmdirFile.Should().Throw<NfsException>().Which.Status.Should().Be(NfsStatus.NotDir);

        var missing = () => client.Remove(root, "nao-existe.txt");
        missing.Should().Throw<NfsException>().Which.Status.Should().Be(NfsStatus.NoEnt);

        Directory.Exists(Path.Combine(_root, "cheia")).Should().BeTrue();
    }

    [Fact]
    public void Moving_a_folder_into_itself_is_refused()
    {
        Start();
        Directory.CreateDirectory(Path.Combine(_root, "a", "b"));
        using var client = Client();
        var root = client.Mount(Export);
        var a = client.Lookup(root, "a")!.Value.Handle;
        var b = client.Lookup(a, "b")!.Value.Handle;

        var act = () => client.Rename(root, "a", b, "a");

        act.Should().Throw<NfsException>().Which.Status.Should().Be(NfsStatus.Inval);
        Directory.Exists(Path.Combine(_root, "a", "b")).Should().BeTrue();
    }

    [Fact]
    public void Renaming_a_folder_keeps_the_handles_of_what_is_inside_it()
    {
        Start();
        Directory.CreateDirectory(Path.Combine(_root, "antes"));
        File.WriteAllText(Path.Combine(_root, "antes", "f.txt"), "conteudo");
        using var client = Client();
        var root = client.Mount(Export);
        var folder = client.Lookup(root, "antes")!.Value.Handle;
        var file = client.Lookup(folder, "f.txt")!.Value.Handle;

        client.Rename(root, "antes", root, "depois");

        var buffer = new byte[64];
        var read = client.Read(file, 0, buffer, buffer.Length, out var eof);
        System.Text.Encoding.UTF8.GetString(buffer, 0, read).Should().Be("conteudo");
        eof.Should().BeTrue();
    }

    // ---- Protocolo -----------------------------------------------------------------------------

    [Fact]
    public void Unsupported_versions_and_procedures_are_answered_with_the_proper_rpc_errors()
    {
        Start();
        using var rpc = RpcClient.Connect("127.0.0.1", _port, RpcIdentity.Anonymous, TimeSpan.FromSeconds(10));

        var version = () => rpc.Call(Rpc.NfsProgram, 4, 0);
        version.Should().Throw<RpcException>().WithMessage("*versão*3 a 3*");

        var mount = () => rpc.Call(Rpc.MountProgram, 1, 0);
        mount.Should().Throw<RpcException>().WithMessage("*versão*");

        var procedure = () => rpc.Call(Rpc.NfsProgram, 3, 99);
        procedure.Should().Throw<RpcException>().WithMessage("*não implementa*");

        var program = () => rpc.Call(123456, 1, 0);
        program.Should().Throw<RpcException>().WithMessage("*não oferece*");

        // Depois dos erros a conexão continua boa.
        rpc.Call(Rpc.NfsProgram, 3, 0);
    }

    private TcpClient RawConnection()
    {
        var tcp = new TcpClient { ReceiveTimeout = 5000, SendTimeout = 5000 };
        tcp.Connect("127.0.0.1", _port);
        return tcp;
    }

    [Fact]
    public void A_record_above_the_size_limit_is_dropped_without_waiting_for_its_body()
    {
        Start();
        using var tcp = RawConnection();
        var stream = tcp.GetStream();
        var header = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(header, 0x80000000u | 16u * 1024 * 1024); // 16 MB: muito além do limite de 1 MB
        stream.Write(header);

        // O servidor fecha a conexão em vez de reservar memória e esperar bytes que talvez nunca venham.
        var read = () =>
        {
            try
            {
                return stream.Read(new byte[16]);
            }
            catch (IOException ex) when (ex.InnerException is SocketException { SocketErrorCode: SocketError.ConnectionReset })
            {
                return 0; // fechada com reset; esperar até o tempo limite NÃO conta como fechada
            }
        };
        read().Should().Be(0);

        using var healthy = Client();
        healthy.ListExports().Should().ContainSingle("o servidor segue atendendo os outros clientes").Which.Should().Be(Export);
    }

    [Fact]
    public void A_garbled_call_is_answered_with_garbage_args_and_a_runt_record_closes_the_connection()
    {
        Start();
        using var tcp = RawConnection();
        var stream = tcp.GetStream();

        Rpc.WriteRecord(stream, new XdrWriter().UInt32(0xCAFE).UInt32(0x6A756E6B).ToArray()); // número, mas não é uma chamada
        var reply = Rpc.ReadRecord(stream)!;
        var reader = new XdrReader(reply, 0, reply.Length);
        reader.UInt32().Should().Be(0xCAFEu);
        reader.UInt32().Should().Be(Rpc.MsgReply);
        reader.UInt32().Should().Be(0u, "aceita, mas...");
        _ = reader.UInt32();
        _ = reader.UInt32();
        reader.UInt32().Should().Be(Rpc.AcceptGarbageArgs);

        Rpc.WriteRecord(stream, [1, 2]); // menos de 4 bytes: nem o número da chamada dá para ler
        Rpc.ReadRecord(stream).Should().BeNull("o servidor encerra a conexão");
    }

    [Fact]
    public void The_portmap_on_the_same_port_points_back_at_the_server()
    {
        Start();
        using var rpc = RpcClient.Connect("127.0.0.1", _port, RpcIdentity.Anonymous, TimeSpan.FromSeconds(10));

        uint GetPort(uint program, uint version, uint protocol) =>
            rpc.Call(Rpc.PortmapProgram, 2, 3, w => w.UInt32(program).UInt32(version).UInt32(protocol).UInt32(0)).UInt32();

        GetPort(Rpc.NfsProgram, 3, 6).Should().Be((uint)_port);
        GetPort(Rpc.MountProgram, 3, 6).Should().Be((uint)_port);
        GetPort(Rpc.NfsProgram, 3, 17).Should().Be(0u, "só TCP");
        GetPort(Rpc.NfsProgram, 2, 6).Should().Be(0u, "só a versão 3");
    }

    [Fact]
    public void The_mount_port_comes_from_the_portmap_or_falls_back_to_the_nfs_port()
    {
        Start();
        var timeout = TimeSpan.FromSeconds(5);

        // Um "portmap" que responde (o próprio servidor) e outro que ninguém atende.
        NfsClient.DiscoverMountPort("127.0.0.1", 1234, RpcIdentity.Anonymous, timeout, portmapPort: _port).Should().Be(_port);
        NfsClient.DiscoverMountPort("127.0.0.1", 1235, RpcIdentity.Anonymous, timeout, portmapPort: ShareTestSupport.FreeTcpPort())
            .Should().Be(1235);
    }

    [Fact]
    public void The_caller_uid_and_gid_are_reported_as_the_owner_of_everything()
    {
        Start();
        File.WriteAllText(Path.Combine(_root, "a.txt"), "x");
        using var client = NfsClient.Connect("127.0.0.1", _port, new RpcIdentity(1000, 100, "teste"), TimeSpan.FromSeconds(10));
        var root = client.Mount(Export);

        var attributes = client.Lookup(root, "a.txt")!.Value.Attributes;

        attributes.Uid.Should().Be(1000u);
        attributes.Gid.Should().Be(100u);
        attributes.Type.Should().Be(NfsFileType.Regular);
    }

    [Fact]
    public void An_invalid_nfs_user_is_reported_before_connecting()
    {
        Start();

        var act = () => NfsRemoteSession.Open(Connection("joao"));

        act.Should().Throw<IOException>().WithMessage("*uid*1000:1000*".Replace("uid", "número"));
    }

    // ---- Serviço de servidor ---------------------------------------------------------------------

    [Fact]
    public void The_nfs_server_starts_without_a_login_and_rejects_a_second_start_and_a_missing_folder()
    {
        _port = ShareTestSupport.FreeTcpPort();
        _service.TryStart(Config(false, _port), out var error).Should().BeTrue(error);
        _service.IsRunning(NetworkProtocol.Nfs).Should().BeTrue();

        _service.TryStart(Config(false, _port), out error).Should().BeFalse();
        error.Should().NotBeNullOrEmpty();

        _service.StopServer(NetworkProtocol.Nfs);
        _service.IsRunning(NetworkProtocol.Nfs).Should().BeFalse();

        var missing = Config(false, ShareTestSupport.FreeTcpPort());
        missing.RemotePath = Path.Combine(_root, "nao-existe");
        _service.TryStart(missing, out error).Should().BeFalse();
        error.Should().Contain("não existe");
    }

    [Fact]
    public void Stopping_the_server_closes_open_connections()
    {
        Start();
        using var client = Client();
        var root = client.Mount(Export);

        _service.StopServer(NetworkProtocol.Nfs);

        var act = () => client.GetAttributes(root);
        act.Should().Throw<Exception>();
    }
}
