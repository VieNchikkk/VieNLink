using System.Runtime.InteropServices;

namespace VieNLink;

/// <summary>
/// Отправка «фейкового» сегмента на Linux/Android без root. Приём взят из ciadpi (byedpi):
/// <c>vmsplice</c> с <c>SPLICE_F_GIFT</c> дарит страницы буфера ядру, <c>splice</c> кладёт их
/// в очередь сокета. Пока сокет жив, вызывающий код перезаписывает тот же буфер настоящими
/// байтами — повторная доставка (retransmit) уходит уже с ними, а DPI успел увидеть фейк.
/// Это единственный способ сделать fake без raw-сокетов и root.
/// </summary>
internal static class SpliceSender
{
    private const uint SpliceFGift = 0x08;

    [StructLayout(LayoutKind.Sequential)]
    private struct IoVec
    {
        public IntPtr Base;
        public nuint Length;
    }

    [DllImport("libc", SetLastError = true)]
    private static extern int pipe(int[] fds);

    [DllImport("libc", SetLastError = true)]
    private static extern nint vmsplice(int fd, ref IoVec iov, nuint nrSegs, uint flags);

    [DllImport("libc", SetLastError = true)]
    private static extern nint splice(int fdIn, IntPtr offIn, int fdOut, IntPtr offOut, nuint len, uint flags);

    [DllImport("libc", SetLastError = true)]
    private static extern int close(int fd);

    /// <summary>
    /// Кладёт <paramref name="length"/> байт из <paramref name="buffer"/> в очередь сокета
    /// с дарением страниц. Возвращает закреплённый буфер, который нельзя освобождать,
    /// пока сокет не закрыт, или <c>null</c>, если платформа/ядро не поддержали приём.
    /// </summary>
    public static GCHandle? TryQueue(int socketHandle, byte[] buffer, int length, out string error)
    {
        error = string.Empty;
        if ((!OperatingSystem.IsLinux() && !OperatingSystem.IsAndroid()) ||
            length <= 0 || buffer.Length < length || socketHandle < 0)
        {
            error = "не Linux/Android";
            return null;
        }

        var fds = new int[2];
        if (pipe(fds) != 0)
        {
            error = "pipe: " + Marshal.GetLastPInvokeError();
            return null;
        }

        var handle = default(GCHandle);
        var owned = false;
        try
        {
            handle = GCHandle.Alloc(buffer, GCHandleType.Pinned);
            owned = true;

            var iov = new IoVec
            {
                Base = handle.AddrOfPinnedObject(),
                Length = (nuint)length,
            };
            var gifted = vmsplice(fds[1], ref iov, 1, SpliceFGift);
            if (gifted <= 0)
            {
                error = "vmsplice: " + Marshal.GetLastPInvokeError();
                return null;
            }

            var remaining = (nuint)gifted;
            var total = 0;
            var lastError = 0;
            while (remaining > 0)
            {
                var sent = splice(fds[0], IntPtr.Zero, socketHandle, IntPtr.Zero, remaining, 0);
                if (sent <= 0)
                {
                    lastError = Marshal.GetLastPInvokeError();
                    break;
                }

                total += (int)sent;
                remaining -= (nuint)sent;
            }

            if (total != length)
            {
                error = $"splice: {lastError}, {total}/{length}";
                return null;
            }

            owned = false;
            return handle;
        }
        finally
        {
            close(fds[0]);
            close(fds[1]);
            if (owned)
            {
                handle.Free();
            }
        }
    }
}
