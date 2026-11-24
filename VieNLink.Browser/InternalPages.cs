using System.Text;

namespace VieNLink;

/// <summary>
/// Внутренние страницы браузера в духе Яндекс Браузера: «Настройки» и «История»
/// открываются вкладками, а не отдельными окнами.
/// </summary>
internal static class InternalPages
{
    public const string SettingsUrl = "https://" + BrowserSettings.StartPageHost + "/settings.html";
    public const string HistoryUrl = "https://" + BrowserSettings.StartPageHost + "/history.html";

    private const string CommonStyle = """
        * { box-sizing: border-box; }
        html, body { height: 100%; scrollbar-width: thin; }
        body { margin: 0; font-family: 'Segoe UI', system-ui, sans-serif; color: #F2F7F6;
               background: #1b2023; }
        body::before { content: ''; position: fixed; inset: 0; z-index: -1;
            background:
              radial-gradient(1100px 620px at 8% 18%, rgba(22,58,50,.96), rgba(22,58,50,0) 62%),
              radial-gradient(900px 700px at 94% 6%, rgba(220,233,239,.95), rgba(220,233,239,0) 68%),
              radial-gradient(1200px 820px at 74% 96%, rgba(226,124,96,.60), rgba(226,124,96,0) 66%),
              radial-gradient(900px 700px at 34% 86%, rgba(92,112,205,.42), rgba(92,112,205,0) 70%),
              linear-gradient(135deg, #16211f 0%, #243038 46%, #3d4a55 100%); }
        .brand { display: flex; align-items: center; gap: 12px; max-width: 1120px;
                 margin: 0 auto; padding: 26px 20px 0; }
        .brand img { width: 38px; height: 38px; border-radius: 10px;
                     box-shadow: 0 8px 22px rgba(0,0,0,.35); }
        .brand span { font-size: 12px; letter-spacing: .18em; text-transform: uppercase;
                      color: #EAF3EF; opacity: .85; }
        .shell { display: flex; gap: 20px; max-width: 1120px; margin: 0 auto;
                 padding: 20px 20px 60px; min-height: calc(100vh - 84px); }
        .side { width: 232px; flex: none; padding: 14px 10px; align-self: flex-start;
                background: rgba(18,24,26,.42); border: 1px solid rgba(255,255,255,.18);
                border-radius: 16px; backdrop-filter: blur(10px); position: sticky; top: 16px; }
        .side h1 { font-size: 15px; margin: 0 0 12px 12px; color: #EAF3EF; }
        .side a { display: block; padding: 8px 12px; border-radius: 9px; color: #DCE8E2;
                  text-decoration: none; font-size: 13px; }
        .side a:hover { background: rgba(255,255,255,.12); }
        .side a.active { background: rgba(134,194,162,.30); color: #F4FFF9; font-weight: 600; }
        .main { flex: 1; min-width: 0; }
        .main h2 { font-size: 21px; margin: 0 0 16px; color: #FFFFFF;
                   text-shadow: 0 2px 12px rgba(0,0,0,.35); }
        .card { background: rgba(255,255,255,.075); border: 1px solid rgba(255,255,255,.14);
                border-radius: 16px; color: #F2F7F6; padding: 6px 18px; margin-bottom: 18px;
                backdrop-filter: blur(14px); box-shadow: 0 14px 34px rgba(0,0,0,.22); }
        .row { display: flex; align-items: center; gap: 14px; padding: 14px 0;
               border-bottom: 1px solid rgba(255,255,255,.09); flex-wrap: wrap; }
        .row:last-child { border-bottom: 0; }
        .row .text { flex: 1 1 220px; min-width: 0; }
        .row .title { display: block; font-size: 14px; font-weight: 600; margin-bottom: 3px;
                      color: #F4FAF7; }
        .row .hint { display: block; font-size: 12.5px; color: rgba(255,255,255,.62); line-height: 1.45; }
        .switch { width: 42px; height: 24px; border-radius: 12px; background: rgba(255,255,255,.22);
                  position: relative; cursor: pointer; flex: none; transition: background .15s; }
        .switch::after { content: ''; position: absolute; top: 3px; left: 3px; width: 18px;
                         height: 18px; border-radius: 50%; background: #fff;
                         box-shadow: 0 1px 3px rgba(0,0,0,.25); transition: left .15s; }
        .switch.on { background: #58A97C; }
        .switch.on::after { left: 21px; }
        input.text, select { height: 38px; border: 1px solid rgba(255,255,255,.18); border-radius: 19px;
                             padding: 0 16px; font-size: 13px; color: #F2F7F6;
                             background: rgba(255,255,255,.10); outline: none; width: 100%;
                             max-width: 280px; min-width: 0; backdrop-filter: blur(10px);
                             box-shadow: 0 8px 22px rgba(0,0,0,.18); }
        input.text::placeholder { color: rgba(255,255,255,.55); }
        input.text:focus, select:focus { border-color: #86C2A2;
                                          box-shadow: 0 0 0 3px rgba(134,194,162,.28), 0 8px 22px rgba(0,0,0,.2); }
        select option { color: #171C1F; background: #FFFFFF; }
        .btn { height: 38px; padding: 0 18px; border-radius: 19px; cursor: pointer; font-size: 13px;
               font-weight: 600; color: #EAF3EF; background: rgba(255,255,255,.12);
               border: 1px solid rgba(255,255,255,.20); backdrop-filter: blur(10px);
               transition: background .15s ease, border-color .15s ease, transform .1s ease; }
        .btn:hover { background: rgba(255,255,255,.20); border-color: rgba(255,255,255,.34); }
        .btn:active { transform: translateY(1px); }
        .btn.danger { background: rgba(255,96,84,.16); color: #FFD9D4;
                      border-color: rgba(255,120,108,.36); }
        .btn.danger:hover { background: rgba(255,96,84,.26); border-color: rgba(255,140,128,.55); }
        .toolbar { display: flex; gap: 12px; align-items: center; margin-bottom: 18px; flex-wrap: wrap; }
        .toolbar input { flex: 1 1 240px; max-width: 460px; }
        .day { font-size: 12px; letter-spacing: .06em; text-transform: uppercase;
               color: rgba(255,255,255,.62); margin: 22px 0 8px; }
        .item { display: flex; align-items: center; gap: 12px; padding: 10px 14px; border-radius: 14px;
                background: rgba(255,255,255,.075); border: 1px solid rgba(255,255,255,.12);
                margin-bottom: 8px; backdrop-filter: blur(12px);
                box-shadow: 0 10px 24px rgba(0,0,0,.18);
                transition: background .15s ease, border-color .15s ease; }
        .item:hover { background: rgba(255,255,255,.14); border-color: rgba(255,255,255,.24); }
        .item img { width: 20px; height: 20px; border-radius: 5px; background: rgba(255,255,255,.12);
                    flex: none; }
        .item .info { flex: 1; min-width: 0; }
        .item .name { font-size: 13.5px; color: #F4FAF7; text-decoration: none; display: block;
                      overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
        .item .name:hover { text-decoration: underline; }
        .item .url { font-size: 12px; color: rgba(255,255,255,.55); overflow: hidden;
                     text-overflow: ellipsis; white-space: nowrap; }
        .item .time { font-size: 12px; color: rgba(255,255,255,.55); flex: none; }
        .item .del { border: 0; background: transparent; cursor: pointer; color: rgba(255,255,255,.6);
                     font-size: 14px; padding: 4px 9px; border-radius: 9px;
                     transition: background .15s ease, color .15s ease; }
        .item .del:hover { background: rgba(255,96,84,.22); color: #FFD9D4; }
        .empty { color: rgba(255,255,255,.75); font-size: 13.5px; padding: 24px 4px; }
        @media (max-width: 820px) {
            .shell { flex-direction: column; }
            .side { width: auto; position: static; }
        }
        """;

    public static string BuildSettings(BrowserSettings settings)
    {
        var html = new StringBuilder();
        html.Append("""
            <!DOCTYPE html>
            <html lang="ru"><head><meta charset="utf-8"><title>Настройки</title><style>
            """);
        html.Append(CommonStyle);
        html.Append("""
            </style></head><body>
            <div class="brand"><img src="browser-icon.png" alt=""><span>VieNLink · браузер</span></div>
            <div class="shell">
              <nav class="side">
                <h1>Настройки</h1>
                <a href="#start" class="active">Запуск</a>
                <a href="#look">Внешний вид</a>
                <a href="#privacy">Приватность и безопасность</a>
                <a href="#extra">Дополнительно</a>
              </nav>
              <main class="main">
                <h2 id="start">Запуск</h2>
                <div class="card">
            """);
        html.Append(Row("text", "Домашняя страница",
            "Пусто — стартовая страница VieNLink с поиском Google и плитками.",
            "<input class=\"text\" id=\"home\" placeholder=\"https://example.com/\" value=\"" +
            Escape(settings.HomePage) + "\" onchange=\"setText('home', this.value)\">"));
        html.Append(Toggle("restore", "Открывать вкладки прошлой сессии",
            "При запуске браузер восстановит вкладки, которые были открыты.", settings.RestoreTabs));
        html.Append(Toggle("clearstart", "Очищать кэш и cookie при запуске",
            "Каждый запуск начинается с чистыми данными сайтов.", settings.ClearDataOnStart));
        html.Append("""
                </div>
                <h2 id="look">Внешний вид</h2>
                <div class="card">
            """);
        html.Append(Row("text", "Масштаб страниц",
            "Размер страниц по умолчанию.", ZoomSelect(settings.Zoom)));
        html.Append(Toggle("bookmarksbar", "Панель закладок",
            "Показывать строку закладок под адресной строкой.", settings.ShowBookmarksBar));
        html.Append(Toggle("favicons", "Значки сайтов во вкладках",
            "Показывать favicon сайта на вкладке.", settings.ShowFavicons));
        html.Append(Toggle("linkstatus", "Показывать адрес ссылки",
            "Строка состояния при наведении на ссылку.", settings.ShowLinkStatusBar));
        html.Append("""
                </div>
                <h2 id="privacy">Приватность и безопасность</h2>
                <div class="card">
            """);
        html.Append(Toggle("savehistory", "Вести историю посещений",
            "Сохранять посещённые страницы для поиска по истории.", settings.SaveHistory));
        html.Append(Toggle("requirevpn", "Только через VPN",
            "Без подключённого сервера страницы не открываются — реальный IP не утечёт.", settings.RequireVpn));
        html.Append(Toggle("blockperm", "Запрещать камеру, микрофон и геолокацию",
            "Сайты не смогут запросить доступ к устройствам и уведомлениям.", settings.BlockSensitivePermissions));
        html.Append(Toggle("autofill", "Автозаполнение форм и пароли",
            "Браузер запоминает введённые данные и предлагает их.", settings.EnableAutofill));
        html.Append("""
                </div>
                <h2 id="extra">Дополнительно</h2>
                <div class="card">
            """);
        html.Append(Toggle("js", "JavaScript на страницах",
            "Выключение ломает большинство современных сайтов.", settings.EnableJavaScript));
        html.Append(Toggle("devtools", "Инструменты разработчика",
            "Пункт меню и клавиша F12.", settings.EnableDevTools));
        html.Append(Toggle("closelast", "Закрывать окно вместе с последней вкладкой",
            "Иначе вместо закрытия откроется новая вкладка.", settings.CloseWithLastTab));
        html.Append("""
                </div>
              </main>
            </div>
            <script>
              function post(command) { window.chrome.webview.postMessage(command); }
              function toggle(key, element) {
                var next = !element.classList.contains('on');
                element.classList.toggle('on', next);
                post('vienlink:setting:' + key + ':' + (next ? '1' : '0'));
              }
              function setText(key, value) { post('vienlink:setting:' + key + ':' + encodeURIComponent(value)); }
              function setZoom(value) { post('vienlink:setting:zoom:' + value); }
              document.querySelectorAll('.switch').forEach(function (element) {
                element.addEventListener('click', function () { toggle(element.dataset.key, element); });
              });
              document.querySelectorAll('.side a').forEach(function (link) {
                link.addEventListener('click', function () {
                  document.querySelectorAll('.side a').forEach(function (item) { item.classList.remove('active'); });
                  link.classList.add('active');
                });
              });
            </script>
            </body></html>
            """);
        return html.ToString();
    }

    public static string BuildHistory(BrowserHistory history)
    {
        var html = new StringBuilder();
        html.Append("""
            <!DOCTYPE html>
            <html lang="ru"><head><meta charset="utf-8"><title>История</title><style>
            """);
        html.Append(CommonStyle);
        html.Append("""
            </style></head><body>
            <div class="brand"><img src="browser-icon.png" alt=""><span>VieNLink · браузер</span></div>
            <div class="shell">
              <nav class="side">
                <h1>История</h1>
                <a href="#" class="active">Все записи</a>
              </nav>
              <main class="main">
                <h2>История посещений</h2>
                <div class="toolbar">
                  <input class="text" id="q" placeholder="Поиск по истории" oninput="filter()">
                  <button class="btn danger" onclick="clearAll()">Очистить историю</button>
                </div>
                <div id="list">
            """);

        var entries = history.Items ?? new List<BrowserHistoryEntry>();
        if (entries.Count == 0)
        {
            html.Append("<div class=\"empty\">История пуста.</div>");
        }
        else
        {
            string? lastDay = null;
            foreach (var entry in entries.Take(500))
            {
                var day = DayLabel(entry.VisitedUtc);
                if (day != lastDay)
                {
                    html.Append("<div class=\"day\">").Append(Escape(day)).Append("</div>");
                    lastDay = day;
                }

                html.Append("<div class=\"item\" data-search=\"")
                    .Append(Escape((entry.Title + " " + entry.Url).ToLowerInvariant()))
                    .Append("\"><img src=\"").Append(Escape(Favicon(entry.Url)))
                    .Append("\" onerror=\"this.style.visibility='hidden'\" alt=\"\">")
                    .Append("<span class=\"info\"><a class=\"name\" href=\"").Append(Escape(entry.Url))
                    .Append("\" title=\"").Append(Escape(entry.Title)).Append("\">")
                    .Append(Escape(entry.Title)).Append("</a><span class=\"url\">")
                    .Append(Escape(entry.Url)).Append("</span></span>")
                    .Append("<span class=\"time\">")
                    .Append(entry.VisitedUtc.ToLocalTime().ToString("HH:mm")).Append("</span>")
                    .Append("<button class=\"del\" title=\"Удалить\" onclick=\"removeEntry('")
                    .Append(Escape(entry.Url)).Append("')\">✕</button></div>");
            }
        }

        html.Append("""
                </div>
              </main>
            </div>
            <script>
              function post(command) { window.chrome.webview.postMessage(command); }
              function filter() {
                var query = document.getElementById('q').value.trim().toLowerCase();
                document.querySelectorAll('.item').forEach(function (item) {
                  item.style.display = !query || item.dataset.search.indexOf(query) >= 0 ? '' : 'none';
                });
                document.querySelectorAll('.day').forEach(function (day) {
                  day.style.display = query ? 'none' : '';
                });
              }
              function removeEntry(url) { post('vienlink:histdel:' + encodeURIComponent(url)); }
              function clearAll() { if (confirm('Очистить всю историю?')) { post('vienlink:histclear'); } }
            </script>
            </body></html>
            """);
        return html.ToString();
    }

    private static string DayLabel(DateTime utc)
    {
        var local = utc.ToLocalTime();
        var today = DateTime.Today;
        if (local.Date == today)
        {
            return "Сегодня";
        }

        if (local.Date == today.AddDays(-1))
        {
            return "Вчера";
        }

        return local.ToString("d MMMM");
    }

    private static string Favicon(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) ? $"{uri.Scheme}://{uri.Host}/favicon.ico" : string.Empty;

    private static string Toggle(string key, string title, string hint, bool value) =>
        "<div class=\"row\"><span class=\"text\"><span class=\"title\">" + Escape(title) +
        "</span><span class=\"hint\">" + Escape(hint) + "</span></span>" +
        "<span class=\"switch" + (value ? " on" : string.Empty) + "\" data-key=\"" + key +
        "\"></span></div>";

    private static string Row(string kind, string title, string hint, string control) =>
        "<div class=\"row\"><span class=\"text\"><span class=\"title\">" + Escape(title) +
        "</span><span class=\"hint\">" + Escape(hint) + "</span></span>" + control + "</div>";

    private static string ZoomSelect(double zoom)
    {
        var values = new[] { 0.75, 0.9, 1.0, 1.1, 1.25, 1.5, 2.0 };
        var html = new StringBuilder("<select onchange=\"setZoom(this.value)\">");
        foreach (var value in values)
        {
            html.Append("<option value=\"").Append(value.ToString(System.Globalization.CultureInfo.InvariantCulture))
                .Append('"');
            if (Math.Abs(zoom - value) < 0.001)
            {
                html.Append(" selected");
            }

            html.Append('>').Append(Math.Round(value * 100)).Append("%</option>");
        }

        return html.Append("</select>").ToString();
    }

    private static string Escape(string value) => System.Net.WebUtility.HtmlEncode(value ?? string.Empty);
}
