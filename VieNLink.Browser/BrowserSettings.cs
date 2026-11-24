using System.IO;
using System.Text.Json.Serialization;

namespace VieNLink;

/// <summary>Поисковая система встроенного браузера: шаблон адреса с подстановкой запроса.</summary>
internal sealed record BrowserSearchEngine(string Id, string Title, string QueryTemplate)
{
    public string BuildUrl(string query) =>
        QueryTemplate.Replace("{query}", Uri.EscapeDataString(query));
}

/// <summary>Закладка браузера.</summary>
internal sealed class BrowserBookmark
{
    public string Title { get; set; } = string.Empty;

    public string Url { get; set; } = string.Empty;
}

/// <summary>
/// Настройки встроенного браузера. Хранятся в %LOCALAPPDATA%\VieNLink\browser.json.
/// Набор намеренно широкий: часть переключателей потом можно убрать.
/// </summary>
internal sealed class BrowserSettings
{
    /// <summary>Домашняя страница по умолчанию — локальная стартовая страница VieNLink.</summary>
    public const string StartPageHost = "vienlink.start";

    public const string StartPageUrl = "https://" + StartPageHost + "/start.html";

    // Поиск — только Google: других поисковиков в браузере нет.
    public static readonly BrowserSearchEngine[] Engines =
    {
        new("google", "Google", "https://www.google.com/search?q={query}"),
    };

    // --- Запуск ---

    /// <summary>Пусто — домашняя страница это локальная стартовая страница.</summary>
    public string HomePage { get; set; } = string.Empty;

    /// <summary>Открывать при запуске вкладки прошлой сессии.</summary>
    public bool RestoreTabs { get; set; } = true;

    /// <summary>Очищать кэш, cookie и данные сайтов при запуске браузера.</summary>
    public bool ClearDataOnStart { get; set; }

    // --- Вид ---

    /// <summary>Масштаб страницы (1.0 = 100%).</summary>
    public double Zoom { get; set; } = 1.0;

    /// <summary>Панель закладок под адресной строкой.</summary>
    public bool ShowBookmarksBar { get; set; }

    /// <summary>Значки сайтов (favicon) во вкладках.</summary>
    public bool ShowFavicons { get; set; } = true;

    /// <summary>Показывать адрес ссылки в строке состояния при наведении.</summary>
    public bool ShowLinkStatusBar { get; set; }

    // --- Поведение ---

    /// <summary>Закрывать окно вместе с последней вкладкой (иначе открывается новая).</summary>
    public bool CloseWithLastTab { get; set; } = true;

    /// <summary>Вести историю посещений.</summary>
    public bool SaveHistory { get; set; } = true;

    // --- Приватность и безопасность ---

    /// <summary>Открывать страницы только при подключённом VPN (защита от утечки IP).</summary>
    public bool RequireVpn { get; set; }

    /// <summary>Если страница не открылась — предложить подключить VPN и повторить.</summary>
    public bool AutoConnectOnFailure { get; set; } = true;

    /// <summary>Панель VPN закреплена слева (иначе справа).</summary>
    public bool VpnPanelDockedLeft { get; set; }

    /// <summary>Автозаполнение форм и сохранение паролей.</summary>
    public bool EnableAutofill { get; set; }

    /// <summary>Запрещать сайтам камеру, микрофон, геолокацию и чтение буфера обмена.</summary>
    public bool BlockSensitivePermissions { get; set; } = true;

    // --- Дополнительно ---

    /// <summary>JavaScript на страницах.</summary>
    public bool EnableJavaScript { get; set; } = true;

    /// <summary>Инструменты разработчика (F12 и пункт меню).</summary>
    public bool EnableDevTools { get; set; }

    // --- Данные ---

    public string SearchEngine { get; set; } = "google";

    public List<BrowserBookmark> Bookmarks { get; set; } = new();

    /// <summary>Адреса вкладок последней сессии (пишутся при закрытии окна).</summary>
    public List<string> Tabs { get; set; } = new();

    [JsonIgnore]
    public BrowserSearchEngine Engine =>
        Engines.FirstOrDefault(engine => engine.Id.Equals(SearchEngine, StringComparison.OrdinalIgnoreCase))
        ?? Engines[0];

    /// <summary>Куда идти по кнопке «Домой»: своя стартовая страница или адрес из настроек.</summary>
    [JsonIgnore]
    public string HomeUrl => string.IsNullOrWhiteSpace(HomePage) ? StartPageUrl : HomePage.Trim();

    public string BuildSearchUrl(string query) => Engine.BuildUrl(query);

    public static BrowserSettings Load()
    {
        var settings = JsonFileStore.Read<BrowserSettings>(AppPaths.BrowserSettingsPath) ?? new BrowserSettings();
        settings.Bookmarks ??= new List<BrowserBookmark>();
        settings.Tabs ??= new List<string>();
        // Версии до 1.3.47 открывали Яндекс: чужие сервисы больше не подставляем.
        if (settings.HomePage.Contains("ya.ru", StringComparison.OrdinalIgnoreCase) ||
            settings.HomePage.Contains("yandex", StringComparison.OrdinalIgnoreCase))
        {
            settings.HomePage = string.Empty;
        }

        settings.SearchEngine = "google";
        return settings;
    }

    public void Save() => JsonFileStore.Write(AppPaths.BrowserSettingsPath, this);
}
