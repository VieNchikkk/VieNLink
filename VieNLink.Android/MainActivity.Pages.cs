using Android.App;
using Android.Content;
using Android.Graphics;
using Android.Views;
using Android.Widget;

namespace VieNLink.Mobile;

public sealed partial class MainActivity
{
    private bool _logCore;
    private StreamFragmentMode? _pendingBypass;

    // -------------------------------------------------------------- подключение

    private View BuildConnectionPage()
    {
        var page = ColumnPage();

        page.AddView(Ui.Section(this, "Быстрое подключение", P));

        var first = Ui.Row(this);
        AddWeighted(first, Ui.ServiceButton(this, P, "YouTube", Resource.Drawable.ic_service_youtube_brand,
            P.YouTube, P.YouTubeSurface, () => StartForService(NodeStore.QuickService.YouTube),
            tintIcon: false), 8);
        AddWeighted(first, Ui.ServiceButton(this, P, "Discord", Resource.Drawable.ic_service_discord,
            P.Discord, P.DiscordSurface, () => StartForService(NodeStore.QuickService.Discord)), 0);
        page.AddView(first);

        var second = Ui.Row(this);
        var secondLayout = new LinearLayout.LayoutParams(
            ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent);
        secondLayout.TopMargin = Ui.Dp(this, 8);
        var universal = Ui.ServiceButton(this, P, "Универсальный", Resource.Drawable.ic_service_universal,
            P.Universal, P.UniversalSurface, () => StartForService(NodeStore.QuickService.Universal));
        AddWeighted(second, universal, 8);
        AddWeighted(second, Ui.ServiceButton(this, P, "Telegram", Resource.Drawable.ic_service_telegram,
            P.Telegram, P.TelegramSurface, () => StartForService(NodeStore.QuickService.Telegram)), 0);
        page.AddView(second, secondLayout);

        var mtproto = Ui.Pill(this, P, "Telegram MTProtoProxies", UiButtonKind.Secondary,
            () => Open(typeof(TelegramActivity)), Resource.Drawable.ic_mtproto, tone: P.Telegram);
        Ui.Add(page, mtproto, 8);

        var addServer = Ui.Pill(this, P, "Добавить сервер", UiButtonKind.Secondary,
            ShowAddServerDialog, Resource.Drawable.ic_nodes);
        Ui.Add(page, addServer, 8);

        var explanation = Ui.Banner(P, this, P.Accent, "Кратко",
            "Нажмите кнопку сервиса — VieNLink сам выберет проверенный сервер с доступом к нему " +
            "и подключится. После подключения в шапке появятся страна, задержка, скорость и оценка.");
        Ui.Add(page, explanation, 12);

        page.AddView(Ui.Section(this, "Как это работает", P));
        var how = Ui.Card(P, this);
        how.AddView(Bullet("1. Серверы собираются из открытых списков, подписок и каналов Telegram."));
        how.AddView(Bullet("2. Каждый сервер проверяется запросом через сам сервер: задержка = сайт реально открылся."));
        how.AddView(Bullet("3. Лучший — тот, кто открывает больше заблокированных доменов, затем быстрейший."));
        how.AddView(Bullet("4. Список серверов скрыт: приложение выбирает и меняет сервер само."));
        page.AddView(how);

        var beta = Ui.Banner(P, this, P.Warning, "Статус: бета-версия",
            "Основные режимы проверены, но возможны сбои. Бесплатные серверы не проверяются никем: " +
            "владелец сервера видит трафик так же, как провайдер. Не входите через них в банки и почту.");
        Ui.Add(page, beta, 12);

        return page;
    }

    private TextView Bullet(string text)
    {
        var view = Ui.Text(this, "•  " + text, 13f, P.MutedStrong);
        view.SetLineSpacing(0, 1.15f);
        view.SetPadding(0, Ui.Dp(this, 4), 0, Ui.Dp(this, 4));
        return view;
    }

    private void AddWeighted(LinearLayout row, View child, int rightMarginDp)
    {
        var layout = new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f);
        layout.RightMargin = Ui.Dp(this, rightMarginDp);
        row.AddView(child, layout);
    }

    // ------------------------------------------------------------------- обход

    private View BuildBypassPage()
    {
        var page = ColumnPage();
        page.AddView(Ui.Section(this, "Обход блокировок", P));

        var running = TunnelService.State == TunnelState.Running;
        var bypass = TunnelService.IsBypassMode;
        var status = Ui.Card(P, this,
            Ui.Text(this,
                bypass ? "TUN-обход (бета) работает" : running ? "Подключён прокси-сервер" : "Обход выключен",
                16f, bypass || running ? P.Positive : P.MutedStrong, bold: true),
            Ui.Hint(this,
                bypass
                    ? "Трафик идёт в TUN и в локальный форвардер с фрагментацией TLS. Прокси-сервер не нужен. " +
                      "Режим в бете: часть сайтов может не открываться."
                    : running
                        ? "При подключении к серверу обход DPI останавливается: режимы взаимоисключающие."
                        : "Выберите режим ниже. TUN-обход (бета) работает без сервера; Zapret и WinDivert на Android недоступны.",
                P));
        page.AddView(status);

        page.AddView(Ui.Section(this, "TUN-обход (Beta)", P));
        var tun = Ui.Card(P, this);
        var tunTop = Ui.Row(this);
        tunTop.AddView(Ui.Text(this, "TUN + фрагментация TLS", 15f, P.TextStrong, bold: true),
            new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f));
        tunTop.AddView(Ui.Badge(this, P, "БЕТА", P.Warning));
        tun.AddView(tunTop);
        var tunHint = Ui.Hint(this,
            "Без прокси-сервера и без WinDivert: sing-box поднимает TUN, TCP идёт в собственный " +
            "SOCKS5-форвардер, который режет TLS ClientHello. На SNI-блоках помогает, при блоке по IP — нет.", P);
        tunHint.SetPadding(0, Ui.Dp(this, 6), 0, 0);
        tun.AddView(tunHint);

        var tunActions = Ui.Row(this);
        var open = Ui.Pill(this, P, "Открыть TUN-обход", UiButtonKind.Accent,
            () => Open(typeof(BypassActivity)), Resource.Drawable.ic_tunnel);
        var quick = Ui.Pill(this, P, "Включить", UiButtonKind.Primary,
            StartLastBypass, Resource.Drawable.ic_power);
        var tunLayout = new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f);
        tunActions.AddView(open, tunLayout);
        tunActions.AddView(new Space(this), new LinearLayout.LayoutParams(Ui.Dp(this, 8), 1));
        tunActions.AddView(quick, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f));
        var actionsLayout = new LinearLayout.LayoutParams(
            ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent);
        actionsLayout.TopMargin = Ui.Dp(this, 10);
        tun.AddView(tunActions, actionsLayout);
        page.AddView(tun);

        page.AddView(Ui.Section(this, "Windows-режимы", P));
        page.AddView(DevelopmentCard(
            "Обход Zapret",
            "Переносится не полностью",
            "В Windows это winws.exe + драйвер WinDivert: перехват пакетов на уровне ядра и права " +
            "администратора. На Android без root драйвер WinDivert установить нельзя, поэтому режим " +
            "отмечен «в разработке». Его задача на телефоне решается TUN-обходом."));

        Ui.AddSpacer(page, this, 8);
        page.AddView(DevelopmentCard(
            "Свой обход DPI",
            "Переносится не полностью",
            "Собственный движок Windows тоже работает через WinDivert (split/disorder/fake TLS " +
            "ClientHello, QUIC-фейк). На Android он недоступен по той же причине. В разработке: " +
            "user-space варианты через TUN и локальный форвардер, без root."));

        Ui.AddSpacer(page, this, 12);
        return page;
    }

    private View DevelopmentCard(string title, string subtitle, string body)
    {
        var card = Ui.Card(P, this);
        var top = Ui.Row(this);
        top.AddView(Ui.Text(this, title, 15f, P.TextStrong, bold: true),
            new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f));
        top.AddView(Ui.Badge(this, P, "В РАЗРАБОТКЕ", P.Warning));
        card.AddView(top);
        var sub = Ui.Text(this, subtitle, 12f, P.Warning);
        sub.SetPadding(0, Ui.Dp(this, 3), 0, 0);
        card.AddView(sub);
        var hint = Ui.Hint(this, body, P);
        hint.SetPadding(0, Ui.Dp(this, 6), 0, 0);
        card.AddView(hint);
        return card;
    }

    private void StartLastBypass()
    {
        var name = AndroidSettingsStore.Current.TunBypassStrategy;
        var mode = Enum.TryParse<StreamFragmentMode>(name, out var parsed)
            ? parsed
            : StreamFragmentMode.RecordSplit;

        var request = VpnStarter.PrepareRequest(this);
        if (request is not null)
        {
            _pendingBypass = mode;
            StartActivityForResult(request, VpnRequestCode);
            return;
        }

        VpnStarter.StartBypass(this, mode);
        Ui.Toast(this, "Запускаю TUN-обход…");
    }

    // --------------------------------------------------------------- инструменты

    private View BuildToolsPage()
    {
        var page = ColumnPage();
        page.AddView(Ui.Section(this, "Инструменты", P));

        // Сведений об серверах здесь нет: для них есть отдельная вкладка «Серверы».
        // Остаётся только счётчик источников — он относится к этому разделу.
        var sources = 0;
        try
        {
            if (NodeStore.IsLoaded)
            {
                sources = NodeStore.SnapshotSources().Count;
            }
            else
            {
                _ = Task.Run(() => NodeStore.EnsureLoaded());
            }
        }
        catch (Exception exception)
        {
            AppLog.Write("android", "Не удалось прочитать сводку инструментов", exception);
        }

        // Ни сводки серверов, ни строки «Готово», ни кнопок проверки здесь нет:
        // для этого есть отдельные экран «Серверы» и кнопка «Проверить серверы» в шапке.
        page.AddView(Ui.Section(this, "Разделы", P));

        AddTile(page, Resource.Drawable.ic_nodes, "Серверы",
            "Сводка, проверка и выбор сервера",
            () => Open(typeof(NodesActivity)), P.Universal);
        AddTile(page, Resource.Drawable.ic_sources, "Источники",
            $"{sources} шт. · подписки, списки и Telegram-каналы",
            () => Open(typeof(SourcesActivity)), P.Accent);
        AddTile(page, Resource.Drawable.ic_tunnel, "Туннелирование",
            "Маршрутизация по доменам и приложениям",
            () => Open(typeof(TunnelActivity)), P.Discord);
        AddTile(page, Resource.Drawable.ic_service_telegram, "Telegram MTProtoProxies",
            $"{MtProtoStore.WorkingCount} рабочих прокси · каналы через RSS",
            () => Open(typeof(TelegramActivity)), P.Telegram);
        AddTile(page, Resource.Drawable.ic_settings, "Настройки",
            "Тема, обновления, данные и VPN, автоматика",
            () => Open(typeof(SettingsActivity)), P.MutedStrong);
        AddTile(page, Resource.Drawable.ic_info, "О программе и лицензии",
            "Версия, GPL-3.0, сторонние компоненты",
            () => Open(typeof(AboutActivity)), P.Accent);

        page.AddView(Ui.Section(this, "Автоматика", P));
        var discover = Ui.Pill(this, P, "Найти новые источники", UiButtonKind.Secondary,
            () => _ = RunDiscoveryAsync(), Resource.Drawable.ic_globe);
        page.AddView(discover);
        var export = Ui.Pill(this, P, "Экспорт профиля", UiButtonKind.Secondary,
            ExportProfile, Resource.Drawable.ic_export);
        Ui.Add(page, export, 8);

        var auto = Ui.Banner(P, this, P.Accent, "Работает само",
            "Фоновое обслуживание раз в 30 минут обновляет устаревшие источники, собирает MTProto " +
            "и удаляет нерабочие записи. Проверка серверов идёт в отдельном процессе :probe.");
        Ui.Add(page, auto, 14);

        return page;
    }

    private void AddTile(LinearLayout page, int icon, string title, string subtitle, Action click, Color tone)
    {
        var tile = Ui.IconTile(this, P, icon, title, subtitle, click, tone);
        var layout = new LinearLayout.LayoutParams(
            ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent);
        layout.BottomMargin = Ui.Dp(this, 8);
        page.AddView(tile, layout);
    }

    /// <summary>Подпись итога проверки — общая для «Инструментов» и экрана «Серверы».</summary>
    internal static string DescribeTestResult(NodeTestSummary summary) => summary.Error is not null
        ? summary.Error
        : $"Проверено {summary.Tested}: рабочих {summary.Working} " +
          $"(универс. {summary.Universal}, YouTube {summary.Youtube}, " +
          $"Telegram {summary.Telegram}, Discord {summary.Discord}), " +
          $"лучший открывает доменов {summary.BestDomains}, удалено {summary.Removed}";

    /// <summary>
    /// Автопоиск новых источников. Строки состояния на странице больше нет —
    /// ход и итог показываем подсказкой и строкой шапки.
    /// </summary>
    private async Task RunDiscoveryAsync()
    {
        ShowHeaderDetail("Ищу новые источники на GitHub и публичных сайтах…", 3600);
        Ui.Toast(this, "Ищу новые источники…");
        try
        {
            var result = await SourceDiscoveryService.DiscoverAsync(
                NodeStore.SnapshotSources(), null, CancellationToken.None);
            var added = NodeStore.AddDiscovered(result);
            var mtproto = MtProtoStore.AddDiscovered(result.MtProto);
            var message = $"Новых источников {added}, серверов {result.Nodes.Count}, MTProto {mtproto}" +
                          (result.Error is null ? string.Empty : ". Ошибка: " + result.Error);
            ShowHeaderDetail(message, 20);
            Ui.Toast(this, message);
            RefreshHeader();
        }
        catch (Exception exception)
        {
            AppLog.Write("discover", "Автопоиск не удался", exception);
            ShowHeaderDetail("Автопоиск не удался: " + exception.Message, 20);
            Ui.Toast(this, "Автопоиск не удался: " + exception.Message);
        }
    }

    private void ExportProfile()
    {
        var node = NodeStore.Selected ?? NodeStore.Snapshot().FirstOrDefault();
        if (node is null)
        {
            Ui.Toast(this, "Нет сервера для экспорта — обновите источники");
            return;
        }

        var options = new[] { "Ссылка сервера", "Список ссылок", "Конфиг sing-box (JSON)" };
        using var dialog = new AlertDialog.Builder(this)
            .SetTitle("Экспорт профиля")
            .SetItems(options, (_, args) =>
            {
                var text = args.Which switch
                {
                    0 => ProfileExport.BuildShareLink(node),
                    1 => ProfileExport.BuildLinkList(NodeStore.OrderForConfig(NodeStore.Snapshot()).Take(20)),
                    _ => ProfileExport.BuildConfig(node, tunnel: true),
                };
                ShareText(text, "Профиль VieNLink");
            })
            .SetNegativeButton("Отмена", (_, _) => { })
            .Create();
        dialog.Show();
    }

    private void ShareText(string text, string subject)
    {
        try
        {
            var send = new Intent(Intent.ActionSend)
                .SetType("text/plain")
                .PutExtra(Intent.ExtraText, text)
                .PutExtra(Intent.ExtraSubject, subject);
            StartActivity(Intent.CreateChooser(send, subject));
        }
        catch (Exception exception)
        {
            Ui.Toast(this, "Не удалось поделиться: " + exception.Message);
        }
    }

    // ------------------------------------------------------------------ журнал

    private View BuildLogPage()
    {
        var page = ColumnPage();
        page.AddView(Ui.Section(this, "Журнал", P));

        var info = Ui.Hint(this, "Читаю…", P);
        var view = new TextView(this)
        {
            Typeface = Typeface.Monospace,
            TextSize = 10.5f,
        };
        view.SetTextColor(P.MutedStrong);
        view.SetTextIsSelectable(true);
        view.SetBackgroundColor(P.Inner);
        view.SetPadding(Ui.Dp(this, 10), Ui.Dp(this, 10), Ui.Dp(this, 10), Ui.Dp(this, 10));

        var actions = Ui.Row(this);
        var refresh = Ui.Pill(this, P, "Обновить", UiButtonKind.Accent,
            () => _ = LoadLogAsync(view, info), Resource.Drawable.ic_refresh);
        var share = Ui.Pill(this, P, "Поделиться", UiButtonKind.Secondary,
            () => ShareText(view.Text ?? string.Empty, "VieNLink — журнал"), Resource.Drawable.ic_export);
        actions.AddView(refresh, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f));
        actions.AddView(new Space(this), new LinearLayout.LayoutParams(Ui.Dp(this, 8), 1));
        actions.AddView(share, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f));
        page.AddView(actions);

        var core = new CheckBox(this)
        {
            Text = "Лог ядра (stderr)",
            Checked = _logCore,
        };
        core.SetTextColor(P.TextSoft());
        core.CheckedChange += (_, args) =>
        {
            _logCore = args.IsChecked;
            _ = LoadLogAsync(view, info);
        };
        Ui.Add(page, core, 8);

        var clear = Ui.Pill(this, P, "Очистить сегодняшний журнал", UiButtonKind.Ghost, () =>
        {
            if (LogReader.Clear())
            {
                Ui.Toast(this, "Журнал очищен");
                _ = LoadLogAsync(view, info);
            }
        }, Resource.Drawable.ic_delete);
        Ui.Add(page, clear, 8);

        page.AddView(info);
        var viewLayout = new LinearLayout.LayoutParams(
            ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent);
        viewLayout.TopMargin = Ui.Dp(this, 8);
        page.AddView(view, viewLayout);

        var hint = Ui.Hint(this, "Показываются последние 200 строк. Журналы хранятся 14 дней, старые удаляются автоматически.", P);
        Ui.Add(page, hint, 8);

        _ = LoadLogAsync(view, info);
        return page;
    }

    private async Task LoadLogAsync(TextView view, TextView info)
    {
        try
        {
            var core = _logCore;
            var (text, source) = await Task.Run(() => LogReader.Read(core, 200));
            RunOnUiThread(() =>
            {
                view.Text = text;
                info.Text = source;
            });
        }
        catch (Exception exception)
        {
            RunOnUiThread(() =>
            {
                view.Text = "Не удалось прочитать журнал: " + exception.Message;
                info.Text = "Ошибка";
            });
        }
    }

}
