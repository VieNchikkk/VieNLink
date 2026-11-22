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
    private TextView? _summary;
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

        _summary = Ui.Text(this, "Читаю…", 16f, P.TextStrong, bold: true);
        _summary.SetPadding(0, Ui.Dp(this, 12), 0, 0);
        page.AddView(_summary);
        _status = Ui.Hint(this, "Серверы из отсева можно перепроверить вручную.", P);
        _status.SetPadding(0, Ui.Dp(this, 4), 0, 0);
        page.AddView(_status);

        var filters = Ui.Row(this);
        AddFilter(filters, "Все", null);
        AddFilter(filters, "YouTube", "youtube");
        AddFilter(filters, "Telegram", "telegram");
        page.AddView(filters);
        var filters2 = Ui.Row(this);
        AddFilter(filters2, "Discord", "discord");
        AddFilter(filters2, "Универсальный", "universal");
        page.AddView(filters2);

        var actions = Ui.Row(this);
        actions.AddView(Ui.Pill(this, P, "Подключение", UiButtonKind.Primary, ConnectSelected),
            new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f));
        actions.AddView(new Space(this), new LinearLayout.LayoutParams(Ui.Dp(this, 8), 1));
        actions.AddView(Ui.Pill(this, P, "Отмена", UiButtonKind.Danger, Cancel),
            new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f));
        page.AddView(actions);

        var actions2 = Ui.Row(this);
        actions2.AddView(Ui.Pill(this, P, "Следующий", UiButtonKind.Accent, Next),
            new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f));
        actions2.AddView(new Space(this), new LinearLayout.LayoutParams(Ui.Dp(this, 8), 1));
        actions2.AddView(Ui.Pill(this, P, "Вернуть (В рабочие)", UiButtonKind.Accent, ReturnSelected),
            new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f));
        page.AddView(actions2);

        page.AddView(Ui.Pill(this, P, "Перепроверить всё", UiButtonKind.Secondary,
            () => _ = RecheckAllAsync(), Resource.Drawable.ic_refresh));

        _list = Ui.Column(this);
        page.AddView(_list);
        SetPage(page);
    }

    private void AddFilter(LinearLayout row, string title, string? service)
    {
        var pill = Ui.Pill(this, P, title, UiButtonKind.Ghost, () =>
        {
            _service = service;
            _selectedIndex = 0;
            Reload();
        });
        if (_service == service)
        {
            pill.SetTextColor(P.Accent);
        }

        row.AddView(pill);
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
        if (_list is null || _summary is null)
        {
            return;
        }

        var items = Filtered();
        if (_selectedIndex >= items.Count)
        {
            _selectedIndex = 0;
        }

        var all = RejectedNodeStore.Load();
        _summary.Text = $"Всего в отсеве: {all.Count} · " +
                        $"YouTube: {all.Count(item => item.Node.YoutubeOk == true)} · " +
                        $"Telegram: {all.Count(item => item.Node.TelegramOk == true)} · " +
                        $"Discord: {all.Count(item => item.Node.DiscordOk == true)} · " +
                        $"Универсальный: {all.Count(item => item.Node.UniversalOk == true)}";
        _list.RemoveAllViews();
        if (items.Count == 0)
        {
            _list.AddView(Ui.Card(P, this,
                Ui.Text(this, "Отсев пуст", 15f, P.MutedStrong, bold: true),
                Ui.Hint(this, "Сюда попадают рабочие серверы, которые перестали работать.", P)));
            return;
        }

        for (var index = 0; index < items.Count; index++)
        {
            AddCard(items[index], index);
        }
    }

    private void AddCard(RejectedNode item, int index)
    {
        if (_list is null)
        {
            return;
        }

        var node = item.Node;
        var card = Ui.Column(this);
        card.SetPadding(Ui.Dp(this, 12), Ui.Dp(this, 10), Ui.Dp(this, 12), Ui.Dp(this, 10));
        card.Background = Ui.Rounded(index == _selectedIndex ? P.AccentSurface : P.Inner, 12, this,
            index == _selectedIndex ? P.AccentBorder : P.BorderSoft);
        card.Clickable = true;
        card.Click += (_, _) =>
        {
            _selectedIndex = index;
            Reload();
        };

        var title = Ui.Text(this, node.Name, 14.5f, P.TextStrong, bold: true);
        title.SetSingleLine(true);
        title.Ellipsize = Android.Text.TextUtils.TruncateAt.End;
        card.AddView(title);
        card.AddView(Ui.Hint(this, node.Server + ":" + node.Port + " · " + node.SourceName, P));
        var services = "Сервисы: " +
                       (node.YoutubeOk == true ? "YouTube " : string.Empty) +
                       (node.TelegramOk == true ? "Telegram " : string.Empty) +
                       (node.DiscordOk == true ? "Discord " : string.Empty) +
                       (node.UniversalOk == true ? "Универсальный" : string.Empty);
        card.AddView(Ui.Hint(this, services.Trim() +
                                 $" · отсеян {item.RejectedAtUtc.ToLocalTime():dd.MM HH:mm} · " +
                                 $"провалов {node.FailCount}", P));

        var buttons = Ui.Row(this);
        buttons.AddView(Ui.Pill(this, P, "Подключение", UiButtonKind.Accent, () => Connect(node)),
            new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f));
        buttons.AddView(new Space(this), new LinearLayout.LayoutParams(Ui.Dp(this, 6), 1));
        buttons.AddView(Ui.Pill(this, P, "Вернуть", UiButtonKind.Ghost, () => Return(node)),
            new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f));
        buttons.AddView(new Space(this), new LinearLayout.LayoutParams(Ui.Dp(this, 6), 1));
        buttons.AddView(Ui.Pill(this, P, "Убрать", UiButtonKind.Ghost, () => Remove(node)),
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

        var settings = SettingsStore.Current;
        var config = SingBoxConfigBuilder.Build(
            new[] { node },
            node.Tag,
            CoreRuntime.MixedPort,
            CoreRuntime.ApiPort,
            CoreRuntime.ApiSecret,
            settings);
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

        VpnStarter.Start(this, _pendingConfig);
        if (_pendingNode is { } node)
        {
            Return(node);
        }

        _pendingConfig = null;
        _pendingNode = null;
        SetStatus("Подключение запущено");
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
