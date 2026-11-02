using System.Diagnostics;
using System.Globalization;

namespace DoraExplorer.App.Infrastructure;

/// <summary>
/// Argumentos de linha de comando do Dora Explorer. Hoje há um só: o processo aberto como administrador recebe o número do
/// processo antigo e espera ele fechar, senão a trava de instância única (<see cref="SingleInstanceGuard"/>) o recusaria.
/// </summary>
internal static class StartupArguments
{
    public const string ElevatedFromOption = "--elevado-de";

    public static string BuildRestartArguments(int currentProcessId) =>
        string.Create(CultureInfo.InvariantCulture, $"{ElevatedFromOption} {currentProcessId}");

    public static bool TryGetParentProcessId(IReadOnlyList<string> args, out int processId)
    {
        processId = 0;
        for (var i = 0; i < args.Count - 1; i++)
        {
            if (string.Equals(args[i], ElevatedFromOption, StringComparison.OrdinalIgnoreCase))
            {
                return int.TryParse(args[i + 1], NumberStyles.None, CultureInfo.InvariantCulture, out processId) && processId > 0;
            }
        }

        return false;
    }

    /// <summary>
    /// Espera o processo <paramref name="processId"/> terminar. Devolve <c>true</c> se ele já tinha terminado (ou nunca existiu)
    /// e <c>false</c> se continua rodando depois de <paramref name="timeout"/>.
    /// </summary>
    public static bool WaitForParentExit(int processId, TimeSpan timeout)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return process.WaitForExit(timeout);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            return true; // não existe mais
        }
    }
}
