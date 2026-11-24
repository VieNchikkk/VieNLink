using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace VieNLink;

/// <summary>
/// Разбор ответа подписки в любом распространённом формате: base64-список ссылок,
/// Xray/V2Ray JSON, sing-box JSON и Clash YAML. Внутри всё сводится к ссылкам,
/// которые дальше разбирает LinkParser — так правила разбора остаются в одном месте.
/// </summary>
internal static class SubscriptionParser
{
    private static readonly Regex LinkRegex = new(
        @"(?i)\b(?:vless|vmess|trojan|ss|hysteria2|hy2|tuic)://[^\s""'<>\\]+",
        RegexOptions.Compiled);

    private static readonly string[] AllowedSchemes = { "vless", "vmess", "trojan", "ss", "hysteria2", "hy2", "tuic" };

    public static List<ProxyNode> Parse(string content, string sourceName)
    {
        var text = content.Trim();
        if (text.Length == 0)
        {
            return new List<ProxyNode>();
        }

        // 1. JSON: конфиг Xray/V2Ray или sing-box.
        if (text[0] is '{' or '[' && TryParseJson(text, sourceName, out var jsonNodes))
        {
            return jsonNodes;
        }

        // 2. YAML Clash.
        if (text.Contains("proxies:", StringComparison.Ordinal) && TryParseClash(text, sourceName, out var clashNodes))
        {
            return clashNodes;
        }

        // 3. Обычный список ссылок.
        var links = ExtractLinks(text);

        // 4. Base64-подписка.
        if (links.Count == 0 && Base64Util.TryDecode(text, out var decoded) && decoded.Contains("://", StringComparison.Ordinal))
        {
            links = ExtractLinks(decoded);
            if (links.Count == 0 && decoded.Contains("proxies:", StringComparison.Ordinal) && TryParseClash(decoded, sourceName, out var decodedClash))
            {
                return decodedClash;
            }
        }

        var nodes = new List<ProxyNode>(links.Count);
        foreach (var link in links)
        {
            var node = LinkParser.Parse(link, sourceName);
            if (node is not null)
            {
                nodes.Add(node);
            }
        }

        return nodes;
    }

    public static List<string> ExtractLinks(string text)
    {
        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (Match match in LinkRegex.Matches(text))
        {
            var link = System.Net.WebUtility.HtmlDecode(match.Value).TrimEnd('.', ',', ';', ')', ']', '}', '>', '"', '\'', '|');
            if (link.Length > 12 && seen.Add(link))
            {
                result.Add(link);
            }
        }

        return result;
    }

    /// <summary>Из заголовка subscription-userinfo достаём объём трафика и дату окончания.</summary>
    public static string? ParseUserInfo(IEnumerable<string>? headerValues)
    {
        if (headerValues is null)
        {
            return null;
        }

        var raw = string.Join(";", headerValues);
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        long? upload = null, download = null, total = null, expire = null;
        foreach (var part in raw.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var pair = part.Split('=', 2);
            if (pair.Length != 2 || !long.TryParse(pair[1].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
            {
                continue;
            }

            switch (pair[0].Trim().ToLowerInvariant())
            {
                case "upload":
                    upload = value;
                    break;
                case "download":
                    download = value;
                    break;
                case "total":
                    total = value;
                    break;
                case "expire":
                    expire = value;
                    break;
            }
        }

        if (total is null or <= 0)
        {
            return null;
        }

        var used = (upload ?? 0) + (download ?? 0);
        var text = $"{FormatBytes(used)} из {FormatBytes(total.Value)}";
        if (expire is > 0)
        {
            text += " · до " + DateTimeOffset.FromUnixTimeSeconds(expire.Value).ToLocalTime().ToString("dd.MM.yyyy");
        }

        return text;
    }

    private static string FormatBytes(long bytes)
    {
        const double gb = 1024d * 1024 * 1024;
        const double mb = 1024d * 1024;
        return bytes >= gb ? (bytes / gb).ToString("n1", CultureInfo.CurrentCulture) + " ГБ"
            : (bytes / mb).ToString("n0", CultureInfo.CurrentCulture) + " МБ";
    }

    // ---------- JSON ----------

    private static bool TryParseJson(string text, string sourceName, out List<ProxyNode> nodes)
    {
        nodes = new List<ProxyNode>();
        try
        {
            using var document = JsonDocument.Parse(text);
            var elements = new List<JsonElement>();
            if (document.RootElement.ValueKind == JsonValueKind.Array)
            {
                elements.AddRange(document.RootElement.EnumerateArray());
            }
            else if (document.RootElement.ValueKind == JsonValueKind.Object)
            {
                if (document.RootElement.TryGetProperty("outbounds", out var outbounds) && outbounds.ValueKind == JsonValueKind.Array)
                {
                    elements.AddRange(outbounds.EnumerateArray());
                }
                else
                {
                    elements.Add(document.RootElement);
                }
            }

            foreach (var element in elements)
            {
                var node = FromJsonOutbound(element, sourceName);
                if (node is not null)
                {
                    nodes.Add(node);
                }
            }
        }
        catch (JsonException)
        {
            return false;
        }

        return nodes.Count > 0;
    }

    private static ProxyNode? FromJsonOutbound(JsonElement element, string sourceName)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        // sing-box: {"type":"vless","server":...,"server_port":...}
        if (element.TryGetProperty("type", out var typeElement) && typeElement.ValueKind == JsonValueKind.String)
        {
            var type = typeElement.GetString() ?? string.Empty;
            if (AllowedSchemes.Contains(type, StringComparer.OrdinalIgnoreCase) &&
                element.TryGetProperty("server", out var server) &&
                element.TryGetProperty("server_port", out var serverPort))
            {
                using var copy = JsonDocument.Parse(element.GetRawText());
                var clone = System.Text.Json.Nodes.JsonNode.Parse(copy.RootElement.GetRawText()) as System.Text.Json.Nodes.JsonObject;
                if (clone is null)
                {
                    return null;
                }

                if (!serverPort.TryGetInt32(out var port))
                {
                    return null;
                }

                var rawServer = server.GetString() ?? string.Empty;
                var serverText = LinkParser.NormalizeHost(rawServer);
                if (port <= 0 || serverText.Contains('/') || serverText.Contains(' ') || serverText.Length == 0)
                {
                    return null;
                }

                // Единый вид адреса: без скобок IPv6, без завершающей точки, punycode.
                clone["server"] = serverText;
                if (clone["tls"] is System.Text.Json.Nodes.JsonObject tls &&
                    tls["server_name"] is System.Text.Json.Nodes.JsonValue serverName &&
                    serverName.TryGetValue<string>(out var nameValue) &&
                    !string.IsNullOrWhiteSpace(nameValue))
                {
                    var normalizedName = LinkParser.NormalizeHost(nameValue);
                    tls["server_name"] = IPAddress.TryParse(normalizedName, out _)
                        ? string.Empty
                        : normalizedName;
                }

                var name = LinkParser.NormalizeName(
                    clone["tag"]?.ToString() ?? serverText + ":" + port);
                // Идентичность не должна зависеть от поля tag (оно пересоздаётся иначе сервер «меняет» Id).
                clone.Remove("tag");
                var identity = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(clone.ToJsonString())))[..20];
                var tag = "n" + identity[..10];
                clone["tag"] = tag;
                var outboundJson = clone.ToJsonString();
                return new ProxyNode
                {
                    RawLink = outboundJson,
                    Protocol = type.ToLowerInvariant(),
                    Name = name,
                    Server = serverText,
                    Port = port,
                    Tag = tag,
                    Identity = identity,
                    SourceName = sourceName,
                    OutboundJson = outboundJson,
                };
            }
        }

        // Xray/V2Ray: {"protocol":"vless","settings":{...},"streamSettings":{...}}
        var link = XrayToLink(element);
        return link is null ? null : LinkParser.Parse(link, sourceName);
    }

    private static string? XrayToLink(JsonElement element)
    {
        if (!element.TryGetProperty("protocol", out var protocolElement) || protocolElement.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        var protocol = (protocolElement.GetString() ?? string.Empty).ToLowerInvariant();
        var name = element.TryGetProperty("tag", out var tagElement) ? tagElement.GetString() ?? string.Empty : string.Empty;
        var stream = element.TryGetProperty("streamSettings", out var streamElement) ? streamElement : default;

        var network = GetJsonString(stream, "network") ?? "tcp";
        var security = GetJsonString(stream, "security") ?? "none";
        var tls = security is "tls" or "xtls";
        var reality = security == "reality";

        var tlsSettings = stream.ValueKind == JsonValueKind.Object && stream.TryGetProperty("tlsSettings", out var ts) ? ts : default;
        var realitySettings = stream.ValueKind == JsonValueKind.Object && stream.TryGetProperty("realitySettings", out var rs) ? rs : default;
        var wsSettings = stream.ValueKind == JsonValueKind.Object && stream.TryGetProperty("wsSettings", out var ws) ? ws : default;
        var grpcSettings = stream.ValueKind == JsonValueKind.Object && stream.TryGetProperty("grpcSettings", out var grpc) ? grpc : default;

        var sni = GetJsonString(realitySettings, "serverName") ?? GetJsonString(tlsSettings, "serverName");
        var fingerprint = GetJsonString(realitySettings, "fingerprint") ?? GetJsonString(tlsSettings, "fingerprint");
        var publicKey = GetJsonString(realitySettings, "publicKey");
        var shortId = GetJsonString(realitySettings, "shortId");
        var path = GetJsonString(wsSettings, "path");
        var host = GetJsonString(wsSettings, "Host");
        var serviceName = GetJsonString(grpcSettings, "serviceName");

        var query = new Dictionary<string, string>();
        query["type"] = network;
        if (tls)
        {
            query["security"] = "tls";
        }

        if (reality)
        {
            query["security"] = "reality";
            if (publicKey is not null)
            {
                query["pbk"] = publicKey;
            }

            if (shortId is not null)
            {
                query["sid"] = shortId;
            }
        }

        if (!string.IsNullOrWhiteSpace(sni))
        {
            query["sni"] = sni;
        }

        if (!string.IsNullOrWhiteSpace(fingerprint))
        {
            query["fp"] = fingerprint;
        }

        if (!string.IsNullOrWhiteSpace(path))
        {
            query["path"] = path;
        }

        if (!string.IsNullOrWhiteSpace(host))
        {
            query["host"] = host;
        }

        if (!string.IsNullOrWhiteSpace(serviceName))
        {
            query["serviceName"] = serviceName;
        }

        var queryString = string.Join("&", query.Select(pair => pair.Key + "=" + Uri.EscapeDataString(pair.Value)));
        var label = "#" + Uri.EscapeDataString(string.IsNullOrWhiteSpace(name) ? protocol : name);

        switch (protocol)
        {
            case "vless":
            case "vmess":
            {
                if (!element.TryGetProperty("settings", out var settings) ||
                    !settings.TryGetProperty("vnext", out var vnext) ||
                    vnext.ValueKind != JsonValueKind.Array)
                {
                    return null;
                }

                var server = vnext.EnumerateArray().FirstOrDefault();
                if (server.ValueKind != JsonValueKind.Object ||
                    !server.TryGetProperty("users", out var users) ||
                    users.ValueKind != JsonValueKind.Array)
                {
                    return null;
                }

                var user = users.EnumerateArray().FirstOrDefault();
                if (user.ValueKind != JsonValueKind.Object)
                {
                    return null;
                }

                var address = GetJsonString(server, "address");
                var port = GetJsonInt(server, "port");
                var id = GetJsonString(user, "id");
                if (address is null || port <= 0 || id is null)
                {
                    return null;
                }

                var flow = GetJsonString(user, "flow");
                if (!string.IsNullOrWhiteSpace(flow))
                {
                    queryString += "&flow=" + Uri.EscapeDataString(flow);
                }

                if (protocol == "vless")
                {
                    return $"vless://{id}@{address}:{port}?{queryString}{label}";
                }

                var vmess = new
                {
                    v = "2",
                    ps = name,
                    add = address,
                    port = port.ToString(CultureInfo.InvariantCulture),
                    id,
                    aid = server.TryGetProperty("alterId", out var aidElement) ? aidElement.ToString() : "0",
                    scy = GetJsonString(user, "security") ?? "auto",
                    net = network,
                    type = "none",
                    host = host ?? string.Empty,
                    path = path ?? string.Empty,
                    tls = tls ? "tls" : string.Empty,
                    sni = sni ?? string.Empty,
                };
                var payload = Convert.ToBase64String(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(vmess)));
                return "vmess://" + payload;
            }

            case "trojan":
            case "shadowsocks":
            {
                if (!element.TryGetProperty("settings", out var settings) ||
                    !settings.TryGetProperty("servers", out var servers) ||
                    servers.ValueKind != JsonValueKind.Array)
                {
                    return null;
                }

                var server = servers.EnumerateArray().FirstOrDefault();
                if (server.ValueKind != JsonValueKind.Object)
                {
                    return null;
                }

                var address = GetJsonString(server, "address");
                var port = GetJsonInt(server, "port");
                if (address is null || port <= 0)
                {
                    return null;
                }

                if (protocol == "trojan")
                {
                    var password = GetJsonString(server, "password");
                    return password is null ? null : $"trojan://{Uri.EscapeDataString(password)}@{address}:{port}?{queryString}{label}";
                }

                var method = GetJsonString(server, "method");
                var ssPassword = GetJsonString(server, "password");
                if (method is null || ssPassword is null)
                {
                    return null;
                }

                var userInfo = Convert.ToBase64String(Encoding.UTF8.GetBytes(method + ":" + ssPassword))
                    .TrimEnd('=').Replace('+', '-').Replace('/', '_');
                return $"ss://{userInfo}@{address}:{port}{label}";
            }

            case "hysteria2":
            {
                if (!element.TryGetProperty("settings", out var settings))
                {
                    return null;
                }

                var address = GetJsonString(settings, "address");
                var port = GetJsonInt(settings, "port");
                var password = GetJsonString(settings, "password") ?? GetJsonString(settings, "auth");
                if (address is null || port <= 0)
                {
                    return null;
                }

                return $"hysteria2://{Uri.EscapeDataString(password ?? string.Empty)}@{address}:{port}?{queryString}{label}";
            }

            default:
                return null;
        }
    }

    // ---------- Clash YAML ----------

    private static bool TryParseClash(string text, string sourceName, out List<ProxyNode> nodes)
    {
        nodes = new List<ProxyNode>();
        var proxies = ParseClashProxies(text);
        foreach (var proxy in proxies)
        {
            var link = ClashToLink(proxy);
            if (link is null)
            {
                continue;
            }

            var node = LinkParser.Parse(link, sourceName);
            if (node is not null)
            {
                nodes.Add(node);
            }
        }

        return nodes.Count > 0;
    }

    private static string? ClashToLink(Dictionary<string, object?> proxy)
    {
        var type = Str(proxy, "type")?.ToLowerInvariant();
        var server = Str(proxy, "server");
        var port = Int(proxy, "port");
        if (type is null || server is null || port <= 0)
        {
            return null;
        }

        var name = Str(proxy, "name") ?? server;
        var label = "#" + Uri.EscapeDataString(name);
        var network = Str(proxy, "network") ?? "tcp";
        var ws = Map(proxy, "ws-opts");
        var grpc = Map(proxy, "grpc-opts");
        var reality = Map(proxy, "reality-opts");
        var query = new Dictionary<string, string> { ["type"] = network };

        var tlsEnabled = Bool(proxy, "tls") || type == "trojan" || reality is not null;
        if (tlsEnabled)
        {
            query["security"] = reality is not null ? "reality" : "tls";
        }

        var sni = Str(proxy, "servername") ?? Str(proxy, "sni");
        if (sni is not null)
        {
            query["sni"] = sni;
        }

        if (Bool(proxy, "skip-cert-verify"))
        {
            query["allowInsecure"] = "1";
        }

        var fingerprint = Str(proxy, "client-fingerprint");
        if (fingerprint is not null)
        {
            query["fp"] = fingerprint;
        }

        if (reality is not null)
        {
            var publicKey = Str(reality, "public-key");
            if (publicKey is null)
            {
                return null;
            }

            query["pbk"] = publicKey;
            var shortId = Str(reality, "short-id");
            if (shortId is not null)
            {
                query["sid"] = shortId;
            }

            query["fp"] ??= "chrome";
        }

        if (ws is not null)
        {
            var path = Str(ws, "path");
            if (path is not null)
            {
                query["path"] = path;
            }

            var headers = Map(ws, "headers");
            var host = headers is not null ? Str(headers, "Host") ?? Str(headers, "host") : null;
            if (host is not null)
            {
                query["host"] = host;
            }
        }

        if (grpc is not null)
        {
            var serviceName = Str(grpc, "grpc-service-name");
            if (serviceName is not null)
            {
                query["serviceName"] = serviceName;
            }
        }

        var queryString = string.Join("&", query.Select(pair => pair.Key + "=" + Uri.EscapeDataString(pair.Value)));

        switch (type)
        {
            case "vless":
            {
                var uuid = Str(proxy, "uuid");
                if (uuid is null)
                {
                    return null;
                }

                var flow = Str(proxy, "flow");
                if (!string.IsNullOrWhiteSpace(flow))
                {
                    queryString += "&flow=" + Uri.EscapeDataString(flow);
                }

                return $"vless://{uuid}@{server}:{port}?{queryString}{label}";
            }

            case "vmess":
            {
                var uuid = Str(proxy, "uuid");
                if (uuid is null)
                {
                    return null;
                }

                var vmess = new
                {
                    v = "2",
                    ps = name,
                    add = server,
                    port = port.ToString(CultureInfo.InvariantCulture),
                    id = uuid,
                    aid = Int(proxy, "alterId").ToString(CultureInfo.InvariantCulture),
                    scy = Str(proxy, "cipher") ?? "auto",
                    net = network,
                    type = "none",
                    host = query.TryGetValue("host", out var wsHost) ? wsHost : string.Empty,
                    path = query.TryGetValue("path", out var wsPath) ? wsPath : string.Empty,
                    tls = tlsEnabled ? "tls" : string.Empty,
                    sni = sni ?? string.Empty,
                };
                return "vmess://" + Convert.ToBase64String(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(vmess)));
            }

            case "trojan":
            {
                var password = Str(proxy, "password");
                return password is null ? null : $"trojan://{Uri.EscapeDataString(password)}@{server}:{port}?{queryString}{label}";
            }

            case "ss":
            {
                var cipher = Str(proxy, "cipher");
                var password = Str(proxy, "password");
                if (cipher is null || password is null)
                {
                    return null;
                }

                var userInfo = Convert.ToBase64String(Encoding.UTF8.GetBytes(cipher + ":" + password))
                    .TrimEnd('=').Replace('+', '-').Replace('/', '_');
                return $"ss://{userInfo}@{server}:{port}{label}";
            }

            case "hysteria2":
            case "hy2":
            {
                var password = Str(proxy, "password") ?? Str(proxy, "auth");
                if (password is null)
                {
                    return null;
                }

                var hyQuery = new Dictionary<string, string> { ["sni"] = sni ?? server };
                if (Bool(proxy, "skip-cert-verify"))
                {
                    hyQuery["insecure"] = "1";
                }

                var obfs = Str(proxy, "obfs");
                if (!string.IsNullOrWhiteSpace(obfs))
                {
                    hyQuery["obfs"] = obfs;
                    var obfsPassword = Str(proxy, "obfs-password");
                    if (!string.IsNullOrWhiteSpace(obfsPassword))
                    {
                        hyQuery["obfs-password"] = obfsPassword;
                    }
                }

                var hyQueryString = string.Join("&", hyQuery.Select(pair => pair.Key + "=" + Uri.EscapeDataString(pair.Value)));
                return $"hysteria2://{Uri.EscapeDataString(password)}@{server}:{port}?{hyQueryString}{label}";
            }

            default:
                return null;
        }
    }

    /// <summary>Упрощённый разбор блока proxies: в Clash-подписке (вложенные словари).</summary>
    private static List<Dictionary<string, object?>> ParseClashProxies(string text)
    {
        var result = new List<Dictionary<string, object?>>();
        var lines = text.ReplaceLineEndings("\n").Split('\n');
        var start = Array.FindIndex(lines, line => line.TrimStart().StartsWith("proxies:", StringComparison.Ordinal));
        if (start < 0)
        {
            return result;
        }

        var proxiesIndent = CountIndent(lines[start]);
        Dictionary<string, object?>? current = null;
        var stack = new List<(int Indent, Dictionary<string, object?> Map)>();

        for (var index = start + 1; index < lines.Length; index++)
        {
            var rawLine = lines[index];
            var trimmed = rawLine.Trim();
            if (trimmed.Length == 0 || trimmed.StartsWith('#'))
            {
                continue;
            }

            var indent = CountIndent(rawLine);
            if (!trimmed.StartsWith("- ", StringComparison.Ordinal) && indent <= proxiesIndent)
            {
                break;
            }

            if (trimmed == "-" || trimmed.StartsWith("- ", StringComparison.Ordinal))
            {
                current = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
                result.Add(current);
                stack.Clear();
                stack.Add((indent, current));

                var rest = trimmed.Length > 2 ? trimmed[2..].Trim() : string.Empty;
                if (rest.Length > 0)
                {
                    ParseClashEntry(rest, indent + 2, stack);
                }

                continue;
            }

            if (current is null)
            {
                continue;
            }

            // Ключ на том же уровне, что и контейнер, относится к его родителю.
            while (stack.Count > 1 && indent <= stack[^1].Indent)
            {
                stack.RemoveAt(stack.Count - 1);
            }

            ParseClashEntry(trimmed, indent, stack);
        }

        return result;
    }

    private static void ParseClashEntry(string text, int indent, List<(int Indent, Dictionary<string, object?> Map)> stack)
    {
        var colon = text.IndexOf(':');
        if (colon <= 0)
        {
            return;
        }

        var key = Unquote(text[..colon].Trim());
        var value = text[(colon + 1)..].Trim();
        var container = stack.Count > 0 ? stack[^1].Map : null;
        if (container is null)
        {
            return;
        }

        if (value.Length == 0)
        {
            var child = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            container[key] = child;
            stack.Add((indent, child));
            return;
        }

        container[key] = Unquote(value);
    }

    private static int CountIndent(string line)
    {
        var count = 0;
        while (count < line.Length && line[count] == ' ')
        {
            count++;
        }

        return count;
    }

    private static string Unquote(string value)
    {
        var text = value.Trim();
        if (text.Length >= 2 && (text[0] == '"' || text[0] == '\'') && text[^1] == text[0])
        {
            text = text[1..^1];
        }

        var comment = text.IndexOf(" #", StringComparison.Ordinal);
        if (comment > 0)
        {
            text = text[..comment].Trim();
        }

        return text;
    }

    private static string? Str(Dictionary<string, object?> map, string key) =>
        map.TryGetValue(key, out var value) ? value as string : null;

    private static bool Bool(Dictionary<string, object?> map, string key) =>
        Str(map, key) is "true" or "yes" or "on" or "1";

    private static int Int(Dictionary<string, object?> map, string key) =>
        int.TryParse(Str(map, key), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : 0;

    private static Dictionary<string, object?>? Map(Dictionary<string, object?> map, string key) =>
        map.TryGetValue(key, out var value) ? value as Dictionary<string, object?> : null;

    private static string? GetJsonString(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(name, out var value))
        {
            return null;
        }

        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number => value.GetRawText(),
            _ => null,
        };
    }

    private static int GetJsonInt(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(name, out var value))
        {
            return 0;
        }

        return value.ValueKind switch
        {
            JsonValueKind.Number when value.TryGetInt32(out var number) => number,
            JsonValueKind.String when int.TryParse(value.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) => parsed,
            _ => 0,
        };
    }
}
