using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace VieNLink;

/// <summary>
/// Разбор ссылок vless://, vmess://, trojan://, ss:// и hysteria2:// в outbound-объекты sing-box.
/// Ошибки разбора не бросаются наружу: битая ссылка просто пропускается.
/// </summary>
internal static class LinkParser
{
    /// <summary>Методы Shadowsocks, которые поддерживает sing-box; остальное — мусор из битых ссылок.</summary>
    private static readonly HashSet<string> SupportedShadowsocksMethods = new(StringComparer.OrdinalIgnoreCase)
    {
        "none", "plain",
        "aes-128-gcm", "aes-256-gcm",
        "chacha20-ietf-poly1305", "xchacha20-ietf-poly1305",
        "2022-blake3-aes-128-gcm", "2022-blake3-aes-256-gcm", "2022-blake3-chacha20-poly1305",
    };

    private static readonly HashSet<string> SupportedVmessSecurities = new(StringComparer.OrdinalIgnoreCase)
    {
        "auto", "none", "zero", "aes-128-gcm", "chacha20-poly1305",
    };

    public static ProxyNode? Parse(string rawLink, string sourceName)
    {
        var raw = rawLink.Trim();
        var separator = raw.IndexOf("://", StringComparison.Ordinal);
        if (separator <= 0 || raw.Length < 10)
        {
            return null;
        }

        try
        {
            return raw[..separator].ToLowerInvariant() switch
            {
                "vless" => ParseVless(raw, sourceName),
                "vmess" => ParseVmess(raw, sourceName),
                "trojan" => ParseTrojan(raw, sourceName),
                "ss" => ParseShadowsocks(raw, sourceName),
                "hysteria2" or "hy2" => ParseHysteria2(raw, sourceName),
                _ => null,
            };
        }
        catch (Exception exception) when (exception is FormatException or UriFormatException or JsonException or ArgumentException or InvalidOperationException)
        {
            return null;
        }
    }

    private static ProxyNode? ParseVless(string raw, string sourceName)
    {
        if (!Uri.TryCreate(raw, UriKind.Absolute, out var uri) || string.IsNullOrWhiteSpace(uri.Host))
        {
            return null;
        }

        var uuid = SafeUnescape(uri.UserInfo);
        if (string.IsNullOrWhiteSpace(uuid))
        {
            return null;
        }

        var query = ParseQuery(uri.Query);
        var port = uri.Port > 0 ? uri.Port : 443;
        var outbound = new JsonObject
        {
            ["type"] = "vless",
            ["tag"] = string.Empty,
            ["server"] = uri.Host,
            ["server_port"] = port,
            ["uuid"] = uuid,
        };

        var flow = Get(query, "flow");
        if (!string.IsNullOrWhiteSpace(flow))
        {
            // Xray-флаг udp443 в sing-box не поддерживается; сам flow оставляем, суффикс — нет.
            var normalizedFlow = flow.Trim().Replace("-udp443", string.Empty, StringComparison.OrdinalIgnoreCase);
            if (normalizedFlow.Equals("xtls-rprx-vision", StringComparison.OrdinalIgnoreCase))
            {
                outbound["flow"] = "xtls-rprx-vision";
            }
        }

        var security = (Get(query, "security") ?? "none").ToLowerInvariant();
        if (security is "tls" or "reality" or "xtls")
        {
            if (!ApplyTls(outbound, query, uri.Host, security))
            {
                return null;
            }
        }

        ApplyTransport(outbound, query);
        return Finish(outbound, "vless", uri.Host, port, NameFromFragment(uri, uri.Host, port), raw, sourceName);
    }

    private static ProxyNode? ParseTrojan(string raw, string sourceName)
    {
        if (!Uri.TryCreate(raw, UriKind.Absolute, out var uri) || string.IsNullOrWhiteSpace(uri.Host))
        {
            return null;
        }

        var password = SafeUnescape(uri.UserInfo);
        if (string.IsNullOrWhiteSpace(password))
        {
            return null;
        }

        var query = ParseQuery(uri.Query);
        var port = uri.Port > 0 ? uri.Port : 443;
        var outbound = new JsonObject
        {
            ["type"] = "trojan",
            ["tag"] = string.Empty,
            ["server"] = uri.Host,
            ["server_port"] = port,
            ["password"] = password,
        };

        var security = (Get(query, "security") ?? "tls").ToLowerInvariant();
        if (security is not "none" && !ApplyTls(outbound, query, uri.Host, "tls"))
        {
            return null;
        }

        ApplyTransport(outbound, query);
        return Finish(outbound, "trojan", uri.Host, port, NameFromFragment(uri, uri.Host, port), raw, sourceName);
    }

    private static ProxyNode? ParseVmess(string raw, string sourceName)
    {
        var payload = raw[(raw.IndexOf("://", StringComparison.Ordinal) + 3)..];
        var hashIndex = payload.IndexOf('#');
        if (hashIndex >= 0)
        {
            payload = payload[..hashIndex];
        }

        if (!Base64Util.TryDecode(payload, out var json))
        {
            return null;
        }

        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        var server = Str(root, "add") ?? Str(root, "address");
        var uuid = Str(root, "id");
        var port = Int(root, "port");
        if (string.IsNullOrWhiteSpace(server) || string.IsNullOrWhiteSpace(uuid) || port <= 0)
        {
            return null;
        }

        var vmessSecurity = (Str(root, "scy") ?? "auto").ToLowerInvariant();
        if (!SupportedVmessSecurities.Contains(vmessSecurity))
        {
            // Битый base64 часто даёт нечитаемый scy — такой узел всё равно не запустится.
            return null;
        }

        var network = (Str(root, "net") ?? "tcp").ToLowerInvariant();
        var host = Str(root, "host");
        var path = Str(root, "path");
        var outbound = new JsonObject
        {
            ["type"] = "vmess",
            ["tag"] = string.Empty,
            ["server"] = server,
            ["server_port"] = port,
            ["uuid"] = uuid,
            ["security"] = vmessSecurity,
            ["alter_id"] = Int(root, "aid"),
        };

        if (!string.IsNullOrWhiteSpace(Str(root, "tls")) && Str(root, "tls") != "none")
        {
            var tls = new JsonObject { ["enabled"] = true };
            var sni = Str(root, "sni");
            tls["server_name"] = string.IsNullOrWhiteSpace(sni) ? host ?? server : sni;
            var alpn = Str(root, "alpn");
            if (!string.IsNullOrWhiteSpace(alpn))
            {
                tls["alpn"] = ToArray(alpn);
            }

            var fingerprint = Str(root, "fp");
            if (!string.IsNullOrWhiteSpace(fingerprint))
            {
                tls["utls"] = new JsonObject { ["enabled"] = true, ["fingerprint"] = NormalizeFingerprint(fingerprint) };
            }

            outbound["tls"] = tls;
        }

        ApplyTransport(outbound, network, path, host, null);
        var name = Str(root, "ps");
        if (string.IsNullOrWhiteSpace(name))
        {
            name = server + ":" + port.ToString(CultureInfo.InvariantCulture);
        }

        return Finish(outbound, "vmess", server, port, name.Trim(), raw, sourceName);
    }

    private static ProxyNode? ParseShadowsocks(string raw, string sourceName)
    {
        var payload = raw[(raw.IndexOf("://", StringComparison.Ordinal) + 3)..];
        var name = string.Empty;
        var hashIndex = payload.IndexOf('#');
        if (hashIndex >= 0)
        {
            name = SafeUnescape(payload[(hashIndex + 1)..]);
            payload = payload[..hashIndex];
        }

        var query = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var queryIndex = payload.IndexOf('?');
        if (queryIndex >= 0)
        {
            query = ParseQuery(payload[queryIndex..]);
            payload = payload[..queryIndex];
        }

        string? method = null;
        string? password = null;
        string? host = null;
        var port = 0;

        var atIndex = payload.LastIndexOf('@');
        if (atIndex > 0)
        {
            var userInfo = payload[..atIndex];
            if (!userInfo.Contains(':'))
            {
                if (!Base64Util.TryDecode(userInfo, out var decoded))
                {
                    return null;
                }

                userInfo = decoded;
            }

            SplitHostPort(payload[(atIndex + 1)..], out host, out port);
            var colon = userInfo.IndexOf(':');
            if (colon <= 0)
            {
                return null;
            }

            method = userInfo[..colon].Trim();
            password = userInfo[(colon + 1)..];
        }
        else
        {
            if (!Base64Util.TryDecode(payload, out var decoded))
            {
                return null;
            }

            var at = decoded.LastIndexOf('@');
            if (at <= 0)
            {
                return null;
            }

            var userInfo = decoded[..at];
            SplitHostPort(decoded[(at + 1)..], out host, out port);
            var colon = userInfo.IndexOf(':');
            if (colon <= 0)
            {
                return null;
            }

            method = userInfo[..colon].Trim();
            password = userInfo[(colon + 1)..];
        }

        if (string.IsNullOrWhiteSpace(method) || string.IsNullOrWhiteSpace(host) || port <= 0)
        {
            return null;
        }

        if (!SupportedShadowsocksMethods.Contains(method))
        {
            // Неизвестный метод sing-box не запустит: лучше отбросить узел при разборе,
            // чем гонять его через 80 проверок конфига.
            return null;
        }

        var outbound = new JsonObject
        {
            ["type"] = "shadowsocks",
            ["tag"] = string.Empty,
            ["server"] = host,
            ["server_port"] = port,
            ["method"] = method,
            ["password"] = password ?? string.Empty,
        };

        var plugin = Get(query, "plugin");
        if (!string.IsNullOrWhiteSpace(plugin))
        {
            // Поддерживаем только встроенные в sing-box плагины, иначе конфиг станет невалидным.
            var pluginName = plugin.Split(';')[0].Trim();
            if (pluginName is "obfs-local" or "v2ray-plugin" or "shadow-tls")
            {
                outbound["plugin"] = pluginName;
                var options = plugin[(pluginName.Length)..].TrimStart(';');
                if (!string.IsNullOrWhiteSpace(options))
                {
                    outbound["plugin_opts"] = options;
                }
            }
            else
            {
                return null;
            }
        }

        if (string.IsNullOrWhiteSpace(name))
        {
            name = host + ":" + port.ToString(CultureInfo.InvariantCulture);
        }

        return Finish(outbound, "shadowsocks", host, port, name.Trim(), raw, sourceName);
    }

    private static ProxyNode? ParseHysteria2(string raw, string sourceName)
    {
        if (!Uri.TryCreate(raw, UriKind.Absolute, out var uri) || string.IsNullOrWhiteSpace(uri.Host))
        {
            return null;
        }

        var password = SafeUnescape(uri.UserInfo);
        var query = ParseQuery(uri.Query);
        var port = uri.Port > 0 ? uri.Port : 443;
        var outbound = new JsonObject
        {
            ["type"] = "hysteria2",
            ["tag"] = string.Empty,
            ["server"] = uri.Host,
            ["server_port"] = port,
        };

        if (!string.IsNullOrEmpty(password))
        {
            outbound["password"] = password;
        }

        if (!ApplyTls(outbound, query, uri.Host, "tls"))
        {
            return null;
        }

        var obfs = Get(query, "obfs");
        if (!string.IsNullOrWhiteSpace(obfs) && !obfs.Equals("none", StringComparison.OrdinalIgnoreCase))
        {
            var obfsPassword = Get(query, "obfs-password") ?? Get(query, "obfs_password") ?? string.Empty;
            outbound["obfs"] = new JsonObject { ["type"] = obfs, ["password"] = obfsPassword };
        }

        return Finish(outbound, "hysteria2", uri.Host, port, NameFromFragment(uri, uri.Host, port), raw, sourceName);
    }

    /// <summary>Reality public_key — 32 байта в base64url; иначе sing-box падает на «illegal base64».</summary>
    private static bool IsValidRealityPublicKey(string value)
    {
        var normalized = value.Replace('-', '+').Replace('_', '/');
        switch (normalized.Length % 4)
        {
            case 2:
                normalized += "==";
                break;
            case 3:
                normalized += "=";
                break;
            case 1:
                return false;
        }

        try
        {
            return Convert.FromBase64String(normalized).Length == 32;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    /// <summary>Reality short_id — hex четной длины до 16 символов; иначе sing-box падает на «odd length hex».</summary>
    private static bool IsValidShortId(string value) =>
        value.Length is > 0 and <= 16 &&
        value.Length % 2 == 0 &&
        value.All(Uri.IsHexDigit);

    private static bool ApplyTls(JsonObject outbound, Dictionary<string, string> query, string host, string security)
    {
        var tls = new JsonObject { ["enabled"] = true };
        var sni = Get(query, "sni") ?? Get(query, "peer");
        var isReality = security.Equals("reality", StringComparison.OrdinalIgnoreCase);

        // Для Reality server_name — это камуфляж (SNI в белый список); без него оставляем пустым,
        // его подставит настройка «SNI-маскировка». Для обычного TLS имя сервера — сам хост.
        tls["server_name"] = !string.IsNullOrWhiteSpace(sni) ? sni : isReality ? string.Empty : host;

        var insecure = Get(query, "allowInsecure") ?? Get(query, "insecure");
        if (insecure is "1" or "true")
        {
            tls["insecure"] = true;
        }

        var alpn = Get(query, "alpn");
        if (!string.IsNullOrWhiteSpace(alpn))
        {
            tls["alpn"] = ToArray(alpn);
        }

        var fingerprint = Get(query, "fp");
        if (security.Equals("reality", StringComparison.OrdinalIgnoreCase))
        {
            var publicKey = Get(query, "pbk");
            if (string.IsNullOrWhiteSpace(publicKey) || !IsValidRealityPublicKey(publicKey))
            {
                return false;
            }

            var reality = new JsonObject
            {
                ["enabled"] = true,
                ["public_key"] = publicKey,
            };
            var shortId = Get(query, "sid");
            if (!string.IsNullOrWhiteSpace(shortId))
            {
                if (!IsValidShortId(shortId))
                {
                    return false;
                }

                reality["short_id"] = shortId;
            }

            tls["reality"] = reality;
            fingerprint ??= "chrome";
        }

        if (!string.IsNullOrWhiteSpace(fingerprint))
        {
            tls["utls"] = new JsonObject { ["enabled"] = true, ["fingerprint"] = NormalizeFingerprint(fingerprint) };
        }

        outbound["tls"] = tls;
        return true;
    }

    /// <summary>
    /// Отпечатки uTLS, которые понимает sing-box. В бесплатных подписках встречаются
    /// чужие значения («unsafe», «randomized» и т.п.): sing-box падает на всём конфиге,
    /// поэтому неизвестный отпечаток подменяем на chrome.
    /// </summary>
    private static readonly HashSet<string> KnownFingerprints = new(StringComparer.OrdinalIgnoreCase)
    {
        "chrome", "firefox", "edge", "safari", "360", "qq", "ios", "android", "random", "randomized",
    };

    private static string NormalizeFingerprint(string? value)
    {
        var fingerprint = value?.Trim();
        return !string.IsNullOrEmpty(fingerprint) && KnownFingerprints.Contains(fingerprint)
            ? fingerprint.ToLowerInvariant()
            : "chrome";
    }

    private static void ApplyTransport(JsonObject outbound, Dictionary<string, string> query)
    {
        ApplyTransport(
            outbound,
            (Get(query, "type") ?? "tcp").ToLowerInvariant(),
            Get(query, "path"),
            Get(query, "host") ?? Get(query, "peer"),
            Get(query, "serviceName"));
    }

    private static void ApplyTransport(JsonObject outbound, string type, string? path, string? host, string? serviceName)
    {
        switch (type)
        {
            case "ws":
            {
                var ws = new JsonObject { ["type"] = "ws" };
                if (!string.IsNullOrWhiteSpace(path))
                {
                    ws["path"] = path;
                }

                if (!string.IsNullOrWhiteSpace(host))
                {
                    ws["headers"] = new JsonObject { ["Host"] = host };
                }

                outbound["transport"] = ws;
                break;
            }

            case "grpc":
            {
                var grpc = new JsonObject
                {
                    ["type"] = "grpc",
                    ["service_name"] = !string.IsNullOrWhiteSpace(serviceName) ? serviceName : path ?? string.Empty,
                };
                outbound["transport"] = grpc;
                break;
            }

            case "http":
            case "h2":
            {
                var http = new JsonObject { ["type"] = "http" };
                if (!string.IsNullOrWhiteSpace(host))
                {
                    http["host"] = ToArray(host);
                }

                if (!string.IsNullOrWhiteSpace(path))
                {
                    http["path"] = path;
                }

                outbound["transport"] = http;
                break;
            }

            case "httpupgrade":
            {
                var upgrade = new JsonObject { ["type"] = "httpupgrade" };
                if (!string.IsNullOrWhiteSpace(host))
                {
                    upgrade["host"] = host;
                }

                if (!string.IsNullOrWhiteSpace(path))
                {
                    upgrade["path"] = path;
                }

                outbound["transport"] = upgrade;
                break;
            }

            case "quic":
                outbound["transport"] = new JsonObject { ["type"] = "quic" };
                break;
        }
    }

    private static ProxyNode? Finish(JsonObject outbound, string protocol, string server, int port, string name, string raw, string sourceName)
    {
        // В списках попадается мусор вида "https://github.com/..." вместо адреса — такие узлы бесполезны.
        if (!IsUsableHost(server) || port is <= 0 or > 65535)
        {
            return null;
        }

        var identitySource = protocol + "|" + server.ToLowerInvariant() + "|" + port.ToString(CultureInfo.InvariantCulture) + "|" + outbound.ToJsonString();
        var identity = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identitySource)))[..20];
        var tag = "n" + identity[..10];
        outbound["tag"] = tag;

        return new ProxyNode
        {
            RawLink = raw,
            Protocol = protocol,
            Name = string.IsNullOrWhiteSpace(name) ? server : name,
            Server = server,
            Port = port,
            Tag = tag,
            Identity = identity,
            SourceName = sourceName,
            OutboundJson = outbound.ToJsonString(),
            AllowInsecure = HasInsecureTls(outbound),
        };
    }

    /// <summary>Узел отключил проверку сертификата — помечаем, чтобы не выбирать его автоматически первым.</summary>
    private static bool HasInsecureTls(JsonObject outbound) =>
        outbound["tls"] is JsonObject tls &&
        tls["insecure"] is JsonValue value &&
        value.TryGetValue<bool>(out var insecure) &&
        insecure;

    private static bool IsUsableHost(string host) =>
        !string.IsNullOrWhiteSpace(host)
        && host.Length <= 253
        && !host.Contains('/')
        && !host.Contains('\\')
        && !host.Contains(' ')
        && !host.Contains('"');

    private static string NameFromFragment(Uri uri, string server, int port)
    {
        var fragment = uri.Fragment.TrimStart('#');
        if (!string.IsNullOrWhiteSpace(fragment))
        {
            var decoded = SafeUnescape(fragment).Trim();
            if (decoded.Length > 0)
            {
                return decoded;
            }
        }

        return server + ":" + port.ToString(CultureInfo.InvariantCulture);
    }

    private static Dictionary<string, string> ParseQuery(string? query)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(query))
        {
            return result;
        }

        foreach (var part in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var index = part.IndexOf('=');
            if (index < 0)
            {
                result[SafeUnescape(part)] = string.Empty;
            }
            else
            {
                result[SafeUnescape(part[..index])] = SafeUnescape(part[(index + 1)..]);
            }
        }

        return result;
    }

    private static void SplitHostPort(string text, out string? host, out int port)
    {
        host = null;
        port = 0;
        text = text.Trim().TrimEnd('/');
        if (text.Length == 0)
        {
            return;
        }

        if (text[0] == '[')
        {
            var close = text.IndexOf(']');
            if (close < 0)
            {
                return;
            }

            host = text[1..close];
            if (text.Length > close + 2 && text[close + 1] == ':')
            {
                _ = int.TryParse(text[(close + 2)..], NumberStyles.Integer, CultureInfo.InvariantCulture, out port);
            }

            return;
        }

        var colon = text.LastIndexOf(':');
        if (colon <= 0)
        {
            return;
        }

        host = text[..colon];
        _ = int.TryParse(text[(colon + 1)..], NumberStyles.Integer, CultureInfo.InvariantCulture, out port);
    }

    private static JsonArray ToArray(string value)
    {
        var array = new JsonArray();
        foreach (var item in value.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            array.Add(item.Trim());
        }

        return array;
    }

    private static string? Get(Dictionary<string, string> query, string key) =>
        query.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value) ? value : null;

    private static string SafeUnescape(string value)
    {
        if (string.IsNullOrEmpty(value) || !value.Contains('%'))
        {
            return value;
        }

        try
        {
            return Uri.UnescapeDataString(value);
        }
        catch (UriFormatException)
        {
            return value;
        }
    }

    private static string? Str(JsonElement root, string name) =>
        root.TryGetProperty(name, out var element)
            ? element.ValueKind switch
            {
                JsonValueKind.String => element.GetString(),
                JsonValueKind.Number => element.GetRawText(),
                JsonValueKind.True => "true",
                JsonValueKind.False => "false",
                _ => null,
            }
            : null;

    private static int Int(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var element))
        {
            return 0;
        }

        return element.ValueKind switch
        {
            JsonValueKind.Number when element.TryGetInt32(out var number) => number,
            JsonValueKind.String when int.TryParse(element.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) => parsed,
            _ => 0,
        };
    }
}
