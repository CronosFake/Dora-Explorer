using System.Runtime;

namespace DoraExplorer.App.Infrastructure;

/// <summary>
/// Depois de carregar uma pasta enorme, o coletor de lixo segue com reservada muita memória que já não tem dono
/// (listas intermediárias, ordenações, blocos grandes fragmentados). Uma coleta compactante, feita uma única vez
/// quando a tela já assentou, devolve essa reserva ao sistema: com 100 mil arquivos a memória privada caiu de
/// cerca de 300 MB para cerca de 160 MB. Cargas seguidas reiniciam a espera, então só a última dispara a coleta.
/// </summary>
public sealed class MemoryTrimmer : IDisposable
{
    /// <summary>Pastas menores que isso não deixam sobra que justifique parar o coletor por um instante.</summary>
    public const int LargeLoadThreshold = 10_000;

    private readonly TimeSpan _settleDelay;
    private readonly Timer _timer;

    public MemoryTrimmer(TimeSpan settleDelay, Action collect)
    {
        _settleDelay = settleDelay;
        _timer = new Timer(_ => collect(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
    }

    public static MemoryTrimmer Shared { get; } = new(TimeSpan.FromSeconds(2), Compact);

    public void ScheduleAfterLargeLoad(int entryCount)
    {
        if (entryCount < LargeLoadThreshold)
        {
            return;
        }

        _ = _timer.Change(_settleDelay, Timeout.InfiniteTimeSpan);
    }

    public void Dispose() => _timer.Dispose();

    private static void Compact()
    {
        GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
        GC.Collect(2, GCCollectionMode.Aggressive, blocking: true, compacting: true);
    }
}
