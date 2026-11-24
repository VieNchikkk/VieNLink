using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;

namespace VieNLink;

/// <summary>
/// Опция TCP MD5 Signature (RFC 2385) на исходящем сокете. Фейковый сегмент с этой опцией
/// сервер отбрасывает, а DPI разбирает как обычный TLS — так fake работает независимо от TTL.
/// Linux/Android: <c>setsockopt(TCP_MD5SIG)</c>; на части прошивок опция выключена в ядре,
/// тогда остаётся подбор TTL.
/// </summary>
internal static class TcpMd5
{
    private const int IpProtoTcp = 6;
    private const int TcpMd5Sig = 14;

    [StructLayout(LayoutKind.Sequential)]
    private struct Md5Sig
    {
        // struct __kernel_sockaddr_storage (128 байт): family, port, addr, padding.
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 128)]
        public byte[] Addr;

        public byte Flags;
        public byte PrefixLen;
        public ushort KeyLen;
        public uint Pad;

        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 80)]
        public byte[] Key;
    }

    [DllImport("libc", SetLastError = true)]
    private static extern int setsockopt(int fd, int level, int optName, ref Md5Sig opt, int len);

    /// <summary>Включает/снимает ключ MD5 для текущего соединения. Возвращает false, если ядро не дало.</summary>
    public static bool TrySet(Socket socket, bool enable, out string error)
    {
        error = string.Empty;
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsAndroid())
        {
            error = "не Linux/Android";
            return false;
        }

        if (socket.RemoteEndPoint is not IPEndPoint peer)
        {
            error = "нет адреса";
            return false;
        }

        var address = peer.Address;
        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        if (address.AddressFamily != AddressFamily.InterNetwork)
        {
            error = "не IPv4";
            return false;
        }

        var sig = new Md5Sig
        {
            Addr = new byte[128],
            Key = new byte[80],
            KeyLen = enable ? (ushort)5 : (ushort)0,
        };

        // sockaddr_in: family (2 байта, little-endian), port (network order), IPv4-адрес.
        sig.Addr[0] = 2;
        sig.Addr[2] = (byte)(peer.Port >> 8);
        sig.Addr[3] = (byte)peer.Port;
        address.GetAddressBytes().CopyTo(sig.Addr, 4);

        var result = setsockopt(
            socket.Handle.ToInt32(), IpProtoTcp, TcpMd5Sig, ref sig, Marshal.SizeOf<Md5Sig>());
        if (result != 0)
        {
            error = "setsockopt: " + Marshal.GetLastPInvokeError();
            return false;
        }

        return true;
    }
}
