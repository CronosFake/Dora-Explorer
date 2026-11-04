using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace DoraExplorer.App.Infrastructure;

/// <summary>
/// Que tipo de disco guarda uma pasta: com cabeça de leitura (HD comum, que sofre com muitas leituras ao mesmo tempo) ou sem (SSD, NVMe,
/// que ganham com elas). A procura de duplicados usa isso para escolher quantas leituras fazer em paralelo.
/// </summary>
internal static class StorageKind
{
    private const uint FileShareReadWrite = 0x1 | 0x2;
    private const uint OpenExisting = 3;
    private const uint IoctlStorageQueryProperty = 0x002D1400;
    private const uint StorageDeviceSeekPenaltyProperty = 7;
    private const uint PropertyStandardQuery = 0;

    [StructLayout(LayoutKind.Sequential)]
    private struct StoragePropertyQuery
    {
        public uint PropertyId;
        public uint QueryType;
        public byte AdditionalParameters;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DeviceSeekPenaltyDescriptor
    {
        public uint Version;
        public uint Size;
        [MarshalAs(UnmanagedType.U1)]
        public bool IncursSeekPenalty;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(
        string fileName, uint desiredAccess, uint shareMode, IntPtr securityAttributes, uint creationDisposition, uint flags, IntPtr template);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(
        SafeFileHandle device, uint code, ref StoragePropertyQuery input, int inputSize,
        out DeviceSeekPenaltyDescriptor output, int outputSize, out int returned, IntPtr overlapped);

    /// <summary>
    /// <see langword="true"/> se o disco que guarda o caminho tem cabeça de leitura (HD), <see langword="false"/> se não tem (SSD),
    /// <see langword="null"/> se não dá para saber (caminho de rede, unidade que não responde, falta de acesso). Não precisa ser administrador.
    /// </summary>
    public static bool? HasSeekPenalty(string path)
    {
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(path));
            if (string.IsNullOrEmpty(root) || root.StartsWith(@"\\", StringComparison.Ordinal) || root.Length < 2 || root[1] != ':')
            {
                return null; // caminho de rede (UNC) ou sem letra de unidade
            }

            // Acesso 0: pergunta ao volume sem abrir os dados dele (por isso dispensa administrador).
            using var volume = CreateFile(@"\\.\" + root[..2], 0, FileShareReadWrite, IntPtr.Zero, OpenExisting, 0, IntPtr.Zero);
            if (volume.IsInvalid)
            {
                return null;
            }

            var query = new StoragePropertyQuery { PropertyId = StorageDeviceSeekPenaltyProperty, QueryType = PropertyStandardQuery };
            return DeviceIoControl(
                volume,
                IoctlStorageQueryProperty,
                ref query,
                Marshal.SizeOf<StoragePropertyQuery>(),
                out var descriptor,
                Marshal.SizeOf<DeviceSeekPenaltyDescriptor>(),
                out _,
                IntPtr.Zero)
                ? descriptor.IncursSeekPenalty
                : null;
        }
        catch (Exception ex) when (ExpectedFailures.IsShellInterop(ex) || ex is ArgumentException or NotSupportedException)
        {
            return null;
        }
    }
}
