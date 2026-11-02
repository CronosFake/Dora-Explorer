using System.Buffers.Binary;
using System.Diagnostics;
using System.IO.Pipes;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using DoraExplorer.App.Models;
using DoraExplorer.App.Services;
using FluentAssertions;
using Renci.SshNet;
using Renci.SshNet.Common;
using Xunit;

namespace DoraExplorer.App.Tests;

public sealed class ServerVirtualPathTests
{
    private static readonly string Root = Path.Combine(Path.GetTempPath(), "dora-vpath-root");

    [Theory]
    [InlineData("/a/b", "/a/b")]
    [InlineData("a/b", "/a/b")]
    [InlineData("/a/../b", "/b")]
    [InlineData("/../../etc", "/etc")]
    [InlineData("\\a\\b", "/a/b")]
    [InlineData("/a//b/./c", "/a/b/c")]
    [InlineData("", "/")]
    [InlineData(null, "/")]
    [InlineData("/..", "/")]
    public void Normalize_never_goes_above_the_root(string? input, string expected) =>
        ServerVirtualPath.Normalize(input).Should().Be(expected);

    [Fact]
    public void Resolve_maps_inside_the_root()
    {
        ServerVirtualPath.TryResolve(Root, "docs/a.txt", out var real, out var virtualPath).Should().BeTrue();
        real.Should().Be(Path.Combine(Root, "docs", "a.txt"));
        virtualPath.Should().Be("/docs/a.txt");

        ServerVirtualPath.TryResolve(Root, "/", out real, out _).Should().BeTrue();
        real.Should().Be(Root);
    }

    [Fact]
    public void Resolve_keeps_dotdot_inside_the_root()
    {
        ServerVirtualPath.TryResolve(Root, "../../x.txt", out var real, out var virtualPath).Should().BeTrue();
        real.Should().Be(Path.Combine(Root, "x.txt"));
        virtualPath.Should().Be("/x.txt");
    }

    [Theory]
    [InlineData("C:/Windows/win.ini")]
    [InlineData("C:\\Windows")]
    [InlineData("/con")]
    [InlineData("/NUL.txt")]
    [InlineData("/dir/aux/b")]
    [InlineData("/com1")]
    [InlineData("/file.txt:stream")]
    [InlineData("/trailing.")]
    [InlineData("/trailing ")]
    [InlineData("/a|b")]
    [InlineData("/what?")]
    [InlineData("\\\\?\\C:\\Windows")]
    public void Resolve_refuses_names_windows_would_treat_as_something_else(string path) =>
        ServerVirtualPath.TryResolve(Root, path, out _, out _).Should().BeFalse();

    [Theory]
    [InlineData("a.txt", true)]
    [InlineData("pasta com espaço", true)]
    [InlineData("ação.txt", true)]
    [InlineData("console.txt", true)]
    [InlineData("CON", false)]
    [InlineData("Lpt9.log", false)]
    [InlineData("..", false)]
    [InlineData(".", false)]
    [InlineData("", false)]
    [InlineData("a/b", false)]
    [InlineData("a\\b", false)]
    [InlineData("name.", false)]
    public void IsSafeSegment_matches_windows_rules(string name, bool expected) =>
        ServerVirtualPath.IsSafeSegment(name).Should().Be(expected);
}

public sealed class LoginThrottleTests
{
    private DateTime _now = new(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);

    private LoginThrottle Create() => new(3, TimeSpan.FromMinutes(10), TimeSpan.FromMinutes(5), () => _now);

    [Fact]
    public void Blocks_after_the_limit_and_unblocks_after_the_penalty()
    {
        var throttle = Create();

        throttle.RecordFailure("10.0.0.1").Should().BeFalse();
        throttle.RecordFailure("10.0.0.1").Should().BeFalse();
        throttle.IsBlocked("10.0.0.1").Should().BeFalse();
        throttle.RecordFailure("10.0.0.1").Should().BeTrue();
        throttle.IsBlocked("10.0.0.1").Should().BeTrue();
        throttle.IsBlocked("10.0.0.2").Should().BeFalse();

        _now += TimeSpan.FromMinutes(5) + TimeSpan.FromSeconds(1);
        throttle.IsBlocked("10.0.0.1").Should().BeFalse();
    }

    [Fact]
    public void Old_failures_do_not_count()
    {
        var throttle = Create();
        throttle.RecordFailure("a");
        throttle.RecordFailure("a");
        _now += TimeSpan.FromMinutes(11);

        throttle.RecordFailure("a").Should().BeFalse();
        throttle.IsBlocked("a").Should().BeFalse();
    }

    [Fact]
    public void A_good_login_clears_the_failures()
    {
        var throttle = Create();
        throttle.RecordFailure("a");
        throttle.RecordFailure("a");
        throttle.RecordSuccess("a");

        throttle.RecordFailure("a").Should().BeFalse();
        throttle.RecordFailure("a").Should().BeFalse();
        throttle.IsBlocked("a").Should().BeFalse();
    }
}

public sealed class ScpCommandTests
{
    [Theory]
    [InlineData("scp -t /pasta", true, false, false, false, "/pasta")]
    [InlineData("scp -r -t \"my folder\"", true, true, false, false, "my folder")]
    [InlineData("scp -rt 'a b'", true, true, false, false, "a b")]
    [InlineData("scp -f -p 'a b.txt'", false, false, false, true, "a b.txt")]
    [InlineData("scp -prf \"/x\"", false, true, false, true, "/x")]
    [InlineData("scp -t -d \"/dir\"", true, false, true, false, "/dir")]
    [InlineData("scp -t -- -weird", true, false, false, false, "-weird")]
    [InlineData("scp -v -t /v", true, false, false, false, "/v")]
    [InlineData("scp -t /dir/it\\'s", true, false, false, false, "/dir/it's")]
    public void Parses_what_real_clients_send(string line, bool sink, bool recursive, bool dirOnly, bool times, string path)
    {
        ScpCommand.TryParse(line, out var command).Should().BeTrue();
        command.Sink.Should().Be(sink);
        command.Recursive.Should().Be(recursive);
        command.TargetMustBeDirectory.Should().Be(dirOnly);
        command.PreserveTimes.Should().Be(times);
        command.Path.Should().Be(path);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("scp")]
    [InlineData("scp -t")]
    [InlineData("scp -t -f /x")]
    [InlineData("scp /x")]
    [InlineData("scp -x -t /a")]
    [InlineData("scp -t /a /b")]
    [InlineData("whoami")]
    [InlineData("cmd /c scp -t /a")]
    [InlineData("scp -t /a; calc")]
    [InlineData("sh -c 'scp -t /a'")]
    public void Refuses_everything_that_is_not_a_plain_scp_transfer(string line)
    {
        // Nada é passado a um shell: "a; calc" são dois argumentos soltos e o scp só aceita um caminho.
        ScpCommand.TryParse(line, out _).Should().BeFalse();
    }
}

/// <summary>Conversa com a sessão SFTP pelo protocolo cru, num canal de verdade (pipe nomeado), sem passar por SSH.</summary>
public sealed class SftpServerSessionTests : IAsyncLifetime, IDisposable
{
    private readonly string _container = Directory.CreateDirectory(
        Path.Combine(Path.GetTempPath(), "dora-sftp-" + Guid.NewGuid().ToString("N"))).FullName;

    private string Root => Path.Combine(_container, "share");

    private PipePair? _pipe;
    private CancellationTokenSource? _cts;
    private Task? _session;
    private RawSftp? _client;
    private readonly List<string> _log = [];

    public Task InitializeAsync()
    {
        _ = Directory.CreateDirectory(Root);
        File.WriteAllText(Path.Combine(Root, "hello.txt"), "hello world");
        File.WriteAllText(Path.Combine(_container, "outside.txt"), "segredo");
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        _cts?.Cancel();
        _pipe?.Dispose();
        if (_session is not null)
        {
            await Task.WhenAny(_session, Task.Delay(5000));
        }

        SshTestHelpers.DeleteDirectory(_container);
    }

    public void Dispose() => _cts?.Dispose();

    private async Task<RawSftp> StartAsync(bool readOnly = false, bool init = true)
    {
        _pipe = await PipePair.CreateAsync();
        _cts = new CancellationTokenSource();
        var session = new SftpServerSession(_pipe.Server, Root, readOnly, "#test", message => { lock (_log) { _log.Add(message); } });
        var server = _pipe.Server;
        _session = Task.Run(async () =>
        {
            try
            {
                await session.RunAsync(_cts.Token);
            }
            finally
            {
                server.Dispose(); // como o canal SSH: quando a sessão acaba, o cliente vê o fim da conversa
            }
        });
        _client = new RawSftp(_pipe.Client);
        if (init)
        {
            await _client.InitAsync();
        }

        return _client;
    }

    [Fact]
    public async Task Version_3_is_negotiated_and_the_root_is_listed()
    {
        var sftp = await StartAsync();

        var handle = (await sftp.CallAsync(RawSftp.Opendir, "/")).Handle;
        var names = (await sftp.CallAsync(RawSftp.Readdir, handle)).Names;
        names.Should().Contain("hello.txt");
        names.Should().NotContain("outside.txt");

        (await sftp.CallAsync(RawSftp.Readdir, handle)).Code.Should().Be(1u, "fim da lista");
        (await sftp.CallAsync(RawSftp.Close, handle)).Code.Should().Be(0u);
    }

    [Fact]
    public async Task A_request_before_init_ends_the_session()
    {
        var sftp = await StartAsync(init: false);

        await sftp.SendAsync(RawSftp.Frame(RawSftp.Stat, true, 1u, "/hello.txt"));

        (await sftp.TryReadAsync()).Should().BeNull();
        await _session!.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Theory]
    [InlineData("C:\\Windows\\win.ini")]
    [InlineData("/con")]
    [InlineData("/hello.txt:secret")]
    [InlineData("\\\\?\\C:\\Windows")]
    public async Task Dangerous_paths_are_denied_without_touching_the_disk(string path)
    {
        var sftp = await StartAsync();

        (await sftp.CallAsync(RawSftp.Open, path, 1u, 0u)).Code.Should().Be(3u);
        (await sftp.CallAsync(RawSftp.Stat, path)).Code.Should().Be(3u);
        (await sftp.CallAsync(RawSftp.Mkdir, path, 0u)).Code.Should().Be(3u);
    }

    [Fact]
    public async Task Dotdot_stays_inside_the_share()
    {
        var sftp = await StartAsync();

        // "../outside.txt" vira "/outside.txt" dentro da pasta compartilhada, onde não existe: o arquivo de fora nunca é alcançado.
        (await sftp.CallAsync(RawSftp.Open, "../outside.txt", 1u, 0u)).Code.Should().Be(2u);
        (await sftp.CallAsync(RawSftp.Open, "/../../../outside.txt", 1u, 0u)).Code.Should().Be(2u);

        var real = await sftp.CallAsync(RawSftp.Realpath, "/a/../../..");
        real.Names.Should().ContainSingle().Which.Should().Be("/");
    }

    [Fact]
    public async Task Upload_and_download_round_trip()
    {
        var sftp = await StartAsync();

        var write = (await sftp.CallAsync(RawSftp.Open, "/up.bin", RawSftp.FlagWrite | RawSftp.FlagCreate | RawSftp.FlagTruncate, 0u)).Handle;
        (await sftp.CallAsync(RawSftp.Write, write, 0UL, new byte[] { 1, 2, 3 })).Code.Should().Be(0u);
        (await sftp.CallAsync(RawSftp.Write, write, 3UL, new byte[] { 4, 5 })).Code.Should().Be(0u);
        (await sftp.CallAsync(RawSftp.Close, write)).Code.Should().Be(0u);
        File.ReadAllBytes(Path.Combine(Root, "up.bin")).Should().Equal(1, 2, 3, 4, 5);

        var read = (await sftp.CallAsync(RawSftp.Open, "/up.bin", RawSftp.FlagRead, 0u)).Handle;
        var data = await sftp.CallAsync(RawSftp.Read, read, 1UL, 100u);
        data.Type.Should().Be(RawSftp.DataReply);
        data.Data.Should().Equal(2, 3, 4, 5);
        (await sftp.CallAsync(RawSftp.Read, read, 5UL, 10u)).Code.Should().Be(1u, "EOF");
        (await sftp.CallAsync(RawSftp.Close, read)).Code.Should().Be(0u);
    }

    [Fact]
    public async Task Read_length_is_capped_so_a_client_cannot_ask_for_gigabytes()
    {
        var sftp = await StartAsync();
        File.WriteAllBytes(Path.Combine(Root, "big.bin"), new byte[600_000]);

        var handle = (await sftp.CallAsync(RawSftp.Open, "/big.bin", RawSftp.FlagRead, 0u)).Handle;
        var reply = await sftp.CallAsync(RawSftp.Read, handle, 0UL, uint.MaxValue);

        reply.Data.Length.Should().BeLessThanOrEqualTo(1 << 18);
    }

    [Fact]
    public async Task Interrupted_upload_removes_the_new_partial_file()
    {
        var sftp = await StartAsync();

        var handle = (await sftp.CallAsync(RawSftp.Open, "/partial.bin", RawSftp.FlagWrite | RawSftp.FlagCreate, 0u)).Handle;
        (await sftp.CallAsync(RawSftp.Write, handle, 0UL, new byte[1000])).Code.Should().Be(0u);
        File.Exists(Path.Combine(Root, "partial.bin")).Should().BeTrue();

        _pipe!.Client.Dispose(); // o cliente some sem fechar o arquivo
        await _session!.WaitAsync(TimeSpan.FromSeconds(5));

        File.Exists(Path.Combine(Root, "partial.bin")).Should().BeFalse();
    }

    [Fact]
    public async Task Interrupted_overwrite_keeps_the_existing_file()
    {
        var sftp = await StartAsync();

        var handle = (await sftp.CallAsync(RawSftp.Open, "/hello.txt", RawSftp.FlagWrite, 0u)).Handle;
        (await sftp.CallAsync(RawSftp.Write, handle, 0UL, "X"u8.ToArray())).Code.Should().Be(0u);

        _pipe!.Client.Dispose();
        await _session!.WaitAsync(TimeSpan.FromSeconds(5));

        File.Exists(Path.Combine(Root, "hello.txt")).Should().BeTrue("só o que foi criado pelo envio é apagado");
    }

    [Fact]
    public async Task Read_only_server_refuses_every_change_but_still_reads()
    {
        var sftp = await StartAsync(readOnly: true);

        (await sftp.CallAsync(RawSftp.Open, "/new.txt", RawSftp.FlagWrite | RawSftp.FlagCreate, 0u)).Code.Should().Be(3u);
        (await sftp.CallAsync(RawSftp.Open, "/hello.txt", RawSftp.FlagWrite, 0u)).Code.Should().Be(3u);
        (await sftp.CallAsync(RawSftp.Mkdir, "/dir", 0u)).Code.Should().Be(3u);
        (await sftp.CallAsync(RawSftp.Remove, "/hello.txt")).Code.Should().Be(3u);
        (await sftp.CallAsync(RawSftp.Rename, "/hello.txt", "/other.txt")).Code.Should().Be(3u);
        (await sftp.CallAsync(RawSftp.Setstat, "/hello.txt", 1u, 0UL)).Code.Should().Be(3u);

        File.ReadAllText(Path.Combine(Root, "hello.txt")).Should().Be("hello world");
        var read = await sftp.CallAsync(RawSftp.Open, "/hello.txt", RawSftp.FlagRead, 0u);
        read.Type.Should().Be(RawSftp.HandleReply);
    }

    [Fact]
    public async Task Oversized_packet_closes_the_session()
    {
        var sftp = await StartAsync();

        await sftp.SendAsync([0x7F, 0xFF, 0xFF, 0xFF]);

        (await sftp.TryReadAsync()).Should().BeNull();
        await _session!.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task Malformed_request_closes_the_session()
    {
        var sftp = await StartAsync();

        // OPEN dizendo que o caminho tem 1000 bytes num pacote que só tem 8.
        var body = new List<byte> { RawSftp.Open };
        body.AddRange(BitConverter.GetBytes(BinaryPrimitives.ReverseEndianness(7u)));
        body.AddRange(BitConverter.GetBytes(BinaryPrimitives.ReverseEndianness(1000u)));
        var packet = new List<byte>(BitConverter.GetBytes(BinaryPrimitives.ReverseEndianness((uint)body.Count)));
        packet.AddRange(body);
        await sftp.SendAsync(packet.ToArray());

        (await sftp.TryReadAsync()).Should().BeNull();
        await _session!.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task Unknown_requests_get_an_unsupported_status_and_the_session_survives()
    {
        var sftp = await StartAsync();

        (await sftp.CallAsync(77, "x")).Code.Should().Be(8u);
        (await sftp.CallAsync(RawSftp.Readlink, "/hello.txt")).Code.Should().Be(8u);
        (await sftp.CallAsync(RawSftp.Stat, "/hello.txt")).Type.Should().Be(RawSftp.AttrsReply);
    }

    [Fact]
    public async Task The_number_of_open_handles_is_limited()
    {
        var sftp = await StartAsync();
        var handles = new List<string>();
        for (var i = 0; i < 64; i++)
        {
            var reply = await sftp.CallAsync(RawSftp.Opendir, "/");
            reply.Type.Should().Be(RawSftp.HandleReply);
            handles.Add(reply.Handle);
        }

        (await sftp.CallAsync(RawSftp.Opendir, "/")).Code.Should().Be(4u);

        (await sftp.CallAsync(RawSftp.Close, handles[0])).Code.Should().Be(0u);
        (await sftp.CallAsync(RawSftp.Opendir, "/")).Type.Should().Be(RawSftp.HandleReply);
    }

    [Fact]
    public async Task Handles_only_work_for_the_kind_of_thing_they_were_opened_for()
    {
        var sftp = await StartAsync();

        var directory = (await sftp.CallAsync(RawSftp.Opendir, "/")).Handle;
        (await sftp.CallAsync(RawSftp.Read, directory, 0UL, 10u)).Code.Should().Be(4u);

        var file = (await sftp.CallAsync(RawSftp.Open, "/hello.txt", RawSftp.FlagRead, 0u)).Handle;
        (await sftp.CallAsync(RawSftp.Readdir, file)).Code.Should().Be(4u);
        (await sftp.CallAsync(RawSftp.Write, file, 0UL, new byte[] { 1 })).Code.Should().Be(4u, "aberto só para leitura");
        (await sftp.CallAsync(RawSftp.Read, "999", 0UL, 10u)).Code.Should().Be(4u);
        File.ReadAllText(Path.Combine(Root, "hello.txt")).Should().Be("hello world");
    }

    [Fact]
    public async Task Rename_only_replaces_with_the_posix_extension()
    {
        var sftp = await StartAsync();
        File.WriteAllText(Path.Combine(Root, "a.txt"), "A");
        File.WriteAllText(Path.Combine(Root, "b.txt"), "B");

        (await sftp.CallAsync(RawSftp.Rename, "/a.txt", "/b.txt")).Code.Should().Be(4u);
        File.ReadAllText(Path.Combine(Root, "b.txt")).Should().Be("B");

        (await sftp.CallAsync(RawSftp.Extended, "posix-rename@openssh.com", "/a.txt", "/b.txt")).Code.Should().Be(0u);
        File.ReadAllText(Path.Combine(Root, "b.txt")).Should().Be("A");
        File.Exists(Path.Combine(Root, "a.txt")).Should().BeFalse();
    }

    [Fact]
    public async Task The_root_cannot_be_removed_or_renamed_and_non_empty_folders_survive()
    {
        var sftp = await StartAsync();
        _ = Directory.CreateDirectory(Path.Combine(Root, "full"));
        File.WriteAllText(Path.Combine(Root, "full", "x.txt"), "x");

        (await sftp.CallAsync(RawSftp.Rmdir, "/")).Code.Should().Be(3u);
        (await sftp.CallAsync(RawSftp.Rename, "/", "/other")).Code.Should().Be(3u);
        (await sftp.CallAsync(RawSftp.Rmdir, "/full")).Code.Should().NotBe(0u);
        File.Exists(Path.Combine(Root, "full", "x.txt")).Should().BeTrue();
        (await sftp.CallAsync(RawSftp.Remove, "/full")).Code.Should().Be(4u, "é pasta, não arquivo");
    }

    [Fact]
    public async Task A_junction_pointing_outside_is_hidden_and_cannot_be_followed()
    {
        var outside = Directory.CreateDirectory(Path.Combine(_container, "outside-dir")).FullName;
        File.WriteAllText(Path.Combine(outside, "secret.txt"), "segredo");
        var junction = Path.Combine(Root, "link");
        SshTestHelpers.CreateJunction(junction, outside);
        var sftp = await StartAsync();

        var handle = (await sftp.CallAsync(RawSftp.Opendir, "/")).Handle;
        (await sftp.CallAsync(RawSftp.Readdir, handle)).Names.Should().NotContain("link");

        (await sftp.CallAsync(RawSftp.Open, "/link/secret.txt", RawSftp.FlagRead, 0u)).Code.Should().Be(3u);
        (await sftp.CallAsync(RawSftp.Opendir, "/link")).Code.Should().Be(3u);
        (await sftp.CallAsync(RawSftp.Open, "/link/new.txt", RawSftp.FlagWrite | RawSftp.FlagCreate, 0u)).Code.Should().Be(3u);
        File.Exists(Path.Combine(outside, "new.txt")).Should().BeFalse();
    }

    [Fact]
    public async Task Setstat_can_truncate_and_set_the_time_but_not_escape()
    {
        var sftp = await StartAsync();

        (await sftp.CallAsync(RawSftp.Setstat, "/hello.txt", 1u, 5UL)).Code.Should().Be(0u);
        new FileInfo(Path.Combine(Root, "hello.txt")).Length.Should().Be(5);
        (await sftp.CallAsync(RawSftp.Setstat, "C:\\Windows\\win.ini", 1u, 0UL)).Code.Should().Be(3u);
        (await sftp.CallAsync(RawSftp.Setstat, "/missing.txt", 1u, 0UL)).Code.Should().Be(2u);
    }
}

/// <summary>Conversa com a sessão SCP pelo protocolo cru, num canal de verdade.</summary>
public sealed class ScpServerSessionTests : IAsyncLifetime, IDisposable
{
    private readonly string _container = Directory.CreateDirectory(
        Path.Combine(Path.GetTempPath(), "dora-scp-" + Guid.NewGuid().ToString("N"))).FullName;

    private string Root => Path.Combine(_container, "share");

    private PipePair? _pipe;
    private CancellationTokenSource? _cts;

    public Task InitializeAsync()
    {
        _ = Directory.CreateDirectory(Root);
        File.WriteAllText(Path.Combine(Root, "a.txt"), "conteudo-a");
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        _cts?.Cancel();
        _pipe?.Dispose();
        await Task.Delay(50);
        SshTestHelpers.DeleteDirectory(_container);
    }

    public void Dispose() => _cts?.Dispose();

    private async Task<(Stream Client, Task<int> Result)> StartAsync(string command, bool readOnly = false)
    {
        ScpCommand.TryParse(command, out var parsed).Should().BeTrue();
        _pipe = await PipePair.CreateAsync();
        _cts = new CancellationTokenSource();
        var session = new ScpServerSession(_pipe.Server, Root, readOnly, "#test", _ => { });
        var server = _pipe.Server;
        var cancellation = _cts.Token;
        return (_pipe.Client, Task.Run(async () =>
        {
            try
            {
                return await session.RunAsync(parsed, cancellation);
            }
            finally
            {
                server.Dispose();
            }
        }));
    }

    private static async Task<int> ReadByteAsync(Stream stream)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var buffer = new byte[1];
        var read = await stream.ReadAsync(buffer, cts.Token);
        return read == 0 ? -1 : buffer[0];
    }

    private static async Task SendAsync(Stream stream, string text) => await stream.WriteAsync(Encoding.UTF8.GetBytes(text));

    private static async Task ExpectAckAsync(Stream stream) => (await ReadByteAsync(stream)).Should().Be(0, "o servidor deveria confirmar");

    private static async Task<string> ReadLineAsync(Stream stream)
    {
        var line = new StringBuilder();
        while (true)
        {
            var b = await ReadByteAsync(stream);
            if (b is -1 or '\n')
            {
                return line.ToString();
            }

            line.Append((char)b);
        }
    }

    private static async Task<byte[]> ReadAsync(Stream stream, int count)
    {
        var buffer = new byte[count];
        var offset = 0;
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (offset < count)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(offset), cts.Token);
            read.Should().BeGreaterThan(0);
            offset += read;
        }

        return buffer;
    }

    [Fact]
    public async Task Sink_receives_a_file_into_the_target_folder()
    {
        var (client, result) = await StartAsync("scp -t -d /");

        await ExpectAckAsync(client);
        await SendAsync(client, "C0644 5 hello.txt\n");
        await ExpectAckAsync(client);
        await SendAsync(client, "hello\0");
        await ExpectAckAsync(client);
        client.Dispose();

        (await result).Should().Be(0);
        File.ReadAllText(Path.Combine(Root, "hello.txt")).Should().Be("hello");
        Directory.GetFiles(Root, ".dora-part-*").Should().BeEmpty();
    }

    [Fact]
    public async Task Sink_uses_the_target_name_when_the_target_is_a_file_path()
    {
        var (client, result) = await StartAsync("scp -t /renamed.txt");

        await ExpectAckAsync(client);
        await SendAsync(client, "C0644 3 ignored.txt\n");
        await ExpectAckAsync(client);
        await SendAsync(client, "abc\0");
        await ExpectAckAsync(client);
        client.Dispose();

        (await result).Should().Be(0);
        File.ReadAllText(Path.Combine(Root, "renamed.txt")).Should().Be("abc");
        File.Exists(Path.Combine(Root, "ignored.txt")).Should().BeFalse();
    }

    [Fact]
    public async Task Sink_receives_a_folder_tree_and_keeps_times()
    {
        var (client, result) = await StartAsync("scp -r -p -t -d /");

        await ExpectAckAsync(client);
        await SendAsync(client, "T1700000000 0 1700000000 0\n");
        await ExpectAckAsync(client);
        await SendAsync(client, "D0755 0 sub\n");
        await ExpectAckAsync(client);
        await SendAsync(client, "C0644 1 x.txt\n");
        await ExpectAckAsync(client);
        await SendAsync(client, "X\0");
        await ExpectAckAsync(client);
        await SendAsync(client, "E\n");
        await ExpectAckAsync(client);
        client.Dispose();

        (await result).Should().Be(0);
        File.ReadAllText(Path.Combine(Root, "sub", "x.txt")).Should().Be("X");
        Directory.GetLastWriteTimeUtc(Path.Combine(Root, "sub")).Should().Be(DateTimeOffset.FromUnixTimeSeconds(1700000000).UtcDateTime, "vale depois que o último arquivo entrou");
    }

    [Fact]
    public async Task Sink_treats_an_end_of_directory_with_nothing_open_as_the_end_of_the_transfer()
    {
        var (client, result) = await StartAsync("scp -r -t -d /");

        await ExpectAckAsync(client);
        await SendAsync(client, "C0644 1 a1.txt\n");
        await ExpectAckAsync(client);
        await SendAsync(client, "X\0");
        await ExpectAckAsync(client);
        await SendAsync(client, "E\n");
        await ExpectAckAsync(client);

        (await result).Should().Be(0);
        File.ReadAllText(Path.Combine(Root, "a1.txt")).Should().Be("X");
    }

    [Theory]
    [InlineData("C0644 3 ../evil.txt")]
    [InlineData("C0644 3 ..\\evil.txt")]
    [InlineData("C0644 3 sub/evil.txt")]
    [InlineData("C0644 3 ..")]
    [InlineData("C0644 3 CON")]
    [InlineData("C0644 3 aux.txt")]
    [InlineData("C0644 3 file.txt:stream")]
    [InlineData("C0644 -3 neg.txt")]
    [InlineData("C0644 abc neg.txt")]
    [InlineData("Z0644 3 other.txt")]
    public async Task Sink_refuses_unsafe_names_and_garbage(string header)
    {
        var (client, result) = await StartAsync("scp -t -d /");

        await ExpectAckAsync(client);
        await SendAsync(client, header + "\n");
        (await ReadByteAsync(client)).Should().Be(1, "o servidor responde com erro");

        (await result).Should().Be(1);
        File.Exists(Path.Combine(_container, "evil.txt")).Should().BeFalse();
        Directory.GetFiles(Root).Select(Path.GetFileName).Should().BeEquivalentTo(["a.txt"]);
    }

    [Fact]
    public async Task Sink_refuses_a_target_outside_or_unsafe()
    {
        var (client, result) = await StartAsync("scp -t -d C:/Windows");

        (await ReadByteAsync(client)).Should().Be(1);
        (await result).Should().Be(1);
    }

    [Fact]
    public async Task Interrupted_upload_leaves_nothing_behind_and_keeps_the_old_file()
    {
        var (client, result) = await StartAsync("scp -t /a.txt");

        await ExpectAckAsync(client);
        await SendAsync(client, "C0644 100 a.txt\n");
        await ExpectAckAsync(client);
        await client.WriteAsync(new byte[10]); // 10 de 100 bytes
        client.Dispose();

        (await result).Should().Be(1);
        File.ReadAllText(Path.Combine(Root, "a.txt")).Should().Be("conteudo-a");
        Directory.GetFiles(Root, ".dora-part-*").Should().BeEmpty();
    }

    [Fact]
    public async Task Client_reported_error_after_the_data_discards_the_file()
    {
        var (client, result) = await StartAsync("scp -t -d /");

        await ExpectAckAsync(client);
        await SendAsync(client, "C0644 2 bad.txt\n");
        await ExpectAckAsync(client);
        await client.WriteAsync(new byte[] { (byte)'o', (byte)'k', 1 }); // status != 0
        (await ReadByteAsync(client)).Should().Be(1);

        (await result).Should().Be(1);
        File.Exists(Path.Combine(Root, "bad.txt")).Should().BeFalse();
    }

    [Fact]
    public async Task Sink_on_a_read_only_server_refuses_before_accepting_anything()
    {
        var (client, result) = await StartAsync("scp -t -d /", readOnly: true);

        (await ReadByteAsync(client)).Should().Be(1);
        (await result).Should().Be(1);
    }

    [Fact]
    public async Task An_endless_line_is_cut_off()
    {
        var (client, result) = await StartAsync("scp -t -d /");

        await ExpectAckAsync(client);
        // Em segundo plano: o servidor corta a conversa no meio, e a escrita do cliente pode terminar com erro (esperado).
        var write = Task.Run(async () =>
        {
            try
            {
                await client.WriteAsync(Encoding.ASCII.GetBytes(new string('C', 10_000)));
            }
            catch (IOException)
            {
                // Pipe fechado pelo servidor: é exatamente o que se espera.
            }
        });

        (await result).Should().Be(1);
        await write.WaitAsync(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task Source_sends_a_file()
    {
        var (client, result) = await StartAsync("scp -f -p /a.txt");

        await client.WriteAsync(new byte[] { 0 });
        (await ReadLineAsync(client)).Should().StartWith("T");
        await client.WriteAsync(new byte[] { 0 });
        (await ReadLineAsync(client)).Should().Be("C0644 10 a.txt");
        await client.WriteAsync(new byte[] { 0 });
        Encoding.UTF8.GetString(await ReadAsync(client, 10)).Should().Be("conteudo-a");
        await ExpectAckAsync(client);
        await client.WriteAsync(new byte[] { 0 });
        client.Dispose();

        (await result).Should().Be(0);
    }

    [Fact]
    public async Task Source_sends_a_folder_only_when_recursive()
    {
        _ = Directory.CreateDirectory(Path.Combine(Root, "dir"));
        File.WriteAllText(Path.Combine(Root, "dir", "in.txt"), "in");

        var (client, result) = await StartAsync("scp -f /dir");
        await client.WriteAsync(new byte[] { 0 });
        (await ReadByteAsync(client)).Should().Be(1, "pasta sem -r");
        (await result).Should().Be(1);
        client.Dispose();

        var (recursive, recursiveResult) = await StartAsync("scp -r -f /dir");
        await recursive.WriteAsync(new byte[] { 0 });
        (await ReadLineAsync(recursive)).Should().Be("D0755 0 dir");
        await recursive.WriteAsync(new byte[] { 0 });
        (await ReadLineAsync(recursive)).Should().Be("C0644 2 in.txt");
        await recursive.WriteAsync(new byte[] { 0 });
        _ = await ReadAsync(recursive, 2);
        await ExpectAckAsync(recursive);
        await recursive.WriteAsync(new byte[] { 0 });
        (await ReadLineAsync(recursive)).Should().Be("E");
        await recursive.WriteAsync(new byte[] { 0 });
        recursive.Dispose();

        (await recursiveResult).Should().Be(0);
    }

    [Fact]
    public async Task Source_reports_missing_files_and_refuses_unsafe_paths()
    {
        var (missing, missingResult) = await StartAsync("scp -f /nope.txt");
        await missing.WriteAsync(new byte[] { 0 });
        (await ReadByteAsync(missing)).Should().Be(1);
        (await missingResult).Should().Be(1);
        missing.Dispose();

        var (unsafePath, unsafeResult) = await StartAsync("scp -f C:/Windows/win.ini");
        await unsafePath.WriteAsync(new byte[] { 0 });
        (await ReadByteAsync(unsafePath)).Should().Be(1);
        (await unsafeResult).Should().Be(1);
    }

    [Fact]
    public async Task Source_dotdot_cannot_reach_files_outside_the_share()
    {
        File.WriteAllText(Path.Combine(_container, "outside.txt"), "segredo");
        var (client, result) = await StartAsync("scp -f ../outside.txt");

        await client.WriteAsync(new byte[] { 0 });
        (await ReadByteAsync(client)).Should().Be(1, "dentro da pasta compartilhada esse arquivo não existe");
        (await result).Should().Be(1);
    }

    [Fact]
    public async Task Source_expands_a_wildcard_in_the_last_name_only()
    {
        File.WriteAllText(Path.Combine(Root, "b.txt"), "B");
        File.WriteAllText(Path.Combine(Root, "c.log"), "C");

        var (client, result) = await StartAsync("scp -f /*.txt");
        var names = new List<string>();
        await client.WriteAsync(new byte[] { 0 });
        for (var i = 0; i < 2; i++)
        {
            var header = await ReadLineAsync(client);
            names.Add(header.Split(' ')[2]);
            var size = int.Parse(header.Split(' ')[1], System.Globalization.CultureInfo.InvariantCulture);
            await client.WriteAsync(new byte[] { 0 });
            _ = await ReadAsync(client, size);
            await ExpectAckAsync(client);
            await client.WriteAsync(new byte[] { 0 });
        }

        client.Dispose();
        (await result).Should().Be(0);
        names.Should().Equal("a.txt", "b.txt");
    }
}

/// <summary>Servidor SFTP/SCP completo, falando SSH de verdade com o cliente SSH.NET.</summary>
public sealed class SshServerEndToEndTests : IDisposable
{
    private const string User = "alice";
    private const string Password = "s3nha-de-teste";

    private readonly string _container = Directory.CreateDirectory(
        Path.Combine(Path.GetTempPath(), "dora-ssh-" + Guid.NewGuid().ToString("N"))).FullName;

    private readonly NetworkServerLogStore _logs = new();
    private readonly NetworkServerService _service;

    public SshServerEndToEndTests()
    {
        _ = Directory.CreateDirectory(Root);
        _service = new NetworkServerService(_logs, Path.Combine(_container, "cert.bin"), HostKeyPath);
    }

    private string Root => Path.Combine(_container, "share");

    private string HostKeyPath => Path.Combine(_container, "host-key.bin");

    public void Dispose()
    {
        _service.Dispose();
        SshTestHelpers.DeleteDirectory(_container);
    }

    private int Start(NetworkProtocol protocol, bool readOnly = false)
    {
        var port = SshTestHelpers.FreeTcpPort();
        _service.TryStart(
            new NetworkConnectionInfo
            {
                Protocol = protocol,
                Host = "127.0.0.1",
                Port = port,
                Username = User,
                Password = Password,
                RemotePath = Root,
                ReadOnly = readOnly
            },
            out var error).Should().BeTrue(error);
        return port;
    }

    private static SftpClient Sftp(int port, string password = Password, string user = User)
    {
        var client = new SftpClient("127.0.0.1", port, user, password);
        client.ConnectionInfo.Timeout = TimeSpan.FromSeconds(10);
        client.OperationTimeout = TimeSpan.FromSeconds(20);
        return client;
    }

    private static ScpClient Scp(int port, string password = Password)
    {
        var connection = new ConnectionInfo("127.0.0.1", port, User, new PasswordAuthenticationMethod(User, password));
        var client = new ScpClient(connection, RemotePathTransformation.ShellQuote);
        client.ConnectionInfo.Timeout = TimeSpan.FromSeconds(10);
        client.OperationTimeout = TimeSpan.FromSeconds(20);
        return client;
    }

    [Theory]
    [InlineData(NetworkProtocol.Sftp)]
    [InlineData(NetworkProtocol.Scp)]
    public void Ssh_servers_require_a_username_and_password(NetworkProtocol protocol)
    {
        var withoutLogin = new NetworkConnectionInfo { Protocol = protocol, Host = "127.0.0.1", Port = SshTestHelpers.FreeTcpPort(), RemotePath = Root };
        _service.TryStart(withoutLogin, out var error).Should().BeFalse();
        error.Should().Contain("usuário e senha");

        var emptyPassword = new NetworkConnectionInfo
        {
            Protocol = protocol,
            Host = "127.0.0.1",
            Port = SshTestHelpers.FreeTcpPort(),
            RemotePath = Root,
            Username = User,
            Password = string.Empty
        };
        _service.TryStart(emptyPassword, out error).Should().BeFalse();
        error.Should().Contain("usuário e senha");
    }

    [Fact]
    public void Sftp_lists_downloads_uploads_and_manages_files()
    {
        File.WriteAllText(Path.Combine(Root, "hello.txt"), "olá mundo");
        _ = Directory.CreateDirectory(Path.Combine(Root, "docs"));
        var port = Start(NetworkProtocol.Sftp);

        using var client = Sftp(port);
        client.Connect();

        client.ListDirectory("/").Select(e => e.Name).Should().Contain(["hello.txt", "docs"]);

        using (var download = new MemoryStream())
        {
            client.DownloadFile("/hello.txt", download);
            Encoding.UTF8.GetString(download.ToArray()).Should().Be("olá mundo");
        }

        var payload = new byte[300_000];
        new Random(42).NextBytes(payload);
        using (var upload = new MemoryStream(payload))
        {
            client.UploadFile(upload, "/docs/data.bin");
        }

        File.ReadAllBytes(Path.Combine(Root, "docs", "data.bin")).Should().Equal(payload);

        client.CreateDirectory("/novo");
        Directory.Exists(Path.Combine(Root, "novo")).Should().BeTrue();
        client.RenameFile("/docs/data.bin", "/novo/renomeado.bin");
        File.Exists(Path.Combine(Root, "novo", "renomeado.bin")).Should().BeTrue();
        client.Exists("/novo/renomeado.bin").Should().BeTrue();
        client.GetAttributes("/hello.txt").Size.Should().Be(Encoding.UTF8.GetByteCount("olá mundo"));
        client.DeleteFile("/novo/renomeado.bin");
        client.DeleteDirectory("/novo");
        Directory.Exists(Path.Combine(Root, "novo")).Should().BeFalse();
    }

    [Fact]
    public void Sftp_upload_over_an_existing_file_replaces_it()
    {
        File.WriteAllText(Path.Combine(Root, "x.txt"), "uma versão bem longa do arquivo");
        var port = Start(NetworkProtocol.Sftp);
        using var client = Sftp(port);
        client.Connect();

        client.UploadFile(new MemoryStream("curta"u8.ToArray()), "/x.txt", canOverride: true);

        File.ReadAllText(Path.Combine(Root, "x.txt")).Should().Be("curta");
    }

    [Fact]
    public void Wrong_password_and_wrong_user_are_refused()
    {
        var port = Start(NetworkProtocol.Sftp);

        var wrongPassword = () => Sftp(port, "errada").Connect();
        wrongPassword.Should().Throw<SshAuthenticationException>();

        var wrongUser = () => Sftp(port, Password, "mallory").Connect();
        wrongUser.Should().Throw<SshAuthenticationException>();

        var empty = () => Sftp(port, string.Empty).Connect();
        empty.Should().Throw<SshAuthenticationException>();

        using var good = Sftp(port);
        good.Connect();
        good.IsConnected.Should().BeTrue();
    }

    [Fact]
    public void Sftp_client_is_confined_to_the_share()
    {
        File.WriteAllText(Path.Combine(_container, "outside.txt"), "segredo");
        File.WriteAllText(Path.Combine(Root, "inside.txt"), "dentro");
        var port = Start(NetworkProtocol.Sftp);
        using var client = Sftp(port);
        client.Connect();

        client.ListDirectory("/..").Select(e => e.Name).Should().Contain("inside.txt").And.NotContain("outside.txt");

        var download = () => client.DownloadFile("../outside.txt", new MemoryStream());
        download.Should().Throw<SftpPathNotFoundException>();

        client.UploadFile(new MemoryStream("x"u8.ToArray()), "/../planted.txt");
        File.Exists(Path.Combine(Root, "planted.txt")).Should().BeTrue("o caminho foi confinado à pasta compartilhada");
        File.Exists(Path.Combine(_container, "planted.txt")).Should().BeFalse();

        var absolute = () => client.DownloadFile("C:\\Windows\\win.ini", new MemoryStream());
        absolute.Should().Throw<SftpPermissionDeniedException>();
    }

    [Fact]
    public void Read_only_sftp_server_allows_reading_and_refuses_changes()
    {
        File.WriteAllText(Path.Combine(Root, "keep.txt"), "mantido");
        var port = Start(NetworkProtocol.Sftp, readOnly: true);
        using var client = Sftp(port);
        client.Connect();

        using var download = new MemoryStream();
        client.DownloadFile("/keep.txt", download);
        Encoding.UTF8.GetString(download.ToArray()).Should().Be("mantido");

        var upload = () => client.UploadFile(new MemoryStream("x"u8.ToArray()), "/nope.txt");
        upload.Should().Throw<SftpPermissionDeniedException>();
        var delete = () => client.DeleteFile("/keep.txt");
        delete.Should().Throw<SftpPermissionDeniedException>();
        var mkdir = () => client.CreateDirectory("/nova");
        mkdir.Should().Throw<SftpPermissionDeniedException>();

        File.Exists(Path.Combine(Root, "keep.txt")).Should().BeTrue();
        File.Exists(Path.Combine(Root, "nope.txt")).Should().BeFalse();
        Directory.Exists(Path.Combine(Root, "nova")).Should().BeFalse();
    }

    [Fact]
    public void Scp_uploads_and_downloads_files_with_a_real_client()
    {
        var port = Start(NetworkProtocol.Scp);
        var local = Path.Combine(_container, "local.bin");
        var payload = new byte[200_000];
        new Random(7).NextBytes(payload);
        File.WriteAllBytes(local, payload);

        using var client = Scp(port);
        client.Connect();

        client.Upload(new FileInfo(local), "/enviado.bin");
        File.ReadAllBytes(Path.Combine(Root, "enviado.bin")).Should().Equal(payload);

        var back = Path.Combine(_container, "back.bin");
        client.Download("/enviado.bin", new FileInfo(back));
        File.ReadAllBytes(back).Should().Equal(payload);

        var missing = () => client.Download("/nao-existe.bin", new FileInfo(Path.Combine(_container, "x.bin")));
        missing.Should().Throw<Exception>();
    }

    [Fact]
    public void Scp_transfers_folders_recursively()
    {
        var source = Directory.CreateDirectory(Path.Combine(_container, "origem")).FullName;
        File.WriteAllText(Path.Combine(source, "um.txt"), "1");
        _ = Directory.CreateDirectory(Path.Combine(source, "fundo"));
        File.WriteAllText(Path.Combine(source, "fundo", "dois.txt"), "2");
        _ = Directory.CreateDirectory(Path.Combine(Root, "destino"));
        var port = Start(NetworkProtocol.Scp);

        using var client = Scp(port);
        client.Connect();
        client.Upload(new DirectoryInfo(source), "/destino");

        // O SSH.NET envia o CONTEÚDO da pasta para dentro do destino (não a pasta em si).
        Directory.GetFiles(Root, "*.txt", SearchOption.AllDirectories)
            .Select(f => Path.GetRelativePath(Root, f).Replace('\\', '/'))
            .Should().BeEquivalentTo(["destino/um.txt", "destino/fundo/dois.txt"]);

        var back = Directory.CreateDirectory(Path.Combine(_container, "volta")).FullName;
        client.Download("/destino", new DirectoryInfo(back));
        Directory.GetFiles(back, "*.txt", SearchOption.AllDirectories).Select(Path.GetFileName).Should().BeEquivalentTo(["um.txt", "dois.txt"]);
    }

    [Fact]
    public void Read_only_scp_server_refuses_uploads()
    {
        File.WriteAllText(Path.Combine(Root, "lido.txt"), "ok");
        var port = Start(NetworkProtocol.Scp, readOnly: true);
        var local = Path.Combine(_container, "up.txt");
        File.WriteAllText(local, "x");
        using var client = Scp(port);
        client.Connect();

        var upload = () => client.Upload(new FileInfo(local), "/up.txt");
        upload.Should().Throw<Exception>();
        File.Exists(Path.Combine(Root, "up.txt")).Should().BeFalse();

        var back = Path.Combine(_container, "lido-local.txt");
        client.Download("/lido.txt", new FileInfo(back));
        File.ReadAllText(back).Should().Be("ok");
    }

    [Fact]
    public void Each_server_only_speaks_its_own_protocol()
    {
        var sftpPort = Start(NetworkProtocol.Sftp);
        var sftpAsScp = () =>
        {
            using var scp = Scp(sftpPort);
            scp.Connect();
            scp.Upload(new MemoryStream("x"u8.ToArray()), "/x.txt");
        };
        sftpAsScp.Should().Throw<Exception>();

        var scpPort = Start(NetworkProtocol.Scp);
        var scpAsSftp = () =>
        {
            using var sftp = Sftp(scpPort);
            sftp.Connect();
            _ = sftp.ListDirectory("/").ToList();
        };
        scpAsSftp.Should().Throw<Exception>();
        File.Exists(Path.Combine(Root, "x.txt")).Should().BeFalse();
    }

    [Fact]
    public void Shell_commands_are_never_executed()
    {
        var port = Start(NetworkProtocol.Sftp);
        using var ssh = new SshClient("127.0.0.1", port, User, Password);
        ssh.ConnectionInfo.Timeout = TimeSpan.FromSeconds(10);
        ssh.Connect();

        var marker = Path.Combine(Root, "pwned.txt");
        string? output = null;
        try
        {
            output = ssh.RunCommand($"cmd /c echo pwned > \"{marker}\" & echo pwned").Result;
        }
        catch (Exception)
        {
            // Recusar também é uma resposta correta.
        }

        (output ?? string.Empty).Should().NotContain("pwned");
        File.Exists(marker).Should().BeFalse();
    }

    [Fact]
    public void Port_forwarding_is_never_opened()
    {
        var port = Start(NetworkProtocol.Sftp);
        using var ssh = new SshClient("127.0.0.1", port, User, Password);
        ssh.ConnectionInfo.Timeout = TimeSpan.FromSeconds(10);
        ssh.Connect();

        var forward = new ForwardedPortLocal("127.0.0.1", (uint)SshTestHelpers.FreeTcpPort(), "127.0.0.1", 1);
        ssh.AddForwardedPort(forward);
        forward.Start();
        var forwardedPort = (int)forward.BoundPort;
        var connect = () =>
        {
            using var tcp = new TcpClient();
            tcp.Connect("127.0.0.1", forwardedPort);
            var stream = tcp.GetStream();
            stream.ReadTimeout = 3000;
            stream.Write("ping"u8);
            return stream.Read(new byte[16], 0, 16);
        };
        var bytesFromTunnel = 0;
        try
        {
            bytesFromTunnel = connect();
        }
        catch (Exception)
        {
            // Sem túnel é o esperado.
        }

        bytesFromTunnel.Should().Be(0, "o servidor não abre canais de encaminhamento");
    }

    [Fact]
    public void Host_key_is_stable_across_restarts()
    {
        var first = FingerprintOf(Start(NetworkProtocol.Sftp));
        _service.StopServer(NetworkProtocol.Sftp);

        var second = FingerprintOf(Start(NetworkProtocol.Sftp));

        second.Should().Equal(first, "senão todo cliente avisaria que a identidade do servidor mudou");
        first.Should().HaveCount(16, "MD5 da chave pública, como o SSH.NET informa");
        File.Exists(HostKeyPath).Should().BeTrue();

        var stored = File.ReadAllBytes(HostKeyPath);
        var readAsPlainKey = () => ECDsa.Create().ImportPkcs8PrivateKey(stored, out _);
        readAsPlainKey.Should().Throw<CryptographicException>("a chave privada não pode ficar em texto claro no disco");
    }

    [Fact]
    public void Log_records_activity_but_never_the_password()
    {
        File.WriteAllText(Path.Combine(Root, "visto.txt"), "conteudo");
        var port = Start(NetworkProtocol.Sftp);

        Action wrong = () => Sftp(port, "senha-errada-123").Connect();
        wrong.Should().Throw<SshAuthenticationException>();
        using (var client = Sftp(port))
        {
            client.Connect();
            client.DownloadFile("/visto.txt", new MemoryStream());
        }

        Thread.Sleep(300);
        var text = _logs.GetText(NetworkProtocol.Sftp);
        text.Should().Contain("login recusado").And.Contain("autenticado como alice").And.Contain("/visto.txt");
        text.Should().NotContain(Password).And.NotContain("senha-errada-123");
        text.Should().Contain("SHA256:");
    }

    [Fact]
    public void Garbage_connections_do_not_stop_the_server()
    {
        var port = Start(NetworkProtocol.Sftp);

        for (var i = 0; i < 5; i++)
        {
            using var tcp = new TcpClient();
            tcp.Connect("127.0.0.1", port);
            var junk = new byte[2000];
            new Random(i).NextBytes(junk);
            tcp.GetStream().Write(junk);
        }

        using (var tcp = new TcpClient())
        {
            tcp.Connect("127.0.0.1", port);
            tcp.GetStream().Write(Encoding.ASCII.GetBytes("SSH-2.0-teste\r\n"));
        }

        using var client = Sftp(port);
        client.Connect();
        client.IsConnected.Should().BeTrue();
    }

    [Fact]
    public void Too_many_failed_logins_block_the_address()
    {
        var port = Start(NetworkProtocol.Sftp);

        var blocked = false;
        for (var attempt = 0; attempt < 12 && !blocked; attempt++)
        {
            try
            {
                Sftp(port, "errada").Connect();
            }
            catch (SshAuthenticationException)
            {
                // ainda respondendo: conta como falha
            }
            catch (Exception)
            {
                blocked = true; // ligação cortada antes da autenticação
            }
        }

        blocked.Should().BeTrue("depois de logins falhados demais o endereço é bloqueado");

        var good = () => Sftp(port).Connect();
        good.Should().Throw<Exception>("nem a senha certa entra durante o bloqueio");
        _logs.GetText(NetworkProtocol.Sftp).Should().Contain("bloqueado");
    }

    [Fact]
    public void Stopping_the_server_disconnects_clients_and_frees_the_port()
    {
        var port = Start(NetworkProtocol.Sftp);
        using var client = Sftp(port);
        client.Connect();

        _service.StopServer(NetworkProtocol.Sftp);

        var wait = Stopwatch.StartNew();
        while (client.IsConnected && wait.Elapsed < TimeSpan.FromSeconds(5))
        {
            Thread.Sleep(50);
        }

        var list = () => client.ListDirectory("/").ToList();
        list.Should().Throw<Exception>();
        var reconnect = () => Sftp(port).Connect();
        reconnect.Should().Throw<Exception>();

        using var listener = new TcpListener(IPAddress.Loopback, port);
        listener.Start();
    }

    private static byte[] FingerprintOf(int port)
    {
        byte[]? fingerprint = null;
        using var client = Sftp(port);
        client.HostKeyReceived += (_, e) => fingerprint = e.FingerPrint;
        client.Connect();
        return fingerprint!;
    }
}

internal static class SshTestHelpers
{
    public static int FreeTcpPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    public static void DeleteDirectory(string directory)
    {
        for (var attempt = 0; attempt < 30; attempt++)
        {
            try
            {
                if (Directory.Exists(directory))
                {
                    // Junções são removidas sem apagar o que está do outro lado.
                    RemoveLinks(new DirectoryInfo(directory));
                    Directory.Delete(directory, recursive: true);
                }

                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Thread.Sleep(100);
            }
        }
    }

    private static void RemoveLinks(DirectoryInfo directory)
    {
        foreach (var child in directory.EnumerateDirectories())
        {
            if (child.LinkTarget is not null)
            {
                child.Delete();
            }
            else
            {
                RemoveLinks(child);
            }
        }
    }

    public static void CreateJunction(string link, string target)
    {
        using var process = Process.Start(new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{target}\"")
        {
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardOutput = true
        })!;
        process.WaitForExit();
        process.ExitCode.Should().Be(0, "a junção de teste precisa existir");
    }
}

/// <summary>Um canal de dois sentidos de verdade (pipe nomeado), para testar as sessões sem SSH no meio.</summary>
internal sealed class PipePair : IDisposable
{
    private PipePair(NamedPipeServerStream server, NamedPipeClientStream client)
    {
        Server = server;
        Client = client;
    }

    public NamedPipeServerStream Server { get; }

    public NamedPipeClientStream Client { get; }

    public static async Task<PipePair> CreateAsync()
    {
        var name = "dora-test-" + Guid.NewGuid().ToString("N");
        // Buffers grandes: com o padrão (minúsculo) os dois lados podem ficar escrevendo um para o outro sem ninguém ler.
        var server = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 1 << 20, 1 << 20);
        var client = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous);
        var accepted = server.WaitForConnectionAsync();
        await client.ConnectAsync(5000);
        await accepted;
        return new PipePair(server, client);
    }

    public void Dispose()
    {
        Client.Dispose();
        Server.Dispose();
    }
}

/// <summary>Cliente SFTP mínimo, escrito à mão, para mandar pacotes que um cliente de verdade nunca mandaria.</summary>
internal sealed class RawSftp(Stream stream)
{
    public const byte Open = 3, Close = 4, Read = 5, Write = 6, Stat = 7, Setstat = 9, Opendir = 11, Readdir = 12, Remove = 13,
        Mkdir = 14, Rmdir = 15, Realpath = 16, Rename = 18, Readlink = 19, Extended = 200;

    public const byte HandleReply = 102, DataReply = 103, NameReply = 104, AttrsReply = 105;
    public const uint FlagRead = 1, FlagWrite = 2, FlagCreate = 8, FlagTruncate = 16;

    private uint _id;

    public sealed record Reply(byte Type, byte[] Body)
    {
        public uint Code => BinaryPrimitives.ReadUInt32BigEndian(Body.AsSpan(4));

        public string Handle => ReadString(Body, 4).Value;

        public byte[] Data => ReadBytes(Body, 4);

        public IReadOnlyList<string> Names
        {
            get
            {
                var names = new List<string>();
                var count = BinaryPrimitives.ReadUInt32BigEndian(Body.AsSpan(4));
                var offset = 8;
                for (var i = 0; i < count; i++)
                {
                    var (name, next) = ReadString(Body, offset);
                    names.Add(name);
                    (_, next) = ReadString(Body, next);
                    var flags = BinaryPrimitives.ReadUInt32BigEndian(Body.AsSpan(next));
                    next += 4;
                    if ((flags & 1) != 0) { next += 8; }
                    if ((flags & 2) != 0) { next += 8; }
                    if ((flags & 4) != 0) { next += 4; }
                    if ((flags & 8) != 0) { next += 8; }
                    offset = next;
                }

                return names;
            }
        }

        private static (string Value, int Next) ReadString(byte[] body, int offset)
        {
            var length = (int)BinaryPrimitives.ReadUInt32BigEndian(body.AsSpan(offset));
            return (Encoding.UTF8.GetString(body, offset + 4, length), offset + 4 + length);
        }

        private static byte[] ReadBytes(byte[] body, int offset)
        {
            var length = (int)BinaryPrimitives.ReadUInt32BigEndian(body.AsSpan(offset));
            return body.AsSpan(offset + 4, length).ToArray();
        }
    }

    public async Task InitAsync()
    {
        await SendAsync(Frame(1, false, 3u));
        var reply = await ReadAsync();
        reply.Type.Should().Be(2);
    }

    public async Task<Reply> CallAsync(byte type, params object[] fields)
    {
        await SendAsync(Frame(type, true, [++_id, .. fields]));
        return await ReadAsync();
    }

    /// <summary>Campos: uint, ulong, string ou byte[] (este vira "string" binária). Se <paramref name="withId"/>, o primeiro é o id.</summary>
    public static byte[] Frame(byte type, bool withId, params object[] fields)
    {
        _ = withId;
        using var body = new MemoryStream();
        body.WriteByte(type);
        foreach (var field in fields)
        {
            switch (field)
            {
                case uint u:
                    body.Write(U32(u));
                    break;
                case ulong ul:
                    var eight = new byte[8];
                    BinaryPrimitives.WriteUInt64BigEndian(eight, ul);
                    body.Write(eight);
                    break;
                case string s:
                    var utf8 = Encoding.UTF8.GetBytes(s);
                    body.Write(U32((uint)utf8.Length));
                    body.Write(utf8);
                    break;
                case byte[] bytes:
                    body.Write(U32((uint)bytes.Length));
                    body.Write(bytes);
                    break;
                default:
                    throw new ArgumentException($"Campo não suportado: {field.GetType()}");
            }
        }

        var payload = body.ToArray();
        return [.. U32((uint)payload.Length), .. payload];
    }

    public async Task SendAsync(byte[] bytes)
    {
        await stream.WriteAsync(bytes);
        await stream.FlushAsync();
    }

    public async Task<Reply> ReadAsync() => (await TryReadAsync()) ?? throw new EndOfStreamException("O servidor fechou o canal.");

    public async Task<Reply?> TryReadAsync()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var header = new byte[4];
        if (!await ReadExactlyAsync(header, cts.Token))
        {
            return null;
        }

        var body = new byte[BinaryPrimitives.ReadUInt32BigEndian(header)];
        if (!await ReadExactlyAsync(body, cts.Token))
        {
            return null;
        }

        return new Reply(body[0], body[1..]);
    }

    private async Task<bool> ReadExactlyAsync(byte[] buffer, CancellationToken cancellationToken)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            int read;
            try
            {
                read = await stream.ReadAsync(buffer.AsMemory(offset), cancellationToken);
            }
            catch (IOException)
            {
                return false;
            }

            if (read == 0)
            {
                return false;
            }

            offset += read;
        }

        return true;
    }

    private static byte[] U32(uint value)
    {
        var bytes = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(bytes, value);
        return bytes;
    }
}
