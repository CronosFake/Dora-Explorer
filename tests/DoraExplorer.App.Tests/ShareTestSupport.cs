using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

namespace DoraExplorer.App.Tests;

/// <summary>Pastas temporárias, portas livres e atalhos para os testes dos servidores e clientes SMB e NFS.</summary>
internal static class ShareTestSupport
{
    public static string NewFolder(string prefix) =>
        Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), prefix + Guid.NewGuid().ToString("N"))).FullName;

    public static int FreeTcpPort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    /// <summary>Apaga a pasta, esperando o servidor soltar os arquivos que acabou de usar.</summary>
    public static void DeleteWhenReleased(string directory)
    {
        for (var attempt = 0; attempt < 30; attempt++)
        {
            try
            {
                if (Directory.Exists(directory))
                {
                    Directory.Delete(directory, recursive: true);
                }

                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Thread.Sleep(100);
            }
        }
    }

    /// <summary>
    /// Cria um atalho de pasta para <paramref name="target"/>: junção no Windows (não pede administrador) e link simbólico
    /// no Linux. O servidor tem de tratar os dois do mesmo jeito: o que aponta para fora da pasta compartilhada não existe.
    /// </summary>
    public static void CreateFolderLink(string link, string target)
    {
        if (!OperatingSystem.IsWindows())
        {
            Directory.CreateSymbolicLink(link, target);
            return;
        }

        using var process = Process.Start(new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{target}\"")
        {
            CreateNoWindow = true,
            UseShellExecute = false
        })!;
        process.WaitForExit();
        if (process.ExitCode != 0 || !Directory.Exists(link))
        {
            throw new InvalidOperationException("Não foi possível criar a junção de teste.");
        }
    }

    /// <summary>Acha um arquivo-fonte do repositório subindo a partir da pasta dos testes (aceita "\\" ou "/" no caminho).</summary>
    public static string LocateSource(string relativePath)
    {
        var normalized = relativePath.Replace('\\', Path.DirectorySeparatorChar).Replace('/', Path.DirectorySeparatorChar);
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            var candidate = Path.Combine(directory.FullName, normalized);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new FileNotFoundException($"Não encontrei {relativePath} a partir de {AppContext.BaseDirectory}");
    }

    /// <summary>Conteúdo que não se repete em blocos pequenos: um bloco trocado de lugar mudaria o resultado.</summary>
    public static byte[] Pattern(int length)
    {
        var data = new byte[length];
        var state = 2166136261u;
        for (var i = 0; i < data.Length; i++)
        {
            state = (state ^ (uint)i) * 16777619u;
            data[i] = (byte)(state >> 13);
        }

        return data;
    }
}
