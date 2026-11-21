using Android.App;
using Android.Content;
using Android.Graphics;
using Android.Net;
using Android.OS;
using Android.Views;
using Android.Widget;

namespace VieNLink.Mobile;

/// <summary>
/// Страница «Telegram MTProtoProxies»: рабочие MTProto-прокси, ручная проверка,
/// открытие ссылки в Telegram и отдельное подключение к Telegram через узел.
/// Сбор идёт автономно из Telegram-источников через RSS-зерцала.
/// </summary>
#if DEBUG
[Activity(Label = "Telegram MTProtoProxies", Theme = "@style/Theme.VieNLink", Exported = true)]
#else
[Activity(Label = "Telegram MTProtoProxies", Theme = "@style/Theme.VieNLink")]
#endif
public sealed class TelegramActivity : BaseActivity
{
    private const int VpnRequestCode = 4004;

    private LinearLayout? _list;
    private LinearLayout? _deadList;
    private TextView? _summary;
    private TextView? _status;
    private string? _pendingConfig;
    private bool _busy;

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);

        var page = ColumnPage();

        var top = Ui.Row(this);
        var back = Ui.Pill(this, P, "Назад", UiButtonKind.Ghost, Finish);
        top.AddView(back, new LinearLayout.LayoutParams(
            ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent));
        var title = Ui.Title(this, "Telegram MTProtoProxies", P);
        title.SetPadding(Ui.Dp(this, 10), 0, 0, 0);
        top.AddView(title, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f));
        top.AddView(Ui.Badge(this, P, "MTProto", P.Telegram));
        page.AddView(top);
        Ui.AddSpacer(page, this, 10);

        var intro = Ui.Card(P, this,
            Ui.Hint(this,
                "MTProto-прокси работают только внутри Telegram: браузер через них пустить нельзя, " +
                "это протокол самого мессенджера. Каналы читаются через RSS-зеркала, рабочие прокси " +
                "проверяются каждые 10 минут, нерабочие удаляются через 24 часа.", P, P.MutedStrong));
        page.AddView(intro);

        _summary = Ui.Text(this, "Прокси: —", 15f, P.TextStrong, bold: true);
        _summary.SetPadding(0, Ui.Dp(this, 16), 0, 0);
        page.AddView(_summary);
        _status = Ui.Hint(this, "Готово", P);
        _status.SetPadding(0, Ui.Dp(this, 4), 0, 0);
        page.AddView(_status);

        var actions = Ui.Row(this);
        var test = Ui.Pill(this, P, "Проверить", UiButtonKind.Accent,
            () => _ = RunTestAsync(), Resource.Drawable.ic_check);
        var collect = Ui.Pill(this, P, "Собрать", UiButtonKind.Secondary,
            () => _ = RunCollectAsync(), Resource.Drawable.ic_refresh);
        actions.AddView(test, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f));
        var gap = new Space(this);
        actions.AddView(gap, new LinearLayout.LayoutParams(Ui.Dp(this, 8), 1));
        actions.AddView(collect, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f));
        Ui.Add(page, actions, 12);

        var openBest = Ui.Pill(this, P, "Открыть лучший в Telegram", UiButtonKind.Primary,
            OpenBest, Resource.Drawable.ic_service_telegram);
        Ui.Add(page, openBest, 8);

        var viaNode = Ui.Pill(this, P, "Подключить Telegram через узел", UiButtonKind.Secondary,
            ConnectTelegramViaNode, Resource.Drawable.ic_tab_connection);
        Ui.Add(page, viaNode, 8);

        page.AddView(Ui.Section(this, "Рабочие прокси", P));
        _list = Ui.Column(this);
        page.AddView(_list);

        page.AddView(Ui.Section(this, "Нерабочие · хранятся 24 часа", P));
        _deadList = Ui.Column(this);
        page.AddView(_deadList);

        var hint = Ui.Banner(P, this, P.Warning, "Автономно",
            "Кнопок сбора в Windows-версии нет: каналы читаются сами. В Android сбор запускается " +
            "при открытии страницы и фоновым обслуживанием раз в 10 минут. Telegram нужен для " +
            "самого прокси — установленное приложение откроет ссылку tg://proxy.");
        Ui.Add(page, hint, 16);

        SetPage(page);
        _ = ReloadAsync();
    }

    protected override void OnResume()
    {
        base.OnResume();
        Reload();
    }

    private async Task ReloadAsync()
    {
        await Task.Run(() => MtProtoStore.Prune());
        Reload();

        if (MtProtoStore.IsDue(DateTime.UtcNow))
        {
            await RunCollectAsync();
        }
    }

    private void Reload()
    {
        RunOnUiThread(() =>
        {
            if (_list is null || _deadList is null || _summary is null)
            {
                return;
            }

            _list.RemoveAllViews();
            _deadList.RemoveAllViews();

            var proxies = MtProtoStore.Snapshot();
            var working = proxies.Where(proxy => proxy.Status == MtProtoStatus.Working).ToList();
            var dead = proxies.Where(proxy => proxy.Status != MtProtoStatus.Working).ToList();
            var best = MtProtoStore.Best();

            _summary.Text = $"Прокси: рабочих {working.Count} · нерабочих {dead.Count}" +
                            (best?.DelayMs is { } delay ? $" · лучший {delay} мс" : string.Empty);

            if (working.Count == 0)
            {
                _list.AddView(EmptyCard(
                    "MTProto-прокси не собраны",
                    "Включите Telegram-канал в «Инструменты → Источники» и нажмите «Собрать»."));
            }

            foreach (var proxy in working.Take(50))
            {
                _list.AddView(ProxyCard(proxy, dead: false));
            }

            if (dead.Count == 0)
            {
                _deadList.AddView(EmptyCard("Нерабочих прокси нет",
                    "Не ответившие при проверке прокси попадут сюда и будут храниться 24 часа."));
            }

            foreach (var proxy in dead.Take(10))
            {
                _deadList.AddView(ProxyCard(proxy, dead: true));
            }
        });
    }

    private View EmptyCard(string title, string text)
    {
        var card = Ui.Card(P, this,
            Ui.Text(this, title, 14.5f, P.MutedStrong, bold: true),
            Ui.Hint(this, text, P));
        var layout = new LinearLayout.LayoutParams(
            ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent);
        layout.BottomMargin = Ui.Dp(this, 8);
        card.LayoutParameters = layout;
        return card;
    }

    private View ProxyCard(MtProtoProxy proxy, bool dead)
    {
        var card = Ui.Column(this);
        card.SetPadding(Ui.Dp(this, 14), Ui.Dp(this, 12), Ui.Dp(this, 14), Ui.Dp(this, 12));
        card.Background = Ui.Rounded(dead ? P.Inner : P.Card, 14, this, dead ? P.Border : P.AccentBorder);

        var top = Ui.Row(this);
        top.AddView(Ui.Text(this, proxy.Server + ":" + proxy.Port, 14f, P.TextStrong, bold: true),
            new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f));

        var tone = dead ? P.Muted : proxy.DelayMs is < 300 ? P.Positive : P.Warning;
        var delay = dead
            ? ExpiryText(proxy)
            : proxy.DelayMs is { } ms ? ms + " мс" : "не проверен";
        top.AddView(Ui.Badge(this, P, delay, tone));
        card.AddView(top);

        var source = Ui.Hint(this,
            "Секрет " + proxy.SecretShort +
            (string.IsNullOrWhiteSpace(proxy.SourceName) ? string.Empty : " · " + proxy.SourceName), P);
        source.SetPadding(0, Ui.Dp(this, 4), 0, 0);
        card.AddView(source);

        var actions = Ui.Row(this);
        var open = Ui.Pill(this, P, "Открыть", UiButtonKind.Accent, () => OpenProxy(proxy), Resource.Drawable.ic_open);
        var copy = Ui.Pill(this, P, "Ссылка", UiButtonKind.Ghost, () => CopyProxy(proxy), Resource.Drawable.ic_copy);
        actions.AddView(open, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f));
        actions.AddView(new Space(this), new LinearLayout.LayoutParams(Ui.Dp(this, 8), 1));
        actions.AddView(copy, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f));
        var actionLayout = new LinearLayout.LayoutParams(
            ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent);
        actionLayout.TopMargin = Ui.Dp(this, 8);
        card.AddView(actions, actionLayout);

        var outer = new LinearLayout.LayoutParams(
            ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent);
        outer.BottomMargin = Ui.Dp(this, 8);
        card.LayoutParameters = outer;
        return card;
    }

    private static string ExpiryText(MtProtoProxy proxy)
    {
        if (proxy.DeadAtUtc is not { } deadAt)
        {
            return "нерабочий";
        }

        var left = TimeSpan.FromHours(MtProtoRetention.DeadHours) - (DateTime.UtcNow - deadAt);
        return left.TotalHours <= 0 ? "удаляется" : $"осталось {Math.Ceiling(left.TotalHours)} ч";
    }

    private async Task RunCollectAsync()
    {
        if (_busy)
        {
            return;
        }

        _busy = true;
        SetStatus("Читаю Telegram-каналы через RSS…");
        try
        {
            var progress = new Progress<string>(SetStatus);
            var summary = await MtProtoStore.CollectAsync(progress, CancellationToken.None);
            SetStatus(summary);
            Reload();
        }
        catch (Exception exception)
        {
            AppLog.Write("mtproto", "Ошибка сбора", exception);
            SetStatus("Ошибка сбора: " + exception.Message);
        }
        finally
        {
            _busy = false;
        }
    }

    private async Task RunTestAsync()
    {
        if (_busy)
        {
            return;
        }

        _busy = true;
        SetStatus("Проверяю TCP-доступность прокси…");
        try
        {
            var progress = new Progress<string>(SetStatus);
            var summary = await MtProtoStore.TestAsync(progress, CancellationToken.None);
            SetStatus(summary);
            Reload();
        }
        catch (Exception exception)
        {
            AppLog.Write("mtproto", "Ошибка проверки", exception);
            SetStatus("Ошибка проверки: " + exception.Message);
        }
        finally
        {
            _busy = false;
        }
    }

    private void OpenBest()
    {
        var best = MtProtoStore.Best();
        if (best is null)
        {
            Ui.Toast(this, "Нет проверенных рабочих прокси — нажмите «Проверить»");
            return;
        }

        OpenProxy(best);
    }

    private void OpenProxy(MtProtoProxy proxy)
    {
        try
        {
            var intent = new Intent(Intent.ActionView, Android.Net.Uri.Parse(proxy.Link)!)
                .AddFlags(ActivityFlags.NewTask);
            StartActivity(intent);
        }
        catch (Exception exception)
        {
            AppLog.Write("mtproto", "Telegram не открылся", exception);
            CopyProxy(proxy);
            Ui.Toast(this, "Telegram не найден — ссылка скопирована");
        }
    }

    private void CopyProxy(MtProtoProxy proxy)
    {
        try
        {
            var clipboard = (ClipboardManager?)GetSystemService(ClipboardService);
            if (clipboard is not null)
            {
                clipboard.PrimaryClip = ClipData.NewPlainText("MTProto", proxy.Link);
            }

            Ui.Toast(this, "Ссылка скопирована");
        }
        catch (Exception exception)
        {
            AppLog.Write("mtproto", "Не удалось скопировать ссылку", exception);
        }
    }

    /// <summary>Подключение только для Telegram: узел с подтверждённым доступом к t.me.</summary>
    private async void ConnectTelegramViaNode()
    {
        if (_busy)
        {
            return;
        }

        _busy = true;
        try
        {
            NodeStore.EnsureLoaded();
            var progress = new Progress<string>(SetStatus);
            SetStatus("Проверяю узел с доступом к Telegram…");
            var node = await NodeStore.PickVerifiedAsync(
                NodeStore.QuickService.Telegram, null, progress, CancellationToken.None);
            if (node is null)
            {
                Ui.Toast(this, "Нет живых узлов с доступом к Telegram");
                SetStatus("Нет живых узлов с доступом к Telegram");
                return;
            }

            if (TunnelService.State == TunnelState.Running &&
                CoreRuntime.IsRunning &&
                CoreRuntime.LoadedTags.Contains(node.Tag))
            {
                using var api = new ClashApiClient(CoreRuntime.ApiPort, CoreRuntime.ApiSecret);
                await api.SelectAsync(SingBoxConfigBuilder.SelectorTag, node.Tag, CancellationToken.None);
                TunnelService.UpdateNodeLabel(node.DisplayDelay);
                SetStatus("Telegram через узел: переключено · " + node.DisplayDelay);
                return;
            }

            var config = NodeStore.BuildTunnelConfig(out var error);
            if (config is null)
            {
                Ui.Toast(this, error ?? "Не удалось собрать конфиг");
                return;
            }

            var request = VpnStarter.PrepareRequest(this);
            if (request is not null)
            {
                _pendingConfig = config;
                StartActivityForResult(request, VpnRequestCode);
                return;
            }

            VpnStarter.Start(this, config);
            SetStatus("Telegram через узел: туннель запускается");
        }
        catch (Exception exception)
        {
            SetStatus("Ошибка: " + exception.Message);
        }
        finally
        {
            _busy = false;
        }
    }

    protected override void OnActivityResult(int requestCode, Result resultCode, Intent? data)
    {
        base.OnActivityResult(requestCode, resultCode, data);
        if (requestCode != VpnRequestCode)
        {
            return;
        }

        if (resultCode == Result.Ok && _pendingConfig is not null)
        {
            VpnStarter.Start(this, _pendingConfig);
            SetStatus("Telegram через узел: туннель запускается");
        }
        else
        {
            SetStatus("Разрешение VPN не выдано");
        }

        _pendingConfig = null;
    }

    private void SetStatus(string text) => RunOnUiThread(() =>
    {
        if (_status is not null)
        {
            _status.Text = text;
        }
    });
}
