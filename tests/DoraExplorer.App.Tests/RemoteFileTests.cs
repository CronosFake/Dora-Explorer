using System.Net;
using System.Net.Sockets;
using DoraExplorer.App.Models;
using DoraExplorer.App.Services;
using FluentAssertions;
using Xunit;

namespace DoraExplorer.App.Tests;

/// <summary>Servidor de mentira em memória: guarda arquivos e pastas e simula falhas de rede.</summary>
internal sealed class FakeRemoteServer
{
    public Dictionary<string, byte[]> Files { get; } = new(StringComparer.Ordinal);

    public HashSet<string> Directories { get; } = new(StringComparer.Ordinal) { "/" };

    public HashSet<string> Links { get; } = new(StringComparer.Ordinal);

    public long? FailDownloadAfterBytes { get; set; }

    public long? FailUploadAfterBytes { get; set; }

    public bool BlockUploadUntilDisposed { get; set; }

    public int DisposedSessions { get; set; }

    public IRemoteSession Open() => new Session(this);

    private sealed class Session(FakeRemoteServer server) : IRemoteSession
    {
        private readonly ManualResetEventSlim _disposed = new();

        public RemoteEntry? Stat(string remotePath)
        {
            if (server.Directories.Contains(remotePath))
            {
                return new RemoteEntry(RemoteTransferEngine.LastSegment(remotePath), true, 0);
            }

            if (server.Links.Contains(remotePath))
            {
                return new RemoteEntry(RemoteTransferEngine.LastSegment(remotePath), false, 0, IsLink: true);
            }

            return server.Files.TryGetValue(remotePath, out var data)
                ? new RemoteEntry(RemoteTransferEngine.LastSegment(remotePath), false, data.Length)
                : null;
        }

        public IReadOnlyList<RemoteEntry> List(string remoteDirectory)
        {
            var prefix = remoteDirectory.TrimEnd('/') + "/";
            IEnumerable<RemoteEntry> Children(IEnumerable<string> paths, Func<string, RemoteEntry> make) =>
                paths.Where(p => p.StartsWith(prefix, StringComparison.Ordinal) && p.Length > prefix.Length && !p[prefix.Length..].Contains('/', StringComparison.Ordinal))
                    .Select(make);

            return
            [
                .. Children(server.Directories, p => new RemoteEntry(p[prefix.Length..], true, 0)),
                .. Children(server.Files.Keys, p => new RemoteEntry(p[prefix.Length..], false, server.Files[p].Length)),
                .. Children(server.Links, p => new RemoteEntry(p[prefix.Length..], false, 0, IsLink: true))
            ];
        }

        public void CreateDirectory(string remoteDirectory) => server.Directories.Add(remoteDirectory);

        public void DeleteFile(string remotePath)
        {
            _ = server.Files.Remove(remotePath);
            _ = server.Links.Remove(remotePath);
        }

        public void DeleteEmptyDirectory(string remoteDirectory)
        {
            if (List(remoteDirectory).Count > 0)
            {
                throw new IOException("A pasta não está vazia.");
            }

            _ = server.Directories.Remove(remoteDirectory);
        }

        public void Rename(string fromPath, string toPath)
        {
            if (server.Files.Remove(fromPath, out var data))
            {
                server.Files[toPath] = data;
            }
            else if (server.Directories.Remove(fromPath))
            {
                server.Directories.Add(toPath);
                foreach (var key in server.Files.Keys.Where(k => k.StartsWith(fromPath + "/", StringComparison.Ordinal)).ToList())
                {
                    server.Files[toPath + key[fromPath.Length..]] = server.Files[key];
                    _ = server.Files.Remove(key);
                }
            }
        }

        public void Download(string remotePath, Stream destination, Action<long> progress, CancellationToken cancellationToken)
        {
            var data = server.Files[remotePath];
            long written = 0;
            foreach (var chunk in data.Chunk(4096))
            {
                cancellationToken.ThrowIfCancellationRequested();
                destination.Write(chunk);
                written += chunk.Length;
                progress(written);
                if (server.FailDownloadAfterBytes is { } limit && written >= limit)
                {
                    throw new IOException("A conexão caiu.");
                }
            }
        }

        public void Upload(Stream source, string remotePath, Action<long> progress, CancellationToken cancellationToken)
        {
            using var buffer = new MemoryStream();
            var chunk = new byte[4096];
            int read;
            long total = 0;
            while ((read = source.Read(chunk, 0, chunk.Length)) > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                buffer.Write(chunk, 0, read);
                total += read;
                progress(total);
                server.Files[remotePath] = buffer.ToArray(); // o servidor já guarda o que chegou
                if (server.FailUploadAfterBytes is { } limit && total >= limit)
                {
                    throw new IOException("A conexão caiu.");
                }

                if (server.BlockUploadUntilDisposed)
                {
                    _disposed.Wait(TimeSpan.FromSeconds(10), cancellationToken);
                    throw new IOException("A sessão foi derrubada.");
                }
            }

            server.Files[remotePath] = buffer.ToArray();
        }

        public void Dispose()
        {
            server.DisposedSessions++;
            _disposed.Set();
        }
    }
}

internal sealed class CapturingProgress : IProgress<FileOperationProgress>
{
    public List<FileOperationProgress> Reports { get; } = [];

    public void Report(FileOperationProgress value)
    {
        lock (Reports)
        {
            Reports.Add(value);
        }
    }
}

public sealed class RemoteTransferEngineTests : IDisposable
{
    private readonly string _local = Directory.CreateDirectory(
        Path.Combine(Path.GetTempPath(), "dora-remote-engine-" + Guid.NewGuid().ToString("N"))).FullName;

    private readonly FakeRemoteServer _server = new();

    public void Dispose() => FileOperationTests.DeleteQuietly(_local);

    private static byte[] Bytes(int length, byte seed = 1) => Enumerable.Range(0, length).Select(i => (byte)((i + seed) % 251)).ToArray();

    [Theory]
    [InlineData("a.txt", "a.txt")]
    [InlineData("pasta", "pasta")]
    public void A_free_name_is_kept(string name, string expected) =>
        RemoteTransferEngine.UniqueName(_ => false, name).Should().Be(expected);

    [Fact]
    public void A_taken_name_gets_a_number_before_the_extension_like_local_copies()
    {
        var taken = new HashSet<string> { "a.txt", "a (1).txt", "pacote.tar.gz", "pasta" };

        RemoteTransferEngine.UniqueName(taken.Contains, "a.txt").Should().Be("a (2).txt");
        RemoteTransferEngine.UniqueName(taken.Contains, "pacote.tar.gz").Should().Be("pacote (1).tar.gz");
        RemoteTransferEngine.UniqueName(taken.Contains, "pasta").Should().Be("pasta (1)");
    }

    [Fact]
    public void Downloading_a_file_copies_the_bytes_reports_progress_and_leaves_no_temporary_file()
    {
        _server.Files["/docs/a.bin"] = Bytes(20_000);
        var progress = new CapturingProgress();
        var tracker = new TransferProgressTracker(progress, null);
        using var session = _server.Open();

        var local = RemoteTransferEngine.Download(session, "/docs/a.bin", _local, tracker, CancellationToken.None);
        tracker.Complete();

        File.ReadAllBytes(local).Should().Equal(_server.Files["/docs/a.bin"]);
        Path.GetFileName(local).Should().Be("a.bin");
        progress.Reports.Should().NotBeEmpty();
        progress.Reports.Max(r => r.BytesDone).Should().Be(20_000);
        Directory.GetFiles(_local, "*.dora-part-*").Should().BeEmpty();
    }

    [Fact]
    public void Downloading_a_folder_brings_everything_inside_and_skips_symbolic_links()
    {
        _server.Directories.UnionWith(["/p", "/p/sub", "/p/vazia"]);
        _server.Files["/p/um.txt"] = Bytes(10);
        _server.Files["/p/sub/dois.txt"] = Bytes(30);
        _server.Links.Add("/p/atalho");
        using var session = _server.Open();

        var local = RemoteTransferEngine.Download(session, "/p", _local, new TransferProgressTracker(null, null), CancellationToken.None);

        File.ReadAllBytes(Path.Combine(local, "um.txt")).Should().Equal(Bytes(10));
        File.ReadAllBytes(Path.Combine(local, "sub", "dois.txt")).Should().Equal(Bytes(30));
        Directory.Exists(Path.Combine(local, "vazia")).Should().BeTrue();
        File.Exists(Path.Combine(local, "atalho")).Should().BeFalse();
    }

    [Fact]
    public void A_download_never_overwrites_an_existing_local_item()
    {
        _server.Files["/a.txt"] = Bytes(5);
        File.WriteAllText(Path.Combine(_local, "a.txt"), "meu arquivo");
        using var session = _server.Open();

        var local = RemoteTransferEngine.Download(session, "/a.txt", _local, new TransferProgressTracker(null, null), CancellationToken.None);

        Path.GetFileName(local).Should().Be("a (1).txt");
        File.ReadAllText(Path.Combine(_local, "a.txt")).Should().Be("meu arquivo");
    }

    [Fact]
    public void A_connection_that_drops_mid_download_leaves_nothing_behind()
    {
        _server.Directories.Add("/p");
        _server.Files["/p/pequeno.txt"] = Bytes(100);
        _server.Files["/p/grande.bin"] = Bytes(50_000);
        _server.FailDownloadAfterBytes = 10_000;
        using var session = _server.Open();

        var act = () => RemoteTransferEngine.Download(session, "/p", _local, new TransferProgressTracker(null, null), CancellationToken.None);

        act.Should().Throw<IOException>();
        Directory.GetFileSystemEntries(_local).Should().BeEmpty("a pasta parcial e o arquivo temporário são apagados");
    }

    [Theory]
    [InlineData(@"..\fora.txt")]
    [InlineData("a/b.txt")]
    [InlineData(@"C:\Windows\x.txt")]
    [InlineData("..")]
    public void A_remote_name_that_tries_to_escape_the_destination_folder_is_refused(string maliciousName)
    {
        _server.Directories.Add("/p");
        _server.Files["/p/" + maliciousName.Replace("/", "\\")] = Bytes(10);
        var destination = Directory.CreateDirectory(Path.Combine(_local, "destino")).FullName;
        using var session = _server.Open();

        var act = () => RemoteTransferEngine.Download(session, "/p", destination, new TransferProgressTracker(null, null), CancellationToken.None);

        act.Should().Throw<IOException>().WithMessage("*inválido*");
        File.Exists(Path.Combine(_local, "fora.txt")).Should().BeFalse();
        Directory.GetFileSystemEntries(destination).Should().BeEmpty();
    }

    [Fact]
    public void Uploading_a_folder_creates_the_tree_and_picks_a_free_name_on_the_server()
    {
        var source = Directory.CreateDirectory(Path.Combine(_local, "projeto")).FullName;
        Directory.CreateDirectory(Path.Combine(source, "src"));
        File.WriteAllBytes(Path.Combine(source, "leia.txt"), Bytes(40));
        File.WriteAllBytes(Path.Combine(source, "src", "codigo.cs"), Bytes(70));
        _server.Directories.Add("/projeto");
        using var session = _server.Open();
        string? chosen = null;

        var remote = RemoteTransferEngine.Upload(session, source, "/", new TransferProgressTracker(null, null), path => chosen = path, CancellationToken.None);

        remote.Should().Be("/projeto (1)");
        chosen.Should().Be(remote);
        _server.Files["/projeto (1)/leia.txt"].Should().Equal(Bytes(40));
        _server.Files["/projeto (1)/src/codigo.cs"].Should().Equal(Bytes(70));
        _server.Files.Keys.Should().NotContain(k => k.StartsWith("/projeto/", StringComparison.Ordinal));
    }

    [Fact]
    public void Deleting_removes_files_and_folders_recursively_but_never_the_server_root()
    {
        _server.Directories.UnionWith(["/x", "/x/y"]);
        _server.Files["/x/a"] = Bytes(1);
        _server.Files["/x/y/b"] = Bytes(1);
        _server.Files["/fica"] = Bytes(1);
        using var session = _server.Open();

        RemoteTransferEngine.Delete(session, "/x", null, CancellationToken.None);

        _server.Directories.Should().BeEquivalentTo(["/"]);
        _server.Files.Keys.Should().BeEquivalentTo(["/fica"]);
        var act = () => RemoteTransferEngine.Delete(session, "/", null, CancellationToken.None);
        act.Should().Throw<IOException>().WithMessage("*raiz*");
        RemoteTransferEngine.Delete(session, "/nao-existe", null, CancellationToken.None);
    }

    [Fact]
    public void Renaming_keeps_the_folder_and_refuses_bad_or_taken_names()
    {
        _server.Directories.Add("/d");
        _server.Files["/d/a.txt"] = Bytes(3);
        _server.Files["/d/b.txt"] = Bytes(3);
        using var session = _server.Open();

        RemoteTransferEngine.Rename(session, "/d/a.txt", "novo.txt").Should().Be("/d/novo.txt");
        _server.Files.Keys.Should().Contain("/d/novo.txt").And.NotContain("/d/a.txt");

        foreach (var bad in new[] { "", "  ", "..", "x/y", @"x\y" })
        {
            var act = () => RemoteTransferEngine.Rename(session, "/d/novo.txt", bad);
            act.Should().Throw<IOException>($"'{bad}' não é um nome");
        }

        var taken = () => RemoteTransferEngine.Rename(session, "/d/novo.txt", "b.txt");
        taken.Should().Throw<IOException>().WithMessage("*Já existe*");
    }

    [Fact]
    public void Scanning_adds_up_bytes_and_files_without_following_links()
    {
        _server.Directories.UnionWith(["/s", "/s/t"]);
        _server.Files["/s/a"] = Bytes(100);
        _server.Files["/s/t/b"] = Bytes(250);
        _server.Links.Add("/s/ln");
        using var session = _server.Open();

        RemoteTransferEngine.Scan(session, "/s", null, CancellationToken.None).Should().Be((350L, 2));
        RemoteTransferEngine.Scan(session, "/nada", null, CancellationToken.None).Should().Be((0L, 0));
    }
}

public sealed class RemoteFileServiceTests : IDisposable
{
    private readonly string _local = Directory.CreateDirectory(
        Path.Combine(Path.GetTempPath(), "dora-remote-service-" + Guid.NewGuid().ToString("N"))).FullName;

    private readonly FakeRemoteServer _server = new();

    private readonly NetworkConnectionInfo _connection = new() { Protocol = NetworkProtocol.Ftp, Host = "servidor.teste", Username = "ana" };

    public void Dispose() => FileOperationTests.DeleteQuietly(_local);

    private RemoteFileService Service() => new(_ => _server.Open());

    [Fact]
    public async Task Tree_paths_and_plain_paths_are_both_understood()
    {
        _server.Directories.Add("/docs");
        _server.Files["/docs/a b.txt"] = [1, 2, 3];

        var byUri = await Service().DownloadAsync(
            _connection, NetworkFileService.BuildRemotePath(NetworkProtocol.Ftp, "servidor.teste", "/docs/a b.txt"), _local, null);

        byUri.Success.Should().BeTrue(byUri.ErrorMessage);
        File.ReadAllBytes(byUri.LocalPath!).Should().Equal(1, 2, 3);
        RemoteFileService.ToRemotePath("/docs/x").Should().Be("/docs/x");
    }

    [Fact]
    public async Task Create_directory_refuses_a_name_that_already_exists()
    {
        _server.Directories.Add("/ja");

        (await Service().CreateDirectoryAsync(_connection, "/ja")).ErrorMessage.Should().Contain("Já existe");
        (await Service().CreateDirectoryAsync(_connection, "/nova")).Success.Should().BeTrue();
        _server.Directories.Should().Contain("/nova");
    }

    [Fact]
    public async Task Rename_returns_the_new_tree_path()
    {
        _server.Files["/a.txt"] = [1];

        var result = await Service().RenameAsync(
            _connection, NetworkFileService.BuildRemotePath(NetworkProtocol.Ftp, "servidor.teste", "/a.txt"), "b.txt");

        result.Success.Should().BeTrue(result.ErrorMessage);
        result.NewPath.Should().Be("dora-ftp://servidor.teste/b.txt");
        _server.Files.Keys.Should().BeEquivalentTo(["/b.txt"]);
    }

    [Fact]
    public async Task Delete_removes_all_the_given_items()
    {
        _server.Files["/a"] = [1];
        _server.Directories.Add("/d");
        _server.Files["/d/x"] = [1];

        var result = await Service().DeleteAsync(_connection, ["/a", "/d"]);

        result.Success.Should().BeTrue(result.ErrorMessage);
        _server.Files.Should().BeEmpty();
        _server.Directories.Should().BeEquivalentTo(["/"]);
    }

    [Fact]
    public async Task A_failed_upload_removes_what_reached_the_server()
    {
        var file = Path.Combine(_local, "grande.bin");
        File.WriteAllBytes(file, new byte[40_000]);
        _server.FailUploadAfterBytes = 8_000;

        var result = await Service().UploadAsync(_connection, file, "/", null);

        result.Success.Should().BeFalse();
        _server.Files.Should().BeEmpty("o envio incompleto é apagado do servidor");
    }

    [Fact]
    public async Task Cancelling_an_upload_stops_it_and_cleans_the_server()
    {
        var file = Path.Combine(_local, "grande.bin");
        File.WriteAllBytes(file, new byte[40_000]);
        _server.BlockUploadUntilDisposed = true;
        using var cancellation = new CancellationTokenSource();

        var task = Service().UploadAsync(_connection, file, "/", null, cancellation.Token);
        await Task.Delay(300);
        await cancellation.CancelAsync();
        var result = await task;

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Be(FileOperationService.CancelledMessage);
        _server.Files.Should().BeEmpty();
        _server.DisposedSessions.Should().BeGreaterThan(0, "cancelar derruba a sessão para interromper a rede");
    }

    [Fact]
    public async Task Moving_inside_the_same_account_only_renames_on_the_server()
    {
        _server.Directories.Add("/destino");
        _server.Files["/a.txt"] = [9, 9];
        var progress = new CapturingProgress();

        var result = await Service().TransferBetweenAsync(_connection, "/a.txt", _connection, "/destino", move: true, progress);

        result.Success.Should().BeTrue(result.ErrorMessage);
        _server.Files.Keys.Should().BeEquivalentTo(["/destino/a.txt"]);
        progress.Reports.Should().BeEmpty("nenhum byte trafegou: foi só um renomear no servidor");
    }

    [Fact]
    public async Task Copying_between_servers_goes_through_a_temporary_folder_that_is_cleaned()
    {
        var other = new FakeRemoteServer();
        other.Directories.Add("/in");
        _server.Files["/dados.bin"] = new byte[10_000];
        var second = new NetworkConnectionInfo { Protocol = NetworkProtocol.Sftp, Host = "outro.teste", Username = "bia" };
        var service = new RemoteFileService(connection => connection.Host == "outro.teste" ? other.Open() : _server.Open());

        var result = await service.TransferBetweenAsync(_connection, "/dados.bin", second, "/in", move: false, null);

        result.Success.Should().BeTrue(result.ErrorMessage);
        other.Files["/in/dados.bin"].Length.Should().Be(10_000);
        _server.Files.Should().ContainKey("/dados.bin", "copiar não apaga a origem");

        other.Files.Clear();
        var moved = await service.TransferBetweenAsync(_connection, "/dados.bin", second, "/in", move: true, null);
        moved.Success.Should().BeTrue(moved.ErrorMessage);
        _server.Files.Should().BeEmpty("mover entre servidores apaga a origem depois de enviar");
    }

    [Fact]
    public async Task Opening_a_remote_file_downloads_it_to_a_temporary_folder()
    {
        _server.Files["/leia.txt"] = [65, 66];

        var result = await Service().DownloadForOpenAsync(_connection, "/leia.txt");

        result.Success.Should().BeTrue(result.ErrorMessage);
        Path.GetFileName(result.LocalPath!).Should().Be("leia.txt");
        File.ReadAllBytes(result.LocalPath!).Should().Equal(65, 66);
        FileOperationTests.DeleteQuietly(Path.GetDirectoryName(result.LocalPath!)!);
    }

    [Fact]
    public void Two_connections_are_the_same_account_only_when_protocol_host_port_and_user_match()
    {
        var a = new NetworkConnectionInfo { Protocol = NetworkProtocol.Ftp, Host = "H", Port = 21, Username = "u" };

        RemoteFileService.SameAccount(a, new NetworkConnectionInfo { Protocol = NetworkProtocol.Ftp, Host = "h", Port = 21, Username = "u" }).Should().BeTrue();
        RemoteFileService.SameAccount(a, new NetworkConnectionInfo { Protocol = NetworkProtocol.Sftp, Host = "H", Port = 21, Username = "u" }).Should().BeFalse();
        RemoteFileService.SameAccount(a, new NetworkConnectionInfo { Protocol = NetworkProtocol.Ftp, Host = "H", Port = 2121, Username = "u" }).Should().BeFalse();
        RemoteFileService.SameAccount(a, new NetworkConnectionInfo { Protocol = NetworkProtocol.Ftp, Host = "H", Port = 21, Username = "outro" }).Should().BeFalse();
    }
}

/// <summary>Ponta a ponta: o serviço remoto contra o servidor FTP embutido do app, com um cliente FluentFTP de verdade.</summary>
public sealed class RemoteFileServiceFtpTests : IDisposable
{
    private const string User = "dora";
    private const string Password = "s3nha-forte";

    private readonly string _serverRoot = Directory.CreateDirectory(
        Path.Combine(Path.GetTempPath(), "dora-remote-ftp-root-" + Guid.NewGuid().ToString("N"))).FullName;

    private readonly string _work = Directory.CreateDirectory(
        Path.Combine(Path.GetTempPath(), "dora-remote-ftp-work-" + Guid.NewGuid().ToString("N"))).FullName;

    private readonly NetworkServerService _servers;
    private readonly NetworkConnectionInfo _connection;

    public RemoteFileServiceFtpTests()
    {
        _servers = new NetworkServerService(new NetworkServerLogStore(), Path.Combine(_work, "ftps.bin"));
        var port = FreePort();
        var config = new NetworkConnectionInfo
        {
            Protocol = NetworkProtocol.Ftp,
            Host = "127.0.0.1",
            Port = port,
            Username = User,
            Password = Password,
            RemotePath = _serverRoot,
            ServerSecurity = FtpServerSecurity.Disabled
        };
        _servers.TryStart(config, out var error).Should().BeTrue(error);

        _connection = new NetworkConnectionInfo
        {
            Protocol = NetworkProtocol.Ftp,
            Host = "127.0.0.1",
            Port = port,
            Username = User,
            Password = Password,
            ClientSecurity = FtpClientSecurity.None
        };
    }

    public void Dispose()
    {
        _servers.Dispose();
        FileOperationTests.DeleteQuietly(_serverRoot);
        FileOperationTests.DeleteQuietly(_work);
    }

    private static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    [Fact]
    public async Task A_folder_goes_up_comes_back_gets_renamed_and_deleted_on_a_real_ftp_server()
    {
        var service = new RemoteFileService();
        var source = Directory.CreateDirectory(Path.Combine(_work, "projeto")).FullName;
        Directory.CreateDirectory(Path.Combine(source, "sub pasta"));
        File.WriteAllBytes(Path.Combine(source, "dados.bin"), Enumerable.Range(0, 150_000).Select(i => (byte)(i % 253)).ToArray());
        File.WriteAllText(Path.Combine(source, "sub pasta", "nota é útil.txt"), "olá, mundo");
        var progress = new CapturingProgress();

        var upload = await service.UploadAsync(_connection, source, "/", progress);

        upload.Success.Should().BeTrue(upload.ErrorMessage);
        File.ReadAllBytes(Path.Combine(_serverRoot, "projeto", "dados.bin")).Should().Equal(File.ReadAllBytes(Path.Combine(source, "dados.bin")));
        File.ReadAllText(Path.Combine(_serverRoot, "projeto", "sub pasta", "nota é útil.txt")).Should().Be("olá, mundo");
        progress.Reports.Max(r => r.BytesDone).Should().BeGreaterThanOrEqualTo(150_000);

        var downloadTo = Directory.CreateDirectory(Path.Combine(_work, "baixado")).FullName;
        var download = await service.DownloadAsync(_connection, "/projeto", downloadTo, null);

        download.Success.Should().BeTrue(download.ErrorMessage);
        File.ReadAllBytes(Path.Combine(downloadTo, "projeto", "dados.bin")).Should().Equal(File.ReadAllBytes(Path.Combine(source, "dados.bin")));
        File.ReadAllText(Path.Combine(downloadTo, "projeto", "sub pasta", "nota é útil.txt")).Should().Be("olá, mundo");

        var renamed = await service.RenameAsync(_connection, "/projeto/dados.bin", "dados-novos.bin");
        renamed.Success.Should().BeTrue(renamed.ErrorMessage);
        File.Exists(Path.Combine(_serverRoot, "projeto", "dados-novos.bin")).Should().BeTrue();
        File.Exists(Path.Combine(_serverRoot, "projeto", "dados.bin")).Should().BeFalse();

        var folder = await service.CreateDirectoryAsync(_connection, "/projeto/vazia");
        folder.Success.Should().BeTrue(folder.ErrorMessage);
        Directory.Exists(Path.Combine(_serverRoot, "projeto", "vazia")).Should().BeTrue();

        var deleted = await service.DeleteAsync(_connection, ["/projeto"]);
        deleted.Success.Should().BeTrue(deleted.ErrorMessage);
        Directory.Exists(Path.Combine(_serverRoot, "projeto")).Should().BeFalse();
    }

    [Fact]
    public async Task A_wrong_password_is_reported_instead_of_hanging()
    {
        var wrong = new NetworkConnectionInfo
        {
            Protocol = NetworkProtocol.Ftp,
            Host = _connection.Host,
            Port = _connection.Port,
            Username = User,
            Password = "errada",
            ClientSecurity = FtpClientSecurity.None
        };

        var result = await new RemoteFileService().CreateDirectoryAsync(wrong, "/x");

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().NotBeNullOrWhiteSpace();
    }
}
