using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace VieNLink;

/// <summary>
/// Встроенный браузер: вкладки, адресная строка, поиск, закладки и панель закладок,
/// поиск по странице, история, загрузки, масштаб и настройки. Движок — WebView2 (Edge),
/// поэтому страницы идут через тот же системный прокси, что и остальной трафик.
/// </summary>
public sealed partial class BrowserWindow : Window
{
    private static readonly Geometry ReloadGeometry = Geometry.Parse(
        "M17.65,6.35C16.2,4.9 14.21,4 12,4A8,8 0 0,0 4,12A8,8 0 0,0 12,20C15.73,20 18.84,17.45 19.73,14H17.65C16.83,16.33 14.61,18 12,18A6,6 0 0,1 6,12A6,6 0 0,1 12,6C13.66,6 15.14,6.69 16.22,7.78L13,11H20V4L17.65,6.35Z");

    private static readonly Geometry StopGeometry = Geometry.Parse(
        "M19,6.41L17.59,5L12,10.59L6.41,5L5,6.41L10.59,12L5,17.59L6.41,19L12,13.41L17.59,19L19,17.59L13.41,12L19,6.41Z");

    private static readonly Geometry StarOutlineGeometry = Geometry.Parse(
        "M12,15.39L8.24,17.66L9.23,13.38L5.91,10.5L10.29,10.13L12,6.09L13.71,10.13L18.09,10.5L14.77,13.38L15.76,17.66M22,9.24L14.81,8.63L12,2L9.19,8.63L2,9.24L7.45,13.97L5.82,21L12,17.27L18.18,21L16.54,13.97L22,9.24Z");

    private static readonly Geometry StarFilledGeometry = Geometry.Parse(
        "M12,17.27L18.18,21L16.54,13.97L22,9.24L14.81,8.62L12,2L9.19,8.62L2,9.24L7.45,13.97L5.82,21L12,17.27Z");

    /// <summary>
    /// Сочетания клавиш перехватываются в самой странице: у WPF-обёртки WebView2 нет
    /// события клавиатуры. Страница присылает строку «vienlink:команда», окно выполняет действие.
    /// </summary>
    private const string ShortcutScript = """
        (function () {
            if (window.__vienlinkShortcuts) { return; }
            window.__vienlinkShortcuts = true;
            document.addEventListener('keydown', function (event) {
                var control = event.ctrlKey || event.metaKey;
                var code = event.code;
                var command = null;
                if (control && code === 'KeyT') { command = 'newtab'; }
                else if (control && code === 'KeyW') { command = 'closetab'; }
                else if (control && code === 'KeyL') { command = 'address'; }
                else if (control && code === 'KeyD') { command = 'bookmark'; }
                else if (control && code === 'KeyF') { command = 'find'; }
                else if (control && code === 'KeyH') { command = 'history'; }
                else if (control && code === 'KeyJ') { command = 'downloads'; }
                else if (control && event.shiftKey && code === 'KeyV') { command = 'vpnp'; }
                else if (control && code === 'KeyR') { command = 'reload'; }
                else if (control && (code === 'Equal' || code === 'NumpadAdd')) { command = 'zoomin'; }
                else if (control && (code === 'Minus' || code === 'NumpadSubtract')) { command = 'zoomout'; }
                else if (control && code === 'Digit0') { command = 'zoomreset'; }
                else if (code === 'F5') { command = 'reload'; }
                else if (code === 'F11') { command = 'fullscreen'; }
                else if (code === 'F12') { command = 'devtools'; }
                else if (event.altKey && code === 'ArrowLeft') { command = 'back'; }
                else if (event.altKey && code === 'ArrowRight') { command = 'forward'; }
                if (!command) { return; }
                event.preventDefault();
                event.stopPropagation();
                window.chrome.webview.postMessage('vienlink:' + command);
            }, true);
        })();
        """;

    /// <summary>Поиск по странице: подсветка всех совпадений (CSS Highlight API) и переход по ним.</summary>
    private const string FindScript = """
        (function () {
            if (window.__vnFind) { return; }
            var state = { ranges: [], current: -1 };
            function injectStyle() {
                if (document.getElementById('vn-find-style')) { return; }
                var style = document.createElement('style');
                style.id = 'vn-find-style';
                style.textContent = '::highlight(vn-find-all){background:rgba(134,194,162,.38);color:inherit}' +
                                    '::highlight(vn-find-current){background:rgba(255,205,110,.85);color:#101210}';
                document.documentElement.appendChild(style);
            }
            function paint() {
                if (!(window.CSS && CSS.highlights && window.Highlight)) { return; }
                if (!state.ranges.length) {
                    CSS.highlights.delete('vn-find-all');
                    CSS.highlights.delete('vn-find-current');
                    return;
                }
                var all = new Highlight();
                for (var i = 0; i < state.ranges.length; i++) { all.add(state.ranges[i]); }
                CSS.highlights.set('vn-find-all', all);
                var current = state.ranges[state.current];
                if (current) {
                    var one = new Highlight();
                    one.add(current);
                    CSS.highlights.set('vn-find-current', one);
                }
            }
            function focusCurrent() {
                var range = state.ranges[state.current];
                if (!range) { return; }
                var holder = range.startContainer.parentElement;
                if (holder) { holder.scrollIntoView({ block: 'center', inline: 'nearest' }); }
                if (!(window.CSS && CSS.highlights && window.Highlight)) {
                    var selection = getSelection();
                    if (selection) { selection.removeAllRanges(); selection.addRange(range); }
                }
            }
            function reset() {
                state.ranges = [];
                state.current = -1;
                if (window.CSS && CSS.highlights) {
                    CSS.highlights.delete('vn-find-all');
                    CSS.highlights.delete('vn-find-current');
                }
                var selection = getSelection();
                if (selection) { selection.removeAllRanges(); }
            }
            window.__vnFind = {
                search: function (text) {
                    reset();
                    injectStyle();
                    if (!text || !document.body) { return JSON.stringify({ count: 0, index: 0 }); }
                    var needle = text.toLowerCase();
                    var walker = document.createTreeWalker(document.body, NodeFilter.SHOW_TEXT, {
                        acceptNode: function (node) {
                            var parent = node.parentElement;
                            if (!parent) { return NodeFilter.FILTER_REJECT; }
                            var tag = parent.tagName;
                            if (tag === 'SCRIPT' || tag === 'STYLE' || tag === 'NOSCRIPT' ||
                                tag === 'TEXTAREA' || tag === 'INPUT') {
                                return NodeFilter.FILTER_REJECT;
                            }
                            return NodeFilter.FILTER_ACCEPT;
                        }
                    });
                    var node, found = [];
                    while ((node = walker.nextNode())) {
                        var value = node.nodeValue;
                        if (!value) { continue; }
                        var lower = value.toLowerCase();
                        var at = 0;
                        while (true) {
                            var index = lower.indexOf(needle, at);
                            if (index < 0) { break; }
                            var range = document.createRange();
                            range.setStart(node, index);
                            range.setEnd(node, index + needle.length);
                            found.push(range);
                            at = index + needle.length;
                        }
                        if (found.length > 800) { break; }
                    }
                    state.ranges = found;
                    if (!found.length) { return JSON.stringify({ count: 0, index: 0 }); }
                    state.current = 0;
                    paint();
                    focusCurrent();
                    return JSON.stringify({ count: found.length, index: 1 });
                },
                next: function (direction) {
                    if (!state.ranges.length) { return JSON.stringify({ count: 0, index: 0 }); }
                    state.current = (state.current + direction + state.ranges.length) % state.ranges.length;
                    paint();
                    focusCurrent();
                    return JSON.stringify({ count: state.ranges.length, index: state.current + 1 });
                },
                clear: function () {
                    reset();
                    return JSON.stringify({ count: 0, index: 0 });
                }
            };
        })();
        """;

    /// <summary>
    /// Локальная стартовая страница: поиск Google, плитки закладок и подсказки.
    /// Живёт в %LOCALAPPDATA%\VieNLink\browser\start и отдаётся как https://vienlink.start/.
    /// </summary>
    private const string StartPageTemplate = """
        <!DOCTYPE html>
        <html lang="ru">
        <head>
        <meta charset="utf-8">
        <title>Новая вкладка</title>
        <style>
          * { box-sizing: border-box; }
          html, body { height: 100%; scrollbar-width: none; }
          body::-webkit-scrollbar { width: 0; height: 0; }
          body { margin: 0; overflow-x: hidden; font-family: 'Segoe UI', system-ui, sans-serif;
                 color: #14181b; background: #1b2023; }
          .wallpaper { position: fixed; inset: 0; z-index: 0;
            background:
              radial-gradient(1100px 620px at 8% 18%, rgba(22,58,50,.96), rgba(22,58,50,0) 62%),
              radial-gradient(900px 700px at 94% 6%, rgba(220,233,239,.95), rgba(220,233,239,0) 68%),
              radial-gradient(1200px 820px at 74% 96%, rgba(226,124,96,.60), rgba(226,124,96,0) 66%),
              radial-gradient(900px 700px at 34% 86%, rgba(92,112,205,.42), rgba(92,112,205,0) 70%),
              linear-gradient(135deg, #16211f 0%, #243038 46%, #3d4a55 100%); }
          .page { position: relative; z-index: 1; min-height: 100%; display: flex; flex-direction: column;
                  align-items: center; padding: 8vh 24px 40px; }
          .brand { display: flex; align-items: center; gap: 12px; margin-bottom: 24px; color: #fff; }
          .brand img { width: 44px; height: 44px; border-radius: 11px; box-shadow: 0 8px 22px rgba(0,0,0,.35); }
          .brand span { font-size: 12px; letter-spacing: .18em; text-transform: uppercase; opacity: .82; }
          form { width: min(720px, 100%); position: relative; }
          form::before { content: ''; position: absolute; inset: -7px; border-radius: 33px; z-index: -1;
                         background: linear-gradient(90deg, rgba(134,194,162,.65), rgba(126,150,255,.55),
                                     rgba(255,143,110,.6));
                         filter: blur(16px); opacity: .45; transition: opacity .18s ease; }
          form:focus-within::before { opacity: .85; }
          input { width: 100%; height: 52px; border: 0; border-radius: 26px; padding: 0 22px;
                  font-size: 15px; color: #14181b; background: rgba(255,255,255,.97);
                  box-shadow: 0 10px 34px rgba(0,0,0,.30), 0 0 0 1px rgba(255,255,255,.35);
                  outline: none; }
          input::placeholder { color: #8a9096; }

          .tiles { width: min(1100px, 100%); margin-top: 30px; display: grid;
                   grid-template-columns: repeat(auto-fill, minmax(158px, 1fr)); gap: 14px; }
          .tile { position: relative; display: flex; flex-direction: column; align-items: center; gap: 9px;
                  padding: 16px 10px 13px; background: #FFFFFF; border-radius: 16px;
                  text-decoration: none; color: #1a1e21; box-shadow: 0 10px 24px rgba(0,0,0,.16);
                  transition: transform .12s ease, box-shadow .18s ease; }
          .tile::after { content: ''; position: absolute; inset: -8px; border-radius: 22px; z-index: -1;
                         background: linear-gradient(90deg, rgba(134,194,162,.6), rgba(126,150,255,.5),
                                     rgba(255,143,110,.55));
                         filter: blur(16px); opacity: 0; transition: opacity .18s ease; }
          .tile:hover { transform: translateY(-2px);
                       box-shadow: 0 14px 30px rgba(0,0,0,.22), 0 0 16px rgba(134,194,162,.85),
                                   0 0 32px rgba(126,150,255,.6), 0 0 48px rgba(255,143,110,.4); }
          .tile .icon { position: relative; width: 32px; height: 32px; }
          .tile .icon img { position: absolute; inset: 0; width: 32px; height: 32px; border-radius: 8px;
                            background: #f2f4f5; opacity: 0; }
          .tile .icon.loaded img { opacity: 1; }
          .tile .icon.loaded .letter { display: none; }
          .tile .letter { position: absolute; inset: 0; display: flex; align-items: center;
                          justify-content: center; border-radius: 8px; background: #eef1f2;
                          color: #46564e; font-weight: 700; font-size: 14px; }
          .tile .name { font-size: 12px; max-width: 100%; overflow: hidden; text-overflow: ellipsis;
                        white-space: nowrap; }
          .tile.add { justify-content: center; background: rgba(255,255,255,.30);
                      border: 1px dashed rgba(255,255,255,.7); color: #fff; font-size: 26px; cursor: pointer; }
          .hint { margin-top: 22px; font-size: 12px; color: rgba(255,255,255,.62); text-align: center; }
          .tile { position: relative; }
          .tile-actions { display: none; position: absolute; top: 6px; right: 6px; gap: 4px; }
          body.editing .tile[data-custom] .tile-actions { display: flex; }
          .mini { width: 22px; height: 22px; border: 0; border-radius: 7px; cursor: pointer;
                  background: rgba(20,26,28,.72); color: #fff; font-size: 11px; line-height: 1; }
          .mini:hover { background: rgba(20,26,28,.92); }
          .editor { display: none; width: min(1100px, 100%); margin-top: 16px; gap: 8px; align-items: center; }
          body.editing .editor { display: flex; }
          .editor input { height: 44px; border: 0; border-radius: 22px; padding: 0 18px; font-size: 13.5px;
                          background: rgba(255,255,255,.97); color: #14181b; outline: none; flex: 1; }
          .editor button, .editor .ghost { height: 40px; padding: 0 18px; border-radius: 20px; border: 0;
                                          cursor: pointer; font-size: 12.5px; font-weight: 600; }
          .editor .add { background: #1f2a24; color: #eafaf1; }
          .editor .ghost { background: rgba(255,255,255,.22); color: #fff; border: 1px solid rgba(255,255,255,.45); }
          .actions { width: min(1100px, 100%); margin-top: 14px; display: flex; justify-content: flex-start; }
          .actions .ghost { height: 34px; padding: 0 16px; border-radius: 17px; cursor: pointer;
                            font-size: 12px; background: rgba(18,24,26,.42); color: #eef4f1;
                            border: 1px solid rgba(255,255,255,.4); }
          .vpnline { position: fixed; right: 18px; bottom: 14px; z-index: 1; font-size: 12px;
                     color: rgba(255,255,255,.68); }
          .vpnline.on { color: #a9f0c9; }
        </style>
        </head>
        <body>
          <div class="wallpaper"></div>
          <div class="page">
            <div class="brand">
              <img src="browser-icon.png" alt="">
              <span>VieNLink · браузер</span>
            </div>
            <form onsubmit="return submitSearch()">
              <input id="q" placeholder="Введите запрос или адрес" autocomplete="off" autofocus>
            </form>

            <div class="tiles">{{TILES}}</div>
            <div class="editor">
              <input id="newurl" placeholder="адрес или название сайта" autocomplete="off">
              <button class="add" type="button" onclick="addTile()">Добавить</button>
            </div>
            <div class="actions" id="donebar" style="display:none">
              <button class="ghost" type="button" onclick="toggleEdit()">Готово</button>
            </div>
            <div class="hint">Ctrl+T — новая вкладка · ☆ — добавить закладку · Ctrl+Shift+V — панель VPN</div>
          </div>
          <div class="vpnline" id="vpn-state">VPN: выключен</div>
          <script>
            window.__vnVpn = function (connected) {
              var element = document.getElementById('vpn-state');
              if (!element) { return; }
              element.textContent = connected ? 'VPN: подключён' : 'VPN: выключен';
              element.className = 'vpnline' + (connected ? ' on' : '');
            };
            var editing = false;
            function toggleEdit() {
              editing = !editing;
              document.body.classList.toggle('editing', editing);
              document.getElementById('donebar').style.display = editing ? 'flex' : 'none';
              if (editing) { document.getElementById('newurl').focus(); }
            }
            window.__vnEdit = function () { if (!editing) { toggleEdit(); } };
            window.__vnAddTile = function (url, title, icon) {
              var grid = document.querySelector('.tiles');
              if (!grid) { return; }
              var link = document.createElement('a');
              link.className = 'tile';
              link.href = url;
              link.title = title;
              link.dataset.custom = '1';
              link.dataset.url = url;
              link.dataset.title = title;
              var initial = (title || '•').trim().charAt(0).toUpperCase() || '•';
              link.innerHTML = '<span class="icon"><img alt="" onload="this.parentNode.classList.add(\'loaded\')" onerror="this.remove()">' +
                '<span class="letter"></span></span><span class="name"></span>' +
                '<span class="tile-actions"><button class="mini" type="button" data-act="rename" title="Переименовать">✎</button>' +
                '<button class="mini" type="button" data-act="remove" title="Удалить">✕</button></span>';
              link.querySelector('img').src = icon;
              link.querySelector('.letter').textContent = initial;
              link.querySelector('.name').textContent = title;
              var add = grid.querySelector('.tile.add');
              if (add) { grid.insertBefore(link, add); } else { grid.appendChild(link); }
            };
            window.__vnRemoveTile = function (url) {
              var nodes = document.querySelectorAll('.tile[data-url]');
              for (var i = 0; i < nodes.length; i++) {
                if (nodes[i].dataset.url === url) { nodes[i].remove(); }
              }
            };
            window.__vnRenameTile = function (url, title) {
              var nodes = document.querySelectorAll('.tile[data-url]');
              for (var i = 0; i < nodes.length; i++) {
                if (nodes[i].dataset.url !== url) { continue; }
                nodes[i].dataset.title = title;
                nodes[i].title = title;
                var name = nodes[i].querySelector('.name');
                if (name) { name.textContent = title; }
                var letter = nodes[i].querySelector('.letter');
                if (letter) { letter.textContent = (title.trim().charAt(0) || '•').toUpperCase(); }
              }
            };
            function addTile() {
              var value = document.getElementById('newurl').value.trim();
              if (!value) { return; }
              window.chrome.webview.postMessage('vienlink:addtile:' + value);
              document.getElementById('newurl').value = '';
            }
            function removeTile(url) {
              window.chrome.webview.postMessage('vienlink:removetile:' + url);
            }
            function renameTile(url, title) {
              var value = prompt('Название сайта', title);
              if (value) { window.chrome.webview.postMessage('vienlink:renametile:' + url + '|' + value); }
            }
            document.addEventListener('keydown', function (event) {
              if (event.key === 'Enter' && event.target && event.target.id === 'newurl') {
                event.preventDefault();
                addTile();
              }
            });
            document.addEventListener('click', function (event) {
              var button = event.target.closest ? event.target.closest('.mini') : null;
              if (!button) { return; }
              var tile = button.closest('.tile');
              if (!tile) { return; }
              event.preventDefault();
              event.stopPropagation();
              if (button.dataset.act === 'remove') {
                removeTile(tile.dataset.url);
              } else {
                renameTile(tile.dataset.url, tile.dataset.title);
              }
            });
            function submitSearch() {
              var value = document.getElementById('q').value.trim();
              if (!value) { return false; }
              window.chrome.webview.postMessage('vienlink:search:' + value);
              return false;
            }
          </script>
        </body>
        </html>
        """;

    /// <summary>Заглушка для режима «Только через VPN»: без сервера страницы не открываются.</summary>
    private const string VpnRequiredTemplate = """
        <!DOCTYPE html>
        <html lang="ru">
        <head>
        <meta charset="utf-8">
        <title>Нужен VPN</title>
        <style>
          body { margin: 0; height: 100vh; display: flex; align-items: center; justify-content: center;
                 background: #18191B; color: #E6E8EA; font-family: 'Segoe UI', system-ui, sans-serif; }
          .card { max-width: 540px; padding: 34px; border-radius: 18px; background: #202326;
                  border: 1px solid #2C3034; text-align: center; }
          h1 { font-size: 22px; margin: 0 0 10px; }
          p { color: #9AA0A6; font-size: 13.5px; line-height: 1.55; margin: 0 0 22px; }
          button { background: linear-gradient(#A9D8BD, #86C2A2); color: #10201A; border: 0;
                   border-radius: 10px; padding: 11px 22px; font-weight: 600; font-size: 14px; cursor: pointer; }
          .note { margin-top: 14px; font-size: 12px; color: #7C8288; }
        </style>
        </head>
        <body>
          <div class="card">
            <h1>Нужно подключение к VPN</h1>
            <p>Включён режим «Только через VPN»: без сервера страницы не открываются, чтобы не показывать
               ваш реальный IP-адрес.</p>
            <button onclick="window.chrome.webview.postMessage('vienlink:vpn-connect')">Подключиться и открыть</button>
            <div class="note">Или отключите «Только через VPN» в панели VPN.</div>
          </div>
        </body>
        </html>
        """;

    private sealed class BrowserTab
    {
        public BrowserTab(WebView2 view) => View = view;

        public WebView2 View { get; }

        public Border Header { get; set; } = null!;

        public TextBlock TitleText { get; set; } = null!;

        public Image Icon { get; set; } = null!;

        public Button CloseButton { get; set; } = null!;

        public string Title { get; set; } = "Новая вкладка";

        public string PendingUrl { get; set; } = string.Empty;

        public bool Pinned { get; set; }
    }

    private static BrowserWindow? _instance;

    private readonly List<BrowserTab> _tabs = new();
    private readonly BrowserSettings _settings = BrowserSettings.Load();
    private readonly BrowserHistory _history = BrowserHistory.Load();
    private readonly DispatcherTimer _findTimer;
    private CoreWebView2Environment? _environment;
    private BrowserTab? _active;
    private string _startFolder = string.Empty;
    private double _zoom;
    private bool _navigating;
    private bool _fullScreen;
    private WindowState _stateBeforeFullScreen = WindowState.Normal;
    private bool _initialized;
    private bool _dataCleared;
    private readonly DispatcherTimer _loadGuard;
    private string _findText = string.Empty;
    private string? _blockedUrl;
    private bool _autoConnectTried;
    private bool _vpnPanelOpen;

    private const string VpnRequiredUrl = "https://" + BrowserSettings.StartPageHost + "/vpn-required.html";

    public BrowserWindow()
    {
        InitializeComponent();
        _zoom = Math.Clamp(_settings.Zoom, 0.35, 3.0);
        _findTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(260) };
        _findTimer.Tick += (_, _) =>
        {
            _findTimer.Stop();
            RunFind("search", _findText);
        };

        // Страховка от «зависшего» индикатора загрузки.
        _loadGuard = new DispatcherTimer { Interval = TimeSpan.FromSeconds(8) };
        _loadGuard.Tick += (_, _) =>
        {
            _loadGuard.Stop();
            SetLoading(false);
        };

        AddressBox.TextChanged += (_, _) => UpdateAddressPlaceholder();
        FindBox.TextChanged += (_, _) =>
        {
            _findText = FindBox.Text;
            _findTimer.Stop();
            _findTimer.Start();
        };

        UpdateAddressPlaceholder();
        UpdateZoomText();
        UpdateBookmarksBarVisibility();

        VpnClient.StateChanged += OnVpnStateChanged;
        BuildVpnServicesPanel();
        ApplyVpnDock();
        RefreshVpnPanel();

        Loaded += OnLoadedAsync;
    }

    /// <summary>Открывает окно браузера отдельным независимым окном (не часть окна VPN).</summary>
    public static void Open(Window? owner = null)
    {
        if (_instance is { IsLoaded: true } existing)
        {
            if (existing.WindowState == WindowState.Minimized)
            {
                existing.WindowState = WindowState.Normal;
            }

            existing.Activate();
            return;
        }

        _instance = new BrowserWindow { WindowStartupLocation = WindowStartupLocation.CenterScreen };
        _instance.Closed += (_, _) => _instance = null;
        _instance.Show();
        _instance.Activate();
    }

    /// <summary>Открыть адрес в браузере новой вкладкой (из истории или закладок).</summary>
    public static void OpenUrl(string url)
    {
        if (_instance is { } window)
        {
            window.Activate();
            _ = window.CreateTabAsync(url);
        }
    }

    private async void OnLoadedAsync(object sender, RoutedEventArgs e)
    {
        if (_initialized)
        {
            return;
        }

        _initialized = true;
        try
        {
            var dataFolder = Path.Combine(AppPaths.DataDirectory, "browser");
            Directory.CreateDirectory(dataFolder);
            _startFolder = Path.Combine(dataFolder, "start");
            Directory.CreateDirectory(_startFolder);
            WriteStartPageIcon(_startFolder);
            WriteStartPage();
            WriteInternalPages();
            _environment = await CoreWebView2Environment.CreateAsync(null, dataFolder);
        }
        catch (Exception exception)
        {
            AppLog.Write("browser", "Не удалось запустить движок WebView2", exception);
            MessageBox.Show(this,
                "Не удалось запустить движок браузера (WebView2).\n\n" +
                "Обычно помогает установка компонента «Microsoft Edge WebView2 Runtime» " +
                "с сайта Microsoft, после чего браузер заработает.\n\n" +
                "Подробности — в журнале приложения.",
                "Браузер", MessageBoxButton.OK, MessageBoxImage.Warning);
            Close();
            return;
        }

        var restored = _settings.RestoreTabs
            ? _settings.Tabs.Where(IsWebAddress).Take(12).ToList()
            : new List<string>();
        if (restored.Count == 0)
        {
            restored.Add(_settings.HomeUrl);
        }

        foreach (var url in restored)
        {
            await CreateTabAsync(url);
        }

        await CheckVpnPresenceAsync();
    }

    /// <summary>Проверяет, установлен ли VPN, и предлагает установить или запустить его.</summary>
    private async Task CheckVpnPresenceAsync()
    {
        try
        {
            await VpnClient.RefreshAsync();
        }
        catch (Exception exception)
        {
            AppLog.Write("vpn", "Не удалось опросить состояние VPN", exception);
        }

        if (VpnClient.Available)
        {
            RefreshVpnPanel();
            return;
        }

        // Никаких модальных окон при старте: состояние и кнопка «Установить/Запустить/Обновить VPN»
        // живут в панели VPN. Это не мешает работать, если VPN ещё не установлен.
        var exe = VpnInstaller.FindInstalledExe();
        StatusText.Text = exe is null
            ? "VPN не установлен — панель VPN подскажет, как поставить"
            : VpnInstaller.IsOutdated(exe)
                ? "VPN нужно обновить — кнопка в панели VPN"
                : VpnClient.Available
                    ? string.Empty
                    : "VPN установлен, но не запущен — кнопка в панели VPN";

        RefreshVpnPanel();
        await Task.CompletedTask;
    }

    /// <summary>Скачивает установщик VieNLink с GitHub (или зеркала) и ставит его.</summary>
    private async Task InstallVpnAsync()
    {
        try
        {
            StatusText.Text = "Ищу установщик VieNLink…";
            var url = await VpnInstaller.FindInstallerUrlAsync(CancellationToken.None);
            if (url is null)
            {
                MessageBox.Show(this,
                    "Не удалось получить ссылку на установщик. Скачайте VieNLink вручную:\n" +
                    VpnInstaller.ReleasesUrl,
                    "VPN", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var progress = new Progress<double>(value =>
                StatusText.Text = $"Скачиваю VieNLink… {value:P0}");
            var path = await VpnInstaller.DownloadInstallerAsync(url, progress, CancellationToken.None);
            if (path is null)
            {
                MessageBox.Show(this,
                    "Не удалось скачать установщик. Проверьте интернет или скачайте вручную:\n" +
                    VpnInstaller.ReleasesUrl,
                    "VPN", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            StatusText.Text = "Запускаю установщик VieNLink…";
            if (!VpnInstaller.RunInstaller(path))
            {
                MessageBox.Show(this, "Установщик не запустился. Попробуйте установить VieNLink вручную.",
                    "VPN", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            VpnInstaller.ResetCache();
            var exe = VpnInstaller.FindInstalledExe();
            if (exe is not null)
            {
                VpnInstaller.Launch(exe);
            }

            await Task.Delay(1500);
            await VpnClient.RefreshAsync();
            RefreshVpnPanel();
            StatusText.Text = "VieNLink установлен";
            MessageBox.Show(this,
                "VieNLink установлен. Панель VPN подключится автоматически, когда приложение запустится.",
                "VPN", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception exception)
        {
            AppLog.Write("vpn", "Установка VPN из браузера не удалась", exception);
            MessageBox.Show(this, "Не удалось установить VPN: " + exception.Message,
                "VPN", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private static bool IsWebAddress(string? url) =>
        !string.IsNullOrWhiteSpace(url) &&
        (url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
         url.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ||
         url.StartsWith("file://", StringComparison.OrdinalIgnoreCase));

    /// <summary>Адрес для строки: у локальной стартовой страницы показываем подсказку, а не https://vienlink.start/…</summary>
    private static string DisplayAddress(string? source) =>
        string.Equals(source, BrowserSettings.StartPageUrl, StringComparison.OrdinalIgnoreCase)
            ? string.Empty
            : source ?? string.Empty;

    private static bool IsLocalPage(string? url) =>
        !string.IsNullOrWhiteSpace(url) &&
        url.StartsWith("https://" + BrowserSettings.StartPageHost + "/", StringComparison.OrdinalIgnoreCase);

    private async Task CreateTabAsync(string url)
    {
        var view = new WebView2
        {
            Visibility = Visibility.Collapsed,
            // Тёмный фон окна: новая вкладка не мигает белым, пока грузится страница.
            DefaultBackgroundColor = System.Drawing.Color.FromArgb(18, 24, 26),
        };
        ContentHost.Children.Add(view);

        var tab = new BrowserTab(view) { PendingUrl = url };
        BuildTabHeader(tab);
        _tabs.Add(tab);

        if (_environment is null)
        {
            Activate(tab);
            return;
        }

        try
        {
            await view.EnsureCoreWebView2Async(_environment);
        }
        catch (Exception exception)
        {
            AppLog.Write("browser", "Не удалось создать вкладку WebView2", exception);
            Activate(tab);
            return;
        }

        if (view.CoreWebView2 is null)
        {
            Activate(tab);
            return;
        }

        ConfigureWebView(tab);
        view.ZoomFactor = _zoom;
        AddressBox.Text = DisplayAddress(url);
        // Заголовок вкладки активен сразу, содержимое покажем после начала отрисовки страницы.
        Activate(tab, focusPage: false, showView: false);
        var shown = false;
        void ShowOnce()
        {
            if (shown)
            {
                return;
            }

            shown = true;
            if (ReferenceEquals(_active, tab))
            {
                ShowTabContent(tab);
            }
        }

        view.CoreWebView2.ContentLoading += (_, _) => ShowOnce();
        view.CoreWebView2.NavigationCompleted += (_, _) => ShowOnce();
        view.CoreWebView2.Navigate(IsWebAddress(url) ? url : _settings.HomeUrl);
    }

    private void ConfigureWebView(BrowserTab tab)
    {
        var view = tab.View;
        var core = view.CoreWebView2;
        // Локальная стартовая страница: файлы отдаются как https://vienlink.start/.
        core.SetVirtualHostNameToFolderMapping(
            BrowserSettings.StartPageHost, _startFolder, CoreWebView2HostResourceAccessKind.DenyCors);
        core.Settings.IsZoomControlEnabled = false;
        // Ctrl+T/Ctrl+W и остальные сочетания обрабатываем сами (см. ShortcutScript).
        core.Settings.AreBrowserAcceleratorKeysEnabled = false;
        core.Settings.IsSwipeNavigationEnabled = false;
        ApplySettings(core);
        core.PermissionRequested += OnPermissionRequested;
        if (_settings.ClearDataOnStart && !_dataCleared)
        {
            _dataCleared = true;
            _ = core.Profile.ClearBrowsingDataAsync(CoreWebView2BrowsingDataKinds.AllProfile);
        }

        core.DocumentTitleChanged += (_, _) =>
        {
            var title = core.DocumentTitle;
            tab.Title = string.IsNullOrWhiteSpace(title) ? HostOf(core.Source) : title.Trim();
            UpdateTabHeader(tab);
            RefreshWindowTitle();
        };
        core.SourceChanged += (_, _) =>
        {
            if (tab != _active)
            {
                return;
            }

            AddressBox.Text = DisplayAddress(core.Source);
            UpdateNavState();
            UpdateBookmarkButton();
        };
        core.HistoryChanged += (_, _) => UpdateNavState();
        core.NavigationStarting += (_, args) =>
        {
            // «Только через VPN»: без подключения внешние страницы не открываем.
            if (_settings.RequireVpn && !VpnClient.Connected &&
                IsWebAddress(args.Uri) && !IsLocalPage(args.Uri))
            {
                args.Cancel = true;
                _blockedUrl = args.Uri;
                SetLoading(false);
                core.Navigate(VpnRequiredUrl);
                return;
            }

            SetLoading(true);
        };
        core.NavigationCompleted += async (_, args) =>
        {
            SetLoading(false);
            if (tab == _active)
            {
                AddressBox.Text = DisplayAddress(core.Source);
                UpdateNavState();
                UpdateBookmarkButton();
            }

            if (args.IsSuccess)
            {
                if (_settings.SaveHistory)
                {
                    _history.Add(core.DocumentTitle, core.Source);
                }

                if (IsLocalPage(core.Source))
                {
                    UpdateStartPageVpnState();
                    if (_pendingTileEdit &&
                        string.Equals(core.Source, BrowserSettings.StartPageUrl, StringComparison.OrdinalIgnoreCase))
                    {
                        _pendingTileEdit = false;
                        _ = core.ExecuteScriptAsync("window.__vnEdit && window.__vnEdit()");
                    }
                }

                return;
            }

            if (args.WebErrorStatus == CoreWebView2WebErrorStatus.OperationCanceled)
            {
                return;
            }

            StatusText.Text = "Не удалось открыть страницу: " + args.WebErrorStatus;

            // Автоподключение: страница не открылась, VPN выключен — подключаемся и повторяем.
            if (_settings.AutoConnectOnFailure && !VpnClient.Connected && !_autoConnectTried)
            {
                _autoConnectTried = true;
                StatusText.Text = "Сайт не открылся — подключаю VPN…";
                try
                {
                    if (await VpnClient.ConnectAsync())
                    {
                        core.Reload();
                    }
                }
                catch (Exception exception)
                {
                    AppLog.Write("browser", "Автоподключение VPN из браузера не удалось", exception);
                }
                finally
                {
                    _autoConnectTried = false;
                }
            }
        };
        core.NewWindowRequested += (_, args) =>
        {
            // Ссылки с target=_blank и window.open открываем в новой вкладке этого окна.
            args.Handled = true;
            if (!string.IsNullOrWhiteSpace(args.Uri))
            {
                _ = CreateTabAsync(args.Uri);
            }
        };
        core.FaviconChanged += (_, _) =>
        {
            if (_settings.ShowFavicons)
            {
                _ = LoadFaviconAsync(tab);
            }
        };
        core.DownloadStarting += (_, args) =>
        {
            var entry = BrowserDownloads.Track(args.DownloadOperation, args.ResultFilePath, core.Source ?? string.Empty);
            StatusText.Text = "Скачивается: " + entry.FileName;
        };
        core.ContainsFullScreenElementChanged += (_, _) => SetFullScreen(core.ContainsFullScreenElement);
        core.StatusBarTextChanged += (_, _) => StatusText.Text = core.StatusBarText ?? string.Empty;
        core.WebMessageReceived += OnWebMessageReceived;
        _ = core.AddScriptToExecuteOnDocumentCreatedAsync(ShortcutScript);
        core.ProcessFailed += (_, args) =>
        {
            AppLog.Write("browser", "Сбой процесса WebView2: " + args.ProcessFailedKind);
            StatusText.Text = "Движок браузера перезапускается…";
        };
    }

    private async Task LoadFaviconAsync(BrowserTab tab)
    {
        try
        {
            var core = tab.View.CoreWebView2;
            if (core is null)
            {
                return;
            }

            await using var stream = await core.GetFaviconAsync(CoreWebView2FaviconImageFormat.Png);
            if (stream is null)
            {
                return;
            }

            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.StreamSource = stream;
            bitmap.DecodePixelWidth = 16;
            bitmap.EndInit();
            bitmap.Freeze();
            tab.Icon.Source = bitmap;
            tab.Icon.Visibility = Visibility.Visible;
        }
        catch (Exception exception)
        {
            // Сайт без значка — обычное дело, шум в журнал не нужен.
            AppLog.Write("browser", "Значок сайта недоступен", exception);
        }
    }

    private static string HostOf(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) && !string.IsNullOrEmpty(uri.Host) ? uri.Host : "Новая вкладка";

    private void BuildTabHeader(BrowserTab tab)
    {
        var icon = new Image
        {
            Width = 16,
            Height = 16,
            Margin = new Thickness(0, 0, 7, 0),
            Visibility = Visibility.Collapsed,
        };
        RenderOptions.SetBitmapScalingMode(icon, BitmapScalingMode.HighQuality);

        var title = new TextBlock
        {
            Text = "Новая вкладка",
            VerticalAlignment = VerticalAlignment.Center,
            MaxWidth = 200,
            TextTrimming = TextTrimming.CharacterEllipsis,
            FontSize = 12.5,
        };
        var close = new Button
        {
            Content = "✕",
            Style = (Style)FindResource("SmallButton"),
            Padding = new Thickness(7, 0, 7, 0),
            Margin = new Thickness(8, 0, 0, 0),
            FontSize = 11,
            ToolTip = "Закрыть вкладку (Ctrl+W)",
            Foreground = (Brush)FindResource("ChromeTextBrush"),
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
        };
        close.Click += (_, args) =>
        {
            args.Handled = true;
            CloseTab(tab);
        };

        var panel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(10, 3, 6, 3) };
        panel.Children.Add(icon);
        panel.Children.Add(title);
        panel.Children.Add(close);

        var border = new Border
        {
            Child = panel,
            CornerRadius = new CornerRadius(9, 9, 0, 0),
            BorderThickness = new Thickness(1),
            Margin = new Thickness(0, 0, 4, 0),
            MinWidth = 120,
            MaxWidth = 280,
            Cursor = Cursors.Hand,
            Background = (Brush)FindResource("SurfaceBrush"),
            BorderBrush = (Brush)FindResource("BorderBrush"),
        };
        border.MouseLeftButtonDown += (_, args) =>
        {
            args.Handled = true;
            if (args.ClickCount == 2)
            {
                CloseTab(tab);
                return;
            }

            Activate(tab);
        };
        border.ContextMenu = BuildTabMenu(tab);
        border.ContextMenuOpening += (_, _) => border.ContextMenu = BuildTabMenu(tab);

        tab.Header = border;
        tab.TitleText = title;
        tab.Icon = icon;
        tab.CloseButton = close;
        TabStrip.Children.Add(border);
        UpdateTabHeader(tab);
    }

    private ContextMenu BuildTabMenu(BrowserTab tab)
    {
        var menu = new ContextMenu();
        var close = new MenuItem { Header = "Закрыть" };
        close.Click += (_, _) => CloseTab(tab);
        menu.Items.Add(close);

        var closeOthers = new MenuItem { Header = "Закрыть другие" };
        closeOthers.Click += (_, _) =>
        {
            foreach (var other in _tabs.Where(item => item != tab).ToList())
            {
                CloseTab(other);
            }

            Activate(tab);
        };
        menu.Items.Add(closeOthers);

        var duplicate = new MenuItem { Header = "Дублировать" };
        duplicate.Click += (_, _) => _ = CreateTabAsync(tab.View.CoreWebView2?.Source ?? _settings.HomeUrl);
        menu.Items.Add(duplicate);

        menu.Items.Add(new Separator());
        var pin = new MenuItem { Header = tab.Pinned ? "Открепить вкладку" : "Закрепить вкладку" };
        pin.Click += (_, _) => SetPinned(tab, !tab.Pinned);
        menu.Items.Add(pin);

        return menu;
    }

    private void SetPinned(BrowserTab tab, bool pinned)
    {
        tab.Pinned = pinned;
        tab.CloseButton.Visibility = pinned ? Visibility.Collapsed : Visibility.Visible;
        tab.TitleText.Visibility = pinned ? Visibility.Collapsed : Visibility.Visible;
        tab.Header.MinWidth = pinned ? 0 : 140;
        if (pinned)
        {
            TabStrip.Children.Remove(tab.Header);
            TabStrip.Children.Insert(0, tab.Header);
        }

        UpdateTabHeader(tab);
    }

    private void UpdateTabHeader(BrowserTab tab)
    {
        tab.TitleText.Text = tab.Title;
        var active = tab == _active;
        tab.Header.Background = active
            ? (Brush)FindResource("ChromeTabActiveBrush")
            : Brushes.Transparent;
        tab.Header.BorderBrush = Brushes.Transparent;
        tab.TitleText.Foreground = (Brush)FindResource(active ? "ChromeTextBrush" : "ChromeMutedBrush");
    }

    private void Activate(BrowserTab tab, bool focusPage = true, bool showView = true)
    {
        var previous = _active;
        _active = tab;
        if (showView)
        {
            // Переключаем только две вкладки: остальные не трогаем, чтобы не перерисовывать окно.
            if (previous is not null && !ReferenceEquals(previous, tab))
            {
                previous.View.Visibility = Visibility.Collapsed;
            }

            tab.View.Visibility = Visibility.Visible;
        }
        foreach (var item in _tabs)
        {
            UpdateTabHeader(item);
        }

        if (tab.View.CoreWebView2 is { } core)
        {
            AddressBox.Text = DisplayAddress(core.Source);
        }

        UpdateNavState();
        UpdateBookmarkButton();
        RefreshWindowTitle();
        if (focusPage)
        {
            FocusPage();
        }
    }

    /// <summary>Показывает содержимое вкладки (вызывается, когда страница начала отрисовываться).</summary>
    private void ShowTabContent(BrowserTab tab)
    {
        foreach (var item in _tabs)
        {
            item.View.Visibility = ReferenceEquals(item, tab) ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    private void FocusPage()
    {
        if (_active is null || !IsActive)
        {
            return;
        }

        // После переключения вкладки клавиатура должна работать на странице, а не в адресной строке.
        _active.View.Focus();
    }

    private void CloseTab(BrowserTab tab)
    {
        var index = _tabs.IndexOf(tab);
        if (index < 0)
        {
            return;
        }

        _tabs.RemoveAt(index);
        ContentHost.Children.Remove(tab.View);
        TabStrip.Children.Remove(tab.Header);
        try
        {
            tab.View.Dispose();
        }
        catch (Exception exception)
        {
            AppLog.Write("browser", "Не удалось освободить вкладку браузера", exception);
        }

        if (_tabs.Count == 0)
        {
            if (_settings.CloseWithLastTab)
            {
                Close();
            }
            else
            {
                _ = CreateTabAsync(_settings.HomeUrl);
            }

            return;
        }

        if (ReferenceEquals(_active, tab))
        {
            Activate(_tabs[Math.Min(index, _tabs.Count - 1)]);
        }
    }

    private void RefreshWindowTitle()
    {
        Title = string.IsNullOrWhiteSpace(_active?.Title) || _active?.Title == "Новая вкладка"
            ? "Браузер — VieNLink"
            : $"{_active!.Title} — VieNLink";
    }

    private void UpdateNavState()
    {
        var core = _active?.View.CoreWebView2;
        BackButton.IsEnabled = core?.CanGoBack == true;
        ForwardButton.IsEnabled = core?.CanGoForward == true;
        // Как в Яндексе: слева видна только «назад», «вперёд» появляется при наличии истории.
        ForwardButton.Visibility = core?.CanGoForward == true ? Visibility.Visible : Visibility.Collapsed;
    }

    private void UpdateAddressPlaceholder() =>
        AddressPlaceholder.Visibility = string.IsNullOrEmpty(AddressBox.Text) ? Visibility.Visible : Visibility.Collapsed;

    private void SetLoading(bool loading)
    {
        _navigating = loading;
        LoadProgress.Visibility = loading ? Visibility.Visible : Visibility.Collapsed;
        LoadProgress.IsIndeterminate = loading;
        ReloadIcon.Data = loading ? StopGeometry : ReloadGeometry;

        _loadGuard?.Stop();
        if (loading)
        {
            _loadGuard?.Start();
        }

        if (!loading)
        {
            if (!StatusText.Text.StartsWith("Скачивается", StringComparison.Ordinal))
            {
                StatusText.Text = string.Empty;
            }
        }
    }

    private void UpdateZoomText() => ZoomText.Text = $"{Math.Round(_zoom * 100)}%";

    private Uri BuildUri(string input)
    {
        var text = (input ?? string.Empty).Trim();
        if (text.Length == 0)
        {
            return new Uri(_settings.HomeUrl);
        }

        if (Uri.TryCreate(text, UriKind.Absolute, out var absolute) &&
            (absolute.Scheme == Uri.UriSchemeHttp || absolute.Scheme == Uri.UriSchemeHttps ||
             absolute.Scheme == Uri.UriSchemeFile || absolute.Scheme == "about"))
        {
            return absolute;
        }

        // «ya.ru» и «youtube.com/watch?v=…» открываем как сайт, всё остальное — как запрос.
        if (!text.Contains(' ') &&
            Uri.TryCreate("https://" + text, UriKind.Absolute, out var guess) &&
            guess.Host.Contains('.'))
        {
            return guess;
        }

        return new Uri(_settings.BuildSearchUrl(text));
    }

    private void Navigate(string input)
    {
        if (_active?.View.CoreWebView2 is not { } core)
        {
            return;
        }

        core.Navigate(BuildUri(input).ToString());
        FocusPage();
    }

    private void GoBack()
    {
        if (_active?.View.CoreWebView2 is { CanGoBack: true } core)
        {
            core.GoBack();
        }
    }

    private void GoForward()
    {
        if (_active?.View.CoreWebView2 is { CanGoForward: true } core)
        {
            core.GoForward();
        }
    }

    private void ReloadOrStop()
    {
        if (_active?.View.CoreWebView2 is not { } core)
        {
            return;
        }

        if (_navigating)
        {
            core.Stop();
            SetLoading(false);
        }
        else
        {
            core.Reload();
        }
    }

    private void SetZoom(double value)
    {
        _zoom = Math.Clamp(Math.Round(value, 2), 0.35, 3.0);
        foreach (var tab in _tabs)
        {
            tab.View.ZoomFactor = _zoom;
        }

        UpdateZoomText();
        _settings.Zoom = _zoom;
    }

    private void FocusAddress()
    {
        AddressBox.Focus();
        AddressBox.SelectAll();
    }

    private void SetFullScreen(bool on)
    {
        if (on == _fullScreen)
        {
            return;
        }

        _fullScreen = on;
        TabBarBorder.Visibility = on ? Visibility.Collapsed : Visibility.Visible;
        NavBarBorder.Visibility = on ? Visibility.Collapsed : Visibility.Visible;
        BookmarksBarBorder.Visibility = on ? Visibility.Collapsed : (_settings.ShowBookmarksBar ? Visibility.Visible : Visibility.Collapsed);
        FindBar.Visibility = on ? Visibility.Collapsed : FindBar.Visibility;
        StatusBar.Visibility = on ? Visibility.Collapsed : Visibility.Visible;
        if (on)
        {
            _stateBeforeFullScreen = WindowState == WindowState.Maximized ? WindowState.Maximized : WindowState.Normal;
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            WindowState = WindowState.Maximized;
        }
        else
        {
            WindowStyle = WindowStyle.SingleBorderWindow;
            ResizeMode = ResizeMode.CanResize;
            WindowState = _stateBeforeFullScreen;
        }
    }

    private void ToggleBookmark()
    {
        if (_active?.View.CoreWebView2 is not { } core || !IsWebAddress(core.Source))
        {
            return;
        }

        var url = core.Source!;
        var existing = _settings.Bookmarks.FirstOrDefault(
            bookmark => bookmark.Url.Equals(url, StringComparison.OrdinalIgnoreCase));
        if (existing is not null)
        {
            _settings.Bookmarks.Remove(existing);
            StatusText.Text = "Удалено из закладок";
        }
        else
        {
            _settings.Bookmarks.Insert(0, new BrowserBookmark
            {
                Title = string.IsNullOrWhiteSpace(core.DocumentTitle) ? HostOf(url) : core.DocumentTitle.Trim(),
                Url = url,
            });
            StatusText.Text = "Добавлено в закладки";
        }

        _settings.Save();
        WriteStartPage();
        BuildBookmarksBar();
        UpdateBookmarkButton();
    }

    private void UpdateBookmarkButton()
    {
        var url = _active?.View.CoreWebView2?.Source;
        var isBookmarked = IsWebAddress(url) &&
                           _settings.Bookmarks.Any(
                               bookmark => bookmark.Url.Equals(url, StringComparison.OrdinalIgnoreCase));
        StarIcon.Data = isBookmarked ? StarFilledGeometry : StarOutlineGeometry;
        StarIcon.Fill = (Brush)FindResource(isBookmarked ? "AccentBrush" : "MutedStrongBrush");
    }


    private void BuildBookmarksBar()
    {
        BookmarksStrip.Children.Clear();
        foreach (var bookmark in _settings.Bookmarks.Take(40))
        {
            var label = bookmark.Title.Length > 24 ? bookmark.Title[..24] + "…" : bookmark.Title;
            var button = new Button
            {
                Content = label,
                Style = (Style)FindResource("SmallButton"),
                Margin = new Thickness(0, 0, 6, 0),
                ToolTip = bookmark.Url,
            };
            var url = bookmark.Url;
            button.Click += (_, _) => Navigate(url);
            BookmarksStrip.Children.Add(button);
        }

        if (_settings.Bookmarks.Count == 0)
        {
            BookmarksStrip.Children.Add(new TextBlock
            {
                Text = "Панель закладок пуста — ☆ добавит текущую страницу",
                FontSize = 11.5,
                Margin = new Thickness(2, 3, 0, 0),
                Foreground = (Brush)FindResource("MutedBrush"),
            });
        }
    }

    private void UpdateBookmarksBarVisibility()
    {
        BookmarksBarBorder.Visibility = _settings.ShowBookmarksBar ? Visibility.Visible : Visibility.Collapsed;
        if (_settings.ShowBookmarksBar)
        {
            BuildBookmarksBar();
        }
    }

    private void OpenFind()
    {
        FindBar.Visibility = Visibility.Visible;
        FindBox.Focus();
        FindBox.SelectAll();
        if (FindBox.Text.Length > 0)
        {
            RunFind("search", FindBox.Text);
        }
    }

    private void CloseFind()
    {
        FindBar.Visibility = Visibility.Collapsed;
        FindCount.Text = string.Empty;
        RunFind("clear", string.Empty);
        FocusPage();
    }

    private async void RunFind(string mode, string? text)
    {
        if (_active?.View.CoreWebView2 is not { } core)
        {
            return;
        }

        try
        {
            await core.ExecuteScriptAsync(FindScript);
            var expression = mode switch
            {
                "search" => $"window.__vnFind.search({JsonSerializer.Serialize(text ?? string.Empty)})",
                "next" => "window.__vnFind.next(1)",
                "previous" => "window.__vnFind.next(-1)",
                _ => "window.__vnFind.clear()",
            };
            var raw = await core.ExecuteScriptAsync(expression);
            var payload = JsonSerializer.Deserialize<string>(raw);
            if (string.IsNullOrEmpty(payload))
            {
                return;
            }

            using var document = JsonDocument.Parse(payload);
            var count = document.RootElement.GetProperty("count").GetInt32();
            var index = document.RootElement.GetProperty("index").GetInt32();
            FindCount.Text = count == 0
                ? (string.IsNullOrEmpty(_findText) ? string.Empty : "нет совпадений")
                : $"{index} из {count}";
        }
        catch (Exception exception)
        {
            AppLog.Write("browser", "Не удалось выполнить поиск по странице", exception);
        }
    }

    private void OpenDevTools()
    {
        if (!_settings.EnableDevTools)
        {
            StatusText.Text = "Инструменты разработчика выключены в настройках браузера";
            return;
        }

        _active?.View.CoreWebView2?.OpenDevToolsWindow();
    }

    /// <summary>Настройки движка для одной вкладки.</summary>
    private void ApplySettings(CoreWebView2 core)
    {
        core.Settings.IsStatusBarEnabled = _settings.ShowLinkStatusBar;
        core.Settings.AreDevToolsEnabled = _settings.EnableDevTools;
        core.Settings.IsScriptEnabled = _settings.EnableJavaScript;
        core.Settings.IsPasswordAutosaveEnabled = _settings.EnableAutofill;
        core.Settings.IsGeneralAutofillEnabled = _settings.EnableAutofill;
    }

    /// <summary>Применить изменённые настройки ко всем открытым вкладкам.</summary>
    private void ApplySettingsToTabs()
    {
        foreach (var tab in _tabs)
        {
            if (tab.View.CoreWebView2 is { } core)
            {
                ApplySettings(core);
            }

            tab.Icon.Visibility = _settings.ShowFavicons && tab.Icon.Source is not null
                ? Visibility.Visible
                : Visibility.Collapsed;
        }

    }

    /// <summary>Сайт просит камеру, микрофон, геолокацию и т. п. — по умолчанию отказываем.</summary>
    private void OnPermissionRequested(object? sender, CoreWebView2PermissionRequestedEventArgs args)
    {
        if (!_settings.BlockSensitivePermissions)
        {
            return;
        }

        if (args.PermissionKind is CoreWebView2PermissionKind.Camera
            or CoreWebView2PermissionKind.Microphone
            or CoreWebView2PermissionKind.Geolocation
            or CoreWebView2PermissionKind.ClipboardRead
            or CoreWebView2PermissionKind.Notifications)
        {
            args.State = CoreWebView2PermissionState.Deny;
            args.Handled = true;
        }
    }

    private void OnWebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs args)
    {
        string? message;
        try
        {
            message = args.TryGetWebMessageAsString();
        }
        catch (Exception exception)
        {
            AppLog.Write("browser", "Не удалось разобрать сообщение страницы", exception);
            return;
        }

        if (message is null)
        {
            return;
        }

        if (message.StartsWith("vienlink:setting:", StringComparison.Ordinal))
        {
            ApplySetting(message["vienlink:setting:".Length..]);
            return;
        }

        if (message.StartsWith("vienlink:histdel:", StringComparison.Ordinal))
        {
            var url = Uri.UnescapeDataString(message["vienlink:histdel:".Length..]);
            var entry = _history.Items.FirstOrDefault(item => item.Url.Equals(url, StringComparison.OrdinalIgnoreCase));
            if (entry is not null)
            {
                _history.Remove(entry);
                WriteInternalPages();
                ReloadInternalPages();
            }

            return;
        }

        if (message == "vienlink:histclear")
        {
            _history.Clear();
            WriteInternalPages();
            ReloadInternalPages();
            return;
        }

        if (message.StartsWith("vienlink:addtile:", StringComparison.Ordinal))
        {
            AddStartPageTile(message["vienlink:addtile:".Length..]);
            return;
        }

        if (message.StartsWith("vienlink:removetile:", StringComparison.Ordinal))
        {
            RemoveStartPageTile(message["vienlink:removetile:".Length..]);
            return;
        }

        if (message.StartsWith("vienlink:renametile:", StringComparison.Ordinal))
        {
            RenameStartPageTile(message["vienlink:renametile:".Length..]);
            return;
        }

        if (message.StartsWith("vienlink:search:", StringComparison.Ordinal))
        {
            SearchFromStartPage(message["vienlink:search:".Length..]);
            return;
        }

        if (message == "vienlink:vpn-connect")
        {
            _ = ConnectVpnFromPageAsync();
            return;
        }

        if (!message.StartsWith("vienlink:", StringComparison.Ordinal))
        {
            return;
        }

        ExecuteShortcut(message["vienlink:".Length..]);
    }

    private void SearchFromStartPage(string query)
    {
        var text = (query ?? string.Empty).Trim();
        if (text.Length == 0 || _active?.View.CoreWebView2 is not { } core)
        {
            return;
        }

        core.Navigate(_settings.BuildSearchUrl(text));
    }

    private void ExecuteShortcut(string command)
    {
        switch (command)
        {
            case "newtab":
                _ = CreateTabAsync(_settings.HomeUrl);
                break;
            case "closetab":
                if (_active is { } tab)
                {
                    CloseTab(tab);
                }

                break;
            case "address":
                FocusAddress();
                break;
            case "bookmark":
                ToggleBookmark();
                break;
            case "find":
                OpenFind();
                break;
            case "history":
                OpenHistory();
                break;
            case "downloads":
                OpenDownloads();
                break;
            case "vpnp":
                ToggleVpnPanel();
                break;
            case "reload":
                ReloadOrStop();
                break;
            case "zoomin":
                SetZoom(_zoom + 0.1);
                break;
            case "zoomout":
                SetZoom(_zoom - 0.1);
                break;
            case "zoomreset":
                SetZoom(1.0);
                break;
            case "fullscreen":
                SetFullScreen(!_fullScreen);
                break;
            case "devtools":
                OpenDevTools();
                break;
            case "back":
                GoBack();
                break;
            case "forward":
                GoForward();
                break;
        }
    }

    private void OnWindowPreviewKeyDown(object sender, KeyEventArgs e)
    {
        var control = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
        if (control && e.Key == Key.L)
        {
            FocusAddress();
            e.Handled = true;
        }
        else if (control && e.Key == Key.T)
        {
            _ = CreateTabAsync(_settings.HomeUrl);
            e.Handled = true;
        }
        else if (control && e.Key == Key.W)
        {
            if (_active is { } tab)
            {
                CloseTab(tab);
            }

            e.Handled = true;
        }
        else if (control && e.Key == Key.F)
        {
            OpenFind();
            e.Handled = true;
        }
        else if (control && e.Key == Key.H)
        {
            OpenHistory();
            e.Handled = true;
        }
        else if (control && e.Key == Key.J)
        {
            OpenDownloads();
            e.Handled = true;
        }
        else if (control && (Keyboard.Modifiers & ModifierKeys.Shift) != 0 && e.Key == Key.V)
        {
            ToggleVpnPanel();
            e.Handled = true;
        }
        else if (e.Key == Key.F5)
        {
            ReloadOrStop();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            if (_fullScreen)
            {
                SetFullScreen(false);
            }
            else if (FindBar.Visibility == Visibility.Visible)
            {
                CloseFind();
            }

            e.Handled = true;
        }
    }

    private void OnAddressGotFocus(object sender, KeyboardFocusChangedEventArgs e) => AddressBox.SelectAll();

    private void OnAddressKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            Navigate(AddressBox.Text);
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            AddressBox.Text = DisplayAddress(_active?.View.CoreWebView2?.Source);
            FocusPage();
            e.Handled = true;
        }
    }

    private void OnNewTabClick(object sender, RoutedEventArgs e) => _ = CreateTabAsync(_settings.HomeUrl);

    private void OnBackClick(object sender, RoutedEventArgs e) => GoBack();

    private void OnForwardClick(object sender, RoutedEventArgs e) => GoForward();

    private void OnReloadClick(object sender, RoutedEventArgs e) => ReloadOrStop();

    private void OnHomeClick(object sender, RoutedEventArgs e) => Navigate(_settings.HomeUrl);

    private void OnBookmarkClick(object sender, RoutedEventArgs e) => ToggleBookmark();


    private void OnZoomInClick(object sender, RoutedEventArgs e) => SetZoom(_zoom + 0.1);

    private void OnZoomOutClick(object sender, RoutedEventArgs e) => SetZoom(_zoom - 0.1);

    private void OnZoomResetClick(object sender, RoutedEventArgs e) => SetZoom(1.0);

    private void OnFindClick(object sender, RoutedEventArgs e) => OpenFind();

    private void OnFindCloseClick(object sender, RoutedEventArgs e) => CloseFind();

    private void OnFindNextClick(object sender, RoutedEventArgs e) => RunFind("next", null);

    private void OnFindPreviousClick(object sender, RoutedEventArgs e) => RunFind("previous", null);

    private void OnFindBoxKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            RunFind((Keyboard.Modifiers & ModifierKeys.Shift) != 0 ? "previous" : "next", null);
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            CloseFind();
            e.Handled = true;
        }
    }

    private void OnHistoryClick(object sender, RoutedEventArgs e) => OpenHistoryPage();

    private void OnDownloadsClick(object sender, RoutedEventArgs e) => OpenDownloads();

    private void OnDownloadsIconClick(object sender, RoutedEventArgs e) => OpenDownloads();

    private void OpenHistory()
    {
        var window = new BrowserHistoryWindow(_history, url => _ = CreateTabAsync(url)) { Owner = this };
        window.ShowDialog();
    }

    private void OpenDownloads()
    {
        var window = new BrowserDownloadsWindow { Owner = this };
        window.ShowDialog();
    }



    private bool _pendingTileEdit;

    /// <summary>Включает режим правки плиток на стартовой странице (пункт меню ☰).</summary>
    private void EditStartPageTiles()
    {
        var tab = _tabs.FirstOrDefault(item => string.Equals(
            item.View.CoreWebView2?.Source, BrowserSettings.StartPageUrl, StringComparison.OrdinalIgnoreCase));
        if (tab?.View.CoreWebView2 is { } core)
        {
            Activate(tab);
            _ = core.ExecuteScriptAsync("window.__vnEdit && window.__vnEdit()");
            return;
        }

        _pendingTileEdit = true;
        _ = CreateTabAsync(BrowserSettings.StartPageUrl);
    }

    private void OnMenuButtonClick(object sender, RoutedEventArgs e) => ToggleBrowserMenu();

    private void OnDevToolsClick(object sender, RoutedEventArgs e) => OpenDevTools();

    private void OnSettingsClick(object sender, RoutedEventArgs e) => OpenSettingsPage();

    private async void OnClearDataClick(object sender, RoutedEventArgs e)
    {
        var answer = MessageBox.Show(this,
            "Очистить кэш, cookie и данные сайтов? Придётся заново войти на сайтах.",
            "Браузер", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (answer != MessageBoxResult.Yes)
        {
            return;
        }

        try
        {
            if (_active?.View.CoreWebView2 is { } core)
            {
                await core.Profile.ClearBrowsingDataAsync(CoreWebView2BrowsingDataKinds.AllProfile);
            }

            StatusText.Text = "Кэш и cookie очищены";
        }
        catch (Exception exception)
        {
            AppLog.Write("browser", "Не удалось очистить данные браузера", exception);
            MessageBox.Show(this, "Не удалось очистить данные: " + exception.Message,
                "Браузер", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void OnAboutClick(object sender, RoutedEventArgs e)
    {
        string version;
        try
        {
            version = CoreWebView2Environment.GetAvailableBrowserVersionString() ?? "неизвестна";
        }
        catch (Exception exception)
        {
            AppLog.Write("browser", "Не удалось определить версию движка", exception);
            version = "неизвестна";
        }

        var browserVersion = typeof(BrowserWindow).Assembly.GetName().Version?.ToString(3) ?? "1.0.0";
        MessageBox.Show(this,
            $"VieNLink Браузер {browserVersion}\nДвижок: WebView2 (Microsoft Edge) {version}\n\n" +
            "Страницы открываются через системный прокси Windows: при подключённом сервере " +
            "трафик идёт через него, в режимах обхода — напрямую или через TUN.",
            "О браузере", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    /// <summary>Добавляет плитку на стартовую страницу (своя закладка).</summary>
    private void AddStartPageTile(string value)
    {
        var text = (value ?? string.Empty).Trim();
        if (text.Length == 0)
        {
            return;
        }

        if (!text.Contains("://", StringComparison.Ordinal))
        {
            text = "https://" + text;
        }

        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) ||
            !uri.Host.Contains('.'))
        {
            StatusText.Text = "Это не похоже на адрес сайта";
            return;
        }

        var url = uri.ToString();
        if (_settings.Bookmarks.Any(bookmark => bookmark.Url.Equals(url, StringComparison.OrdinalIgnoreCase)))
        {
            StatusText.Text = "Такая плитка уже есть";
            return;
        }

        _settings.Bookmarks.Insert(0, new BrowserBookmark { Title = uri.Host, Url = url });
        _settings.Save();
        WriteStartPage();
        ApplyToStartPages(
            "window.__vnAddTile && window.__vnAddTile(" +
            JsonSerializer.Serialize(url) + ", " + JsonSerializer.Serialize(uri.Host) + ", " +
            JsonSerializer.Serialize(FaviconUrl(url)) + ")");
        StatusText.Text = "Плитка добавлена";
    }

    private void RemoveStartPageTile(string url)
    {
        var bookmark = _settings.Bookmarks.FirstOrDefault(
            item => item.Url.Equals(url, StringComparison.OrdinalIgnoreCase));
        if (bookmark is null)
        {
            return;
        }

        _settings.Bookmarks.Remove(bookmark);
        _settings.Save();
        WriteStartPage();
        ApplyToStartPages("window.__vnRemoveTile && window.__vnRemoveTile(" +
                          JsonSerializer.Serialize(url) + ")");
    }

    private void RenameStartPageTile(string payload)
    {
        var separator = payload.IndexOf('|');
        if (separator <= 0)
        {
            return;
        }

        var url = payload[..separator];
        var title = payload[(separator + 1)..].Trim();
        var bookmark = _settings.Bookmarks.FirstOrDefault(
            item => item.Url.Equals(url, StringComparison.OrdinalIgnoreCase));
        if (bookmark is null || title.Length == 0)
        {
            return;
        }

        bookmark.Title = title.Length > 40 ? title[..40] : title;
        _settings.Save();
        WriteStartPage();
        ApplyToStartPages("window.__vnRenameTile && window.__vnRenameTile(" +
                          JsonSerializer.Serialize(url) + ", " +
                          JsonSerializer.Serialize(bookmark.Title) + ")");
    }

    /// <summary>Выполняет скрипт на открытых стартовых страницах (без перезагрузки вкладки).</summary>
    private void ApplyToStartPages(string script)
    {
        foreach (var tab in _tabs)
        {
            var core = tab.View.CoreWebView2;
            if (core is not null &&
                string.Equals(core.Source, BrowserSettings.StartPageUrl, StringComparison.OrdinalIgnoreCase))
            {
                _ = core.ExecuteScriptAsync(script);
            }
        }
    }

    private void ReloadStartPages()
    {
        foreach (var tab in _tabs)
        {
            var core = tab.View.CoreWebView2;
            if (core is not null && IsLocalPage(core.Source))
            {
                core.Reload();
            }
        }
    }

    /// <summary>Собирает локальную стартовую страницу: поиск Google и плитки закладок.</summary>
    private void WriteStartPage()
    {
        if (string.IsNullOrEmpty(_startFolder))
        {
            return;
        }

        var tiles = new System.Text.StringBuilder();
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void AddTile(string title, string url, bool custom = false)
        {
            if (!IsWebAddress(url) || !used.Add(url))
            {
                return;
            }

            tiles.Append("<a class=\"tile\" href=\"").Append(HtmlEscape(url))
                .Append("\" title=\"").Append(HtmlEscape(title)).Append('"');
            if (custom)
            {
                tiles.Append(" data-custom=\"1\" data-url=\"").Append(HtmlEscape(url))
                    .Append("\" data-title=\"").Append(HtmlEscape(title)).Append('"');
            }

            tiles.Append("><span class=\"icon\"><img src=\"").Append(HtmlEscape(FaviconUrl(url)))
                .Append("\" alt=\"\" onload=\"this.parentNode.classList.add('loaded')\" onerror=\"this.remove()\">")
                .Append("<span class=\"letter\">").Append(HtmlEscape(Initial(title, url)))
                .Append("</span></span><span class=\"name\">").Append(HtmlEscape(title)).Append("</span>");
            if (custom)
            {
                tiles.Append("<span class=\"tile-actions\">")
                    .Append("<button class=\"mini\" type=\"button\" data-act=\"rename\" title=\"Переименовать\">✎</button>")
                    .Append("<button class=\"mini\" type=\"button\" data-act=\"remove\" title=\"Удалить\">✕</button>")
                    .Append("</span>");
            }

            tiles.Append("</a>");
        }

        foreach (var service in new (string Title, string Url)[]
                 {
                     ("Google", "https://www.google.com/"),
                     ("YouTube", "https://www.youtube.com/"),
                     ("Telegram", "https://web.telegram.org/"),
                     ("Discord", "https://discord.com/app"),
                     ("GitHub", "https://github.com/"),
                 })
        {
            AddTile(service.Title, service.Url);
        }

        foreach (var bookmark in _settings.Bookmarks.Take(8))
        {
            var title = string.IsNullOrWhiteSpace(bookmark.Title) ? HostOf(bookmark.Url) : bookmark.Title;
            AddTile(title, bookmark.Url, custom: true);
        }

        tiles.Append(
            "<div class=\"tile add\" onclick=\"toggleEdit()\" title=\"Добавить сайт\">+</div>");

        try
        {
            File.WriteAllText(
                Path.Combine(_startFolder, "start.html"),
                StartPageTemplate.Replace("{{TILES}}", tiles.ToString()),
                new System.Text.UTF8Encoding(false));
            File.WriteAllText(
                Path.Combine(_startFolder, "vpn-required.html"),
                VpnRequiredTemplate,
                new System.Text.UTF8Encoding(false));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            AppLog.Write("browser", "Не удалось записать стартовую страницу", exception);
        }
    }

    /// <summary>Перерисовывает внутренние страницы «Настройки» и «История».</summary>
    private void WriteInternalPages()
    {
        if (string.IsNullOrEmpty(_startFolder))
        {
            return;
        }

        try
        {
            File.WriteAllText(Path.Combine(_startFolder, "settings.html"),
                InternalPages.BuildSettings(_settings), new System.Text.UTF8Encoding(false));
            File.WriteAllText(Path.Combine(_startFolder, "history.html"),
                InternalPages.BuildHistory(_history), new System.Text.UTF8Encoding(false));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            AppLog.Write("browser", "Не удалось записать внутренние страницы", exception);
        }
    }

    private void OpenSettingsPage()
    {
        WriteInternalPages();
        _ = CreateTabAsync(InternalPages.SettingsUrl);
    }

    private void OpenHistoryPage()
    {
        WriteInternalPages();
        _ = CreateTabAsync(InternalPages.HistoryUrl);
    }

    private void ReloadInternalPages()
    {
        foreach (var tab in _tabs)
        {
            var core = tab.View.CoreWebView2;
            if (core is null || string.IsNullOrWhiteSpace(core.Source))
            {
                continue;
            }

            if (core.Source.StartsWith(InternalPages.SettingsUrl, StringComparison.OrdinalIgnoreCase) ||
                core.Source.StartsWith(InternalPages.HistoryUrl, StringComparison.OrdinalIgnoreCase))
            {
                core.Reload();
            }
        }
    }

    /// <summary>Применяет настройку, присланную со страницы настроек.</summary>
    private void ApplySetting(string payload)
    {
        var firstColon = payload.IndexOf(':');
        if (firstColon <= 0)
        {
            return;
        }

        var key = payload[..firstColon];
        var raw = payload[(firstColon + 1)..];

        switch (key)
        {
            case "home":
                _settings.HomePage = Uri.UnescapeDataString(raw).Trim();
                WriteStartPage();
                break;
            case "zoom":
                if (double.TryParse(raw, System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out var zoom))
                {
                    SetZoom(zoom);
                }

                break;
            default:
            {
                var value = raw == "1";
                switch (key)
                {
                    case "restore": _settings.RestoreTabs = value; break;
                    case "clearstart": _settings.ClearDataOnStart = value; break;
                    case "bookmarksbar": _settings.ShowBookmarksBar = value; break;
                    case "favicons": _settings.ShowFavicons = value; break;
                    case "linkstatus": _settings.ShowLinkStatusBar = value; break;
                    case "savehistory": _settings.SaveHistory = value; break;
                    case "requirevpn": _settings.RequireVpn = value; break;
                    case "blockperm": _settings.BlockSensitivePermissions = value; break;
                    case "autofill": _settings.EnableAutofill = value; break;
                    case "js": _settings.EnableJavaScript = value; break;
                    case "devtools": _settings.EnableDevTools = value; break;
                    case "closelast": _settings.CloseWithLastTab = value; break;
                    default: return;
                }

                break;
            }
        }

        _settings.Save();
        UpdateBookmarksBarVisibility();
        ApplySettingsToTabs();
        StatusText.Text = "Настройка сохранена";
    }

    /// <summary>Кладёт значок браузера рядом со стартовой страницей (отдаётся как browser-icon.png).</summary>
    private static void WriteStartPageIcon(string folder)
    {
        try
        {
            var resource = Application.GetResourceStream(new Uri("Assets/browser-icon.png", UriKind.Relative));
            if (resource is null)
            {
                return;
            }

            using var stream = resource.Stream;
            using var file = File.Create(Path.Combine(folder, "browser-icon.png"));
            stream.CopyTo(file);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
                                             or ArgumentException)
        {
            AppLog.Write("browser", "Не удалось подготовить значок стартовой страницы", exception);
        }
    }

    private static string HtmlEscape(string value) => System.Net.WebUtility.HtmlEncode(value ?? string.Empty);

    private static string FaviconUrl(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri)
            ? $"{uri.Scheme}://{uri.Host}/favicon.ico"
            : string.Empty;

    private static string Initial(string title, string url)
    {
        var symbol = title.FirstOrDefault(char.IsLetterOrDigit);
        if (symbol == default)
        {
            symbol = HostOf(url).FirstOrDefault(char.IsLetterOrDigit);
        }

        return symbol == default ? "•" : char.ToUpperInvariant(symbol).ToString();
    }

    // --- Панель VPN ---

    private void OnVpnStateChanged(object? sender, EventArgs e) => Dispatcher.BeginInvoke(RefreshVpnPanel);

    private void RefreshVpnPanel()
    {
        var connected = VpnClient.Connected;
        VpnStateText.Text = connected ? "Подключён" : VpnClient.Held ? "Трафик заблокирован" : "Не подключён";
        VpnStateText.Foreground = (Brush)FindResource(
            connected ? "AccentBrush" : VpnClient.Held ? "DangerBrush" : "TextBrush");
        VpnServerText.Text = VpnClient.Available ? VpnClient.StatusLabel : "Окно VieNLink недоступно";

        VpnConnectButton.Content = connected ? "Отключиться" : "Подключиться";
        var canCommand = VpnClient.Available && !VpnClient.Busy;
        VpnConnectButton.IsEnabled = canCommand;
        VpnSwitchButton.IsEnabled = canCommand;
        VpnZapretButton.IsEnabled = canCommand;
        VpnDesyncButton.IsEnabled = canCommand;
        VpnOnlyCheck.IsChecked = _settings.RequireVpn;
        VpnBypassText.Text = VpnClient.ZapretRunning || VpnClient.DesyncRunning
            ? "Включён: " + VpnClient.ActiveMode
            : "Выключен — сайты открываются напрямую или через сервер";

        var vpnState = connected
            ? (VpnClient.DelayMs > 0 ? $"VPN · {VpnClient.DelayMs} мс" : "VPN · вкл")
            : VpnClient.Available
                ? "VPN · выкл"
                : "VPN не запущен";
        VpnIcon.Fill = (Brush)FindResource(connected ? "AccentBrush" : "ChromeMutedBrush");
        VpnStatusButton.ToolTip = "Панель VPN · " + vpnState;

        // Если VPN не запущен — предлагаем установить, обновить или запустить.
        var installed = VpnInstaller.FindInstalledExe();
        var outdated = VpnInstaller.IsOutdated(installed);
        VpnInstallButton.Visibility = VpnClient.Available ? Visibility.Collapsed : Visibility.Visible;
        VpnInstallButton.Content = outdated
            ? "Обновить VPN"
            : installed is null
                ? "Установить VPN"
                : "Запустить VPN";
        VpnConnectionHintText.Text = VpnClient.Available
            ? string.Empty
            : installed is null
                ? "VPN не установлен — браузер может поставить его."
                : outdated
                    ? "Версия VPN старее нужной — обновите, чтобы панель работала."
                    : "VPN установлен, но не запущен.";

        UpdateStartPageVpnState();
    }

    private void BuildVpnServicesPanel()
    {
        var services = new (string Title, string Url)[]
        {
            ("YouTube", "https://www.youtube.com/"),
            ("Telegram Web", "https://web.telegram.org/"),
            ("Discord", "https://discord.com/app"),
            ("GitHub", "https://github.com/"),
        };

        foreach (var service in services)
        {
            var button = new Button
            {
                Content = service.Title,
                Style = (Style)FindResource("SmallButton"),
                Margin = new Thickness(0, 0, 6, 6),
            };
            var url = service.Url;
            button.Click += (_, _) => Navigate(url);
            VpnServicesPanel.Children.Add(button);
        }
    }

    private void ToggleVpnPanel()
    {
        _vpnPanelOpen = !_vpnPanelOpen;
        VpnPanel.Visibility = _vpnPanelOpen ? Visibility.Visible : Visibility.Collapsed;
        if (_vpnPanelOpen)
        {
            RefreshVpnPanel();
        }
    }

    /// <summary>Панель VPN можно закрепить у левого или правого края окна.</summary>
    private void ApplyVpnDock()
    {
        var left = _settings.VpnPanelDockedLeft;
        Grid.SetColumn(ContentRoot, left ? 1 : 0);
        Grid.SetColumn(VpnPanel, left ? 0 : 1);
        ContentColumn.Width = left ? GridLength.Auto : new GridLength(1, GridUnitType.Star);
        VpnPanelColumn.Width = left ? new GridLength(1, GridUnitType.Star) : GridLength.Auto;
        VpnPanel.BorderThickness = left ? new Thickness(0, 0, 1, 0) : new Thickness(1, 0, 0, 0);
    }

    private string _lastStartPageVpnFlag = string.Empty;

    private void UpdateStartPageVpnState()
    {
        var flag = VpnClient.Connected ? "true" : "false";
        if (flag == _lastStartPageVpnFlag)
        {
            return;
        }

        _lastStartPageVpnFlag = flag;
        foreach (var tab in _tabs)
        {
            var core = tab.View.CoreWebView2;
            if (core is null || !IsLocalPage(core.Source))
            {
                continue;
            }

            _ = core.ExecuteScriptAsync($"window.__vnVpn && window.__vnVpn({flag})");
        }
    }

    private async Task ConnectVpnFromPageAsync()
    {
        try
        {
            StatusText.Text = "Подключаюсь к VPN…";
            if (await VpnClient.ConnectAsync() && !string.IsNullOrWhiteSpace(_blockedUrl))
            {
                var target = _blockedUrl;
                _blockedUrl = null;
                Navigate(target);
            }
        }
        catch (Exception exception)
        {
            AppLog.Write("browser", "Подключение VPN со страницы не удалось", exception);
        }
        finally
        {
            RefreshVpnPanel();
        }
    }

    private void OnVpnStatusClick(object sender, RoutedEventArgs e) => ToggleVpnPanel();

    private void OnVpnMenuClick(object sender, RoutedEventArgs e) => ToggleVpnPanel();

    private void OnVpnPanelCloseClick(object sender, RoutedEventArgs e)
    {
        _vpnPanelOpen = false;
        VpnPanel.Visibility = Visibility.Collapsed;
    }

    private void OnVpnDockClick(object sender, RoutedEventArgs e)
    {
        _settings.VpnPanelDockedLeft = !_settings.VpnPanelDockedLeft;
        _settings.Save();
        ApplyVpnDock();
    }

    private async void OnVpnConnectClick(object sender, RoutedEventArgs e)
    {
        try
        {
            if (VpnClient.Connected)
            {
                await VpnClient.DisconnectAsync();
            }
            else
            {
                await VpnClient.ConnectAsync();
            }
        }
        catch (Exception exception)
        {
            AppLog.Write("browser", "Управление VPN из браузера не удалось", exception);
        }
        finally
        {
            RefreshVpnPanel();
        }
    }

    private async void OnVpnSwitchClick(object sender, RoutedEventArgs e)
    {
        try
        {
            await VpnClient.SwitchServerAsync();
        }
        catch (Exception exception)
        {
            AppLog.Write("browser", "Смена сервера из браузера не удалась", exception);
        }
        finally
        {
            RefreshVpnPanel();
        }
    }

    private void OnVpnOnlyChanged(object sender, RoutedEventArgs e)
    {
        _settings.RequireVpn = VpnOnlyCheck.IsChecked == true;
        _settings.Save();
        RefreshVpnPanel();
    }

    private async void OnVpnZapretClick(object sender, RoutedEventArgs e)
    {
        try
        {
            await VpnClient.ToggleZapretAsync();
        }
        catch (Exception exception)
        {
            AppLog.Write("browser", "Переключение zapret из браузера не удалось", exception);
        }
        finally
        {
            RefreshVpnPanel();
        }
    }

    private async void OnVpnDesyncClick(object sender, RoutedEventArgs e)
    {
        try
        {
            await VpnClient.ToggleDesyncAsync();
        }
        catch (Exception exception)
        {
            AppLog.Write("browser", "Переключение обхода из браузера не удалось", exception);
        }
        finally
        {
            RefreshVpnPanel();
        }
    }

    private void OnVpnOpenMainClick(object sender, RoutedEventArgs e) => VpnClient.OpenMain();

    private async void OnVpnInstallClick(object sender, RoutedEventArgs e)
    {
        var exe = VpnInstaller.FindInstalledExe();
        if (VpnInstaller.IsOutdated(exe) || (exe is not null && VpnInstaller.IsProcessRunning))
        {
            // Старая версия или запущенная без канала — ставим актуальную.
            await InstallVpnAsync();
            RefreshVpnPanel();
            return;
        }

        if (exe is not null)
        {
            VpnInstaller.Launch(exe);
            await Task.Delay(2000);
            await VpnClient.RefreshAsync();
            RefreshVpnPanel();
            return;
        }

        await InstallVpnAsync();
        RefreshVpnPanel();
    }

    // --- Своя рамка окна (как в Яндексе): вкладки в заголовке ---

    private void OnTitleBarMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
        {
            ToggleMaximize();
            e.Handled = true;
            return;
        }

        if (e.LeftButton != MouseButtonState.Pressed)
        {
            return;
        }

        try
        {
            DragMove();
        }
        catch (InvalidOperationException)
        {
            // Окно уже не в состоянии перетаскивания — ничего страшного.
        }
    }

    private void ToggleMaximize() =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void OnMinimizeClick(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void OnMaximizeClick(object sender, RoutedEventArgs e) => ToggleMaximize();

    private void OnCloseWindowClick(object sender, RoutedEventArgs e) => Close();

    private void OnWindowStateChanged(object sender, EventArgs e)
    {
        MaximizeButton.Content = WindowState == WindowState.Maximized ? "\uE923" : "\uE922";
        // Развёрнутое окно — без рамки, обычное — с полосой под растягивание.
        RootGrid.Margin = WindowState == WindowState.Maximized ? new Thickness(0) : new Thickness(4);
    }

    private void OnWindowClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        VpnClient.StateChanged -= OnVpnStateChanged;        if (_settings.RestoreTabs)
        {
            _settings.Tabs = _tabs
                .Select(tab => tab.View.CoreWebView2?.Source)
                .Where(IsWebAddress)
                .Select(url => url!)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        else
        {
            _settings.Tabs.Clear();
        }

        _settings.Zoom = _zoom;
        _settings.Save();

        foreach (var tab in _tabs.ToList())
        {
            try
            {
                tab.View.Dispose();
            }
            catch (Exception exception)
            {
                AppLog.Write("browser", "Не удалось освободить вкладку при закрытии", exception);
            }
        }
    }
}