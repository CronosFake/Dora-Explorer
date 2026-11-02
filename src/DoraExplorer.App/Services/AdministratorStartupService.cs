using DoraExplorer.App.Contracts;
using DoraExplorer.App.Infrastructure;

namespace DoraExplorer.App.Services;

/// <summary>
/// Menu Preferência &gt; "Iniciar sempre como administrador". Ligado, o app confere ao abrir se está elevado e, se não estiver,
/// pede a confirmação do Windows (UAC) e reabre como administrador.
/// </summary>
public sealed class AdministratorStartupService
{
    private readonly AppSettingsService _settingsService;
    private readonly IElevationService _elevation;

    public AdministratorStartupService(AppSettingsService settingsService, IElevationService elevation)
    {
        _settingsService = settingsService;
        _elevation = elevation;
    }

    public bool IsEnabled { get; private set; }

    public event EventHandler? Changed;

    public void Load() => IsEnabled = _settingsService.LoadStartAsAdministrator();

    /// <summary>
    /// Liga a opção e pede o acesso de administrador na hora. A opção é gravada antes do pedido porque o processo atual fecha
    /// logo depois (o novo lê o arquivo). Se o usuário recusa a confirmação do Windows (<paramref name="errorMessage"/> nulo) ou o
    /// reinício falha, a opção volta a desligada para o check refletir a realidade.
    /// </summary>
    public bool TryEnable(out string? errorMessage)
    {
        errorMessage = null;
        Persist(true);

        if (_elevation.IsElevated)
        {
            Changed?.Invoke(this, EventArgs.Empty);
            return true;
        }

        if (_elevation.TryRestartElevated(out errorMessage))
        {
            Changed?.Invoke(this, EventArgs.Empty);
            return true;
        }

        Persist(false);
        Changed?.Invoke(this, EventArgs.Empty);
        return false;
    }

    public void Disable()
    {
        Persist(false);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Chamado ao abrir o app, antes de criar a janela. Devolve <c>true</c> quando o app está sendo reaberto como administrador e
    /// esta abertura deve parar. Recusar a confirmação do Windows abre o app normalmente e mantém a opção (pergunta na próxima vez).
    /// </summary>
    public bool TryElevateOnStartup(IReadOnlyList<string> args)
    {
        if (!IsEnabled || _elevation.IsElevated)
        {
            return false;
        }

        // Já foi aberto pelo pedido de elevação e ainda assim não está elevado: pedir de novo giraria em círculos.
        if (StartupArguments.TryGetParentProcessId(args, out _))
        {
            AppLogService.Warning("Aberto para ser administrador, mas sem acesso elevado; seguindo sem pedir de novo.");
            return false;
        }

        if (_elevation.TryRestartElevated(out var errorMessage))
        {
            return true;
        }

        if (errorMessage is not null)
        {
            AppLogService.Warning($"Não foi possível iniciar como administrador: {errorMessage}");
        }

        return false;
    }

    private void Persist(bool value)
    {
        IsEnabled = value;
        _settingsService.SaveStartAsAdministrator(value);
    }
}
