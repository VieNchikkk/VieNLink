using Android.App;
using Android.Content;
using Android.OS;
using Android.Views;
using Android.Widget;

namespace VieNLink.Mobile;

/// <summary>
/// Отсев: рабочие серверы, переставшие работать. Их можно перепроверить,
/// вернуть вручную или удалить из отдельного файла.
/// </summary>
#if DEBUG
[Activity(Label = "Отсев — Перепроверка", Theme = "@style/Theme.VieNLink", Exported = true)]
#else
[Activity(Label = "Отсев — Перепроверка", Theme = "@style/Theme.VieNLink")]
#endif
public sealed class RejectedActivity : BaseActivity
{
    private const int VpnRequestCode = 4102;
    private const int StatusIntervalMs = 1000;

    private LinearLayout? _list;
    private LinearLayout? _filterRow;
    private TextView? _totalText;
    private TextView? _youtubeCount;
    private TextView? _telegramCount;
    private TextView? _discordCount;
    private TextView? _universalCount;
    private TextView? _status;
    private LinearLayout? _connCard;
    private ScrollView? _scroll;
    private TextView? _connDot;
    private TextView? _connState;
    private TextView? _connNode;
    private TextView? _connDetails;
    private TextView? _connExit;
    private TextView? _connTraffic;
    private TextView? _connHint;
    private TextView? _disconnect;
    private string? _service;
    private int _selectedIndex;
    private bool _busy;
    private CancellationTokenSource? _cts;
    private string? _pendingConfig;
    private ProxyNode? _pendingNode;
    private ProxyNode? _connectedNode;
    private TunnelState? _lastConnState;
    private Handler? _tickHandler;
    private Action? _tick;
    private CancellationTokenSource? _trafficCts;
    private long _downBytes;
    private long _upBytes;
    private DateTime _lastTrafficUi;
    private bool _exitBusy;
    private DateTime _lastExitAttempt = DateTime.MinValue;
    private bool _resumed;
    private string? _autoReturnedNodeId;
    private bool _connectedReturned;

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        BuildUi();
        Reload();
    }

    protected override void OnResume()
    {
        base.OnResume();
        _resumed = true;
        Reload();
        // Состояние запоминаем до первого обновления: при открытии экрана
        // не нужно заново сообщать, что VPN уже подключён.
        _lastConnState = TunnelService.State;
        RefreshConnectionStatus();

        _tickHandler = new Handler(Looper.MainLooper!);
        _tick = () =>
        {
            RefreshConnectionStatus();
            if (_tick is { } tick)
            {
                _tickHandler?.PostDelayed(tick, StatusIntervalMs);
            }
        };
        _tickHandler.PostDelayed(_tick, StatusIntervalMs);
    }

    protected override void OnPause()
    {
        _resumed = false;
        if (_tickHandler is not null && _tick is not null)
        {
            _tickHandler.RemoveCallbacks(_tick);
        }

        _tickHandler = null;
        _tick = null;
        StopTrafficMonitor();
        base.OnPause();
    }

    private void BuildUi()
    {
        var page = ColumnPage();

        var top = Ui.Row(this);
        top.AddView(Ui.Pill(this, P, "Назад", UiButtonKind.Ghost, Finish));
        var title = Ui.Title(this, "Отсев — Перепроверка", P);
        title.SetPadding(Ui.Dp(this, 10), 0, 0, 0);
        top.AddView(title, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f));
        page.AddView(top);

        // Сводка: большое число и разбивка по сервисам.
        var summaryCard = Ui.Column(this);
        summaryCard.SetPadding(Ui.Dp(this, 14), Ui.Dp(this, 12), Ui.Dp(this, 14), Ui.Dp(this, 12));
        summaryCard.Background = Ui.Rounded(P.Card, 16, this, P.Border);
        var totalRow = Ui.Row(this);
        var totalColumn = Ui.Column(this);
        totalColumn.AddView(Ui.Text(this, "В ОТСЕВЕ", 10.5f, P.Muted, bold: true));
        _totalText = Ui.Text(this, "0", 34f, P.Accent, bold: true);
        _totalText.SetPadding(0, Ui.Dp(this, 2), 0, 0);
        totalColumn.AddView(_totalText);
        totalColumn.AddView(Ui.Hint(this, "серверов ждут перепроверки", P));
        totalRow.AddView(totalColumn, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f));
        summaryCard.AddView(totalRow);

        var metrics = Ui.Row(this);
        metrics.SetPadding(0, Ui.Dp(this, 10), 0, 0);
        _youtubeCount = AddMetric(metrics, "YouTube", P.YouTube);
        _telegramCount = AddMetric(metrics, "Telegram", P.Telegram);
        _discordCount = AddMetric(metrics, "Discord", P.Discord);
        _universalCount = AddMetric(metrics, "Универс.", P.Accent);
        summaryCard.AddView(metrics);
        Ui.Add(page, summaryCard, 12);

        page.AddView(Ui.Banner(P, this, P.Accent, "Что это",
            "Сюда попадают серверы, которые уже были рабочими, но перестали открывать сайт. " +
            "Перепроверка запускается кнопкой «Перепроверить всё» и автоматически при " +
            "запуске приложения (через отдельный процесс, не трогая VPN): серверы, которые " +
            "открылись, возвращаются в рабочие, остальные остаются в отсеве."));

        page.AddView(Ui.Section(this, "Фильтр", P));
        var filterScroll = new HorizontalScrollView(this)
        {
            HorizontalScrollBarEnabled = false,
        };
        _filterRow = Ui.Row(this);
        filterScroll.AddView(_filterRow, new FrameLayout.LayoutParams(
            ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent));
        page.AddView(filterScroll);
        BuildFilterChips();

        page.AddView(Ui.Section(this, "Действия", P));
        Ui.Add(page, Ui.Pill(this, P, "Перепроверить всё", UiButtonKind.Accent,
            () => _ = RecheckAllAsync(), Resource.Drawable.ic_refresh), 0);

        var actions = Ui.Row(this);
        var actionsLayout = new LinearLayout.LayoutParams(
            ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent);
        actionsLayout.TopMargin = Ui.Dp(this, 8);
        actions.AddView(Ui.Pill(this, P, "Подключение", UiButtonKind.Primary, ConnectSelected,
                Resource.Drawable.ic_power),
            new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f));
        actions.AddView(new Space(this), new LinearLayout.LayoutParams(Ui.Dp(this, 8), 1));
        actions.AddView(Ui.Pill(this, P, "Вернуть (В рабочие)", UiButtonKind.Accent, ReturnSelected,
                Resource.Drawable.ic_check),
            new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f));
        page.AddView(actions, actionsLayout);

        var actions2 = Ui.Row(this);
        var actions2Layout = new LinearLayout.LayoutParams(
            ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent);
        actions2Layout.TopMargin = Ui.Dp(this, 8);
        actions2.AddView(Ui.Pill(this, P, "Следующий", UiButtonKind.Ghost, Next),
            new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f));
        actions2.AddView(new Space(this), new LinearLayout.LayoutParams(Ui.Dp(this, 8), 1));
        actions2.AddView(Ui.Pill(this, P, "Отмена", UiButtonKind.Danger, Cancel),
            new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f));
        page.AddView(actions2, actions2Layout);

        BuildConnectionCard(page);

        _status = Ui.Hint(this, "Серверы из отсева можно перепроверить вручную.", P);
        _status.SetPadding(0, Ui.Dp(this, 10), 0, 0);
        page.AddView(_status);

        page.AddView(Ui.Section(this, "Серверы в отсеве", P));
        _list = Ui.Column(this);
        page.AddView(_list);
        SetPage(page);
        _scroll = page.Parent as ScrollView;
    }

    /// <summary>
    /// Полная сводка подключения: состояние, сервер, адрес и протокол, выход, трафик
    /// и время сессии. Обновляется раз в секунду, пока экран открыт.
    /// </summary>
    private void BuildConnectionCard(LinearLayout page)
    {
        page.AddView(Ui.Section(this, "Состояние подключения", P));
        var card = Ui.HeaderCard(P, this);
        _connCard = card;

        var stateRow = Ui.Row(this);
        _connDot = Ui.Text(this, "●", 15f, P.Muted);
        stateRow.AddView(_connDot);
        _connState = Ui.Text(this, "VPN не подключён", 16f, P.TextStrong, bold: true);
        _connState.SetPadding(Ui.Dp(this, 7), 0, 0, 0);
        stateRow.AddView(_connState, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f));
        card.AddView(stateRow);

        _connNode = Ui.Text(this, "Сервер не выбран", 14.5f, P.TextStrong, bold: true);
        _connNode.SetPadding(0, Ui.Dp(this, 8), 0, 0);
        card.AddView(_connNode);

        _connDetails = Ui.Hint(this, "Адрес, протокол и задержка появятся после подключения.", P);
        _connDetails.SetPadding(0, Ui.Dp(this, 4), 0, 0);
        card.AddView(_connDetails);

        _connExit = Ui.Hint(this, string.Empty, P);
        _connExit.SetPadding(0, Ui.Dp(this, 2), 0, 0);
        card.AddView(_connExit);

        _connTraffic = Ui.Hint(this, "Трафик: —", P);
        _connTraffic.SetPadding(0, Ui.Dp(this, 2), 0, 0);
        card.AddView(_connTraffic);

        _connHint = Ui.Hint(this,
            "Нажмите «Подключение» — VPN поднимется на выбранном сервере. " +
            "Статус, выход и трафик появятся здесь.", P, P.MutedStrong);
        _connHint.SetPadding(0, Ui.Dp(this, 8), 0, 0);
        card.AddView(_connHint);

        _disconnect = Ui.Pill(this, P, "Отключить", UiButtonKind.Danger, () =>
        {
            VpnStarter.Stop(this);
            SetStatus("Отключаю VPN…");
        }, Resource.Drawable.ic_power);
        _disconnect.Visibility = ViewStates.Gone;
        var disconnectLayout = new LinearLayout.LayoutParams(
            ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent);
        disconnectLayout.TopMargin = Ui.Dp(this, 10);
        card.AddView(_disconnect, disconnectLayout);

        Ui.Add(page, card, 4);
    }

    private void RefreshConnectionStatus()
    {
        if (_connState is null)
        {
            return;
        }

        var state = TunnelService.State;
        var running = state == TunnelState.Running;
        var starting = state == TunnelState.Starting;
        var bypass = TunnelService.IsBypassMode;
        var error = TunnelService.LastError;
        var node = NodeStore.Selected ?? _connectedNode;
        var showTraffic = NodeStore.Settings.ShowTraffic;
        var showExit = NodeStore.Settings.ShowExitInfo;

        _connState.Text = running
            ? bypass ? "TUN-обход (бета) работает" : "Подключено"
            : starting
                ? "Подключаюсь…"
                : error is null
                    ? "VPN не подключён"
                    : "Ошибка подключения";
        _connState.SetTextColor(running ? P.Positive : starting ? P.Warning : error is null ? P.TextStrong : P.Danger);
        if (_connDot is not null)
        {
            _connDot.SetTextColor(running ? P.Positive : starting ? P.Warning : error is null ? P.Muted : P.Danger);
        }

        if (_connNode is not null)
        {
            _connNode.Text = bypass
                ? "Прокси-сервер не используется"
                : node is null
                    ? "Сервер не выбран"
                    : NodeDisplay.Name(node);
        }

        if (_connDetails is not null)
        {
            if (bypass)
            {
                _connDetails.Text = "Трафик идёт в TUN с фрагментацией TLS, без сервера.";
            }
            else if (node is null)
            {
                _connDetails.Text = "Выберите сервер в списке ниже и нажмите «Подключение».";
            }
            else
            {
                var protocol = string.IsNullOrWhiteSpace(node.Protocol)
                    ? string.Empty
                    : " · " + node.Protocol.ToUpperInvariant();
                var speed = node.SpeedMbps is { } mbps ? $" · {mbps:0.#} Мбит/с" : string.Empty;
                var prefix = running || starting ? string.Empty : "Последний сервер: ";
                _connDetails.Text = $"{prefix}{node.Server}:{node.Port}{protocol} · " +
                                    $"задержка {node.DisplayDelay}{speed}";
            }
        }

        if (_connExit is not null)
        {
            if (showExit && running)
            {
                _connExit.Visibility = ViewStates.Visible;
                var exit = NetworkProbe.CachedExit;
                _connExit.Text = exit is null ? "Выход: определяется…" : "Выход: " + exit.Short;
            }
            else
            {
                _connExit.Visibility = ViewStates.Gone;
            }
        }

        if (_connTraffic is not null)
        {
            if (showTraffic)
            {
                _connTraffic.Visibility = ViewStates.Visible;
                var uptime = running && TunnelService.StartedAtUtc is { } started
                    ? " · в сети " + (DateTime.UtcNow - started).ToString(@"hh\:mm\:ss")
                    : string.Empty;
                _connTraffic.Text = running
                    ? $"Трафик: ↓ {FormatBytes(_downBytes)} · ↑ {FormatBytes(_upBytes)}{uptime}"
                    : "Трафик: —";
            }
            else
            {
                _connTraffic.Visibility = ViewStates.Gone;
            }
        }

        if (_connHint is not null)
        {
            if (running)
            {
                _connHint.Text = _connectedReturned
                    ? "Сервер возвращён в рабочие: трафик идёт через него."
                    : bypass
                        ? "VPN держится, пока вы не нажмёте «Отключить»."
                        : "VPN держится, пока вы не нажмёте «Отключить». Сервер из отсева останется " +
                          "здесь, пока не нажмёте «Вернуть».";
                _connHint.SetTextColor(P.MutedStrong);
            }
            else if (starting)
            {
                _connHint.Text = "Поднимаю TUN и запускаю ядро по выбранному серверу…";
                _connHint.SetTextColor(P.MutedStrong);
            }
            else if (error is not null)
            {
                _connHint.Text = "Причина: " + error;
                _connHint.SetTextColor(P.Danger);
            }
            else
            {
                _connHint.Text = "Нажмите «Подключение» — VPN поднимется на выбранном сервере. " +
                                 "Статус, выход и трафик появятся здесь.";
                _connHint.SetTextColor(P.MutedStrong);
            }
        }

        if (_disconnect is not null)
        {
            var active = running || starting || CoreRuntime.IsRunning;
            _disconnect.Visibility = active ? ViewStates.Visible : ViewStates.Gone;
            _disconnect.Enabled = active;
        }

        if (running && showTraffic && _resumed)
        {
            EnsureTrafficMonitor();
        }
        else
        {
            StopTrafficMonitor();
            if (!running && !starting)
            {
                _downBytes = 0;
                _upBytes = 0;
            }
        }

        if (_resumed && running && showExit && NetworkProbe.CachedExit is null && !_exitBusy &&
            DateTime.UtcNow - _lastExitAttempt > TimeSpan.FromSeconds(20))
        {
            _ = LookupExitAsync();
        }

        // Сообщение внизу — только при смене состояния: иначе каждый такт
        // затирал бы итоги перепроверки.
        if (_lastConnState != state)
        {
            if (state == TunnelState.Running)
            {
                SetStatus("VPN подключён" +
                          (node is null ? "." : ": " + NodeDisplay.Name(node) + ". ") +
                          "Сервер остаётся в отсеве — нажмите «Вернуть», чтобы перенести в рабочие.");
            }
            else if (state == TunnelState.Starting)
            {
                SetStatus("Запускаю VPN…");
            }
            else if (state == TunnelState.Stopped && error is not null)
            {
                SetStatus("VPN не поднялся: " + error);
            }

            _lastConnState = state;
        }

        // Подключение из отсева удалось: через сервер реально пошёл трафик — это
        // лучшее доказательство, что он рабочий. Возвращаем его в рабочий список
        // автоматически, один раз на сервер.
        if (running && _connectedNode is { } connected &&
            !string.Equals(_autoReturnedNodeId, connected.Id, StringComparison.OrdinalIgnoreCase))
        {
            _autoReturnedNodeId = connected.Id;
            var stillRejected = RejectedNodeStore.Load()
                .Any(item => item.Node.Id.Equals(connected.Id, StringComparison.OrdinalIgnoreCase));
            _connectedReturned = true;
            if (stillRejected)
            {
                var name = NodeDisplay.Name(connected);
                Return(connected);
                SetStatus("VPN подключён: " + name + " — сервер возвращён в рабочие");
            }
        }
    }

    private async Task LookupExitAsync()
    {
        _exitBusy = true;
        _lastExitAttempt = DateTime.UtcNow;
        try
        {
            await NetworkProbe.LookupExitAsync(CancellationToken.None);
            RunOnUiThread(RefreshConnectionStatus);
        }
        catch (Exception exception)
        {
            AppLog.Write("traffic", "Выход из отсева не определён", exception);
        }
        finally
        {
            _exitBusy = false;
        }
    }

    /// <summary>Суммарный трафик сессии из Clash API: только пока экран открыт и VPN работает.</summary>
    private void EnsureTrafficMonitor()
    {
        if (_trafficCts is not null || !CoreRuntime.IsRunning)
        {
            return;
        }

        var cts = new CancellationTokenSource();
        _trafficCts = cts;
        _ = Task.Run(async () =>
        {
            try
            {
                using var api = new ClashApiClient(CoreRuntime.ApiPort, CoreRuntime.ApiSecret);
                await api.StreamTrafficAsync((up, down) =>
                {
                    Interlocked.Add(ref _upBytes, up);
                    Interlocked.Add(ref _downBytes, down);
                    if (DateTime.UtcNow - _lastTrafficUi > TimeSpan.FromSeconds(1))
                    {
                        _lastTrafficUi = DateTime.UtcNow;
                        RunOnUiThread(RefreshConnectionStatus);
                    }
                }, cts.Token);
            }
            catch (System.OperationCanceledException)
            {
                // Поток остановлен при уходе с экрана или отключении.
            }
            catch (Exception exception)
            {
                AppLog.Write("traffic", "Поток трафика в отсеве не удался", exception);
            }
            finally
            {
                if (ReferenceEquals(_trafficCts, cts))
                {
                    _trafficCts = null;
                }

                cts.Dispose();
            }
        });
    }

    private void StopTrafficMonitor()
    {
        try
        {
            _trafficCts?.Cancel();
        }
        catch (Exception)
        {
            // Поток мог уже закрыться.
        }

        _trafficCts = null;
    }

    private static string FormatBytes(long bytes)
    {
        if (bytes < 1024)
        {
            return bytes + " Б";
        }

        if (bytes < 1024L * 1024)
        {
            return (bytes / 1024.0).ToString("0.#") + " КБ";
        }

        if (bytes < 1024L * 1024 * 1024)
        {
            return (bytes / 1024.0 / 1024.0).ToString("0.#") + " МБ";
        }

        return (bytes / 1024.0 / 1024.0 / 1024.0).ToString("0.##") + " ГБ";
    }

    /// <summary>Прокручивает экран к сводке подключения: кнопка «Подключение» есть и в карточке списка.</summary>
    private void RevealConnectionStatus()
    {
        var card = _connCard;
        var scroll = _scroll;
        if (card is null || scroll is null)
        {
            return;
        }

        scroll.Post(() =>
        {
            try
            {
                scroll.SmoothScrollTo(0, Math.Max(0, card.Top - Ui.Dp(this, 8)));
            }
            catch (Exception)
            {
                // Прокрутка — удобство, а не обязательный шаг.
            }
        });
    }

    private TextView AddMetric(LinearLayout row, string label, Android.Graphics.Color tone)
    {
        var column = Ui.Column(this);
        column.SetPadding(Ui.Dp(this, 8), Ui.Dp(this, 7), Ui.Dp(this, 8), Ui.Dp(this, 7));
        column.Background = Ui.Rounded(P.Inner, 12, this, P.BorderSoft);
        column.SetGravity(GravityFlags.Center);
        var value = Ui.Text(this, "0", 18f, tone, bold: true);
        value.Gravity = GravityFlags.Center;
        column.AddView(value, new LinearLayout.LayoutParams(
            ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent));
        var caption = Ui.Text(this, label, 9.5f, P.Muted);
        caption.Gravity = GravityFlags.Center;
        caption.SetSingleLine(true);
        column.AddView(caption, new LinearLayout.LayoutParams(
            ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent));
        var layout = new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f);
        layout.RightMargin = Ui.Dp(this, 6);
        row.AddView(column, layout);
        return value;
    }

    private void BuildFilterChips()
    {
        if (_filterRow is null)
        {
            return;
        }

        _filterRow.RemoveAllViews();
        AddFilter(_filterRow, "Все", null);
        AddFilter(_filterRow, "YouTube", "youtube");
        AddFilter(_filterRow, "Telegram", "telegram");
        AddFilter(_filterRow, "Discord", "discord");
        AddFilter(_filterRow, "Универсальный", "universal");
    }

    private void AddFilter(LinearLayout row, string title, string? service)
    {
        var selected = _service == service;
        var pill = Ui.Pill(this, P, title,
            selected ? UiButtonKind.Accent : UiButtonKind.Ghost, () =>
            {
                _service = service;
                _selectedIndex = 0;
                BuildFilterChips();
                Reload();
            });
        var layout = new LinearLayout.LayoutParams(
            ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent);
        layout.RightMargin = Ui.Dp(this, 6);
        row.AddView(pill, layout);
    }

    private List<RejectedNode> Filtered()
    {
        var items = RejectedNodeStore.Load();
        return items.Where(item => _service switch
        {
            "youtube" => item.Node.YoutubeOk == true,
            "telegram" => item.Node.TelegramOk == true,
            "discord" => item.Node.DiscordOk == true,
            "universal" => item.Node.UniversalOk == true,
            _ => true,
        }).ToList();
    }

    private void Reload()
    {
        if (_list is null || _totalText is null)
        {
            return;
        }

        var items = Filtered();
        if (_selectedIndex >= items.Count)
        {
            _selectedIndex = 0;
        }

        var all = RejectedNodeStore.Load();
        _totalText.Text = all.Count.ToString();
        if (_youtubeCount is not null)
        {
            _youtubeCount.Text = all.Count(item => item.Node.YoutubeOk == true).ToString();
        }

        if (_telegramCount is not null)
        {
            _telegramCount.Text = all.Count(item => item.Node.TelegramOk == true).ToString();
        }

        if (_discordCount is not null)
        {
            _discordCount.Text = all.Count(item => item.Node.DiscordOk == true).ToString();
        }

        if (_universalCount is not null)
        {
            _universalCount.Text = all.Count(item => item.Node.UniversalOk == true).ToString();
        }

        _list.RemoveAllViews();
        if (items.Count == 0)
        {
            AddEmptyState();
            return;
        }

        for (var index = 0; index < items.Count; index++)
        {
            AddCard(items[index], index);
        }
    }

    private void AddEmptyState()
    {
        if (_list is null)
        {
            return;
        }

        var card = Ui.Column(this);
        card.SetPadding(Ui.Dp(this, 20), Ui.Dp(this, 26), Ui.Dp(this, 20), Ui.Dp(this, 26));
        card.Background = Ui.Rounded(P.Card, 16, this, P.BorderSoft);
        card.SetGravity(GravityFlags.Center);
        var icon = new ImageView(this);
        var drawable = Ui.Icon(this, Resource.Drawable.ic_check);
        if (drawable is not null)
        {
            drawable.SetTint(P.Accent);
            icon.SetImageDrawable(drawable);
        }

        card.AddView(icon, new LinearLayout.LayoutParams(Ui.Dp(this, 44), Ui.Dp(this, 44)));
        var title = Ui.Text(this, "Отсев пуст", 17f, P.TextStrong, bold: true);
        title.Gravity = GravityFlags.Center;
        title.SetPadding(0, Ui.Dp(this, 12), 0, 0);
        card.AddView(title);
        var hint = Ui.Hint(this,
            "Сюда попадают рабочие серверы, которые перестали работать. " +
            "Пока их нет — всё в порядке.", P);
        hint.Gravity = GravityFlags.Center;
        hint.SetPadding(0, Ui.Dp(this, 4), 0, 0);
        card.AddView(hint);
        _list.AddView(card);
    }

    private void AddCard(RejectedNode item, int index)
    {
        if (_list is null)
        {
            return;
        }

        var node = item.Node;
        var selected = index == _selectedIndex;
        var card = Ui.Column(this);
        card.SetPadding(Ui.Dp(this, 14), Ui.Dp(this, 12), Ui.Dp(this, 14), Ui.Dp(this, 12));
        card.Background = Ui.Rounded(selected ? P.AccentSurface : P.Card, 15, this,
            selected ? P.AccentBorder : P.Border);
        card.Clickable = true;
        card.Focusable = true;
        card.Click += (_, _) =>
        {
            _selectedIndex = index;
            Reload();
        };

        var topRow = Ui.Row(this);
        var title = Ui.Text(this, node.Name, 15f, P.TextStrong, bold: true);
        title.SetSingleLine(true);
        title.Ellipsize = Android.Text.TextUtils.TruncateAt.End;
        topRow.AddView(title, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f));
        if (selected)
        {
            topRow.AddView(Ui.Badge(this, P, "ВЫБРАН", P.Accent));
        }

        topRow.AddView(Ui.Badge(this, P,
            node.Status == NodeStatus.Working ? "ПРОВЕРКА ПРОШЛА" : node.FailCount + " провал(а)",
            node.Status == NodeStatus.Working ? P.Positive : P.Danger));
        card.AddView(topRow);

        var address = Ui.Hint(this, node.Server + ":" + node.Port, P);
        address.SetPadding(0, Ui.Dp(this, 4), 0, 0);
        card.AddView(address);
        var source = Ui.Hint(this, "Источник: " + node.SourceName, P);
        source.SetSingleLine(true);
        source.Ellipsize = Android.Text.TextUtils.TruncateAt.Middle;
        card.AddView(source);

        var services = Ui.Row(this);
        services.SetPadding(0, Ui.Dp(this, 8), 0, 0);
        if (node.YoutubeOk == true)
        {
            services.AddView(Ui.Badge(this, P, "YOUTUBE", P.YouTube));
        }

        if (node.TelegramOk == true)
        {
            services.AddView(Ui.Badge(this, P, "TELEGRAM", P.Telegram));
        }

        if (node.DiscordOk == true)
        {
            services.AddView(Ui.Badge(this, P, "DISCORD", P.Discord));
        }

        if (node.UniversalOk == true)
        {
            services.AddView(Ui.Badge(this, P, "УНИВЕРСАЛЬНЫЙ", P.Accent));
        }

        if (services.ChildCount == 0)
        {
            services.AddView(Ui.Badge(this, P, "СЕРВИСЫ НЕ ОТМЕЧЕНЫ", P.Muted));
        }

        card.AddView(services);

        var meta = Ui.Hint(this,
            $"Отсеян {item.RejectedAtUtc.ToLocalTime():dd.MM HH:mm}" +
            (node.DelayMs is null ? string.Empty : $" · был {node.DelayMs} мс"), P);
        meta.SetPadding(0, Ui.Dp(this, 8), 0, 0);
        card.AddView(meta);

        // «Подключение» — отдельной строкой на всю ширину: в ряду из трёх кнопок
        // подпись «Подключение» ломалась посреди слова на узких экранах.
        var connect = Ui.Pill(this, P, "Подключение", UiButtonKind.Accent, () =>
            {
                _selectedIndex = index;
                Connect(node);
            }, Resource.Drawable.ic_power);
        var connectLayout = new LinearLayout.LayoutParams(
            ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent);
        connectLayout.TopMargin = Ui.Dp(this, 10);
        card.AddView(connect, connectLayout);

        var buttons = Ui.Row(this);
        buttons.AddView(Ui.Pill(this, P, "Вернуть", UiButtonKind.Ghost, () => Return(node),
                Resource.Drawable.ic_check),
            new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f));
        buttons.AddView(new Space(this), new LinearLayout.LayoutParams(Ui.Dp(this, 6), 1));
        buttons.AddView(Ui.Pill(this, P, "Убрать", UiButtonKind.Ghost, () => Remove(node),
                Resource.Drawable.ic_delete, P.Danger),
            new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f));
        var layout = new LinearLayout.LayoutParams(
            ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent);
        layout.TopMargin = Ui.Dp(this, 6);
        card.AddView(buttons, layout);

        var outer = new LinearLayout.LayoutParams(
            ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent);
        outer.BottomMargin = Ui.Dp(this, 8);
        _list.AddView(card, outer);
    }

    private RejectedNode? Selected()
    {
        var items = Filtered();
        return items.Count == 0 ? null : items[Math.Clamp(_selectedIndex, 0, items.Count - 1)];
    }

    private void ConnectSelected()
    {
        if (Selected() is not { } item)
        {
            SetStatus("В отсеве нет подходящих серверов");
            return;
        }

        Connect(item.Node);
    }

    private void Connect(ProxyNode node)
    {
        if (_busy)
        {
            SetStatus("Дождитесь окончания операции");
            return;
        }

        if (RejectedCheckService.IsRunning || NodeTester.IsRunning ||
            RejectedBootCheckService.IsRunning)
        {
            SetStatus("Дождитесь окончания перепроверки отсева");
            return;
        }

        var settings = SettingsStore.Current.Clone();
        // Android подключается через VpnService: конфиг обязан содержать TUN,
        // иначе служба стартует без перехвата трафика.
        settings.TunnelMode = TunnelMode.Tun;
        settings.OnlySelectedApps = false;
        settings.AppRouteMode = AppRouteMode.All;
        settings.AppList = string.Empty;
        var config = SingBoxConfigBuilder.Build(
            new[] { node },
            node.Tag,
            CoreRuntime.MixedPort,
            CoreRuntime.ApiPort,
            CoreRuntime.ApiSecret,
            settings,
            dnsThroughProxy: true);
        _pendingConfig = config;
        _pendingNode = node;

        // Экран «Отсев» подключается в обход общего списка, поэтому выбранный сервер
        // фиксируем в хранилище: уведомление, health-check и главный экран должны
        // показывать именно его, а не прошлый выбор.
        try
        {
            NodeStore.Select(node);
        }
        catch (Exception exception)
        {
            AppLog.Write("nodes", "Не удалось запомнить сервер из отсева", exception);
        }

        // Сбрасываем прежние выход/скорость: они относились к другому серверу.
        TunnelService.UpdateNodeLabel(node.DisplayDelay);

        RevealConnectionStatus();

        var request = VpnStarter.PrepareRequest(this);
        if (request is not null)
        {
            StartActivityForResult(request, VpnRequestCode);
            return;
        }

        StartPending();
    }

    private void StartPending()
    {
        if (_pendingConfig is null)
        {
            return;
        }

        _connectedNode = _pendingNode;
        _downBytes = 0;
        _upBytes = 0;
        _autoReturnedNodeId = null;
        _connectedReturned = false;
        VpnStarter.Start(this, _pendingConfig);
        _pendingConfig = null;
        _pendingNode = null;
        SetStatus("Запускаю VPN…");
        RefreshConnectionStatus();
    }

    protected override void OnActivityResult(int requestCode, Result resultCode, Intent? data)
    {
        base.OnActivityResult(requestCode, resultCode, data);
        if (requestCode != VpnRequestCode)
        {
            return;
        }

        if (resultCode == Result.Ok)
        {
            StartPending();
        }
        else
        {
            SetStatus("Разрешение VPN не выдано");
            RefreshConnectionStatus();
        }
    }

    private async Task RecheckAllAsync()
    {
        if (_busy)
        {
            SetStatus("Перепроверка уже идёт");
            return;
        }

        if (RejectedBootCheckService.IsRunning)
        {
            SetStatus("Идёт автоподключение отсева, дождитесь окончания");
            return;
        }

        if (TunnelService.State == TunnelState.Running)
        {
            SetStatus("Остановите VPN перед перепроверкой отсева");
            return;
        }

        var items = Filtered();
        if (items.Count == 0)
        {
            SetStatus("В отсеве нет серверов для проверки");
            return;
        }

        _busy = true;
        _cts = new CancellationTokenSource();
        try
        {
            var result = await RejectedCheckService.RecheckAsync(
                new Progress<string>(SetStatus), _cts.Token);
            SetStatus(result.Error ?? (result.Restored > 0
                ? $"Перепроверено {result.Tested}: в рабочие возвращено {result.Restored}."
                : $"Перепроверено {result.Tested}: рабочих {result.Working}. " +
                  "В рабочие никто не вернулся."));
            Reload();
        }
        catch (System.OperationCanceledException)
        {
            SetStatus("Перепроверка отменена");
        }
        catch (Exception exception)
        {
            SetStatus("Ошибка перепроверки: " + exception.Message);
            AppLog.Write("nodes", "Перепроверка отсева не удалась", exception);
        }
        finally
        {
            _busy = false;
            _cts?.Dispose();
            _cts = null;
        }
    }

    private void Cancel()
    {
        _cts?.Cancel();
        SetStatus("Отмена");
    }

    private void Next()
    {
        var items = Filtered();
        if (items.Count == 0)
        {
            return;
        }

        _selectedIndex = (_selectedIndex + 1) % items.Count;
        Reload();
        SetStatus("Выбран: " + items[_selectedIndex].Node.Name);
    }

    private void ReturnSelected()
    {
        if (Selected() is { } item)
        {
            Return(item.Node);
        }
    }

    private void Return(ProxyNode node)
    {
        node.Status = NodeStatus.Working;
        node.FailCount = 0;
        // Возвращённый сервер снова в списке — не должен выпасть
        // по сроку давности при следующей загрузке кэша.
        node.LastSeenUtc = DateTime.UtcNow;
        var restored = NodeStore.RestoreRejected(new[] { node });
        if (_connectedNode is { } connected &&
            connected.Id.Equals(node.Id, StringComparison.OrdinalIgnoreCase))
        {
            _connectedReturned = true;
        }
        SetStatus(restored
            ? "Возвращён в рабочие: " + node.Name
            : "Не удалось вернуть: список серверов переполнен — сначала проверьте и удалите лишние");
        Reload();
    }

    private void Remove(ProxyNode node)
    {
        RejectedNodeStore.Remove(new[] { node.Id });
        SetStatus("Удалён из отсева: " + node.Name);
        Reload();
    }

    private void SetStatus(string text) => RunOnUiThread(() =>
    {
        if (_status is not null)
        {
            _status.Text = text;
        }
    });
}
