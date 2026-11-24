using Android.App;
using Android.Content;
using Android.Graphics;
using Android.Views;
using Android.Widget;

namespace VieNLink.Mobile;

public sealed partial class MainActivity
{
    private StreamFragmentMode? _pendingBypass;

    // -------------------------------------------------------------- подключение

    private View BuildConnectionPage()
    {
        var page = ColumnPage();

        // Кнопки сервисов перенесены в шапку: здесь остаётся пояснение и MTProto.
        var explanation = Ui.Banner(P, this, P.Accent, "Быстрое подключение",
            "Кнопки сервисов — в шапке: нажмите сервис, и VieNLink выберет проверенный сервер " +
            "с доступом к нему и подключится. На каждой кнопке видно, сколько серверов готовы. " +
            "После подключения в шапке появятся страна, задержка, скорость и оценка.");
        page.AddView(explanation);

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
                        ? "TUN-обход и прокси-сервер взаимоисключающие: при подключении к серверу обход останавливается."
                        : "TUN-обход (бета) работает без сервера. Остальные сайты — через прокси-сервер.",
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
            "Без прокси-сервера: sing-box поднимает TUN, TCP идёт в собственный " +
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

        Ui.AddSpacer(page, this, 12);
        return page;
    }

    private void StartLastBypass()
    {
        if (!VpnStarter.CanStartBypass(out var blockedReason))
        {
            Ui.Toast(this, blockedReason);
            return;
        }

        var name = NetworkPresets.GetPage(this);
        var mode = Enum.TryParse<StreamFragmentMode>(name, out var parsed)
            ? parsed
            : StreamFragmentMode.Oob;

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
        if (!DeviceProfile.IsTelevision)
        {
            AddTile(page, Resource.Drawable.ic_service_telegram, "Telegram MTProtoProxies",
                $"{MtProtoStore.WorkingCount} рабочих прокси · каналы через RSS",
                () => Open(typeof(TelegramActivity)), P.Telegram);
        }
        AddTile(page, Resource.Drawable.ic_delete, "Отсев — Перепроверка",
            "Рабочие серверы, переставшие работать",
            () => Open(typeof(RejectedActivity)), P.Warning);
        AddTile(page, Resource.Drawable.ic_tab_log, "Журнал",
            "События приложения и лог ядра sing-box",
            () => Open(typeof(LogActivity)), P.Warning);
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
                NodeStore.SnapshotSources(), null, CancellationToken.None,
                NodeStore.Settings.SourceFreshnessHours,
                includeWeb: !DeviceProfile.IsTelevision);
            var added = await Task.Run(() => NodeStore.AddDiscovered(result));
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

}
