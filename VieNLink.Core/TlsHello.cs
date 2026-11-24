namespace VieNLink;

using System.Text;

/// <summary>
/// Разбор TLS ClientHello и список целевых доменов. Живёт в ядре, потому что нужен
/// обоим движкам: WinDivert-десинку в Windows и SOCKS5-форвардеру в TUN-обходе
/// (в том числе в Android-версии, где WinDivert недоступен).
/// </summary>
internal static class TlsHello
{    /// <summary>
    /// Домены, для которых включается десинк. Остальные идут напрямую.
    /// Взят из целей zapret/десинка (YouTube, Telegram, Discord, X, Instagram, Facebook).
    /// </summary>
    internal static readonly string[] TargetDomains =
    {
        "youtube.com", "youtu.be", "youtube-nocookie.com", "googlevideo.com", "ytimg.com", "ggpht.com",
        "youtubei.googleapis.com", "youtube.googleapis.com", "gstatic.com", "googleusercontent.com",
        "t.me", "telegram.org", "telegram.me",
        "discord.com", "discordapp.com", "discord.gg",
        "x.com", "twitter.com", "twimg.com",
        "instagram.com", "cdninstagram.com", "facebook.com", "fbcdn.net",
    };

    /// <summary>
    /// Проверочные домены для оценки сервера: чем больше из них открывается, тем лучше
    /// сервер. Минимальный набор для скорости: базовые сервисы (Cloudflare/YouTube/
    /// Telegram/Discord) проверяются отдельно в основной партии, здесь — только то, что
    /// реально решает качество: видео-CDN YouTube и Discord. `discord.com` обязателен —
    /// на ПК именно по нему ставится отметка Discord.
    /// </summary>
    internal static readonly string[] VerificationDomains =
    {
        "googlevideo.com", "discord.com", "t.me",
    };

    /// <summary>Десинк включается только для этих доменов (и поддоменов); остальные — напрямую.</summary>
    internal static bool IsTargetHost(string host) =>
        TargetDomains.Any(domain => host.Equals(domain, StringComparison.OrdinalIgnoreCase) ||
                                    host.EndsWith("." + domain, StringComparison.OrdinalIgnoreCase));

    /// <summary>Видео-CDN: для него может понадобиться отдельная стратегия, другой маршрут и TTL.</summary>
    internal static readonly string[] VideoDomains =
    {
        "googlevideo.com", "ytimg.com", "ggpht.com", "youtube-nocookie.com",
    };

    internal static bool IsVideoHost(string host) =>
        VideoDomains.Any(domain => host.Equals(domain, StringComparison.OrdinalIgnoreCase) ||
                                   host.EndsWith("." + domain, StringComparison.OrdinalIgnoreCase));

    internal static bool IsTargetPayload(string payload) =>
        TargetDomains.Any(domain => payload.Contains(domain, StringComparison.OrdinalIgnoreCase));

    private static readonly string[] BenignHosts =
    {
        "www.google.com", "www.google.com.",
        "www.google.ru", "www.google.ru.",
        "google.com", "google.com.",
        "google.ru", "google.ru.",
        "mail.ru", "mail.ru.",
        "vk.com", "vk.com.",
        "ya.ru", "ya.ru.",
        "x.com", "x.com.",
        "a.co", "a.co.",
    };

    /// <summary>Домен той же длины, что и настоящий SNI: DPI проверит его вместо цели.</summary>
    internal static string BenignHost(int length)
    {
        const string seed = "www.google.com";
        if (length <= 0)
        {
            return string.Empty;
        }

        var exact = BenignHosts.FirstOrDefault(host => host.Length == length);
        if (exact is not null)
        {
            return exact;
        }

        if (length <= seed.Length)
        {
            var candidate = seed[..length];
            return candidate.EndsWith('.') ? new string('a', length) : candidate;
        }

        var builder = new StringBuilder(seed);
        while (builder.Length < length)
        {
            if (builder.Length + 2 <= length)
            {
                builder.Append(".a");
            }
            else
            {
                builder.Append('a');
            }
        }

        return builder.ToString(0, length);
    }

    /// <summary>
    /// Находит в полезной нагрузке TCP-сегмента полное имя хоста, оканчивающееся на
    /// целевой домен. Нужно, когда ClientHello не влез в один сегмент и обычный разбор
    /// не сработал: SNI всё равно виден в первых байтах, и его надо разрезать.
    /// </summary>
    internal static bool TryLocateHost(string payload, out int offset, out int length)
    {
        static bool IsHostChar(char value) => char.IsLetterOrDigit(value) || value is '-' or '.' or '_';

        foreach (var domain in TargetDomains)
        {
            var index = payload.IndexOf(domain, StringComparison.OrdinalIgnoreCase);
            if (index < 0)
            {
                continue;
            }

            var end = index + domain.Length;
            while (end < payload.Length && IsHostChar(payload[end]))
            {
                end++;
            }

            // Имя хоста в DNS не длиннее 253 символов; заодно это не даёт
            // «съесть» соседние байты, если перед SNI случайно буква или цифра.
            var start = index;
            var lowerBound = Math.Max(0, end - 253);
            while (start > lowerBound && IsHostChar(payload[start - 1]))
            {
                start--;
            }

            offset = start;
            length = end - start;
            return true;
        }

        offset = 0;
        length = 0;
        return false;
    }

    internal static bool TryParseTlsClientHello(
        ReadOnlySpan<byte> payload,
        out int sniOffset,
        out int sniLength)
        => TryParseTlsClientHello(payload, out sniOffset, out sniLength, out _);

    /// <summary>
    /// Ищет SNI в TLS ClientHello. Смещения считаются от начала TLS-потока,
    /// поэтому метод годится и для пакета из WinDivert, и для обычного TCP-потока.
    /// </summary>
    internal static bool TryParseTlsClientHello(
        ReadOnlySpan<byte> payload,
        out int sniOffset,
        out int sniLength,
        out int extensionsLengthOffset)
    {
        sniOffset = 0;
        sniLength = 0;
        extensionsLengthOffset = 0;
        var payloadLength = payload.Length;
        if (payloadLength < 11 ||
            payload[0] != 0x16 || payload[1] != 0x03 || payload[5] != 0x01)
        {
            return false;
        }

        // ClientHello может не помещаться в один TCP-сегмент (большой key share).
        // Не требуем полную запись: SNI обычно находится в начале и в этом сегменте.
        var p = 9; // 5 байт записи + 4 байта заголовка handshake
        if (p + 2 + 32 > payloadLength)
        {
            return false;
        }

        p += 2 + 32; // версия и random
        if (p >= payloadLength)
        {
            return false;
        }

        var sessionLength = payload[p];
        p += 1 + sessionLength;
        if (p + 2 > payloadLength)
        {
            return false;
        }

        var cipherLength = (payload[p] << 8) | payload[p + 1];
        p += 2 + cipherLength;
        if (p + 1 > payloadLength)
        {
            return false;
        }

        var compressionLength = payload[p];
        p += 1 + compressionLength;
        if (p + 2 > payloadLength)
        {
            return false;
        }

        var extensionsLength = (payload[p] << 8) | payload[p + 1];
        extensionsLengthOffset = p;
        p += 2;
        var extensionsEnd = Math.Min(p + extensionsLength, payloadLength);
        while (p + 4 <= extensionsEnd)
        {
            var type = (payload[p] << 8) | payload[p + 1];
            var length = (payload[p + 2] << 8) | payload[p + 3];
            var data = p + 4;
            if (data + length > payloadLength)
            {
                break;
            }

            if (type == 0 && length >= 5)
            {
                var listLength = (payload[data] << 8) | payload[data + 1];
                var q = data + 2;
                var listEnd = Math.Min(q + listLength, data + length);
                while (q + 3 <= listEnd)
                {
                    var nameType = payload[q];
                    var nameLength = (payload[q + 1] << 8) | payload[q + 2];
                    q += 3;
                    if (nameType == 0 && nameLength > 0 && q + nameLength <= listEnd)
                    {
                        sniOffset = q;
                        sniLength = nameLength;
                        return true;
                    }

                    q += nameLength;
                }
            }

            p = data + length;
        }

        return false;
    }
}
