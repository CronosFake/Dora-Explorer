namespace DoraExplorer.App.Infrastructure;

/// <summary>
/// Registro das operações demoradas em andamento (copiar, mover, compactar...). Serve para o fechamento da
/// janela: sair no meio de uma cópia deixaria o arquivo de destino pela metade, então o app pergunta, cancela
/// e espera o trabalho limpar o que ficou antes de encerrar.
/// </summary>
public sealed class OperationTracker
{
    private readonly object _gate = new();
    private readonly List<Entry> _entries = [];

    public static OperationTracker Shared { get; } = new();

    public bool HasActive
    {
        get
        {
            lock (_gate)
            {
                return _entries.Count > 0;
            }
        }
    }

    /// <summary>Registra uma operação; descartar o resultado a tira do registro.</summary>
    public IDisposable Begin(CancellationTokenSource cancellation, Task completion)
    {
        var entry = new Entry(cancellation, completion);
        lock (_gate)
        {
            _entries.Add(entry);
        }

        return new Registration(this, entry);
    }

    /// <summary>
    /// Pede o cancelamento de todas as operações e espera elas terminarem (inclusive a limpeza do que ficou
    /// pela metade). Devolve falso se alguma não terminou dentro do prazo.
    /// </summary>
    public async Task<bool> CancelAllAsync(TimeSpan timeout)
    {
        Entry[] snapshot;
        lock (_gate)
        {
            snapshot = [.. _entries];
        }

        foreach (var entry in snapshot)
        {
            try
            {
                entry.Cancellation.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // A operação acabou entre o registro e o cancelamento.
            }
        }

        var all = Task.WhenAll(snapshot.Select(entry => WaitIgnoringFailure(entry.Completion)));
        return await Task.WhenAny(all, Task.Delay(timeout)).ConfigureAwait(false) == all;
    }

    private static async Task WaitIgnoringFailure(Task task)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Cancelar é o esperado.
        }
        catch (Exception)
        {
            // A falha da operação já é mostrada a quem a iniciou; aqui só interessa ela ter parado.
        }
    }

    private void Remove(Entry entry)
    {
        lock (_gate)
        {
            _ = _entries.Remove(entry);
        }
    }

    private sealed record Entry(CancellationTokenSource Cancellation, Task Completion);

    private sealed class Registration(OperationTracker owner, Entry entry) : IDisposable
    {
        public void Dispose() => owner.Remove(entry);
    }
}
