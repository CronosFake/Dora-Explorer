namespace DoraExplorer.App.Models;

/// <summary>Resultado de copiar ou mover: cancelar pelo usuário não é erro e não deve abrir mensagem.</summary>
public sealed record TransferOutcome(bool Success, string? ErrorMessage, bool Cancelled);
