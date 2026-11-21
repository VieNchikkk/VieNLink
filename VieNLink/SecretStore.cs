using System.Runtime.InteropServices;
using System.Text;

namespace VieNLink;

/// <summary>
/// Шифрование секретов через DPAPI Windows (CryptProtectData).
/// Ссылки на подписки содержат токены доступа: в открытом JSON их видеть не должны
/// ни другие программы, ни резервные копии. Ключ привязан к учётной записи Windows,
/// поэтому файл нельзя просто перенести на другой компьютер — это ожидаемо.
/// </summary>
internal static class SecretStore
{
    private const int CryptProtectUiForbidden = 0x1;

    public static byte[] Protect(string text) => Transform(Encoding.UTF8.GetBytes(text), protect: true);

    public static string? Unprotect(byte[] data)
    {
        try
        {
            return Encoding.UTF8.GetString(Transform(data, protect: false));
        }
        catch (Exception exception) when (exception is InvalidOperationException or ArgumentException)
        {
            AppLog.Write("secrets", "Не удалось расшифровать файл секретов (другая учётная запись?)", exception);
            return null;
        }
    }

    private static byte[] Transform(byte[] data, bool protect)
    {
        var input = default(DataBlob);
        var output = default(DataBlob);
        var handle = GCHandle.Alloc(data, GCHandleType.Pinned);
        try
        {
            input.cbData = data.Length;
            input.pbData = handle.AddrOfPinnedObject();
            var ok = protect
                ? CryptProtectData(ref input, "VieNLink", IntPtr.Zero, IntPtr.Zero, IntPtr.Zero,
                    CryptProtectUiForbidden, out output)
                : CryptUnprotectData(ref input, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero,
                    CryptProtectUiForbidden, out output);
            if (!ok)
            {
                throw new InvalidOperationException("DPAPI вернул ошибку " + Marshal.GetLastWin32Error());
            }

            var result = new byte[output.cbData];
            Marshal.Copy(output.pbData, result, 0, output.cbData);
            return result;
        }
        finally
        {
            if (output.pbData != IntPtr.Zero)
            {
                LocalFree(output.pbData);
            }

            handle.Free();
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DataBlob
    {
        public int cbData;
        public IntPtr pbData;
    }

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CryptProtectData(
        ref DataBlob pDataIn,
        string? szDataDescr,
        IntPtr pOptionalEntropy,
        IntPtr pvReserved,
        IntPtr pPromptStruct,
        int dwFlags,
        out DataBlob pDataOut);

    [DllImport("crypt32.dll", SetLastError = true)]
    private static extern bool CryptUnprotectData(
        ref DataBlob pDataIn,
        IntPtr ppszDataDescr,
        IntPtr pOptionalEntropy,
        IntPtr pvReserved,
        IntPtr pPromptStruct,
        int dwFlags,
        out DataBlob pDataOut);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr hMem);
}

/// <summary>Реализация <see cref="ISecretProtector"/> для Windows: DPAPI текущей учётной записи.</summary>
internal sealed class DpapiSecretProtector : ISecretProtector
{
    public byte[] Protect(string text) => SecretStore.Protect(text);

    public string? Unprotect(byte[] data) => SecretStore.Unprotect(data);
}
