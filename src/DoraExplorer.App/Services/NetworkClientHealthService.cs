using System.Net.Sockets;
using DoraExplorer.App.Contracts;
using DoraExplorer.App.Models;

namespace DoraExplorer.App.Services;

/// <summary>
/// Verifica periodicamente os perfis de cliente e expõe o estado de saúde: primeiro se o servidor
/// está acessível (sem enviar credenciais) e, só então, se o login funciona.
/// </summary>
/// <remarks>
/// Para não bloquear contas nem disparar alarmes nos equipamentos, o login em segundo plano é
/// suspenso depois de falhas seguidas até o usuário testar/ligar manualmente ou mudar as credenciais.
/// O TFTP (UDP, sem sessão) não tem verificação passiva e fica sem estado.
/// </remarks>
public sealed class NetworkClientHealthService : IDisposable
{
    private const int MaxConsecutiveLoginFailures = 2;

    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan ReachabilityTimeout = TimeSpan.FromSeconds(5);

    private readonly INetworkFileService _networkFileService;
    private readonly Func<NetworkClientProfile, CancellationToken, Task<bool>> _isReachable;
    private readonly object _sync = new();
    private readonly Dictionary<string, bool> _healthByProfileId = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (int Credentials, int Failures)> _loginFailures = new(StringComparer.Ordinal);
    private CancellationTokenSource? _cts;
    private Task? _loop;

    public NetworkClientHealthService(INetworkFileService networkFileService)
        : this(networkFileService, ProbeTcpAsync)
    {
    }

    // Usado nos testes para simular a rede.
    internal NetworkClientHealthService(
        INetworkFileService networkFileService,
        Func<NetworkClientProfile, CancellationToken, Task<bool>> isReachable)
    {
        _networkFileService = networkFileService;
        _isReachable = isReachable;
    }

    public event EventHandler? HealthChanged;

    public void Start()
    {
        if (_loop is not null)
        {
            return;
        }

        _cts = new CancellationTokenSource();
        _loop = Task.Run(() => RunLoopAsync(_cts.Token));
    }

    public bool? GetHealth(string profileId)
    {
        lock (_sync)
        {
            return _healthByProfileId.TryGetValue(profileId, out var healthy) ? healthy : null;
        }
    }

    /// <summary>Teste pedido pelo usuário: faz login de verdade e reinicia a contagem de falhas.</summary>
    public async Task<bool> TestAndUpdateAsync(NetworkClientProfile profile, CancellationToken cancellationToken = default)
    {
        var result = await _networkFileService.TestConnectionAsync(profile.ToConnection(), cancellationToken)
            .ConfigureAwait(false);
        SetHealth(profile.Id, result.Success);
        return result.Success;
    }

    public void Remove(string profileId)
    {
        lock (_sync)
        {
            _healthByProfileId.Remove(profileId);
            _loginFailures.Remove(profileId);
        }

        HealthChanged?.Invoke(this, EventArgs.Empty);
    }

    public void SetHealth(string profileId, bool healthy)
    {
        lock (_sync)
        {
            _healthByProfileId[profileId] = healthy;
            if (healthy)
            {
                _loginFailures.Remove(profileId);
            }
        }

        HealthChanged?.Invoke(this, EventArgs.Empty);
    }

    public void Dispose()
    {
        try
        {
            _cts?.Cancel();
        }
        catch (Exception ex) when (ex is ObjectDisposedException or AggregateException)
        {
            // ignored
        }

        _cts?.Dispose();
        _cts = null;
        _loop = null;
    }

    /// <summary>Verificação passiva de um perfil; <c>Healthy</c> é nulo quando não há como verificar.</summary>
    internal async Task<(bool? Healthy, string? Reason)> CheckProfileAsync(
        NetworkClientProfile profile,
        CancellationToken cancellationToken)
    {
        if (profile.Protocol == NetworkProtocol.Tftp)
        {
            return (null, null);
        }

        if (!await _isReachable(profile, cancellationToken).ConfigureAwait(false))
        {
            return (false, "servidor inacessível");
        }

        var credentials = HashCode.Combine(profile.Host, profile.Port, profile.Username, profile.Password);
        int failures;
        lock (_sync)
        {
            failures = _loginFailures.TryGetValue(profile.Id, out var known) && known.Credentials == credentials
                ? known.Failures
                : 0;
        }

        if (failures >= MaxConsecutiveLoginFailures)
        {
            return (false, "login recusado; verificação automática suspensa até novo teste manual");
        }

        var result = await _networkFileService.TestConnectionAsync(profile.ToConnection(), cancellationToken)
            .ConfigureAwait(false);

        lock (_sync)
        {
            if (result.Success)
            {
                _loginFailures.Remove(profile.Id);
            }
            else
            {
                _loginFailures[profile.Id] = (credentials, failures + 1);
            }
        }

        return (result.Success, result.ErrorMessage);
    }

    private static async Task<bool> ProbeTcpAsync(NetworkClientProfile profile, CancellationToken cancellationToken)
    {
        var port = profile.Port > 0 ? profile.Port : profile.Protocol == NetworkProtocol.Ftp ? 21 : 22;

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(ReachabilityTimeout);
        using var client = new TcpClient();

        try
        {
            await client.ConnectAsync(profile.Host, port, timeout.Token).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return false;
        }
        catch (Exception ex) when (ex is SocketException or ArgumentException)
        {
            return false;
        }
    }

    private async Task RunLoopAsync(CancellationToken cancellationToken)
    {
        // Primeira passagem pouco depois do arranque.
        await Task.Delay(TimeSpan.FromSeconds(15), cancellationToken).ConfigureAwait(false);

        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await RefreshAllAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                AppLogService.Error("Falha no health-check de clientes de rede", ex);
            }

            try
            {
                await Task.Delay(Interval, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
        }
    }

    private async Task RefreshAllAsync(CancellationToken cancellationToken)
    {
        // Em segundo plano nunca se abre diálogo: servidores SSH não aprovados ficam como indisponíveis.
        using var nonInteractive = HostKeyVerifier.BeginNonInteractive();

        var profiles = App.SettingsService.LoadNetworkClientProfiles();
        var changed = false;

        foreach (var profile in profiles)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var (healthy, reason) = await CheckProfileAsync(profile, cancellationToken).ConfigureAwait(false);
            if (healthy is not { } value)
            {
                continue;
            }

            bool? previous;
            lock (_sync)
            {
                previous = _healthByProfileId.TryGetValue(profile.Id, out var known) ? known : null;
                _healthByProfileId[profile.Id] = value;
            }

            if (previous == value)
            {
                continue;
            }

            changed = true;
            if (value)
            {
                AppLogService.Info($"Servidor disponível: {profile.DisplayName} ({profile.Protocol} {profile.Host})");
            }
            else
            {
                AppLogService.Warning(
                    $"Servidor indisponível: {profile.DisplayName} ({profile.Protocol} {profile.Host}) — {reason}");
            }
        }

        if (changed)
        {
            HealthChanged?.Invoke(this, EventArgs.Empty);
        }
    }
}
