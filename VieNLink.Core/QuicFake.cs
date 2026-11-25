namespace VieNLink;

/// <summary>
/// Фейковый QUIC Initial с безобидным SNI (www.google.com) — шаблон из zapret.
/// Отправляется несколько раз перед настоящим Initial с малым TTL: DPI видит обычный
/// google-хост, а сервер фейк отбрасывает (другой connection id). Так же сделано
/// в Windows-движке (<c>DesyncService.TryBuildQuicFake</c>), только там собирается
/// готовый IP-пакет, а здесь — полезная нагрузка UDP-датаграммы.
/// </summary>
internal static class QuicFake
{
    /// <summary>Сколько раз повторять фейк — как <c>--dpi-desync-repeats=11</c> в zapret.</summary>
    public const int Repeats = 11;

    /// <summary>TTL фейка: до DPI доходит, до сервера — нет (стандарт zapret).</summary>
    public const int Ttl = 8;

    /// <summary>Минимальный размер UDP-датаграммы QUIC Initial (RFC 9000, §14.1).</summary>
    public const int MinimumLength = 1200;

    private const string TemplateFileName = "quic_initial_www_google_com.bin";

    private static readonly Lazy<byte[]> TemplateBytes = new(
        LoadTemplate, LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>Полезная нагрузка фейкового Initial (1200 байт). Пусто, если шаблона нет.</summary>
    public static ReadOnlyMemory<byte> Template => TemplateBytes.Value;

    public static bool IsAvailable => TemplateBytes.Value.Length > 0;

    /// <summary>
    /// Похожа ли датаграмма на QUIC Initial: длинный заголовок, ненулевая версия
    /// и размер не меньше 1200 байт (все Initial по RFC дополняются до этой длины).
    /// </summary>
    public static bool IsInitial(ReadOnlySpan<byte> datagram)
    {
        if (datagram.Length < MinimumLength || (datagram[0] & 0x80) == 0)
        {
            return false;
        }

        // Версия 0 — Version Negotiation, это не Initial.
        return !(datagram[1] == 0 && datagram[2] == 0 && datagram[3] == 0 && datagram[4] == 0);
    }

    private static byte[] LoadTemplate()
    {
        try
        {
            var assembly = typeof(QuicFake).Assembly;
            var name = Array.Find(assembly.GetManifestResourceNames(),
                item => item.EndsWith(TemplateFileName, StringComparison.Ordinal));
            if (name is null)
            {
                AppLog.Write("tun-bypass", "Шаблон QUIC-фейка не найден в сборке: " + TemplateFileName);
                return Array.Empty<byte>();
            }

            using var stream = assembly.GetManifestResourceStream(name);
            if (stream is null)
            {
                return Array.Empty<byte>();
            }

            var buffer = new byte[stream.Length];
            var total = 0;
            while (total < buffer.Length)
            {
                var read = stream.Read(buffer, total, buffer.Length - total);
                if (read <= 0)
                {
                    break;
                }

                total += read;
            }

            if (total < MinimumLength || (buffer[0] & 0x80) == 0)
            {
                var first = total > 0 ? $", первый 0x{buffer[0]:X2}" : string.Empty;
                AppLog.Write("tun-bypass", $"Шаблон QUIC-фейка не похож на Initial ({total} байт{first})");
                return Array.Empty<byte>();
            }

            return total == buffer.Length ? buffer : buffer.AsSpan(0, total).ToArray();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
                                              or NotSupportedException)
        {
            AppLog.Write("tun-bypass", "Не удалось прочитать шаблон QUIC-фейка", exception);
            return Array.Empty<byte>();
        }
    }
}
