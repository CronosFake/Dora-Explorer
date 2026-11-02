using System.Collections.Concurrent;

namespace DoraExplorer.App.Services;

/// <summary>
/// Bloqueia um endereço depois de logins falhados demais (como o servidor FTP): <paramref name="maxFailures"/>
/// falhas dentro de <paramref name="window"/> bloqueiam o endereço por <paramref name="blockFor"/>.
/// </summary>
internal sealed class LoginThrottle(int maxFailures, TimeSpan window, TimeSpan blockFor, Func<DateTime>? clock = null)
{
    private readonly ConcurrentDictionary<string, Record> _records = new(StringComparer.Ordinal);
    private readonly Func<DateTime> _now = clock ?? (() => DateTime.UtcNow);

    public bool IsBlocked(string address) =>
        _records.TryGetValue(address, out var record) && record.BlockedUntil > _now();

    /// <summary>Registra uma falha; devolve verdadeiro se com ela o endereço acabou de ser bloqueado.</summary>
    public bool RecordFailure(string address)
    {
        var record = _records.GetOrAdd(address, _ => new Record { WindowStart = _now() });
        lock (record)
        {
            var now = _now();
            if (now - record.WindowStart > window)
            {
                record.Count = 0;
                record.WindowStart = now;
            }

            record.Count++;
            if (record.Count < maxFailures)
            {
                return false;
            }

            record.BlockedUntil = now + blockFor;
            record.Count = 0;
            return true;
        }
    }

    public void RecordSuccess(string address) => _ = _records.TryRemove(address, out _);

    private sealed class Record
    {
        public int Count;
        public DateTime WindowStart;
        public DateTime BlockedUntil;
    }
}
