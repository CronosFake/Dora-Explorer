namespace DoraExplorer.App.Infrastructure;

/// <summary>
/// "Acesso negado" ao copiar ou mover um arquivo. O Windows só devolve o código 5, sem dizer se foi a origem (leitura) ou o
/// destino (gravação) que recusou; por isso a exceção carrega os dois caminhos e o texto em português cita os dois
/// (<see cref="FileErrorText.Describe"/>).
/// </summary>
internal sealed class TransferAccessDeniedException(string source, string destination, string verb, string windowsMessage)
    : UnauthorizedAccessException(windowsMessage)
{
    public string SourcePath { get; } = source;

    public string DestinationPath { get; } = destination;

    /// <summary>"copiar" ou "mover" (infinitivo, para a frase "acesso negado ao copiar ...").</summary>
    public string Verb { get; } = verb;
}
