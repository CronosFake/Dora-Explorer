namespace DoraExplorer.App.Contracts;

/// <summary>
/// Nível de acesso do aplicativo. O Windows não deixa um programa já aberto virar administrador: a única saída é abrir de novo
/// com a confirmação do Windows (UAC) e fechar o processo antigo.
/// </summary>
public interface IElevationService
{
    /// <summary>O aplicativo está rodando como administrador (com o token elevado).</summary>
    bool IsElevated { get; }

    /// <summary>
    /// Abre o Dora Explorer de novo como administrador e fecha este. Devolve <c>false</c> sem mensagem quando o usuário recusa a
    /// confirmação do Windows (o app continua aberto), e <c>false</c> com mensagem quando não foi possível reiniciar.
    /// </summary>
    bool TryRestartElevated(out string? errorMessage);
}
