using DoraExplorer.App.Infrastructure;
using DoraExplorer.App.Models;
using DoraExplorer.App.ViewModels;
using DoraExplorer.App.Services;
using FluentAssertions;
using Xunit;

namespace DoraExplorer.App.Tests;

public sealed class PathSafetyServiceTests
{
    [Theory]
    [InlineData(@"C:\Parent\Child", @"C:\Parent", true)]
    [InlineData(@"C:\Parent", @"C:\Parent", false)]
    [InlineData(@"C:\Other", @"C:\Parent", false)]
    [InlineData(@"C:\Foo", @"C:\", true)]
    [InlineData(@"C:\Foo\Bar", @"C:\", true)]
    [InlineData(@"C:\", @"C:\", false)]
    [InlineData(@"D:\Foo", @"C:\", false)]
    [InlineData(@"C:\ParentX", @"C:\Parent", false)]
    [InlineData(@"C:\Parent\Child\", @"C:\Parent\", true)]
    [InlineData(@"c:\parent\child", @"C:\PARENT", true)]
    [InlineData(@"C:\Parent\..\Other", @"C:\Parent", false)]
    [InlineData(@"C:\Parent\Child\..\..\Other", @"C:\Parent", false)]
    public void IsSubPath_detects_child_directories(string childPath, string parentPath, bool expected)
    {
        PathSafetyService.IsSubPath(childPath, parentPath).Should().Be(expected);
    }
}

public sealed class NetworkFileServiceTests
{
    [Theory]
    [InlineData(NetworkProtocol.Ftp, "ftp.exemplo.com", "/pub/arquivos")]
    [InlineData(NetworkProtocol.Ftp, "127.0.0.1", "/")]
    [InlineData(NetworkProtocol.Tftp, "10.0.0.2", "/boot")]
    [InlineData(NetworkProtocol.Sftp, "host", "/")]
    [InlineData(NetworkProtocol.Scp, "server", "/data")]
    public void TryParseRemotePath_roundtrips_BuildRemotePath(
        NetworkProtocol protocol,
        string host,
        string remotePath)
    {
        var path = NetworkFileService.BuildRemotePath(protocol, host, remotePath);
        var success = NetworkFileService.TryParseRemotePath(
            path,
            out var parsedProtocol,
            out var parsedHost,
            out var parsedRemotePath);

        success.Should().BeTrue();
        parsedProtocol.Should().Be(protocol);
        parsedHost.Should().Be(host);
        parsedRemotePath.Should().Be(remotePath.StartsWith('/') ? remotePath : "/" + remotePath);
    }

    [Theory]
    [InlineData("dora-http://host/x")]
    [InlineData("ftp://host/x")]
    [InlineData("dora-://host/x")]
    public void TryParseRemotePath_rejects_unknown_schemes(string path)
    {
        NetworkFileService.TryParseRemotePath(path, out _, out _, out _).Should().BeFalse();
    }

    [Fact]
    public void TryParseRemotePath_rejects_local_paths()
    {
        NetworkFileService.TryParseRemotePath(@"C:\Windows", out _, out _, out _).Should().BeFalse();
    }
}

public sealed class FileOpenServiceTests
{
    [Fact]
    public void The_open_with_window_refuses_a_missing_file_instead_of_showing_anything()
    {
        var shown = FileOpenService.TryShowNativeOpenWithDialog(@"C:\nao-existe\arquivo.txt", IntPtr.Zero, out var error);

        shown.Should().BeFalse();
        error.Should().Contain("não existe");
    }

    [Fact]
    public void The_open_with_window_refuses_an_empty_path()
    {
        FileOpenService.TryShowNativeOpenWithDialog("  ", IntPtr.Zero, out var error).Should().BeFalse();

        error.Should().NotBeNullOrWhiteSpace();
    }
}

public sealed class OperationTrackerTests
{
    [Fact]
    public void An_operation_counts_as_active_only_while_it_is_registered()
    {
        var tracker = new OperationTracker();
        using var cancellation = new CancellationTokenSource();

        tracker.HasActive.Should().BeFalse();
        var registration = tracker.Begin(cancellation, new TaskCompletionSource().Task);
        tracker.HasActive.Should().BeTrue();

        registration.Dispose();
        tracker.HasActive.Should().BeFalse();
    }

    [Fact]
    public async Task Cancelling_all_waits_for_the_operation_to_finish_its_cleanup()
    {
        var tracker = new OperationTracker();
        using var cancellation = new CancellationTokenSource();
        var cleanedUp = false;
        var work = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(Timeout.Infinite, cancellation.Token);
            }
            catch (OperationCanceledException)
            {
                await Task.Delay(150);
                cleanedUp = true;
            }
        });
        using var registration = tracker.Begin(cancellation, work);

        var finished = await tracker.CancelAllAsync(TimeSpan.FromSeconds(5));

        finished.Should().BeTrue();
        cleanedUp.Should().BeTrue("o fechamento só continua depois de o trabalho apagar o que ficou pela metade");
    }

    [Fact]
    public async Task Cancelling_all_gives_up_after_the_timeout_when_an_operation_never_stops()
    {
        var tracker = new OperationTracker();
        using var cancellation = new CancellationTokenSource();
        var neverEnds = new TaskCompletionSource();
        using var registration = tracker.Begin(cancellation, neverEnds.Task);

        var finished = await tracker.CancelAllAsync(TimeSpan.FromMilliseconds(150));

        finished.Should().BeFalse();
        cancellation.IsCancellationRequested.Should().BeTrue();
        neverEnds.SetResult();
    }

    [Fact]
    public async Task A_failed_or_already_disposed_operation_does_not_break_cancelling_the_others()
    {
        var tracker = new OperationTracker();
        var disposed = new CancellationTokenSource();
        var failed = Task.FromException(new InvalidOperationException("falhou"));
        using var first = tracker.Begin(disposed, failed);
        disposed.Dispose();
        using var healthy = new CancellationTokenSource();
        using var second = tracker.Begin(healthy, Task.CompletedTask);

        var finished = await tracker.CancelAllAsync(TimeSpan.FromSeconds(2));

        finished.Should().BeTrue();
        healthy.IsCancellationRequested.Should().BeTrue();
    }
}

public sealed class ExpectedFailuresTests
{
    [Fact]
    public void A_bitlocker_locked_volume_is_recognized_by_its_hresult()
    {
        var locked = new IOException("Essa unidade está bloqueada pela Criptografia de Unidade de Disco BitLocker.", unchecked((int)0x80310000));

        ExpectedFailures.IsLockedVolume(locked).Should().BeTrue();
    }

    [Fact]
    public void Other_io_failures_are_not_mistaken_for_a_locked_volume()
    {
        ExpectedFailures.IsLockedVolume(new IOException("disco cheio")).Should().BeFalse();
        ExpectedFailures.IsLockedVolume(new IOException("x", unchecked((int)0x80070005))).Should().BeFalse();
        ExpectedFailures.IsLockedVolume(new UnauthorizedAccessException()).Should().BeFalse();
    }
}

public sealed class SqliteSecurityTests
{
    // CVE-2025-6965 (corrupção de memória) só foi corrigida no SQLite 3.50.2; versões anteriores do pacote
    // nativo (SQLitePCLRaw.lib.e_sqlite3 2.1.11 ou menor) vinham com um SQLite mais antigo.
    [Fact]
    public void The_bundled_sqlite_is_new_enough_to_include_the_fix_for_CVE_2025_6965()
    {
        using var connection = new Microsoft.Data.Sqlite.SqliteConnection("Data Source=:memory:");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT sqlite_version()";

        var version = Version.Parse((string)command.ExecuteScalar()!);

        version.Should().BeGreaterThanOrEqualTo(new Version(3, 50, 2));
    }
}

public sealed class MemoryTrimmerTests
{
    private static readonly TimeSpan Settle = TimeSpan.FromMilliseconds(60);

    [Fact]
    public void Small_loads_never_trigger_a_collection()
    {
        var collections = 0;
        using var trimmer = new MemoryTrimmer(Settle, () => Interlocked.Increment(ref collections));

        trimmer.ScheduleAfterLargeLoad(MemoryTrimmer.LargeLoadThreshold - 1);
        Thread.Sleep(400);

        collections.Should().Be(0);
    }

    [Fact]
    public void A_large_load_triggers_one_collection_after_the_settle_delay()
    {
        using var done = new ManualResetEventSlim();
        var collections = 0;
        using var trimmer = new MemoryTrimmer(Settle, () =>
        {
            Interlocked.Increment(ref collections);
            done.Set();
        });

        trimmer.ScheduleAfterLargeLoad(MemoryTrimmer.LargeLoadThreshold);

        done.Wait(TimeSpan.FromSeconds(5)).Should().BeTrue();
        Thread.Sleep(300);
        collections.Should().Be(1);
    }

    [Fact]
    public void Back_to_back_large_loads_wait_for_the_last_one()
    {
        using var done = new ManualResetEventSlim();
        var collections = 0;
        using var trimmer = new MemoryTrimmer(TimeSpan.FromMilliseconds(400), () =>
        {
            Interlocked.Increment(ref collections);
            done.Set();
        });

        for (var i = 0; i < 4; i++)
        {
            trimmer.ScheduleAfterLargeLoad(50_000);
            Thread.Sleep(150);
        }

        collections.Should().Be(0, "cada nova carga reinicia a espera");
        done.Wait(TimeSpan.FromSeconds(5)).Should().BeTrue();
        Thread.Sleep(600);
        collections.Should().Be(1);
    }
}

public sealed class RangeObservableCollectionTests
{
    [Fact]
    public void ReplaceAll_swaps_the_content_with_a_single_reset_notification()
    {
        var collection = new RangeObservableCollection<int> { 1, 2, 3 };
        var notifications = new List<System.Collections.Specialized.NotifyCollectionChangedAction>();
        var properties = new List<string?>();
        collection.CollectionChanged += (_, e) => notifications.Add(e.Action);
        ((System.ComponentModel.INotifyPropertyChanged)collection).PropertyChanged += (_, e) => properties.Add(e.PropertyName);

        collection.ReplaceAll(Enumerable.Range(10, 100_000));

        collection.Count.Should().Be(100_000);
        collection[0].Should().Be(10);
        collection[^1].Should().Be(100_009);
        notifications.Should().Equal(System.Collections.Specialized.NotifyCollectionChangedAction.Reset);
        properties.Should().Contain(["Count", "Item[]"]);
    }

    [Fact]
    public void ReplaceAll_with_nothing_empties_the_collection()
    {
        var collection = new RangeObservableCollection<string> { "a", "b" };

        collection.ReplaceAll([]);

        collection.Should().BeEmpty();
    }

    [Fact]
    public void SyncTo_with_the_same_content_raises_no_notification()
    {
        var collection = new RangeObservableCollection<string> { "a", "b", "c" };
        var notifications = 0;
        collection.CollectionChanged += (_, _) => notifications++;

        collection.SyncTo(["a", "b", "c"]);

        notifications.Should().Be(0);
        collection.Should().Equal("a", "b", "c");
    }

    [Fact]
    public void SyncTo_touches_only_what_changed_and_never_resets()
    {
        var collection = new RangeObservableCollection<string> { "a", "b", "c", "d" };
        var actions = new List<System.Collections.Specialized.NotifyCollectionChangedAction>();
        collection.CollectionChanged += (_, e) => actions.Add(e.Action);

        collection.SyncTo(["a", "c", "d", "novo"]);

        collection.Should().Equal("a", "c", "d", "novo");
        actions.Should().Equal(
            System.Collections.Specialized.NotifyCollectionChangedAction.Remove,
            System.Collections.Specialized.NotifyCollectionChangedAction.Add);
    }

    [Fact]
    public void SyncTo_moves_reordered_items_instead_of_recreating_them()
    {
        var collection = new RangeObservableCollection<string> { "a", "b", "c" };
        var actions = new List<System.Collections.Specialized.NotifyCollectionChangedAction>();
        collection.CollectionChanged += (_, e) => actions.Add(e.Action);

        collection.SyncTo(["c", "a", "b"]);

        collection.Should().Equal("c", "a", "b");
        actions.Should().OnlyContain(a => a == System.Collections.Specialized.NotifyCollectionChangedAction.Move);
    }

    [Fact]
    public void SyncTo_reaches_the_target_for_any_mix_of_insertions_removals_and_moves()
    {
        var random = new Random(1234);
        for (var round = 0; round < 200; round++)
        {
            var before = Enumerable.Range(0, 40).Where(_ => random.Next(3) > 0).OrderBy(_ => random.Next()).ToList();
            var after = Enumerable.Range(0, 40).Where(_ => random.Next(3) > 0).OrderBy(_ => random.Next()).ToList();
            var collection = new RangeObservableCollection<int>();
            collection.ReplaceAll(before);

            collection.SyncTo(after);

            collection.Should().Equal(after, $"rodada {round}");
        }
    }

    [Fact]
    public void SyncTo_swaps_everything_at_once_above_the_limit_and_when_there_are_repeats()
    {
        var big = Enumerable.Range(0, RangeObservableCollection<int>.SyncToLimit + 1).ToList();
        var collection = new RangeObservableCollection<int> { -1 };
        var actions = new List<System.Collections.Specialized.NotifyCollectionChangedAction>();
        collection.CollectionChanged += (_, e) => actions.Add(e.Action);

        collection.SyncTo(big);
        collection.SyncTo([1, 1, 2]);

        collection.Should().Equal(1, 1, 2);
        actions.Should().Equal(
            System.Collections.Specialized.NotifyCollectionChangedAction.Reset,
            System.Collections.Specialized.NotifyCollectionChangedAction.Reset);
    }

    [Fact]
    public void Tree_nodes_expose_the_bulk_collection_and_still_work_as_a_normal_observable_collection()
    {
        var node = new NavigationTreeNodeViewModel("pasta", NavigationNodeKind.Directory, @"C:\pasta");
        var asObservable = (System.Collections.ObjectModel.ObservableCollection<NavigationTreeNodeViewModel>)node.Children;

        asObservable.Add(new NavigationTreeNodeViewModel("a.txt", NavigationNodeKind.File, @"C:\pasta\a.txt"));
        node.Children.ReplaceAll([new NavigationTreeNodeViewModel("b.txt", NavigationNodeKind.File, @"C:\pasta\b.txt")]);

        node.Children.Select(c => c.Name).Should().Equal("b.txt");
    }
}