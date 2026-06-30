using System.Diagnostics;
using System.Net;
using System.Runtime.InteropServices;

namespace RvcRealtimeGui.Services;

/// <summary>
/// 指定 TCP ポートを LISTEN しているプロセスを iphlpapi.dll 経由で列挙し、強制終了する。
/// </summary>
internal static class PortKiller
{
    const int AF_INET = 2;
    const int TCP_TABLE_OWNER_PID_LISTENER = 3;
    const uint MIB_TCP_STATE_LISTEN = 2;

    [DllImport("iphlpapi.dll", SetLastError = true)]
    static extern uint GetExtendedTcpTable(
        IntPtr pTcpTable, ref int pdwSize, bool bOrder,
        int ulAf, int TableClass, uint Reserved = 0);

    [StructLayout(LayoutKind.Sequential)]
    struct MIB_TCPROW_OWNER_PID
    {
        public uint state;
        public uint localAddr;
        public uint localPort;   // ネットワークバイトオーダー (上位2バイト)
        public uint remoteAddr;
        public uint remotePort;
        public uint owningPid;
    }

    /// <summary>
    /// 指定ポートを LISTEN しているプロセス ID 一覧を返す。
    /// </summary>
    public static List<int> GetListeningPids(int port)
    {
        var pids = new List<int>();
        int size = 0;
        GetExtendedTcpTable(IntPtr.Zero, ref size, false, AF_INET, TCP_TABLE_OWNER_PID_LISTENER);
        IntPtr buf = Marshal.AllocHGlobal(size);
        try
        {
            uint ret = GetExtendedTcpTable(buf, ref size, false, AF_INET, TCP_TABLE_OWNER_PID_LISTENER);
            if (ret != 0) return pids;

            int numEntries = Marshal.ReadInt32(buf);
            IntPtr rowPtr = buf + 4;
            int rowSize = Marshal.SizeOf<MIB_TCPROW_OWNER_PID>();
            for (int i = 0; i < numEntries; i++)
            {
                var row = Marshal.PtrToStructure<MIB_TCPROW_OWNER_PID>(rowPtr);
                // localPort はネットワークバイトオーダーで先頭2バイトに格納
                int localPort = IPAddress.NetworkToHostOrder((short)(row.localPort & 0xFFFF)) & 0xFFFF;
                if (localPort == port && row.state == MIB_TCP_STATE_LISTEN)
                    pids.Add((int)row.owningPid);
                rowPtr += rowSize;
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buf);
        }
        return pids;
    }

    /// <summary>
    /// 指定ポートを LISTEN している全プロセスをプロセスツリーごと強制終了する。
    /// kill した PID を返す。
    /// </summary>
    public static List<int> KillListeners(int port)
    {
        var killed = new List<int>();
        int self = System.Environment.ProcessId;
        foreach (int pid in GetListeningPids(port))
        {
            if (pid == 0 || pid == 4 || pid == self) continue; // System / 自プロセスは除外
            try
            {
                using var proc = Process.GetProcessById(pid);
                proc.Kill(entireProcessTree: true);
                proc.WaitForExit(2000);
                killed.Add(pid);
            }
            catch { }
        }
        return killed;
    }
}
