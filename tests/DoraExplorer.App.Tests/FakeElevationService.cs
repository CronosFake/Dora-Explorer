using DoraExplorer.App.Contracts;

namespace DoraExplorer.App.Tests;

/// <summary>
/// Elevação de mentira: o aviso do Windows (UAC) roda numa área protegida e não pode ser acionado em teste, então só conta os
/// pedidos de reinício e devolve o resultado combinado.
/// </summary>
public sealed class FakeElevationService : IElevationService
{
    public bool IsElevated { get; set; }

    public bool RestartSucceeds { get; set; }

    public string? RestartError { get; set; }

    public int RestartRequests { get; private set; }

    public Action? OnRestart { get; set; }

    public bool TryRestartElevated(out string? errorMessage)
    {
        RestartRequests++;
        OnRestart?.Invoke();
        errorMessage = RestartSucceeds ? null : RestartError;
        return RestartSucceeds;
    }
}
