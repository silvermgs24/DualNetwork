using System.Net;
using System.Runtime.InteropServices;
using DualNetwork.Core.Model;
using DualNetwork.Core.Net;

namespace DualNetwork.Windows.Interop;

/// <summary>
/// Thin wrapper over the IP Helper API (iphlpapi.dll). Rows are read and written with explicit byte
/// offsets so the layout is unambiguous; offsets are documented next to each field (x64 and x86 identical).
/// </summary>
internal static class IpHelper
{
    public const ushort AF_INET = 2;
    public const ushort AF_INET6 = 23;

    public const int NO_ERROR = 0;
    public const int ERROR_ACCESS_DENIED = 5;
    public const int ERROR_INVALID_PARAMETER = 87;
    public const int ERROR_NOT_FOUND = 1168;
    public const int ERROR_OBJECT_ALREADY_EXISTS = 5010;

    // MIB_IPFORWARD_ROW2 (104 bytes)
    private const int RowSize = 104;
    private const int R_InterfaceLuid = 0;       // NET_LUID (8)
    private const int R_InterfaceIndex = 8;      // ULONG
    private const int R_DestPrefix = 12;         // IP_ADDRESS_PREFIX { SOCKADDR_INET (28); UINT8 PrefixLength } = 32
    private const int R_DestPrefixLength = 40;
    private const int R_NextHop = 44;            // SOCKADDR_INET (28)
    private const int R_Metric = 84;             // ULONG
    private const int R_Protocol = 88;           // NL_ROUTE_PROTOCOL
    private const int R_Origin = 100;            // NL_ROUTE_ORIGIN

    // SOCKADDR_INET (28 bytes): family @0, IPv4 address @4 (network byte order), IPv6 address @8.
    private const int SA_Family = 0;
    private const int SA_V4Addr = 4;
    private const int SA_V6Addr = 8;
    private const int SockAddrSize = 28;

    // MIB_IPINTERFACE_ROW (168 bytes)
    private const int IfRowSize = 168;
    private const int IF_InterfaceIndex = 16;
    private const int IF_UseAutomaticMetric = 44;
    private const int IF_Metric = 148;
    private const int IF_Connected = 156;

    private const int MIB_IPPROTO_NETMGMT = 3;

    [DllImport("iphlpapi.dll")] private static extern int GetIpForwardTable2(ushort family, out IntPtr table);
    [DllImport("iphlpapi.dll")] private static extern void FreeMibTable(IntPtr memory);
    [DllImport("iphlpapi.dll")] private static extern void InitializeIpForwardEntry(IntPtr row);
    [DllImport("iphlpapi.dll")] private static extern int CreateIpForwardEntry2(IntPtr row);
    [DllImport("iphlpapi.dll")] private static extern int DeleteIpForwardEntry2(IntPtr row);
    [DllImport("iphlpapi.dll")] private static extern int GetIpInterfaceTable(ushort family, out IntPtr table);
    [DllImport("iphlpapi.dll")]
    private static extern int GetBestRoute2(IntPtr interfaceLuid, uint interfaceIndex, IntPtr sourceAddress,
        IntPtr destinationAddress, uint addressSortOptions, IntPtr bestRoute, IntPtr bestSourceAddress);

    public delegate void MibChangeCallback(IntPtr callerContext, IntPtr row, int notificationType);
    [DllImport("iphlpapi.dll")] public static extern int NotifyRouteChange2(ushort family, MibChangeCallback callback, IntPtr context, [MarshalAs(UnmanagedType.U1)] bool initialNotification, out IntPtr handle);
    [DllImport("iphlpapi.dll")] public static extern int NotifyIpInterfaceChange(ushort family, MibChangeCallback callback, IntPtr context, [MarshalAs(UnmanagedType.U1)] bool initialNotification, out IntPtr handle);
    [DllImport("iphlpapi.dll")] public static extern int NotifyUnicastIpAddressChange(ushort family, MibChangeCallback callback, IntPtr context, [MarshalAs(UnmanagedType.U1)] bool initialNotification, out IntPtr handle);
    [DllImport("iphlpapi.dll")] public static extern int CancelMibChangeNotify2(IntPtr handle);

    // ------------------------------------------------------------------ routes

    public static List<RouteEntry> GetIPv4Routes() =>
        ReadForwardTable(AF_INET, row =>
        {
            var dest = ReadV4(row + R_DestPrefix);
            var len = Marshal.ReadByte(row, R_DestPrefixLength);
            var next = ReadV4(row + R_NextHop);
            return new RouteEntry(new IPv4Prefix(dest, len), IPv4.FromUInt(next),
                Marshal.ReadInt32(row, R_InterfaceIndex), Marshal.ReadInt32(row, R_Metric),
                ProtocolName(Marshal.ReadInt32(row, R_Protocol)));
        });

    /// <summary>Interface indexes that currently have an IPv6 default route (::/0).</summary>
    public static HashSet<int> GetIPv6DefaultRouteInterfaces() =>
        ReadForwardTable(AF_INET6, row => Marshal.ReadByte(row, R_DestPrefixLength) == 0 &&
                                          Marshal.ReadInt16(row, R_DestPrefix + SA_Family) == AF_INET6
                ? Marshal.ReadInt32(row, R_InterfaceIndex) : -1)
            .Where(i => i >= 0).ToHashSet();

    private static List<T> ReadForwardTable<T>(ushort family, Func<IntPtr, T> read)
    {
        var rc = GetIpForwardTable2(family, out var table);
        if (rc != NO_ERROR) throw new Win32Error("GetIpForwardTable2", rc);
        try
        {
            var count = Marshal.ReadInt32(table);
            var list = new List<T>(count);
            // MIB_IPFORWARD_TABLE2: ULONG NumEntries, then rows aligned to 8.
            for (var i = 0; i < count; i++) list.Add(read(table + 8 + i * RowSize));
            return list;
        }
        finally { FreeMibTable(table); }
    }

    private static string ProtocolName(int p) => p switch
    {
        2 => "Local", 3 => "NetMgmt", 4 => "ICMP", 8 => "RIP", 13 => "OSPF", 14 => "BGP",
        1002 => "NT Autostatic", 1003 => "NT Static", 1004 => "NT Static (non-DOD)",
        _ => $"Protocol {p}",
    };

    /// <summary>Create a non-persistent IPv4 route (active store only; cleared by reboot).</summary>
    public static int CreateRoute(IPv4Prefix destination, IPAddress nextHop, int interfaceIndex, int metric)
    {
        var row = Marshal.AllocHGlobal(RowSize);
        try
        {
            InitializeIpForwardEntry(row);
            FillKey(row, destination, nextHop, interfaceIndex);
            Marshal.WriteInt32(row, R_Metric, metric);
            Marshal.WriteInt32(row, R_Protocol, MIB_IPPROTO_NETMGMT);
            return CreateIpForwardEntry2(row);
        }
        finally { Marshal.FreeHGlobal(row); }
    }

    public static int DeleteRoute(IPv4Prefix destination, IPAddress nextHop, int interfaceIndex)
    {
        var row = Marshal.AllocHGlobal(RowSize);
        try
        {
            InitializeIpForwardEntry(row);
            FillKey(row, destination, nextHop, interfaceIndex);
            return DeleteIpForwardEntry2(row);
        }
        finally { Marshal.FreeHGlobal(row); }
    }

    private static void FillKey(IntPtr row, IPv4Prefix destination, IPAddress nextHop, int interfaceIndex)
    {
        Marshal.WriteInt64(row, R_InterfaceLuid, 0); // index is used when the LUID is zero
        Marshal.WriteInt32(row, R_InterfaceIndex, interfaceIndex);
        WriteV4(row + R_DestPrefix, destination.Network);
        Marshal.WriteByte(row, R_DestPrefixLength, (byte)destination.Length);
        WriteV4(row + R_NextHop, IPv4.ToUInt(nextHop));
    }

    /// <summary>Asks Windows which route it would use for <paramref name="destination"/> (ground truth, not a simulation).</summary>
    public static (int InterfaceIndex, IPv4Prefix Prefix, IPAddress NextHop, IPAddress Source)? BestRoute(IPAddress destination, int interfaceIndex = 0)
    {
        var dest = Marshal.AllocHGlobal(SockAddrSize);
        var best = Marshal.AllocHGlobal(RowSize);
        var src = Marshal.AllocHGlobal(SockAddrSize);
        try
        {
            Zero(dest, SockAddrSize);
            WriteV4(dest, IPv4.ToUInt(destination));
            var rc = GetBestRoute2(IntPtr.Zero, (uint)interfaceIndex, IntPtr.Zero, dest, 0, best, src);
            if (rc != NO_ERROR) return null;
            return (Marshal.ReadInt32(best, R_InterfaceIndex),
                new IPv4Prefix(ReadV4(best + R_DestPrefix), Marshal.ReadByte(best, R_DestPrefixLength)),
                IPv4.FromUInt(ReadV4(best + R_NextHop)),
                IPv4.FromUInt(ReadV4(src)));
        }
        finally
        {
            Marshal.FreeHGlobal(dest);
            Marshal.FreeHGlobal(best);
            Marshal.FreeHGlobal(src);
        }
    }

    // ------------------------------------------------------------------ interfaces

    public sealed record IpInterface(int Index, int Metric, bool AutomaticMetric, bool Connected);

    public static Dictionary<int, IpInterface> GetIPv4Interfaces()
    {
        var rc = GetIpInterfaceTable(AF_INET, out var table);
        if (rc != NO_ERROR) throw new Win32Error("GetIpInterfaceTable", rc);
        try
        {
            var count = Marshal.ReadInt32(table);
            var result = new Dictionary<int, IpInterface>(count);
            for (var i = 0; i < count; i++)
            {
                var row = table + 8 + i * IfRowSize;
                var idx = Marshal.ReadInt32(row, IF_InterfaceIndex);
                result[idx] = new IpInterface(idx, Marshal.ReadInt32(row, IF_Metric),
                    Marshal.ReadByte(row, IF_UseAutomaticMetric) != 0, Marshal.ReadByte(row, IF_Connected) != 0);
            }
            return result;
        }
        finally { FreeMibTable(table); }
    }

    // ------------------------------------------------------------------ sockaddr helpers

    private static uint ReadV4(IntPtr sockaddr)
    {
        if ((ushort)Marshal.ReadInt16(sockaddr, SA_Family) != AF_INET) return 0;
        var b = new byte[4];
        Marshal.Copy(sockaddr + SA_V4Addr, b, 0, 4);
        return ((uint)b[0] << 24) | ((uint)b[1] << 16) | ((uint)b[2] << 8) | b[3];
    }

    private static void WriteV4(IntPtr sockaddr, uint address)
    {
        Zero(sockaddr, SockAddrSize);
        Marshal.WriteInt16(sockaddr, SA_Family, (short)AF_INET);
        Marshal.Copy(new[] { (byte)(address >> 24), (byte)(address >> 16), (byte)(address >> 8), (byte)address }, 0, sockaddr + SA_V4Addr, 4);
    }

    private static void Zero(IntPtr p, int size)
    {
        for (var i = 0; i < size; i++) Marshal.WriteByte(p, i, 0);
    }

    // Unused but documents the IPv6 field position for future IPv6 support.
    internal const int SockAddrV6Offset = SA_V6Addr;
    internal const int RouteOriginOffset = R_Origin;
}

public sealed class Win32Error : Exception
{
    public int Code { get; }
    public Win32Error(string api, int code)
        : base($"{api} failed: {new System.ComponentModel.Win32Exception(code).Message} (error {code})") => Code = code;
}
