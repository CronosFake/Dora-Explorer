using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using DoraExplorer.App.Models;

namespace DoraExplorer.App.Services;

/// <summary>
/// Verifica ocupação de portas locais. A parada de sessões Dora nunca termina
/// processos externos que estejam a escutar na mesma porta.
/// </summary>
public static class NetworkPortGuard
{
    /// <summary>Porta ocupada por outro processo, em TCP ou em UDP (use a sobrecarga com o protocolo para um servidor).</summary>
    public static bool IsPortUsedByOtherProcess(int port) => IsUsedByOtherProcess(port, tcp: true, udp: true);

    /// <summary>
    /// Porta ocupada por outro processo <b>no transporte do servidor</b>: FTP, SFTP, SCP, SMB e NFS usam TCP, TFTP usa UDP. Um socket UDP
    /// de qualquer programa na mesma porta não impede um servidor TCP de subir (e vice-versa), então olhar os dois recusava
    /// servidor sem motivo, e era o que fazia testes e o app falharem de vez em quando com "porta em uso".
    /// </summary>
    public static bool IsPortUsedByOtherProcess(int port, NetworkProtocol protocol)
    {
        var udp = protocol == NetworkProtocol.Tftp;
        return IsUsedByOtherProcess(port, tcp: !udp, udp: udp);
    }

    private static bool IsUsedByOtherProcess(int port, bool tcp, bool udp)
    {
        if (port is < 1 or > 65535)
        {
            return false;
        }

        try
        {
            if (TryFindListenerPids(port, tcp, udp, out var pids))
            {
                var current = Environment.ProcessId;
                return pids.Any(pid => pid != current);
            }
        }
        catch (Exception ex)
        {
            AppLogService.Error($"Falha ao inspecionar porta {port}", ex);
        }

        // Fallback: se a porta está em escuta e não conseguimos o PID, tratar como "outro".
        return IsListening(port, tcp, udp);
    }

    public static bool IsPortListening(int port) => IsListening(port, tcp: true, udp: true);

    private static bool IsListening(int port, bool tcp, bool udp)
    {
        try
        {
            var props = IPGlobalProperties.GetIPGlobalProperties();
            return (tcp && props.GetActiveTcpListeners().Any(ep => ep.Port == port))
                   || (udp && props.GetActiveUdpListeners().Any(ep => ep.Port == port));
        }
        catch (Exception ex) when (ex is NetworkInformationException or PlatformNotSupportedException)
        {
            return false;
        }
    }

    /// <summary>
    /// Na inicialização: só tenta libertar listeners do próprio processo Dora.
    /// Nunca termina processos de terceiros.
    /// </summary>
    public static void ForceStopOwnedListeners(IEnumerable<int> ports)
    {
        var current = Environment.ProcessId;
        foreach (var port in ports.Distinct())
        {
            if (!TryFindListenerPids(port, tcp: true, udp: true, out var pids))
            {
                continue;
            }

            foreach (var pid in pids.Where(p => p == current))
            {
                AppLogService.Info(
                    $"Porta {port} ainda associada ao processo Dora (PID {pid}) na inicialização — sessão será limpa sem terminar processos externos.");
            }

            if (pids.Any(p => p != current))
            {
                AppLogService.Info(
                    $"Porta {port} em uso por outro processo — não será terminado na inicialização.");
            }
        }
    }

    private static bool TryFindListenerPids(int port, bool tcp, bool udp, out HashSet<int> pids)
    {
        pids = [];
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        var tcpFound = tcp && TryGetTcpListenerPids(port, pids);
        var udpFound = udp && TryGetUdpListenerPids(port, pids);
        return tcpFound | udpFound;
    }

    private static bool TryGetTcpListenerPids(int port, HashSet<int> pids)
    {
        var result = GetExtendedTcpTable(
            IntPtr.Zero,
            out var bufferSize,
            sort: true,
            af: 2,
            tableClass: TcpTableClass.TcpTableOwnerPidListener,
            reserved: 0);

        if (result != 0 && result != 122)
        {
            return false;
        }

        var buffer = Marshal.AllocHGlobal(bufferSize);
        try
        {
            result = GetExtendedTcpTable(
                buffer,
                out bufferSize,
                sort: true,
                af: 2,
                tableClass: TcpTableClass.TcpTableOwnerPidListener,
                reserved: 0);

            if (result != 0)
            {
                return false;
            }

            var count = Marshal.ReadInt32(buffer);
            var rowPtr = IntPtr.Add(buffer, 4);
            var rowSize = Marshal.SizeOf<MibTcpRowOwnerPid>();
            var found = false;

            for (var i = 0; i < count; i++)
            {
                var row = Marshal.PtrToStructure<MibTcpRowOwnerPid>(rowPtr);
                var localPort = ntohs(row.LocalPort);
                if (localPort == port && row.OwningPid > 0)
                {
                    pids.Add((int)row.OwningPid);
                    found = true;
                }

                rowPtr = IntPtr.Add(rowPtr, rowSize);
            }

            return found;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static bool TryGetUdpListenerPids(int port, HashSet<int> pids)
    {
        var result = GetExtendedUdpTable(
            IntPtr.Zero,
            out var bufferSize,
            sort: true,
            af: 2,
            tableClass: UdpTableClass.UdpTableOwnerPid,
            reserved: 0);

        if (result != 0 && result != 122)
        {
            return false;
        }

        var buffer = Marshal.AllocHGlobal(bufferSize);
        try
        {
            result = GetExtendedUdpTable(
                buffer,
                out bufferSize,
                sort: true,
                af: 2,
                tableClass: UdpTableClass.UdpTableOwnerPid,
                reserved: 0);

            if (result != 0)
            {
                return false;
            }

            var count = Marshal.ReadInt32(buffer);
            var rowPtr = IntPtr.Add(buffer, 4);
            var rowSize = Marshal.SizeOf<MibUdpRowOwnerPid>();
            var found = false;

            for (var i = 0; i < count; i++)
            {
                var row = Marshal.PtrToStructure<MibUdpRowOwnerPid>(rowPtr);
                var localPort = ntohs(row.LocalPort);
                if (localPort == port && row.OwningPid > 0)
                {
                    pids.Add((int)row.OwningPid);
                    found = true;
                }

                rowPtr = IntPtr.Add(rowPtr, rowSize);
            }

            return found;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static int ntohs(uint portInNetworkOrder) =>
        (int)(((portInNetworkOrder & 0xFF) << 8) | ((portInNetworkOrder >> 8) & 0xFF));

    private enum TcpTableClass
    {
        TcpTableOwnerPidListener = 3
    }

    private enum UdpTableClass
    {
        UdpTableOwnerPid = 1
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MibTcpRowOwnerPid
    {
        public uint State;
        public uint LocalAddr;
        public uint LocalPort;
        public uint RemoteAddr;
        public uint RemotePort;
        public uint OwningPid;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MibUdpRowOwnerPid
    {
        public uint LocalAddr;
        public uint LocalPort;
        public uint OwningPid;
    }

    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern uint GetExtendedTcpTable(
        IntPtr tcpTable,
        out int tcpTableLength,
        bool sort,
        int af,
        TcpTableClass tableClass,
        uint reserved);

    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern uint GetExtendedUdpTable(
        IntPtr udpTable,
        out int udpTableLength,
        bool sort,
        int af,
        UdpTableClass tableClass,
        uint reserved);
}
