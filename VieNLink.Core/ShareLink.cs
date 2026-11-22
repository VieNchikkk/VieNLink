using System.Text;
using System.Text.Json;

namespace VieNLink;

/// <summary>
/// Ссылка для «Поделиться»: у узлов из sing-box/Xray JSON поле RawLink хранит
/// outbound-объект, а не URI. Такой JSON нельзя вставить в другой клиент, поэтому
/// собираем обычную ссылку (vless/vmess/trojan/ss/hysteria2) из полей outbound.
/// </summary>
internal static class ShareLink
{
    /// <summary>Готовая ссылка на узел: исходная, если она уже URI, иначе собранная из JSON.</summary>
    public static string Build(ProxyNode node)
    {
        var raw = node.RawLink?.Trim() ?? string.Empty;
        if (LooksLikeUri(raw))
        {
            // Даже у готовой ссылки подпись часто содержит мусор из источника
            // (`\U0001F1EB`, `%20`, название канала): оставляем адрес сервера.
            return CleanFragment(raw, node);
        }

        try
        {
            using var document = JsonDocument.Parse(node.OutboundJson);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return raw;
            }

            var link = BuildFromOutbound(root, node.Server);
            return string.IsNullOrWhiteSpace(link) ? raw : link;
        }
        catch (JsonException)
        {
            return raw;
        }
    }

    /// <summary>Заменяет подпись после # на чистый адрес сервера (без экранированных флагов).</summary>
    private static string CleanFragment(string link, ProxyNode node)
    {
        var hash = link.IndexOf('#');
        if (hash < 0)
        {
            return link;
        }

        // vmess:// — base64 без фрагмента; vless/trojan/ss/hysteria2 — обычный URI.
        var body = link[..hash];
        var name = string.IsNullOrWhiteSpace(node.Server) ? node.Name : node.Server;
        return body + "#" + Uri.EscapeDataString(name.Trim());
    }

    /// <summary>Ссылка уже похожа на URI, а не на JSON-объект.</summary>
    public static bool LooksLikeUri(string value) =>
        value.Contains("://", StringComparison.Ordinal) &&
        !value.TrimStart().StartsWith('{');

    private static string? BuildFromOutbound(JsonElement root, string serverName)
    {
        var type = String(root, "type");
        var server = String(root, "server");
        var port = Int(root, "server_port");
        if (string.IsNullOrWhiteSpace(type) || string.IsNullOrWhiteSpace(server) || port is not > 0)
        {
            return null;
        }

        var tls = Object(root, "tls");
        var transport = Object(root, "transport");
        var query = Query(tls, transport);
        var queryPart = query.Count == 0 ? string.Empty : "?" + Join(query);
        var label = "#" + Uri.EscapeDataString(string.IsNullOrWhiteSpace(serverName) ? server : serverName);
        var authority = $"{server}:{port}";

        switch (type.ToLowerInvariant())
        {
            case "vless":
            {
                var uuid = String(root, "uuid");
                if (string.IsNullOrWhiteSpace(uuid))
                {
                    return null;
                }

                var flow = String(root, "flow");
                if (!string.IsNullOrWhiteSpace(flow))
                {
                    query["flow"] = flow;
                }

                return $"vless://{uuid}@{authority}{queryPart}{label}";
            }

            case "trojan":
            {
                var password = String(root, "password");
                return string.IsNullOrWhiteSpace(password)
                    ? null
                    : $"trojan://{Uri.EscapeDataString(password)}@{authority}{queryPart}{label}";
            }

            case "hysteria2":
            case "hy2":
            {
                var password = String(root, "password");
                if (string.IsNullOrWhiteSpace(password))
                {
                    return null;
                }

                var obfs = Object(root, "obfs");
                var obfsType = String(obfs, "type");
                if (!string.IsNullOrWhiteSpace(obfsType))
                {
                    query["obfs"] = obfsType;
                    var obfsPassword = String(obfs, "password");
                    if (!string.IsNullOrWhiteSpace(obfsPassword))
                    {
                        query["obfs-password"] = obfsPassword;
                    }
                }

                return $"hysteria2://{Uri.EscapeDataString(password)}@{authority}{queryPart}{label}";
            }

            case "shadowsocks":
            {
                var method = String(root, "method");
                var password = String(root, "password");
                if (string.IsNullOrWhiteSpace(method) || string.IsNullOrWhiteSpace(password))
                {
                    return null;
                }

                var credentials = Convert.ToBase64String(Encoding.UTF8.GetBytes(method + ":" + password))
                    .TrimEnd('=');
                return $"ss://{credentials}@{authority}{label}";
            }

            case "vmess":
                return BuildVmess(root, serverName, server, port.Value);
        }

        return null;
    }

    private static string BuildVmess(JsonElement root, string name, string server, int port)
    {
        var uuid = String(root, "uuid");
        if (string.IsNullOrWhiteSpace(uuid))
        {
            return string.Empty;
        }

        var tls = Object(root, "tls");
        var transport = Object(root, "transport");
        var transportType = String(transport, "type") ?? "tcp";
        var vmess = new Dictionary<string, object?>
        {
            ["v"] = "2",
            ["ps"] = string.IsNullOrWhiteSpace(name) ? server : name,
            ["add"] = server,
            ["port"] = port.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["id"] = uuid,
            ["aid"] = (Int(root, "alter_id") ?? 0).ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["scy"] = String(root, "security") ?? "auto",
            ["net"] = transportType,
            ["type"] = "none",
            ["host"] = Header(transport, "Host") ?? String(transport, "host") ?? string.Empty,
            ["path"] = String(transport, "path") ?? string.Empty,
            ["tls"] = tls is { ValueKind: JsonValueKind.Object } &&
                      (Bool(tls, "enabled") ?? false) ? "tls" : string.Empty,
            ["sni"] = String(tls, "server_name") ?? string.Empty,
            ["fp"] = String(Object(tls, "utls"), "fingerprint") ?? string.Empty,
        };

        var json = JsonSerializer.Serialize(vmess);
        return "vmess://" + Convert.ToBase64String(Encoding.UTF8.GetBytes(json));
    }

    private static Dictionary<string, string> Query(JsonElement tls, JsonElement transport)
    {
        var query = new Dictionary<string, string>(StringComparer.Ordinal);
        var transportType = String(transport, "type");
        if (!string.IsNullOrWhiteSpace(transportType) && !transportType.Equals("tcp", StringComparison.OrdinalIgnoreCase))
        {
            query["type"] = transportType;
        }

        if (tls.ValueKind == JsonValueKind.Object && (Bool(tls, "enabled") ?? false))
        {
            var reality = Object(tls, "reality");
            var realityOn = reality is { ValueKind: JsonValueKind.Object } && (Bool(reality, "enabled") ?? false);
            query["security"] = realityOn ? "reality" : "tls";

            Add(query, "sni", String(tls, "server_name"));
            Add(query, "fp", String(Object(tls, "utls"), "fingerprint"));
            Add(query, "alpn", JoinArray(tls, "alpn"));
            if (Bool(tls, "insecure") == true)
            {
                query["insecure"] = "1";
            }

            if (realityOn)
            {
                Add(query, "pbk", String(reality, "public_key"));
                Add(query, "sid", String(reality, "short_id"));
            }
        }

        var path = String(transport, "path");
        Add(query, "path", path);
        Add(query, "host", Header(transport, "Host") ?? String(transport, "host"));
        Add(query, "serviceName", String(transport, "service_name"));
        return query;
    }

    private static string Join(Dictionary<string, string> query) =>
        string.Join("&", query.Select(pair => pair.Key + "=" + Uri.EscapeDataString(pair.Value)));

    private static string? JoinArray(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var array) || array.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

            var values = array.EnumerateArray()
                .Where(item => item.ValueKind == JsonValueKind.String)
                .Select(item => item.GetString() ?? string.Empty)
                .Where(value => value.Length > 0);
        var text = string.Join(",", values);
        return text.Length == 0 ? null : text;
    }

    private static void Add(Dictionary<string, string> query, string key, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            query[key] = value;
        }
    }

    private static JsonElement Object(JsonElement parent, string name) =>
        parent.ValueKind == JsonValueKind.Object &&
        parent.TryGetProperty(name, out var value) &&
        value.ValueKind == JsonValueKind.Object
            ? value
            : default;

    private static string? String(JsonElement parent, string name) =>
        parent.ValueKind == JsonValueKind.Object &&
        parent.TryGetProperty(name, out var value) &&
        value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static int? Int(JsonElement parent, string name) =>
        parent.ValueKind == JsonValueKind.Object &&
        parent.TryGetProperty(name, out var value) &&
        value.TryGetInt32(out var number)
            ? number
            : null;

    private static bool? Bool(JsonElement parent, string name) =>
        parent.ValueKind == JsonValueKind.Object &&
        parent.TryGetProperty(name, out var value) &&
        value.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? value.GetBoolean()
            : null;

    private static string? Header(JsonElement transport, string name)
    {
        var headers = Object(transport, "headers");
        return String(headers, name) ?? String(headers, name.ToLowerInvariant());
    }
}
