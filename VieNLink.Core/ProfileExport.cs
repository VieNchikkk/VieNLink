using System.Text;

namespace VieNLink;

/// <summary>
/// Экспорт выбранного сервера для сторонних клиентов (SFA, Exclave, Karing) и для запуска
/// sing-box на другом устройстве. Нужен потому, что на Android системный прокси недоступен,
/// а полноценный клиент ещё не готов: телефон подключается через готовый клиент.
/// </summary>
internal static class ProfileExport
{
    /// <summary>
    /// Ссылка исходного формата (vless://, ss:// …) — самый совместимый способ:
    /// её понимают все мобильные клиенты, и она несёт ровно те же параметры, что у сервера.
    /// </summary>
    public static string BuildShareLink(ProxyNode node) =>
        string.IsNullOrWhiteSpace(node.RawLink)
            ? node.OutboundJson
            : node.RawLink;

    /// <summary>Несколько ссылок построчно — формат, который принимают списки подписок.</summary>
    public static string BuildLinkList(IEnumerable<ProxyNode> nodes)
    {
        var builder = new StringBuilder();
        foreach (var node in nodes)
        {
            var link = BuildShareLink(node);
            if (string.IsNullOrWhiteSpace(link))
            {
                continue;
            }

            builder.AppendLine(link.Trim());
        }

        return builder.ToString().TrimEnd();
    }

    /// <summary>
    /// Готовый конфиг sing-box для одного сервера.
    /// <paramref name="tunnel"/> = true — с TUN-инбаундом: подходит для `sing-box run`
    /// на телефоне или роутере (нужны права на TUN). false — только mixed-порт для проверки.
    /// </summary>
    public static string BuildConfig(ProxyNode node, bool tunnel)
    {
        var settings = new AppSettings
        {
            TunnelMode = tunnel ? TunnelMode.Tun : TunnelMode.SystemProxy,
            SiteRouteMode = SiteRouteMode.All,
        };

        return SingBoxConfigBuilder.Build(
            new[] { node },
            node.Tag,
            mixedPort: 20800,
            apiPort: 20801,
            apiSecret: "vienlink",
            settings);
    }

    /// <summary>Предлагаемое имя файла: протокол, хост и порт — без символов, запрещённых в Windows.</summary>
    public static string SuggestFileName(ProxyNode node)
    {
        var raw = $"{node.Protocol}-{node.Server}-{node.Port}";
        var invalid = Path.GetInvalidFileNameChars();
        var cleaned = new string(raw.Select(c => invalid.Contains(c) ? '_' : c).ToArray());
        return "vienlink-" + cleaned + ".json";
    }
}
