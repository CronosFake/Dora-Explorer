using System.Data.Common;
using System.Runtime.InteropServices;
using System.Security;
using Microsoft.CSharp.RuntimeBinder;

namespace DoraExplorer.App.Infrastructure;

/// <summary>
/// Famílias de falhas que o código trata de propósito ("melhor esforço"). Usadas em filtros
/// <c>catch (Exception ex) when (...)</c> no lugar de um <c>catch</c> genérico: assim, falhas
/// previstas continuam a ser ignoradas, mas um erro de programação (por exemplo,
/// <see cref="NullReferenceException"/>) deixa de ser engolido em silêncio.
/// </summary>
internal static class ExpectedFailures
{
    /// <summary>Arquivo ou pasta inexistente, sem permissão, em uso, ou caminho/nome inválido.</summary>
    public static bool IsFileSystem(Exception exception) =>
        exception is IOException
            or UnauthorizedAccessException
            or ArgumentException
            or NotSupportedException
            or SecurityException;

    /// <summary>
    /// Unidade protegida pelo BitLocker e ainda bloqueada (ERROR_FVE_LOCKED_VOLUME): é um estado normal do
    /// disco, que a árvore já mostra como "Bloqueada (BitLocker)", e não um erro do programa.
    /// </summary>
    public static bool IsLockedVolume(Exception exception) =>
        exception is IOException { HResult: unchecked((int)0x80310000) };

    /// <summary>
    /// Falhas ao falar com o shell do Windows: COM, P/Invoke, registro, objetos <c>dynamic</c> e ícones.
    /// </summary>
    public static bool IsShellInterop(Exception exception) =>
        IsFileSystem(exception) ||
        exception is ExternalException // COMException, Win32Exception
            or InvalidCastException
            or InvalidOperationException
            or NotImplementedException
            or FormatException
            or TypeLoadException // inclui EntryPointNotFoundException e DllNotFoundException
            or MarshalDirectiveException
            or RuntimeBinderException;

    /// <summary>Falhas de leitura/consulta do índice SQLite de pastas.</summary>
    public static bool IsIndexStore(Exception exception) =>
        IsFileSystem(exception) ||
        exception is DbException
            or InvalidCastException
            or FormatException
            or OverflowException;
}
