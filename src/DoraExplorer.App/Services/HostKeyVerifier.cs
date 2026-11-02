using System.Windows;
using Renci.SshNet;

namespace DoraExplorer.App.Services;

public enum HostKeyDecision
{
    /// <summary>A chave apresentada é igual à guardada.</summary>
    Trusted,

    /// <summary>Servidor ainda não conhecido: é preciso o usuário confirmar a impressão digital.</summary>
    Unknown,

    /// <summary>A chave difere da guardada: possível interceptação (man-in-the-middle).</summary>
    Changed
}

/// <summary>
/// Verifica a chave do servidor SSH (SFTP/SCP) antes de enviar credenciais. Sem isto, qualquer
/// máquina que se faça passar pelo servidor receberia o usuário e a senha.
/// </summary>
public static class HostKeyVerifier
{
    private static readonly AsyncLocal<bool> NonInteractive = new();

    private static readonly Lazy<KnownHostsStore> DefaultStore = new(() => new KnownHostsStore(KnownHostsStore.DefaultPath));

    /// <summary>
    /// Dentro deste escopo nunca se mostra diálogo: servidores desconhecidos ou alterados são recusados.
    /// Usado pela verificação periódica em segundo plano.
    /// </summary>
    public static IDisposable BeginNonInteractive()
    {
        var previous = NonInteractive.Value;
        NonInteractive.Value = true;
        return new Scope(previous);
    }

    public static HostKeyDecision Decide(KnownHostEntry? stored, KnownHostEntry offered)
    {
        if (stored is null)
        {
            return HostKeyDecision.Unknown;
        }

        return string.Equals(stored.Sha256, offered.Sha256, StringComparison.Ordinal) &&
               string.Equals(stored.Algorithm, offered.Algorithm, StringComparison.OrdinalIgnoreCase)
            ? HostKeyDecision.Trusted
            : HostKeyDecision.Changed;
    }

    public static void Connect(BaseClient client, string host, int port) =>
        Connect(client, host, port, DefaultStore.Value, Confirm);

    public static void Connect(
        BaseClient client,
        string host,
        int port,
        KnownHostsStore store,
        Func<string, string, bool> confirm)
    {
        string? failure = null;

        client.HostKeyReceived += (_, e) =>
        {
            var offered = new KnownHostEntry(e.HostKeyName, e.FingerPrintSHA256);
            var stored = store.TryGet(host, port);
            var decision = Decide(stored, offered);

            switch (decision)
            {
                case HostKeyDecision.Trusted:
                    e.CanTrust = true;
                    return;

                case HostKeyDecision.Unknown:
                    e.CanTrust = confirm(
                        "Novo servidor SSH",
                        $"Primeira ligação a {host}:{port}.\n\n" +
                        $"Algoritmo: {offered.Algorithm}\n" +
                        $"Impressão digital: SHA256:{offered.Sha256}\n\n" +
                        "Confirme que esta impressão digital pertence ao servidor antes de continuar.\n" +
                        "Confiar neste servidor e guardá-lo?");
                    if (!e.CanTrust)
                    {
                        failure = $"A identificação do servidor SSH {host}:{port} não foi confirmada. " +
                                  "Conecte-se manualmente uma vez para aprová-la.";
                        break;
                    }

                    store.Save(host, port, offered);
                    AppLogService.Info($"Servidor SSH {host}:{port} guardado ({offered.Algorithm} SHA256:{offered.Sha256}).");
                    return;

                default:
                    AppLogService.Warning(
                        $"A chave do servidor SSH {host}:{port} MUDOU. Guardada: {stored!.Algorithm} SHA256:{stored.Sha256}; " +
                        $"recebida: {offered.Algorithm} SHA256:{offered.Sha256}.");
                    e.CanTrust = confirm(
                        "ATENÇÃO: identificação do servidor alterada",
                        $"A identificação do servidor {host}:{port} MUDOU.\n\n" +
                        $"Guardada: {stored.Algorithm} SHA256:{stored.Sha256}\n" +
                        $"Recebida: {offered.Algorithm} SHA256:{offered.Sha256}\n\n" +
                        "Isto pode indicar um ataque (interceptação) ou apenas que o equipamento foi reinstalado.\n" +
                        "Só continue se tiver certeza. Substituir a identificação guardada?");
                    if (!e.CanTrust)
                    {
                        failure = $"A identificação do servidor SSH {host}:{port} mudou em relação à guardada. Ligação recusada.";
                        break;
                    }

                    store.Save(host, port, offered);
                    return;
            }
        };

        try
        {
            client.Connect();
        }
        catch (Exception ex) when (failure is not null)
        {
            // O SSH.NET reporta só "Key exchange negotiation failed"; devolve-se o motivo real.
            throw new IOException(failure, ex);
        }
    }

    /// <summary>Pergunta ao usuário (Sim/Não); recusa sem perguntar em verificações de segundo plano.</summary>
    internal static bool Confirm(string title, string message)
    {
        if (NonInteractive.Value)
        {
            return false;
        }

        var app = Application.Current;
        if (app is null)
        {
            return false;
        }

        return app.Dispatcher.Invoke(() =>
        {
            var result = app.MainWindow is { } owner
                ? MessageBox.Show(owner, message, title, MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No)
                : MessageBox.Show(message, title, MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
            return result == MessageBoxResult.Yes;
        });
    }

    private sealed class Scope : IDisposable
    {
        private readonly bool _previous;

        public Scope(bool previous)
        {
            _previous = previous;
        }

        public void Dispose() => NonInteractive.Value = _previous;
    }
}
