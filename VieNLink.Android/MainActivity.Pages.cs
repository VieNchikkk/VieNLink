using Android.App;
using Android.Content;
using Android.Graphics;
using Android.OS;
using Android.Views;
using Android.Widget;

namespace VieNLink.Mobile;

public sealed partial class MainActivity
{
    private StreamFragmentMode? _pendingBypass;

    // Ссылки на карточку обхода: состояние меняется из плитки/уведомления, а страница
    // строится один раз — иначе «Обход выключен» висит поверх работающего TUN.
    private TextView? _bypassBadge;
    /// <summary>Последнее показанное состояние плашки обхода: фон не пересобираем зря.</summary>
    private bool? _bypassBadgeOn;
    private TextView? _bypassStart;
    private TextView? _bypassStop;
    private TextView? _bypassPageStatus;
    private TextView? _bypassPageHint;

    // Подбор стратегии и QUIC — прямо на главном экране, рядом с включением обхода.
    private TextView? _bypassTune;
    private TextView? _bypassTuneStatus;
    private CheckBox? _bypassQuic;
    private string _bypassQuicKey = string.Empty;
    private CancellationTokenSource? _tuneCts;

    // -------------------------------------------------------------- подключение

    private View BuildConnectionPage()
    {
        var page = ColumnPage();

        // Обход — сразу под шапкой: главное действие без сервера, не нужно искать вкладку.
        var bypass = VpnStarter.IsBypassRunning;
        var serverRunning = VpnStarter.IsServerRunning;
        var tunCard = Ui.Card(P, this);
        var tunTop = Ui.Row(this);
        tunTop.AddView(Ui.Text(this, "Обход блокировок без сервера", 15f, P.TextStrong, bold: true),
            new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f));
        _bypassBadge = Ui.Badge(this, P, bypass ? "РАБОТАЕТ" : "ВЫКЛЮЧЕН",
            bypass ? P.Positive : P.Muted);
        tunTop.AddView(_bypassBadge);
        tunCard.AddView(tunTop);
        tunCard.AddView(Ui.Hint(this,
            "Без прокси-сервера. Настройка и подробности — на вкладке «Обход».", P));
        var tunButtons = Ui.Row(this);
        var startBypass = Ui.Pill(this, P, "Использовать обход", UiButtonKind.Accent,
            StartBypassFromMain, Resource.Drawable.ic_power);
        var stopBypass = Ui.Pill(this, P, "Выключить обход", UiButtonKind.Secondary,
            StopBypassFromMain);
        _bypassStart = startBypass;
        _bypassStop = stopBypass;
        startBypass.Enabled = !bypass && !serverRunning;
        startBypass.Alpha = startBypass.Enabled ? 1f : 0.5f;
        stopBypass.Enabled = bypass;
        stopBypass.Alpha = stopBypass.Enabled ? 1f : 0.5f;
        tunButtons.AddView(startBypass, new LinearLayout.LayoutParams(
            0, ViewGroup.LayoutParams.WrapContent, 1f));
        tunButtons.AddView(new Space(this), new LinearLayout.LayoutParams(Ui.Dp(this, 8), 1));
        tunButtons.AddView(stopBypass, new LinearLayout.LayoutParams(
            0, ViewGroup.LayoutParams.WrapContent, 1f));
        var tunButtonsLayout = new LinearLayout.LayoutParams(
            ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent);
        tunButtonsLayout.TopMargin = Ui.Dp(this, 10);
        tunCard.AddView(tunButtons, tunButtonsLayout);

        // Подбор стратегии — здесь же, где включается обход: не нужно искать экран TUN.
        // Кнопка работает и как «Остановить подбор», пока идёт перебор.
        _bypassTune = Ui.Pill(this, P, "Подобрать стратегию", UiButtonKind.Accent,
            TuneBypassFromMain, Resource.Drawable.ic_speed);
        var tuneLayout = new LinearLayout.LayoutParams(
            ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent);
        tuneLayout.TopMargin = Ui.Dp(this, 8);
        tunCard.AddView(_bypassTune, tuneLayout);

        _bypassTuneStatus = Ui.Hint(this, string.Empty, P);
        _bypassTuneStatus.Visibility = ViewStates.Gone;
        _bypassTuneStatus.SetPadding(0, Ui.Dp(this, 6), 0, 0);
        tunCard.AddView(_bypassTuneStatus);

        // QUIC — параметр сети, применяется при следующем включении обхода.
        var quic = new CheckBox(this)
        {
            Text = "Блокировать QUIC (UDP/443), чтобы браузер шёл по TCP",
            Checked = NetworkPresets.GetBlockQuic(this),
        };
        _bypassQuic = quic;
        quic.SetTextColor(P.TextSoft());
        Ui.StyleSystemText(quic);
        quic.CheckedChange += (_, args) =>
        {
            NetworkPresets.SetBlockQuic(this, args.IsChecked);
            AppLog.Write("tun-bypass",
                $"QUIC на «{NetworkPresets.Label(this)}»: " +
                (args.IsChecked ? "блокировать (HTTP-соединения пойдут по TCP)" : "через релей с фейком"));
        };
        var quicLayout = new LinearLayout.LayoutParams(
            ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent);
        quicLayout.TopMargin = Ui.Dp(this, 8);
        tunCard.AddView(quic, quicLayout);

        var quicHint = Ui.Hint(this,
            "Видео не идёт — попробуйте наоборот: галочка стоит — трафик по TCP; снята — QUIC " +
            "через релей с фейком. После смены перезапустите обход.", P);
        quicHint.SetPadding(Ui.Dp(this, 28), 0, 0, 0);
        tunCard.AddView(quicHint);

        Ui.Add(page, tunCard, 12);

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

    /// <summary>
    /// Обновляет карточки обхода без пересборки страницы: обход могут включить
    /// с плитки или из уведомления, а страница «Подключение»/«Обход» уже построена.
    /// </summary>
    private void RefreshBypassState(bool bypass, bool serverRunning)
    {
        // Плашку и фон меняем только при смене состояния: шапка обновляется каждые
        // 3 секунды, а здесь каждый такт создавался новый GradientDrawable.
        if (_bypassBadge is not null && _bypassBadgeOn != bypass)
        {
            _bypassBadgeOn = bypass;
            var tone = bypass ? P.Positive : P.Muted;
            _bypassBadge.Text = bypass ? "РАБОТАЕТ" : "ВЫКЛЮЧЕН";
            _bypassBadge.SetTextColor(tone);
            _bypassBadge.Background = Ui.Rounded(
                Color.Argb(40, tone.R, tone.G, tone.B), 8, this,
                Color.Argb(110, tone.R, tone.G, tone.B));
        }

        if (_bypassStart is not null)
        {
            _bypassStart.Enabled = !bypass && !serverRunning;
            _bypassStart.Alpha = _bypassStart.Enabled ? 1f : 0.5f;
        }

        if (_bypassStop is not null)
        {
            _bypassStop.Enabled = bypass;
            _bypassStop.Alpha = _bypassStop.Enabled ? 1f : 0.5f;
        }

        // Подбор возможен только на остановленном обходе (иначе проверка шла бы поверх туннеля).
        if (_bypassTune is not null)
        {
            var canTune = !bypass && !serverRunning;
            _bypassTune.Enabled = canTune;
            _bypassTune.Alpha = canTune ? 1f : 0.5f;
        }

        if (_bypassPageStatus is not null)
        {
            SetText(_bypassPageStatus, bypass
                ? "TUN-обход (бета) работает"
                : serverRunning ? "Подключён прокси-сервер" : "Обход выключен",
                bypass || serverRunning ? P.Positive : P.MutedStrong);
        }

        if (_bypassPageHint is not null)
        {
            SetText(_bypassPageHint, bypass
                ? "Трафик идёт в TUN и в локальный форвардер с фрагментацией TLS. Прокси-сервер не нужен. " +
                  "Режим в бете: часть сайтов может не открываться."
                : serverRunning
                    ? "TUN-обход и прокси-сервер взаимоисключающие: при подключении к серверу обход останавливается."
                    : "TUN-обход (бета) работает без сервера. Остальные сайты — через прокси-сервер.");
        }
    }

    private void StartBypassFromMain()
    {
        if (!VpnStarter.CanStartBypass(out var reason))
        {
            Ui.Toast(this, reason);
            return;
        }

        var name = NetworkPresets.ResolvePage(this);
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
        new Handler(Looper.MainLooper!).PostDelayed(() => SelectTab(0), 900);
    }

    /// <summary>
    /// Приводит галочку QUIC к пресету текущей сети. Вызывается при возврате в приложение:
    /// после смены Wi-Fi/LTE значение для этой сети может отличаться. Каждый такт шапки
    /// этого не делаем — определение сети (SSID/MCCMNC) не бесплатное.
    /// </summary>
    private void SyncBypassQuic()
    {
        if (_bypassQuic is null)
        {
            return;
        }

        try
        {
            var key = NetworkPresets.Key(this);
            if (string.Equals(key, _bypassQuicKey, StringComparison.Ordinal))
            {
                return;
            }

            _bypassQuicKey = key;
            var block = NetworkPresets.GetBlockQuic(this);
            if (_bypassQuic.Checked != block)
            {
                _bypassQuic.Checked = block;
            }
        }
        catch (Exception exception)
        {
            AppLog.Write("tun-bypass", "Не удалось обновить галочку QUIC", exception);
        }
    }

    private void StopBypassFromMain()
    {
        VpnStarter.Stop(this);
        Ui.Toast(this, "Выключаю TUN-обход…");
        new Handler(Looper.MainLooper!).PostDelayed(() => SelectTab(0), 900);
    }

    /// <summary>
    /// Подбор стратегии прямо с главного экрана: быстрый перебор («Первая рабочая»)
    /// с проверкой страницы, видео и QUIC. Подтверждённая стратегия сохраняется для
    /// текущей сети и обход включается сразу; подробный перебор («Подобрать лучшую»)
    /// остаётся на экране TUN.
    /// </summary>
    private async void TuneBypassFromMain()
    {
        // Пока идёт подбор, эта же кнопка его останавливает.
        if (_tuneCts is not null)
        {
            _tuneCts.Cancel();
            if (_bypassTuneStatus is not null)
            {
                _bypassTuneStatus.Text = "Останавливаю подбор…";
            }

            return;
        }

        if (VpnStarter.IsBypassRunning || VpnStarter.IsServerRunning)
        {
            Ui.Toast(this, "Сначала выключите обход или прокси-сервер");
            return;
        }

        if (_connecting)
        {
            Ui.Toast(this, "Дождитесь окончания подключения");
            return;
        }

        var cts = new CancellationTokenSource();
        _tuneCts = cts;
        SetTuneRunning(true);
        if (_bypassTuneStatus is not null)
        {
            _bypassTuneStatus.Visibility = ViewStates.Visible;
            _bypassTuneStatus.Text = "Готовлюсь…";
        }

        AppLog.Write("tun-bypass",
            "Подбор стратегии с главного экрана: " + NetworkPresets.Describe(this));
        var progress = new Progress<AutotuneStep>(step =>
        {
            AppLog.Write("tun-bypass", "автоподбор: " + step.Text);
            if (_bypassTuneStatus is not null)
            {
                _bypassTuneStatus.Text = step.Text;
            }
        });

        try
        {
            var outcome = await BypassAutotune.RunAsync(this, AutotuneMode.Quick, progress, cts.Token);
            // Галочка QUIC могла быть выставлена пробой — приводим её к сохранённому пресету.
            if (_bypassQuic is not null)
            {
                _bypassQuic.Checked = NetworkPresets.GetBlockQuic(this);
            }

            var summary = outcome.Summary(NetworkPresets.Label(this));
            if (_bypassTuneStatus is not null)
            {
                _bypassTuneStatus.Text = summary;
            }

            Ui.Toast(this, summary);
            AppLog.Write("tun-bypass", "Подбор стратегии (главный экран): " + summary);

            if (outcome.Strong)
            {
                StartBypassFromMain();
            }
        }
        catch (System.OperationCanceledException)
        {
            if (_bypassTuneStatus is not null)
            {
                _bypassTuneStatus.Text = "Подбор остановлен.";
            }
        }
        catch (Exception exception)
        {
            AppLog.Write("tun-bypass", "Подбор стратегии с главного экрана не удался", exception);
            if (_bypassTuneStatus is not null)
            {
                _bypassTuneStatus.Text = "Подбор не удался: " + exception.Message;
            }

            Ui.Toast(this, "Подбор не удался: " + exception.Message);
        }
        finally
        {
            if (ReferenceEquals(_tuneCts, cts))
            {
                _tuneCts = null;
            }

            cts.Dispose();
            SetTuneRunning(false);
        }
    }

    /// <summary>Кнопка подбора: «Подобрать стратегию» ↔ «Остановить подбор».</summary>
    private void SetTuneRunning(bool running)
    {
        if (_bypassTune is null)
        {
            return;
        }

        _bypassTune.Text = running ? "Остановить подбор" : "Подобрать стратегию";
        // Кнопка остаётся активной и во время подбора: ею же подбор останавливается.
        _bypassTune.Enabled = true;
        _bypassTune.Alpha = 1f;
    }

    private View BuildBypassPage()
    {
        var page = ColumnPage();
        page.AddView(Ui.Section(this, "Обход блокировок", P));

        var running = TunnelService.State == TunnelState.Running;
        var bypass = TunnelService.IsBypassMode;
        _bypassPageStatus = Ui.Text(this,
            bypass ? "TUN-обход (бета) работает" : running ? "Подключён прокси-сервер" : "Обход выключен",
            16f, bypass || running ? P.Positive : P.MutedStrong, bold: true);
        _bypassPageHint = Ui.Hint(this,
            bypass
                ? "Трафик идёт в TUN и в локальный форвардер с фрагментацией TLS. Прокси-сервер не нужен. " +
                  "Режим в бете: часть сайтов может не открываться."
                : running
                    ? "TUN-обход и прокси-сервер взаимоисключающие: при подключении к серверу обход останавливается."
                    : "TUN-обход (бета) работает без сервера. Остальные сайты — через прокси-сервер.",
            P);
        var status = Ui.Card(P, this, _bypassPageStatus, _bypassPageHint);
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
        // Одна понятная кнопка: открывает экран TUN, где видно текущую стратегию,
        // данные обхода и кнопки «Включить TUN-обход» / «Выключить обход».
        var open = Ui.Pill(this, P, "Настроить и включить", UiButtonKind.Accent,
            () => Open(typeof(BypassActivity)), Resource.Drawable.ic_tunnel);
        tunActions.AddView(open, new LinearLayout.LayoutParams(
            ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent));
        var actionsLayout = new LinearLayout.LayoutParams(
            ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent);
        actionsLayout.TopMargin = Ui.Dp(this, 10);
        tun.AddView(tunActions, actionsLayout);
        page.AddView(tun);

        Ui.AddSpacer(page, this, 12);
        return page;
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
