using System.IO;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace VieNLink;

/// <summary>
/// Сборка конфига sing-box: mixed-порт для браузера, при необходимости TUN-адаптер,
/// selector для переключения сервера и правила маршрутизации по приложениям и доменам.
/// </summary>
internal static class SingBoxConfigBuilder
{
    public const string SelectorTag = "proxy";
    public const string DirectTag = "direct";
    public const string TunTag = "tun-in";

    public static string Build(
        IReadOnlyList<ProxyNode> nodes,
        string? selectedTag,
        int mixedPort,
        int apiPort,
        string apiSecret,
        AppSettings settings,
        bool dnsThroughProxy = false,
        bool explicitDns = false)
    {
        var included = new List<ProxyNode>(nodes.Count);
        var seenTags = new HashSet<string>(StringComparer.Ordinal);
        foreach (var node in nodes)
        {
            if (string.IsNullOrWhiteSpace(node.OutboundJson) || !seenTags.Add(node.Tag))
            {
                continue;
            }

            included.Add(node);
        }

        if (included.Count == 0)
        {
            throw new InvalidOperationException("Нет серверов для запуска прокси");
        }

        var tags = included.Select(n => n.Tag).ToList();
        var defaultTag = !string.IsNullOrWhiteSpace(selectedTag) && tags.Contains(selectedTag) ? selectedTag : tags[0];

        var outbounds = new JsonArray
        {
            new JsonObject
            {
                ["type"] = "selector",
                ["tag"] = SelectorTag,
                ["outbounds"] = ToJsonArray(tags),
                ["default"] = defaultTag,
            },
        };

        foreach (var node in included)
        {
            if (JsonNode.Parse(node.OutboundJson) is not JsonObject outbound)
            {
                continue;
            }

            // Маскировка SNI: серверам Reality/TLS без собственного server_name подставляем домен из настроек,
            // чтобы вручную не прописывать sni= в каждой ссылке.
            if (!string.IsNullOrWhiteSpace(settings.DefaultSni) &&
                outbound["tls"] is JsonObject tls &&
                (tls["server_name"] is null ||
                 (tls["server_name"] is JsonValue value && string.IsNullOrWhiteSpace(value.GetValue<string>()))))
            {
                tls["server_name"] = settings.DefaultSni.Trim();
            }

            ApplyTlsFragment(outbound, settings);
            outbounds.Add(outbound);
        }

        outbounds.Add(new JsonObject { ["type"] = "direct", ["tag"] = DirectTag });

        var tunEnabled = settings.TunnelMode == TunnelMode.Tun;
        var inbounds = new JsonArray
        {
            new JsonObject
            {
                ["type"] = "mixed",
                ["tag"] = "mixed-in",
                ["listen"] = "127.0.0.1",
                ["listen_port"] = mixedPort,
            },
        };

        if (tunEnabled)
        {
            inbounds.Add(BuildTunInbound(settings));
        }

        var (rules, finalTag) = BuildRouteRules(settings, tunEnabled);

        var config = new JsonObject
        {
            ["log"] = new JsonObject
            {
                ["level"] = "warn",
                ["timestamp"] = true,
            },
        };

        // DNS: в TUN он обязателен для запросов из адаптера. В режиме системного прокси
        // тоже задаём явный local-DNS — иначе sing-box не разрешает доменные адреса
        // серверов («dns: lookup failed … empty result») и бракует рабочие серверы.
        if (tunEnabled)
        {
            // dnsThroughProxy: DNS по TCP через сервер. Нужно там, где локальный UDP/53
            // зарезан или подменяется (мобильные сети, Android без root).
            // explicitDns: прямой UDP-DNS без прокси. Нужен там, где адрес самого
            // прокси — домен: DNS через прокси даёт «DNS query loopback» (чтобы
            // подключиться к прокси, нужно разрешить его домен, а резолвер идёт в прокси).
            config["dns"] = explicitDns
                ? new JsonObject
                {
                    ["servers"] = new JsonArray
                    {
                        new JsonObject
                        {
                            ["type"] = "udp",
                            ["tag"] = "udp",
                            ["server"] = "77.88.8.8",
                        },
                        new JsonObject
                        {
                            ["type"] = "udp",
                            ["tag"] = "udp2",
                            ["server"] = "1.1.1.1",
                        },
                    },
                    ["final"] = "udp",
                    ["strategy"] = "ipv4_only",
                }
                : dnsThroughProxy
                ? new JsonObject
                {
                    ["servers"] = new JsonArray
                    {
                        new JsonObject
                        {
                            ["type"] = "tcp",
                            ["tag"] = "remote",
                            ["server"] = "1.1.1.1",
                            ["detour"] = SelectorTag,
                        },
                    },
                    ["final"] = "remote",
                    // Только IPv4: IPv6-маршруты внутри TUN на Android часто недоступны
                    // (`::/0 unreachable`), и клиенты, получив AAAA, висят в рукопожатии.
                    ["strategy"] = "ipv4_only",
                }
                : new JsonObject
                {
                    ["servers"] = new JsonArray
                    {
                        new JsonObject
                        {
                            ["type"] = "local",
                            ["tag"] = "local",
                        },
                    },
                    ["final"] = "local",
                    ["strategy"] = "ipv4_only",
                };
        }
        else if (dnsThroughProxy)
        {
            // Пробное ядро без TUN, но DNS — через сам сервер (как при подключении
            // кнопкой «Подключение»). Локальный UDP/53 на мобильных сетях режется,
            // а 1.1.1.1 у части операторов заблокирован; TCP-DNS через сервер работает.
            config["dns"] = new JsonObject
            {
                ["servers"] = new JsonArray
                {
                    new JsonObject
                    {
                        ["type"] = "tcp",
                        ["tag"] = "remote",
                        ["server"] = "1.1.1.1",
                        ["detour"] = SelectorTag,
                    },
                },
                ["final"] = "remote",
                ["strategy"] = "ipv4_only",
            };
        }
        else if (explicitDns)
        {
            // Пробные/тестовые ядра без TUN: sing-box без явного DNS не разрешает
            // доменные адреса серверов («dns: lookup failed … empty result»), и рабочие
            // серверы бракуются. `local`/libbox тоже даёт пустой ответ — берём UDP-DNS
            // (Яндекс; 1.1.1.1 у части российских операторов блокируется) и только IPv4.
            config["dns"] = new JsonObject
            {
                ["servers"] = new JsonArray
                {
                    new JsonObject
                    {
                        ["type"] = "udp",
                        ["tag"] = "udp",
                        ["server"] = "77.88.8.8",
                    },
                },
                ["final"] = "udp",
                ["strategy"] = "ipv4_only",
            };
        }

        config["inbounds"] = inbounds;
        config["outbounds"] = outbounds;

        var route = new JsonObject
        {
            ["rules"] = rules,
            ["final"] = finalTag,
        };
        if (tunEnabled)
        {
            // Без этого исходящие sing-box могут снова уйти в TUN и зациклиться.
            route["auto_detect_interface"] = true;
        }

        config["route"] = route;
        config["experimental"] = new JsonObject
        {
            ["clash_api"] = new JsonObject
            {
                ["external_controller"] = "127.0.0.1:" + apiPort,
                ["secret"] = apiSecret,
            },
        };

        return config.ToJsonString(new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        });
    }

    /// <summary>
    /// Конфиг TUN-обхода без прокси-сервера: TUN забирает трафик, а исходящие TCP-соединения
    /// идут в локальный SOCKS5-форвардер, который режет TLS ClientHello. WinDivert не нужен.
    /// </summary>
    public static string BuildTunBypass(
        int forwarderPort,
        int apiPort,
        string apiSecret,
        bool blockQuic,
        string? processName,
        string? onlyProcess = null,
        string? dnsServer = null)
    {
        var rules = new JsonArray
        {
            new JsonObject { ["action"] = "sniff" },
            new JsonObject { ["protocol"] = "dns", ["action"] = "hijack-dns" },
            new JsonObject
            {
                // Принудительно IPv4: TUN-стек и прямой выход в этой схеме надёжно работают
                // по IPv4, а IPv6-маршруты на многих провайдерах ломают соединение.
                ["action"] = "resolve",
                ["strategy"] = "ipv4_only",
            },
            new JsonObject
            {
                ["ip_is_private"] = true,
                ["action"] = "route",
                ["outbound"] = DirectTag,
            },
        };

        if (!string.IsNullOrWhiteSpace(processName))
        {
            // Сам форвардер и служебные запросы приложения не должны уходить обратно в TUN.
            // На Android этот процесс исключён из VPN на уровне VpnService, правило не нужно.
            rules.Add(new JsonObject
            {
                ["process_name"] = new JsonArray { processName },
                ["action"] = "route",
                ["outbound"] = DirectTag,
            });
        }

        if (blockQuic)
        {
            // QUIC не фрагментируем: блокируем UDP/443, браузер сам перейдёт на TCP.
            rules.Add(new JsonObject
            {
                ["network"] = "udp",
                ["port"] = 443,
                ["action"] = "reject",
            });
        }

        rules.Add(new JsonObject
        {
            ["network"] = "udp",
            ["action"] = "route",
            ["outbound"] = DirectTag,
        });

        // Тестовый режим: через форвардер идёт только указанный процесс,
        // остальной трафик — напрямую. Нужен, чтобы живой тест TUN не рвал
        // соединение самого агента/оболочки.
        var finalTag = "fragment";
        if (onlyProcess is not null)
        {
            rules.Add(new JsonObject
            {
                ["process_name"] = new JsonArray { onlyProcess },
                ["action"] = "route",
                ["outbound"] = "fragment",
            });
            finalTag = DirectTag;
        }

        var config = new JsonObject
        {
            ["log"] = new JsonObject
            {
                ["level"] = "warn",
                ["timestamp"] = true,
            },
            ["dns"] = BuildTunBypassDns(dnsServer),
            ["inbounds"] = new JsonArray
            {
                new JsonObject
                {
                    ["type"] = "tun",
                    ["tag"] = "tun-bypass-in",
                    ["interface_name"] = "VieNLink-Bypass",
                    ["address"] = new JsonArray { "172.19.0.1/30" },
                    ["mtu"] = 1400,
                    ["auto_route"] = true,
                    ["strict_route"] = false,
                    ["dns_mode"] = "hijack",
                    ["stack"] = "gvisor",
                },
            },
            ["outbounds"] = new JsonArray
            {
                new JsonObject
                {
                    ["type"] = "socks",
                    ["tag"] = "fragment",
                    ["server"] = "127.0.0.1",
                    ["server_port"] = forwarderPort,
                },
                new JsonObject { ["type"] = "direct", ["tag"] = DirectTag },
            },
            ["route"] = new JsonObject
            {
                ["rules"] = rules,
                ["final"] = finalTag,
                ["auto_detect_interface"] = true,
            },
            ["experimental"] = new JsonObject
            {
                ["clash_api"] = new JsonObject
                {
                    ["external_controller"] = "127.0.0.1:" + apiPort,
                    ["secret"] = apiSecret,
                },
            },
        };

        return config.ToJsonString(new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        });
    }

    public static void WriteToFile(string path, string json)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        // sing-box не понимает BOM — пишем строго UTF-8 без него.
        File.WriteAllText(path, json, new UTF8Encoding(false));
    }

    /// <summary>
    /// Правила маршрутизации. Порядок важен: сначало снайфинг и DNS, потом локальная сеть,
    /// затем ограничения по приложениям и доменам, и только в конце — общий выход.
    /// </summary>
    internal static (JsonArray Rules, string FinalTag) BuildRouteRules(AppSettings settings, bool tunEnabled)
    {
        var rules = new JsonArray();

        if (tunEnabled)
        {
            // Домены из SNI/HTTP нужны для правил по сайтам: в TUN назначение — это уже IP.
            rules.Add(new JsonObject { ["action"] = "sniff" });
            rules.Add(new JsonObject { ["protocol"] = "dns", ["action"] = "hijack-dns" });
        }

        rules.Add(new JsonObject
        {
            ["ip_is_private"] = true,
            ["action"] = "route",
            ["outbound"] = DirectTag,
        });

        var apps = ParseAppNames(settings.AppList);
        var sites = ParseDomains(settings.SiteList);
        var appMode = settings.AppRouteMode;
        if (appMode == AppRouteMode.All && settings.OnlySelectedApps)
        {
            // Совместимость со старой настройкой «только выбранные приложения».
            appMode = AppRouteMode.OnlyListed;
        }

        var appFilter = appMode == AppRouteMode.OnlyListed && apps.Count > 0;
        if (appMode == AppRouteMode.AllExceptListed && apps.Count > 0)
        {
            // Исключённые приложения всегда идут напрямую, остальные — по правилам сайтов.
            rules.Add(RouteRule(DirectTag, apps, null));
        }

        var finalTag = SelectorTag;

        switch (settings.SiteRouteMode)
        {
            case SiteRouteMode.All:
                if (appFilter)
                {
                    rules.Add(RouteRule(SelectorTag, apps, null));
                    finalTag = DirectTag;
                }

                break;

            case SiteRouteMode.OnlyListed:
                if (sites.Count > 0)
                {
                    rules.Add(RouteRule(SelectorTag, appFilter ? apps : null, sites));
                }

                finalTag = DirectTag;
                break;

            case SiteRouteMode.AllExceptListed:
                if (sites.Count > 0)
                {
                    rules.Add(RouteRule(DirectTag, appFilter ? apps : null, sites));
                }

                if (appFilter)
                {
                    rules.Add(RouteRule(SelectorTag, apps, null));
                    finalTag = DirectTag;
                }
                else
                {
                    finalTag = SelectorTag;
                }

                break;
        }

        return (rules, finalTag);
    }

    /// <summary>
    /// «Быстрый» обход DPI: sing-box сам режет TLS-рукопожатие на части, и ТСПУ
    /// не видит SNI целиком. Работает без WinDivert и без внешних утилит, но только
    /// для соединений через сервер. QUIC/Hysteria2 не трогаем: там другой транспорт.
    /// </summary>
    private static void ApplyTlsFragment(JsonObject outbound, AppSettings settings)
    {
        if (!settings.TlsFragment && !settings.TlsRecordFragment)
        {
            return;
        }

        var type = outbound["type"] is JsonValue typeValue ? typeValue.GetValue<string>() : string.Empty;
        if (type is not ("vless" or "vmess" or "trojan" or "anytls" or "shadowtls"))
        {
            return;
        }

        if (outbound["tls"] is not JsonObject tls)
        {
            return;
        }

        if (tls["enabled"] is JsonValue enabledValue && enabledValue.TryGetValue<bool>(out var enabled) && !enabled)
        {
            return;
        }

        if (settings.TlsFragment)
        {
            tls["fragment"] = true;
        }

        if (settings.TlsRecordFragment)
        {
            tls["record_fragment"] = true;
        }
    }

    private static JsonObject BuildTunBypassDns(string? dnsServer)
    {
        // Windows: локальный резолвер. Android: свой UDP-сервер, потому что системный
        // резолвер там недоступен ядру и запросы из TUN зациклились бы.
        if (string.IsNullOrWhiteSpace(dnsServer))
        {
            return new JsonObject
            {
                ["servers"] = new JsonArray
                {
                    new JsonObject { ["type"] = "local", ["tag"] = "local" },
                },
                ["final"] = "local",
                ["strategy"] = "ipv4_only",
            };
        }

        return new JsonObject
        {
            ["servers"] = new JsonArray
            {
                new JsonObject { ["type"] = "udp", ["tag"] = "remote", ["server"] = dnsServer },
            },
            ["final"] = "remote",
            ["strategy"] = "ipv4_only",
        };
    }

    private static JsonObject BuildTunInbound(AppSettings settings) => new()
    {
        ["type"] = "tun",
        ["tag"] = TunTag,
        ["interface_name"] = "VieNLink",
        ["address"] = new JsonArray { "172.19.0.1/30" },
        ["mtu"] = 1400,
        ["auto_route"] = true,
        ["strict_route"] = settings.TunStrictRoute,
        ["dns_mode"] = "hijack",
        ["stack"] = "gvisor",
    };

    private static JsonObject RouteRule(string outbound, IReadOnlyList<string>? processNames, IReadOnlyList<string>? domains)
    {
        var rule = new JsonObject { ["action"] = "route", ["outbound"] = outbound };
        if (processNames is { Count: > 0 })
        {
            rule["process_name"] = ToJsonArray(processNames);
        }

        if (domains is { Count: > 0 })
        {
            rule["domain_suffix"] = ToJsonArray(domains);
        }

        return rule;
    }

    /// <summary>Имена процессов: обрезаем путь и добавляем .exe, если забыли.</summary>
    internal static List<string> ParseAppNames(string? text)
    {
        var result = new List<string>();
        foreach (var raw in SplitValues(text))
        {
            var value = raw.Replace('/', '\\');
            var index = value.LastIndexOf('\\');
            if (index >= 0)
            {
                value = value[(index + 1)..];
            }

            if (value.Length == 0)
            {
                continue;
            }

            if (!value.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            {
                value += ".exe";
            }

            if (!result.Contains(value, StringComparer.OrdinalIgnoreCase))
            {
                result.Add(value);
            }
        }

        return result;
    }

    /// <summary>Домены: убираем схему и путь, звёздочку и точку на конце; для суффикса нужны обе формы.</summary>
    internal static List<string> ParseDomains(string? text)
    {
        var result = new List<string>();
        foreach (var raw in SplitValues(text))
        {
            var value = raw.Trim().TrimEnd('.');
            if (value.Contains("://", StringComparison.Ordinal) && Uri.TryCreate(value, UriKind.Absolute, out var uri))
            {
                value = uri.Host;
            }

            var slash = value.IndexOf('/');
            if (slash >= 0)
            {
                value = value[..slash];
            }

            value = value.TrimStart('*').TrimStart('.').ToLowerInvariant();
            if (value.Length == 0 || value.Contains(' '))
            {
                continue;
            }

            foreach (var candidate in new[] { value, "." + value })
            {
                if (!result.Contains(candidate, StringComparer.OrdinalIgnoreCase))
                {
                    result.Add(candidate);
                }
            }
        }

        return result;
    }

    private static IEnumerable<string> SplitValues(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            yield break;
        }

        foreach (var part in text.Split(new[] { '\r', '\n', ',', ';' }, StringSplitOptions.RemoveEmptyEntries))
        {
            var value = part.Trim();
            if (value.Length > 0 && !value.StartsWith('#'))
            {
                yield return value;
            }
        }
    }

    private static JsonArray ToJsonArray(IEnumerable<string> values)
    {
        var array = new JsonArray();
        foreach (var value in values)
        {
            array.Add(value);
        }

        return array;
    }
}
