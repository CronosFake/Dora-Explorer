namespace DoraExplorer.App.Infrastructure;

public static class OperationTimeouts
{
    public static readonly TimeSpan Network = TimeSpan.FromSeconds(60);

    public static readonly TimeSpan FileTransfer = TimeSpan.FromMinutes(30);

    public static readonly TimeSpan FileOperation = TimeSpan.FromMinutes(15);

    public static CancellationTokenSource CreateLinked(TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        var source = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        source.CancelAfter(timeout);
        return source;
    }

    /// <summary>
    /// Executa trabalho bloqueante em segundo plano com um tempo limite que realmente vale:
    /// ao expirar (ou ser cancelado), quem espera recebe <see cref="OperationCanceledException"/> de
    /// imediato e o trabalho recebe o token para parar de forma cooperativa. Passar o token só a
    /// <see cref="Task.Run(Action, CancellationToken)"/> não interrompe uma chamada já em curso.
    /// </summary>
    public static async Task<T> RunAsync<T>(
        Func<CancellationToken, T> work,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        using var linked = CreateLinked(timeout, cancellationToken);

        // O token é copiado antes: o trabalho pode continuar depois de o CTS ser descartado.
        var token = linked.Token;
        var task = Task.Run(() => work(token), CancellationToken.None);

        // Se o trabalho for abandonado por timeout e falhar depois, a exceção é observada aqui.
        _ = task.ContinueWith(
            static finished => _ = finished.Exception,
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);

        return await task.WaitAsync(token).ConfigureAwait(false);
    }

    /// <summary>
    /// Como <see cref="RunAsync{T}"/>, mas o limite é de INATIVIDADE: o trabalho recebe uma função "ainda estou
    /// andando" e cada chamada reinicia a contagem. Serve para cópias longas, que podem durar horas desde que
    /// os bytes continuem andando; só quem ficou parado por <paramref name="inactivity"/> é cancelado.
    /// </summary>
    public static async Task<T> RunWithInactivityAsync<T>(
        Func<CancellationToken, Action, T> work,
        TimeSpan inactivity,
        CancellationToken cancellationToken = default)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        linked.CancelAfter(inactivity);

        var token = linked.Token;
        void KeepAlive()
        {
            try
            {
                linked.CancelAfter(inactivity);
            }
            catch (ObjectDisposedException)
            {
                // A espera já terminou (por limite ou cancelamento): o trabalho só está encerrando.
            }
        }

        var task = Task.Run(() => work(token, KeepAlive), CancellationToken.None);
        _ = task.ContinueWith(
            static finished => _ = finished.Exception,
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);

        try
        {
            return await task.WaitAsync(token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Cancelado pelo usuário: o trabalho está terminando e apagando o que ficou pela metade. Quem chamou
            // só deve olhar a pasta depois disso, senão veria o arquivo parcial por um instante. (Se o limite
            // estourou por inatividade o trabalho está parado, e aí não vale esperar.)
            await Task.WhenAny(task, Task.Delay(CancellationCleanupGrace, CancellationToken.None)).ConfigureAwait(false);
            throw;
        }
    }

    private static readonly TimeSpan CancellationCleanupGrace = TimeSpan.FromSeconds(5);
}
