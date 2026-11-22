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

    private LinearLayout? _list;
    private LinearLayout? _filterRow;
    private TextView? _totalText;
    private TextView? _youtubeCount;
    private TextView? _telegramCount;
    private TextView? _discordCount;
    private TextView? _universalCount;
    private TextView? _status;
    private string? _service;
    private int _selectedIndex;
    private bool _busy;
    private CancellationTokenSource? _cts;
    private string? _pendingConfig;
    private ProxyNode? _pendingNode;

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        BuildUi();
        Reload();
    }

    protected override void OnResume()
    {
        base.OnResume();
        Reload();
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
        _universalCount = AddMetric(metrics, "Универсальный", P.Accent);
        summaryCard.AddView(metrics);
        Ui.Add(page, summaryCard, 12);

        page.AddView(Ui.Banner(P, this, P.Accent, "Что это",
            "Сюда попадают серверы, которые уже были рабочими, но перестали открывать сайт. " +
            "Их можно перепроверить вручную и вернуть в рабочие."));

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

        _status = Ui.Hint(this, "Серверы из отсева можно перепроверить вручную.", P);
        _status.SetPadding(0, Ui.Dp(this, 10), 0, 0);
        page.AddView(_status);

        page.AddView(Ui.Section(this, "Серверы в отсеве", P));
        _list = Ui.Column(this);
        page.AddView(_list);
        SetPage(page);
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

        topRow.AddView(Ui.Badge(this, P, node.FailCount + " провал(а)", P.Danger));
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

        var buttons = Ui.Row(this);
        buttons.AddView(Ui.Pill(this, P, "Подключение", UiButtonKind.Accent, () => Connect(node),
                Resource.Drawable.ic_power),
            new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f));
        buttons.AddView(new Space(this), new LinearLayout.LayoutParams(Ui.Dp(this, 6), 1));
        buttons.AddView(Ui.Pill(this, P, "Вернуть", UiButtonKind.Ghost, () => Return(node),
                Resource.Drawable.ic_check),
            new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f));
        buttons.AddView(new Space(this), new LinearLayout.LayoutParams(Ui.Dp(this, 6), 1));
        buttons.AddView(Ui.Pill(this, P, "Убрать", UiButtonKind.Ghost, () => Remove(node),
                Resource.Drawable.ic_delete, P.Danger),
            new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f));
        var layout = new LinearLayout.LayoutParams(
            ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent);
        layout.TopMargin = Ui.Dp(this, 10);
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

        var node = _pendingNode;
        VpnStarter.Start(this, _pendingConfig);
        _pendingConfig = null;
        _pendingNode = null;
        SetStatus("Подключение запущено");
        if (node is null)
        {
            return;
        }

        // Не убираем сервер из отсева сразу: ждём, пока служба действительно поднимется.
        var handler = new Handler(Looper.MainLooper!);
        handler.PostDelayed(() =>
        {
            if (TunnelService.State == TunnelState.Running)
            {
                Return(node);
            }
            else
            {
                SetStatus("Сервер не подключился — остался в отсеве");
            }
        }, 3000);
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
        }
    }

    private async Task RecheckAllAsync()
    {
        if (_busy)
        {
            SetStatus("Перепроверка уже идёт");
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
            var nodes = items.Select(item => item.Node).ToList();
            var progress = new Progress<string>(SetStatus);
            var summary = await NodeTester.TestNodesAsync(nodes, progress, _cts.Token);
            NodeStore.RestoreRejected(nodes);
            SetStatus($"Перепроверено {nodes.Count}: рабочих {summary.Working}");
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
        NodeStore.RestoreRejected(new[] { node });
        SetStatus("Возвращён в рабочие: " + node.Name);
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
