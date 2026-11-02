using DoraExplorer.App.Infrastructure;

namespace DoraExplorer.App.Services;

/// <summary>
/// Regras de copiar, enviar e apagar em pastas remotas, sobre uma <see cref="IRemoteSession"/>: percorre pastas,
/// escolhe nomes que não existem ("nome (1).txt", como na cópia local), mostra o andamento, apaga o que ficou
/// pela metade e recusa nomes perigosos que um servidor mal-intencionado poderia mandar (".." e separadores).
/// </summary>
internal static class RemoteTransferEngine
{
    /// <summary>Nome livre na pasta de destino: o próprio nome ou "nome (1).ext", "nome (2).ext"...</summary>
    public static string UniqueName(Func<string, bool> exists, string name)
    {
        if (!exists(name))
        {
            return name;
        }

        var (baseName, extension) = SplitName(name);
        for (var index = 1; ; index++)
        {
            var numbered = $"{baseName} ({index}){extension}";
            if (!exists(numbered))
            {
                return numbered;
            }
        }
    }

    public static (long Bytes, int Files) Scan(IRemoteSession session, string remotePath, Action? heartbeat, CancellationToken cancellationToken)
    {
        var item = session.Stat(remotePath);
        if (item is null)
        {
            return (0, 0);
        }

        if (!item.IsDirectory)
        {
            return item.IsLink ? (0, 0) : (item.Length, 1);
        }

        long bytes = 0;
        var files = 0;
        var pending = new Stack<string>();
        pending.Push(remotePath);
        while (pending.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            heartbeat?.Invoke();
            var directory = pending.Pop();
            foreach (var entry in session.List(directory))
            {
                if (entry.IsLink)
                {
                    continue;
                }

                if (entry.IsDirectory)
                {
                    pending.Push(Combine(directory, entry.Name));
                }
                else
                {
                    bytes += entry.Length;
                    files++;
                }
            }
        }

        return (bytes, files);
    }

    /// <summary>Baixa um arquivo ou pasta para <paramref name="localDirectory"/>; devolve o caminho local criado.</summary>
    public static string Download(
        IRemoteSession session,
        string remotePath,
        string localDirectory,
        TransferProgressTracker tracker,
        CancellationToken cancellationToken)
    {
        var item = session.Stat(remotePath) ?? throw new FileNotFoundException($"O item remoto não existe: {remotePath}");
        var name = SafeName(LastSegment(remotePath));
        var local = Path.Combine(localDirectory, UniqueName(
            candidate => File.Exists(Path.Combine(localDirectory, candidate)) || Directory.Exists(Path.Combine(localDirectory, candidate)),
            name));

        try
        {
            if (item.IsDirectory)
            {
                DownloadDirectory(session, remotePath, local, tracker, cancellationToken);
            }
            else
            {
                DownloadFile(session, remotePath, local, tracker, cancellationToken);
            }
        }
        catch (Exception)
        {
            DeleteLocalQuietly(local);
            throw;
        }

        return local;
    }

    /// <summary>
    /// Envia um arquivo ou pasta para <paramref name="remoteDirectory"/>. <paramref name="targetChosen"/> recebe o
    /// caminho remoto antes de começar, para quem chamou poder limpar se algo falhar.
    /// </summary>
    public static string Upload(
        IRemoteSession session,
        string localPath,
        string remoteDirectory,
        TransferProgressTracker tracker,
        Action<string>? targetChosen,
        CancellationToken cancellationToken)
    {
        var name = Path.GetFileName(localPath.TrimEnd('\\', '/'));
        var remote = Combine(remoteDirectory, UniqueName(candidate => session.Stat(Combine(remoteDirectory, candidate)) is not null, name));
        targetChosen?.Invoke(remote);

        if (Directory.Exists(localPath))
        {
            UploadDirectory(session, localPath, remote, tracker, cancellationToken);
        }
        else if (File.Exists(localPath))
        {
            UploadFile(session, localPath, remote, tracker, cancellationToken);
        }
        else
        {
            throw new FileNotFoundException($"O item de origem não existe: {localPath}");
        }

        return remote;
    }

    /// <summary>Apaga um arquivo ou pasta (com tudo dentro). Nunca apaga a raiz do servidor.</summary>
    public static void Delete(IRemoteSession session, string remotePath, Action? heartbeat, CancellationToken cancellationToken)
    {
        if (IsRoot(remotePath))
        {
            throw new IOException("Não é permitido excluir a raiz do servidor.");
        }

        var item = session.Stat(remotePath);
        if (item is null)
        {
            return;
        }

        cancellationToken.ThrowIfCancellationRequested();
        heartbeat?.Invoke();

        if (!item.IsDirectory || item.IsLink)
        {
            session.DeleteFile(remotePath);
            return;
        }

        foreach (var entry in session.List(remotePath))
        {
            Delete(session, Combine(remotePath, SafeName(entry.Name)), heartbeat, cancellationToken);
        }

        session.DeleteEmptyDirectory(remotePath);
    }

    /// <summary>Novo caminho de um item renomeado na mesma pasta; recusa nomes vazios, com separador ou já existentes.</summary>
    public static string Rename(IRemoteSession session, string remotePath, string newName)
    {
        if (IsRoot(remotePath))
        {
            throw new IOException("Não é possível renomear a raiz do servidor.");
        }

        var name = SafeName(newName.Trim());
        var target = Combine(ParentOf(remotePath), name);
        if (!string.Equals(target, remotePath, StringComparison.Ordinal) && session.Stat(target) is not null)
        {
            throw new IOException($"Já existe um item chamado '{name}' nesta pasta.");
        }

        session.Rename(remotePath, target);
        return target;
    }

    public static string Combine(string parent, string child) =>
        parent.EndsWith('/') ? parent + child : parent + "/" + child;

    public static string ParentOf(string remotePath)
    {
        var trimmed = remotePath.TrimEnd('/');
        var slash = trimmed.LastIndexOf('/');
        return slash <= 0 ? "/" : trimmed[..slash];
    }

    public static string LastSegment(string remotePath)
    {
        var trimmed = remotePath.TrimEnd('/');
        var slash = trimmed.LastIndexOf('/');
        return slash >= 0 ? trimmed[(slash + 1)..] : trimmed;
    }

    /// <summary>
    /// Um nome vindo do servidor (ou digitado) é só um nome: sem separadores, sem ".." e sem caracteres que o
    /// Windows não aceita. Sem isto, um servidor poderia gravar fora da pasta de destino.
    /// </summary>
    public static string SafeName(string name)
    {
        if (string.IsNullOrWhiteSpace(name) ||
            name is "." or ".." ||
            name.Contains('/', StringComparison.Ordinal) ||
            name.Contains('\\', StringComparison.Ordinal) ||
            name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            throw new IOException($"Nome inválido: '{name}'.");
        }

        return name;
    }

    private static bool IsRoot(string remotePath) => remotePath.Trim('/').Length == 0;

    private static void DownloadDirectory(
        IRemoteSession session,
        string remotePath,
        string localPath,
        TransferProgressTracker tracker,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(localPath);
        foreach (var entry in session.List(remotePath))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (entry.IsLink)
            {
                continue;
            }

            var childRemote = Combine(remotePath, SafeName(entry.Name));
            var childLocal = Path.Combine(localPath, entry.Name);
            if (entry.IsDirectory)
            {
                DownloadDirectory(session, childRemote, childLocal, tracker, cancellationToken);
            }
            else
            {
                DownloadFile(session, childRemote, childLocal, tracker, cancellationToken);
            }
        }
    }

    private static void DownloadFile(
        IRemoteSession session,
        string remotePath,
        string localPath,
        TransferProgressTracker tracker,
        CancellationToken cancellationToken)
    {
        tracker.BeginFile(remotePath);

        // Baixa para um temporário: uma falha a meio não deixa um arquivo que parece completo.
        var temp = localPath + ".dora-part-" + Guid.NewGuid().ToString("N")[..8];
        try
        {
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, FileOptions.SequentialScan))
            {
                session.Download(remotePath, stream, tracker.FileProgress, cancellationToken);
            }

            File.Move(temp, localPath);
        }
        finally
        {
            if (File.Exists(temp))
            {
                File.Delete(temp);
            }
        }

        tracker.EndFile(new FileInfo(localPath).Length);
    }

    private static void UploadDirectory(
        IRemoteSession session,
        string localPath,
        string remotePath,
        TransferProgressTracker tracker,
        CancellationToken cancellationToken)
    {
        session.CreateDirectory(remotePath);
        var options = new EnumerationOptions { IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint };
        foreach (var entry in new DirectoryInfo(localPath).EnumerateFileSystemInfos("*", options))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var childRemote = Combine(remotePath, entry.Name);
            if (entry is DirectoryInfo)
            {
                UploadDirectory(session, entry.FullName, childRemote, tracker, cancellationToken);
            }
            else
            {
                UploadFile(session, entry.FullName, childRemote, tracker, cancellationToken);
            }
        }
    }

    private static void UploadFile(
        IRemoteSession session,
        string localPath,
        string remotePath,
        TransferProgressTracker tracker,
        CancellationToken cancellationToken)
    {
        tracker.BeginFile(localPath);
        using var stream = new FileStream(localPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 81920, FileOptions.SequentialScan);
        session.Upload(stream, remotePath, tracker.FileProgress, cancellationToken);
        tracker.EndFile(stream.Length);
    }

    private static (string BaseName, string Extension) SplitName(string name)
    {
        if (name.EndsWith(".tar.gz", StringComparison.OrdinalIgnoreCase))
        {
            return (name[..^".tar.gz".Length], ".tar.gz");
        }

        var extension = Path.GetExtension(name);
        return string.IsNullOrEmpty(extension) || extension.Length == name.Length
            ? (name, string.Empty)
            : (name[..^extension.Length], extension);
    }

    private static void DeleteLocalQuietly(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
            else if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex) when (ExpectedFailures.IsFileSystem(ex))
        {
            // Limpeza de melhor esforço: o erro que importa é o da transferência.
        }
    }
}
