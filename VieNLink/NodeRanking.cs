namespace VieNLink;

/// <summary>
/// Оценка того, насколько трафик узла похож на обычный HTTPS.
/// Чем выше оценка, тем сложнее ТСПУ отличить подключение от визита на популярный сайт.
/// </summary>
internal static class NodeRanking
{
    /// <summary>Чем больше, тем предпочтительнее узел при выборе и попадании в конфиг.</summary>
    public static int Score(ProxyNode node)
    {
        var json = node.OutboundJson;
        var reality = Has(json, "\"reality\":");
        var utls = Has(json, "\"utls\":");
        var vision = Has(json, "xtls-rprx-vision");
        var tls = reality || Has(json, "\"tls\":") || IsHysteria2(node);
        var transport = HasTransport(json);
        var obfs = Has(json, "\"obfs\":");

        var score = 0;
        if (reality)
        {
            score += 100;
        }

        if (utls)
        {
            score += 15;
        }

        if (vision)
        {
            score += 15;
        }

        if (transport)
        {
            score += 10;
        }

        if (IsHysteria2(node))
        {
            score += 30;
        }

        if (obfs)
        {
            score += 15;
        }

        if (tls)
        {
            score += 10;
        }
        else
        {
            score -= 50;
        }

        // Отключённая проверка сертификата — риск перехвата: такой узел выбираем последним.
        if (node.AllowInsecure)
        {
            score -= 40;
        }

        return score;
    }

    /// <summary>Узел маскирует трафик под обычный HTTPS/CDN — его и оставляет фильтр «только маскированные».</summary>
    public static bool IsMasked(ProxyNode node)
    {
        var json = node.OutboundJson;
        if (Has(json, "\"reality\":") || IsHysteria2(node))
        {
            return true;
        }

        return Has(json, "\"tls\":") && (Has(json, "\"utls\":") || HasTransport(json));
    }

    /// <summary>Короткая пометка для таблицы; значок ⚠ — TLS-сертификат не проверяется.</summary>
    public static string Label(ProxyNode node) => LabelCore(node) + (node.AllowInsecure ? " ⚠" : string.Empty);

    private static string LabelCore(ProxyNode node)
    {
        var json = node.OutboundJson;
        var reality = Has(json, "\"reality\":");
        var utls = Has(json, "\"utls\":");
        var vision = Has(json, "xtls-rprx-vision");

        if (reality)
        {
            return vision ? "REALITY · Vision" : "REALITY";
        }

        if (IsHysteria2(node))
        {
            return Has(json, "\"obfs\":") ? "Hysteria2 · obfs" : "Hysteria2";
        }

        if (Has(json, "\"tls\":"))
        {
            if (Has(json, "\"type\":\"ws\""))
            {
                return utls ? "WS · TLS · uTLS" : "WS · TLS";
            }

            if (Has(json, "\"type\":\"grpc\""))
            {
                return utls ? "gRPC · TLS · uTLS" : "gRPC · TLS";
            }

            if (Has(json, "httpupgrade"))
            {
                return "HTTPUpgrade · TLS";
            }

            if (Has(json, "\"type\":\"quic\"") || Has(json, "\"type\":\"http\""))
            {
                return "QUIC/HTTP · TLS";
            }

            return utls ? "TLS · uTLS" : "TLS";
        }

        return node.Protocol.Equals("shadowsocks", StringComparison.OrdinalIgnoreCase)
            ? "Shadowsocks"
            : "без TLS";
    }

    /// <summary>Домен, под который маскируется TLS-рукопожатие (server_name).</summary>
    public static string CamouflageHost(ProxyNode node)
    {
        var match = System.Text.RegularExpressions.Regex.Match(
            node.OutboundJson,
            "\"server_name\"\\s*:\\s*\"([^\"]+)\"");
        return match.Success ? match.Groups[1].Value : string.Empty;
    }

    /// <summary>
    /// Семейство камуфляжа. Позволяет не ставить подряд узлы с одним и тем же
    /// заблокированным шаблоном: умер YouTube-SNI — пробуем Yandex-SNI.
    /// </summary>
    public static string Family(ProxyNode node)
    {
        var host = CamouflageHost(node).ToLowerInvariant();
        if (host.Length == 0)
        {
            return "none";
        }

        if (EndsWithAny(host, "youtube.com", "googlevideo.com", "google.com", "gstatic.com", "googleapis.com", "ytimg.com"))
        {
            return "google";
        }

        if (EndsWithAny(host, "yandex.ru", "yandex.net", "yandex.com", "ya.ru", "yastatic.net"))
        {
            return "yandex";
        }

        if (host.Contains("cloudflare", StringComparison.Ordinal))
        {
            return "cloudflare";
        }

        if (EndsWithAny(host, "vk.com", "vk.ru", "userapi.com"))
        {
            return "vk";
        }

        var parts = host.Split('.', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length >= 2 ? parts[^2] + "." + parts[^1] : host;
    }

    /// <summary>Пометка камуфляжного домена для таблицы.</summary>
    public static string CamouflageLabel(ProxyNode node) =>
        Family(node) switch
        {
            "google" => "YouTube/Google",
            "yandex" => "Yandex",
            "cloudflare" => "Cloudflare",
            "vk" => "VK",
            "none" => "—",
            var family => family,
        };

    private static bool EndsWithAny(string host, params string[] suffixes) =>
        suffixes.Any(suffix => host.Equals(suffix, StringComparison.Ordinal) ||
                               host.EndsWith("." + suffix, StringComparison.Ordinal));

    private static bool IsHysteria2(ProxyNode node) =>
        node.Protocol.Equals("hysteria2", StringComparison.OrdinalIgnoreCase) ||
        node.Protocol.Equals("hy2", StringComparison.OrdinalIgnoreCase);

    private static bool HasTransport(string json) =>
        Has(json, "\"type\":\"ws\"") ||
        Has(json, "\"type\":\"grpc\"") ||
        Has(json, "httpupgrade") ||
        Has(json, "\"type\":\"quic\"") ||
        Has(json, "\"type\":\"http\"");

    private static bool Has(string json, string fragment) =>
        json.Contains(fragment, StringComparison.Ordinal);
}
