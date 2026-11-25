using Android.App;
using Android.Content;
using Android.Graphics;
using Android.OS;
using Android.Views;
using Android.Widget;

namespace VieNLink.Mobile;

/// <summary>
/// TUN-обход без прокси-сервера: TUN → встроенный SOCKS5-форвардер, который режет TLS ClientHello.
/// Стратегии те же, что в общем ядре; удачный выбор запоминается в bypass-profiles.json.
/// Автоподбор из Android-версии недоступен: процесс приложения исключён из TUN, поэтому
/// проверить результат можно только из другого приложения или после отключения.
/// </summary>
#if DEBUG
[Activity(Label = "TUN-обход", Theme = "@style/Theme.VieNLink", Exported = true)]
#else
[Activity(Label = "TUN-обход", Theme = "@style/Theme.VieNLink")]
#endif
public sealed class BypassActivity : BaseActivity
{
    private const int VpnRequestCode = 3003;
    private const int StatusIntervalMs = 2000;

    private readonly List<StreamFragmentStrategy> _strategies = TlsFragmentForwarder.Strategies
        .OrderByDescending(strategy => strategy.Mode == StreamFragmentMode.Oob)
        .ThenByDescending(strategy => strategy.Mode == StreamFragmentMode.RecordSplit)
        .ToList();

    private LinearLayout? _list;
    private TextView? _status;
    private TextView? _forwarder;
    private TextView? _strategyHeader;
    private TextView? _stats;
    private CheckBox? _quicCheck;
    private string? _lastPageResult;
    private string? _lastVideoResult;
    private string? _lastQuicResult;
    private Button? _start;
    private Button? _stop;
    private Button? _autotuneQuick;
    private Button? _autotuneBest;
    private TextView? _autotuneStatus;
    private TextView? _autotunePhase;
    private ProgressBar? _autotuneProgress;
    private CancellationTokenSource? _autotuneCts;
    private Spinner? _videoSpinner;
    private bool _videoSpinnerReady;
    private int _selected;
    private string _lastNetworkKey = string.Empty;
    private Handler? _handler;
    private Action? _tick;

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        BuildUi();

        var saved = NetworkPresets.ResolvePage(this);

        _selected = Math.Max(0, _strategies.FindIndex(strategy =>
            strategy.Mode.ToString().Equals(saved, StringComparison.OrdinalIgnoreCase)));

        // Если обход уже работает, показываем выбранной реально запущенную стратегию,
        // а не только сохранённый пресет (они могут разойтись после смены сети).
        if (ForwarderHost.IsRunning)
        {
            var running = _strategies.FindIndex(strategy => strategy.Mode == ForwarderHost.Mode);
            if (running >= 0)
            {
                _selected = running;
            }
        }

        AppLog.Write("tun-bypass", "Экран TUN: " + NetworkPresets.Describe(this));
        _lastNetworkKey = NetworkPresets.Key(this);

        if (Intent?.GetBooleanExtra("start", false) == true)
        {
            _ = new Handler(Looper.MainLooper!).Post(StartBypass);
        }

        RenderStrategies();
        UpdateButtons();
    }

    private void BuildUi()
    {
        var page = ColumnPage();

        var top = Ui.Row(this);
        top.AddView(Ui.Pill(this, P, "Назад", UiButtonKind.Ghost, Finish));
        var title = Ui.Title(this, "TUN-обход", P);
        title.SetPadding(Ui.Dp(this, 10), 0, 0, 0);
        top.AddView(title, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f));
        top.AddView(Ui.Badge(this, P, "БЕТА", P.Warning));
        page.AddView(top);

        var info = Ui.Banner(P, this, P.Accent, "Без сервера и без root",
            "sing-box поднимает TUN, TCP идёт в собственный SOCKS5-форвардер, который режет TLS " +
            "ClientHello. На SNI-блоке страницы открываются; при блоке по IP (часто видео YouTube) " +
            "нужен прокси-сервер.");
        page.AddView(info);

        var state = Ui.HeaderCard(P, this);
        _status = Ui.Text(this, VpnStarter.StateLabel, 16f, P.TextStrong, bold: true);
        state.AddView(_status);
        _forwarder = Ui.Hint(this, "Форвардер не запущен", P);
        _forwarder.SetPadding(0, Ui.Dp(this, 6), 0, 0);
        state.AddView(_forwarder);
        page.AddView(state);

        // Все данные, которые участвуют в выборе: сеть, источники пресетов, база оператора,
        // счётчики форвардера и итог последнего подбора — чтобы не лазить в журнал.
        var statsCard = Ui.Card(P, this);
        statsCard.AddView(Ui.Text(this, "Данные обхода", 14.5f, P.TextStrong, bold: true));
        _stats = Ui.Hint(this, "…", P);
        _stats.SetPadding(0, Ui.Dp(this, 4), 0, 0);
        statsCard.AddView(_stats);
        Ui.Add(page, statsCard, 10);

        var actions = Ui.Row(this);
        _start = MakePrimary("Включить TUN-обход", StartBypass);
        _stop = MakeSecondary("Выключить обход", () => VpnStarter.Stop(this));
        actions.AddView(_start, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f));
        actions.AddView(new Space(this), new LinearLayout.LayoutParams(Ui.Dp(this, 8), 1));
        actions.AddView(_stop, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f));
        Ui.Add(page, actions, 12);

        var autotune = Ui.Card(P, this);
        var autotuneTop = Ui.Row(this);
        autotuneTop.AddView(Ui.Text(this, "Подбор стратегии", 14.5f, P.TextStrong, bold: true),
            new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f));
        autotuneTop.AddView(Ui.Badge(this, P, "5–10 МИН", P.Accent));
        autotune.AddView(autotuneTop);

        // Показатели хода: какой этап (страница/видео), какая стратегия по счёту и что вышло.
        _autotunePhase = Ui.Text(this, string.Empty, 14f, P.TextStrong, bold: true);
        _autotunePhase.Visibility = ViewStates.Gone;
        _autotunePhase.SetPadding(0, Ui.Dp(this, 6), 0, 0);
        autotune.AddView(_autotunePhase);

        _autotuneProgress = new ProgressBar(this, null, Android.Resource.Attribute.ProgressBarStyleHorizontal)
        {
            Indeterminate = false,
            Max = 1,
            Progress = 0,
            Visibility = ViewStates.Gone,
        };
        _autotuneProgress.ProgressTintList = Android.Content.Res.ColorStateList.ValueOf(P.Accent);
        _autotuneProgress.ProgressBackgroundTintList = Android.Content.Res.ColorStateList.ValueOf(P.Inner);
        var progressLayout = new LinearLayout.LayoutParams(
            ViewGroup.LayoutParams.MatchParent, Ui.Dp(this, 8));
        progressLayout.TopMargin = Ui.Dp(this, 6);
        progressLayout.BottomMargin = Ui.Dp(this, 6);
        autotune.AddView(_autotuneProgress, progressLayout);

        _autotuneStatus = Ui.Hint(this,
            "«Первая рабочая» — быстрый перебор: останавливается на первой стратегии, где открылись " +
            "страница и превью. «Подобрать лучшую» — проверяет все и выбирает по числу доменов и " +
            "скорости (КБ/с), затем так же ищет лучшую для видео по реальному потоку. Результат " +
            "сохраняется для текущей сети и обход включается сразу. Подбор можно остановить кнопкой. " +
            "Если видео идёт, а проба пишет «0 байт» — видео тянется по QUIC, TCP-стратегия ему не нужна.", P);
        _autotuneStatus.SetPadding(0, Ui.Dp(this, 4), 0, Ui.Dp(this, 8));
        autotune.AddView(_autotuneStatus);

        var autotuneActions = Ui.Row(this);
        _autotuneQuick = MakeSecondary("Первая рабочая", () => RunAutotune(AutotuneMode.Quick));
        _autotuneBest = MakeSecondary("Подобрать лучшую", () => RunAutotune(AutotuneMode.Best));
        autotuneActions.AddView(_autotuneQuick,
            new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f));
        autotuneActions.AddView(new Space(this), new LinearLayout.LayoutParams(Ui.Dp(this, 8), 1));
        autotuneActions.AddView(_autotuneBest,
            new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f));
        autotune.AddView(autotuneActions);

        var sharePresets = MakeSecondary("Поделиться подборками под операторов", ShareProviderPresets);
        var shareLayout = new LinearLayout.LayoutParams(
            ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent);
        shareLayout.TopMargin = Ui.Dp(this, 8);
        autotune.AddView(sharePresets, shareLayout);
        Ui.Add(page, autotune, 12);

        var videoCard = Ui.Card(P, this);
        videoCard.AddView(Ui.Text(this, "Стратегия для видео (googlevideo)", 14.5f, P.TextStrong, bold: true));
        videoCard.AddView(Ui.Hint(this,
            "Для приложения YouTube обычно работает та же OOB, что и для страниц. " +
            "Если видео тормозит — подберите отдельную стратегию для googlevideo/ytimg; " +
            "если видео идёт по QUIC, эта стратегия на него не влияет.", P));
        _videoSpinner = new Spinner(this);
        var videoItems = new List<string> { "Как для страниц" };
        videoItems.AddRange(_strategies.Select(strategy => strategy.Name));
        var videoAdapter = new ArrayAdapter<string>(
            this, Android.Resource.Layout.SimpleSpinnerItem, videoItems);
        videoAdapter.SetDropDownViewResource(Android.Resource.Layout.SimpleSpinnerDropDownItem);
        _videoSpinner.Adapter = videoAdapter;
        var savedVideo = NetworkPresets.GetVideo(this);
        var savedVideoIndex = _strategies.FindIndex(
            strategy => strategy.Mode.ToString().Equals(savedVideo, StringComparison.OrdinalIgnoreCase));
        _videoSpinner.SetSelection(savedVideoIndex >= 0 ? savedVideoIndex + 1 : 0, false);
        _videoSpinner.ItemSelected += (_, args) =>
        {
            if (!_videoSpinnerReady)
            {
                return;
            }

            var position = (int)args.Position;
            var mode = position <= 0 ? string.Empty : _strategies[position - 1].Mode.ToString();
            NetworkPresets.SetVideo(this, mode);
            AppLog.Write("tun-bypass",
                $"Видео-стратегия для «{NetworkPresets.Label(this)}»: " +
                (string.IsNullOrEmpty(mode) ? "как для страниц" : mode));
        };
        _videoSpinnerReady = true;
        videoCard.AddView(_videoSpinner);
        Ui.Add(page, videoCard, 10);

        var quic = new CheckBox(this)
        {
            Text = "Блокировать QUIC (UDP/443), чтобы браузер шёл по TCP",
            Checked = NetworkPresets.GetBlockQuic(this),
        };
        _quicCheck = quic;
        quic.SetTextColor(P.TextSoft());
        Ui.StyleSystemText(quic);
        quic.CheckedChange += (_, args) =>
        {
            NetworkPresets.SetBlockQuic(this, args.IsChecked);
            AppLog.Write("tun-bypass",
                $"QUIC на «{NetworkPresets.Label(this)}»: " +
                (args.IsChecked ? "блокировать (HTTP-соединения пойдут по TCP)" : "через релей с фейком"));
        };
        page.AddView(quic);
        var quicHint = Ui.Hint(this,
            "QUIC обходит TCP-фрагментацию. Галочка стоит — UDP/443 блокируется и Chrome с YouTube " +
            "переходят на TCP. Галочка снята — QUIC идёт через форвардер: перед настоящим Initial " +
            "уходит фейковый с безобидным SNI (www.google.com). Если с фейком видео стало хуже — " +
            "верните галочку: на некоторых операторах QUIC напрямую работает лучше.", P);
        quicHint.SetPadding(Ui.Dp(this, 28), 0, 0, Ui.Dp(this, 8));
        page.AddView(quicHint);

        var allHosts = new CheckBox(this)
        {
            Text = "Резать все сайты, а не только YouTube / Telegram / Discord",
            Checked = SettingsStore.Current.TunBypassAllHosts,
        };
        allHosts.SetTextColor(P.TextSoft());
        Ui.StyleSystemText(allHosts);
        allHosts.CheckedChange += (_, args) =>
        {
            SettingsStore.Current.TunBypassAllHosts = args.IsChecked;
            SettingsStore.Save();
        };
        page.AddView(allHosts);
        var allHostsHint = Ui.Hint(this,
            "Если провайдер режет сайты вне встроенного списка. Применяется при следующем " +
            "включении обхода; первые запросы к любому сайту могут стать медленнее.", P);
        allHostsHint.SetPadding(Ui.Dp(this, 28), 0, 0, Ui.Dp(this, 8));
        page.AddView(allHostsHint);

        _strategyHeader = Ui.Section(this, "Стратегия фрагментации · " + NetworkPresets.Label(this), P);
        page.AddView(_strategyHeader);
        _list = Ui.Column(this);
        page.AddView(_list);

        var profile = BypassProfileStore.Ranked("tun").FirstOrDefault();
        if (profile is not null)
        {
            var last = Ui.Card(P, this,
                Ui.Text(this, "Последний удачный профиль", 14f, P.TextStrong, bold: true),
                Ui.Hint(this, $"{profile.Name} · качество {profile.Quality}/100 · использован {profile.Uses} раз · " +
                               profile.LastSuccessUtc.ToLocalTime().ToString("dd.MM HH:mm"), P));
            Ui.Add(page, last, 8);
        }

        SetPage(page);
    }

    private Button MakePrimary(string text, Action click)
    {
        var button = new Button(this) { Text = text };
        button.SetTextColor(P.AccentContrast);
        Ui.StyleSystemText(button, 13.5f);
        button.SetTypeface(Typeface.Create("sans-serif-medium", TypefaceStyle.Normal), TypefaceStyle.Normal);
        button.Background = Ui.Pressable(this, P.Accent, Ui.Blend(P.Accent, P.White, 0.15f), 14);
        button.Click += (_, _) => click();
        return button;
    }

    private Button MakeSecondary(string text, Action click)
    {
        var button = new Button(this) { Text = text };
        button.SetTextColor(P.Text);
        Ui.StyleSystemText(button, 13.5f);
        button.Background = Ui.Pressable(this, P.Surface, P.Inner, 14, P.Border);
        button.Click += (_, _) => click();
        return button;
    }

    private void RenderStrategies()
    {
        if (_list is null)
        {
            return;
        }

        _list.RemoveAllViews();
        for (var index = 0; index < _strategies.Count; index++)
        {
            var strategy = _strategies[index];
            var selected = index == _selected;
            // Лямбда захватывает переменную цикла: без копии после for там остаётся Count.
            var selectedIndex = index;
            var card = Ui.Column(this);
            card.SetPadding(Ui.Dp(this, 14), Ui.Dp(this, 11), Ui.Dp(this, 14), Ui.Dp(this, 11));
            card.Background = Ui.Rounded(selected ? P.AccentSurface : P.Card, 14, this,
                selected ? P.AccentBorder : P.Border);
            card.Clickable = true;
            card.Click += (_, _) =>
            {
                _selected = selectedIndex;
                NetworkPresets.SetPage(this, strategy.Mode.ToString());
                // Ручной выбор: попадает в базу по операторам и не перезаписывается автоподбором.
                ProviderPresetStore.RecordChoice(
                    NetworkPresets.Key(this),
                    strategy.Mode.ToString(),
                    NetworkPresets.GetVideo(this),
                    NetworkPresets.GetBlockQuic(this));
                RenderStrategies();
            };

            var row = Ui.Row(this);
            row.AddView(Ui.Text(this, strategy.Name, 14f, selected ? P.Accent : P.TextStrong, bold: true),
                new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f));
            if (strategy.Mode == StreamFragmentMode.Oob)
            {
                row.AddView(Ui.Badge(this, P, "РЕКОМЕНДУЕТСЯ", P.Positive));
            }

            if (selected)
            {
                row.AddView(Ui.Badge(this, P, "ВЫБРАНО", P.Accent));
            }

            card.AddView(row);
            var description = Ui.Hint(this, strategy.Description, P);
            description.SetPadding(0, Ui.Dp(this, 3), 0, 0);
            card.AddView(description);

            var layout = new LinearLayout.LayoutParams(
                ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent);
            layout.BottomMargin = Ui.Dp(this, 7);
            _list.AddView(card, layout);
        }
    }

    protected override void OnResume()
    {
        base.OnResume();
        RefreshStatus();

        _handler = new Handler(Looper.MainLooper!);
        _tick = () =>
        {
            RefreshStatus();
            _handler?.PostDelayed(_tick, StatusIntervalMs);
        };
        _handler.PostDelayed(_tick, StatusIntervalMs);
    }

    protected override void OnPause()
    {
        if (_handler is not null && _tick is not null)
        {
            _handler.RemoveCallbacks(_tick);
        }

        _handler = null;
        _tick = null;
        base.OnPause();
    }

    private void StartBypass()
    {
        if (!VpnStarter.CanStartBypass(out var blockedReason))
        {
            Ui.Toast(this, blockedReason);
            return;
        }

        if (_selected < 0 || _selected >= _strategies.Count)
        {
            _selected = 0;
        }

        var strategy = _strategies[_selected];
        NetworkPresets.SetPage(this, strategy.Mode.ToString());
        AppLog.Write("tun-bypass",
            $"Старт TUN-обхода: «{strategy.Name}»; {NetworkPresets.Describe(this)}");

        var request = VpnStarter.PrepareRequest(this);
        if (request is not null)
        {
            StartActivityForResult(request, VpnRequestCode);
            return;
        }

        Launch(strategy);
    }

    private void Launch(StreamFragmentStrategy strategy)
    {
        VpnStarter.StartBypass(this, strategy.Mode);

        BypassProfileStore.Record(new BypassProfile
        {
            Mode = "tun",
            StrategyIndex = _selected,
            Name = strategy.Name,
            Quality = 30,
            BlockQuic = NetworkPresets.GetBlockQuic(this),
            Uses = 1,
        });

        // Ручной выбор запоминается по клику на карточку стратегии (RecordChoice),
        // а простое «Включить» с готовым пресетом ручным выбором не считается:
        // иначе подтверждённый базой оператора пресет нельзя было бы обновить автоподбором.
        Ui.Toast(this, "Запускаю TUN-обход: " + strategy.Name);
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
            if (_selected < 0 || _selected >= _strategies.Count)
            {
                _selected = 0;
            }

            Launch(_strategies[_selected]);
        }
        else
        {
            Ui.Toast(this, "Разрешение VPN не выдано");
        }
    }

    private async void RunAutotune(AutotuneMode mode)
    {
        // Пока идёт перебор, кнопка, которой его запустили, работает как «Остановить подбор».
        if (_autotuneCts is not null)
        {
            _autotuneCts.Cancel();
            if (_autotuneStatus is not null)
            {
                _autotuneStatus.Text = "Останавливаю подбор…";
            }

            return;
        }

        if (TunnelService.State == TunnelState.Running || ForwarderHost.IsRunning)
        {
            Ui.Toast(this, "Сначала выключите обход");
            return;
        }

        var cts = new CancellationTokenSource();
        _autotuneCts = cts;
        SetAutotuneRunning(true, mode);
        AutotuneResult? page = null;
        AutotuneResult? video = null;
        var cancelled = false;
        var modeLabel = mode == AutotuneMode.Best ? "«Подобрать лучшую»" : "«Первая рабочая»";
        AppLog.Write("tun-bypass",
            $"Подбор стратегии ({modeLabel}): " + NetworkPresets.Describe(this));

        if (_autotunePhase is not null)
        {
            _autotunePhase.Visibility = ViewStates.Visible;
            _autotunePhase.Text = "Подбираю стратегию…";
        }

        if (_autotuneProgress is not null)
        {
            _autotuneProgress.Visibility = ViewStates.Visible;
            _autotuneProgress.Max = 1;
            _autotuneProgress.Progress = 0;
        }

        if (_autotuneStatus is not null)
        {
            _autotuneStatus.Text = "Готовлюсь…";
        }

        IProgress<AutotuneStep> progress = new Progress<AutotuneStep>(step =>
        {
            AppLog.Write("tun-bypass", "автоподбор: " + step.Text);
            if (_autotunePhase is not null)
            {
                _autotunePhase.Text = step.Scope == AutotuneScope.Video
                    ? "Шаг 2 из 2 · видео (googlevideo)"
                    : "Шаг 1 из 2 · страница YouTube";
            }

            if (_autotuneProgress is not null)
            {
                _autotuneProgress.Max = Math.Max(step.Total, 1);
                _autotuneProgress.Progress = Math.Clamp(step.Index, 0, step.Total);
            }

            if (_autotuneStatus is not null)
            {
                _autotuneStatus.Text = step.Text;
            }
        });

        try
        {
            // Известный пресет (включая зашитую базу оператора) проверяем первым.
            var preferredPage = Enum.TryParse<StreamFragmentMode>(
                NetworkPresets.GetPage(this), out var pageMode)
                ? pageMode
                : (StreamFragmentMode?)null;
            var preferredVideo = Enum.TryParse<StreamFragmentMode>(
                NetworkPresets.GetVideo(this), out var videoMode)
                ? videoMode
                : preferredPage;
            page = await StrategyAutotune.RunAsync(
                AutotuneScope.Page, progress, cts.Token, preferredPage, mode);
            video = await StrategyAutotune.RunAsync(
                AutotuneScope.Video, progress, cts.Token, preferredVideo, mode);

            // QUIC не зависит от TCP-стратегии: проверяем один раз и выставляем автоматически.
            var quicForwarder = new TlsFragmentForwarder();
            bool quicOk;
            try
            {
                await quicForwarder.StartAsync(StreamFragmentMode.None);
                quicOk = await StrategyAutotune.ProbeQuicAsync(quicForwarder.Port, cts.Token);
            }
            finally
            {
                await quicForwarder.StopAsync();
            }

            var networkUp = quicOk || await StrategyAutotune.IsNetworkReachableAsync(cts.Token);
            if (!quicOk && !networkUp)
            {
                // Сеть пропала — «QUIC не проходит» ничего не значит, настройку не трогаем.
                _lastQuicResult = "не проверено — пропала сеть (настройку не меняю)";
                AppLog.Write("tun-bypass", "QUIC: сеть недоступна — настройку не меняю");
            }
            else
            {
                NetworkPresets.SetBlockQuic(this, !quicOk);
                if (_quicCheck is not null)
                {
                    _quicCheck.Checked = !quicOk;
                }

                _lastQuicResult = quicOk
                    ? "проходит (оставляю через релей)"
                    : "не проходит (блокирую — видео пойдёт по TCP)";
                AppLog.Write("tun-bypass", quicOk
                    ? "QUIC: проходит через релей — оставляю включённым (видео может идти по QUIC)"
                    : "QUIC: не проходит — блокирую, видео пойдёт по TCP");
            }            if (_autotuneStatus is not null)
            {
                _autotuneStatus.Text = quicOk
                    ? "QUIC проходит — оставляю через релей."
                    : "QUIC не проходит — блокирую, видео пойдёт по TCP.";
            }
        }
        catch (System.OperationCanceledException)
        {
            cancelled = true;
            AppLog.Write("tun-bypass", "Подбор стратегии: остановлен");
        }
        catch (Exception exception)
        {
            AppLog.Write("tun-bypass", "Подбор стратегии не удался", exception);
            if (_autotuneStatus is not null)
            {
                _autotuneStatus.Text = "Подбор не удался: " + exception.Message;
            }

            Ui.Toast(this, "Подбор не удался: " + exception.Message);
        }
        finally
        {
            if (ReferenceEquals(_autotuneCts, cts))
            {
                _autotuneCts = null;
            }

            cts.Dispose();
            SetAutotuneRunning(false);
        }

        if (cancelled)
        {
            _lastPageResult = modeLabel + ": остановлен";
            _lastVideoResult = null;
            if (_autotunePhase is not null)
            {
                _autotunePhase.Text = "Подбор остановлен";
            }

            if (_autotuneStatus is not null)
            {
                _autotuneStatus.Text = "Подбор остановлен.";
            }

            return;
        }

        if (page?.Aborted == true)
        {
            _lastPageResult = modeLabel + ": прерван — сеть пропала";
            _lastVideoResult = null;
            if (_autotunePhase is not null)
            {
                _autotunePhase.Text = "Подбор прерван";
            }

            if (_autotuneStatus is not null)
            {
                _autotuneStatus.Text = "Подбор прерван: сеть перестала отвечать. Проверьте подключение и повторите.";
            }

            AppLog.Write("tun-bypass", "Подбор стратегии прерван: сеть перестала отвечать");
            return;
        }

        // Применяем после снятия состояния подбора: запуск обхода отменяет активный подбор.
        if (page?.Strategy is not null)
        {
            var index = _strategies.IndexOf(page.Strategy);
            if (index >= 0)
            {
                _selected = index;
                NetworkPresets.SetPage(this, page.Strategy.Mode.ToString());
                RenderStrategies();
            }
        }

        // Видео-пресет меняем только если проба реально скачала поток. Иначе оставляем
        // прежний: подменять его страничной стратегией — значит врать «видео работает».
        if (video?.Strategy is not null)
        {
            NetworkPresets.SetVideo(this, video.Strategy.Mode.ToString());
            if (_videoSpinner is not null)
            {
                var index = _strategies.FindIndex(item => item.Mode == video.Strategy.Mode);
                _videoSpinner.SetSelection(index >= 0 ? index + 1 : 0, false);
            }
        }

        var summary = page?.Strategy is null
            ? "Ни одна стратегия не открыла страницу YouTube."
            : $"{NetworkPresets.Label(this)}: " +
              $"{(mode == AutotuneMode.Best ? "лучшая" : "первая рабочая")} {page.Strategy.Name} " +
              $"({page.Score}/{page.Total}" +
              (page.Strong ? ")" : ", превью не прошло)") +
              (page.SpeedKbps > 0 ? $", {page.SpeedKbps} КБ/с" : string.Empty) +
              (video?.Strategy is not null
                  ? video.AudioOnly
                      ? $", видео не идёт — звук {video.Strategy.Name} ({video.SpeedKbps} КБ/с)"
                      : $", видео {video.Strategy.Name} ({video.Score}/{video.Total}" +
                        (video.SpeedKbps > 0 ? $", {video.SpeedKbps} КБ/с)" : ")")
                  : ", видео не проверено (0 байт)");

        _lastPageResult = page?.Strategy is null
            ? $"{modeLabel}: ничего не найдено"
            : $"{modeLabel}: {page.Strategy.Name} — {page.Score} из {page.Total}" +
              (page.Strong ? " (страница и превью)" : " (только страница)") +
              (page.SpeedKbps > 0 ? $", {page.SpeedKbps} КБ/с" : string.Empty);
        _lastVideoResult = video?.Strategy is null
            ? "не проверено — медиасервер не отдал байты"
            : video.AudioOnly
                ? $"только звук — {video.Strategy.Name}, {video.SpeedKbps} КБ/с"
                : $"{video.Strategy.Name} — {video.Score} из {video.Total}, {video.SpeedKbps} КБ/с";
        if (_autotunePhase is not null)
        {
            _autotunePhase.Text = "Готово";
        }

        if (_autotuneStatus is not null)
        {
            _autotuneStatus.Text = summary;
        }

        Ui.Toast(this, summary);
        AppLog.Write("tun-bypass", "Подбор стратегии: " + summary);

        // Автоматически включаем только подтверждённую страницу (с превью).
        if (page?.Strategy is not null && page.Strong)
        {
            ProviderPresetStore.RecordSuccess(
                NetworkPresets.Key(this),
                page.Strategy.Mode.ToString(),
                video?.Strategy?.Mode.ToString() ?? NetworkPresets.GetVideo(this),
                NetworkPresets.GetBlockQuic(this),
                transport: video?.Strategy is not null ? "tcp" : string.Empty,
                videoKbps: video?.SpeedKbps ?? 0);
            StartBypass();
        }
    }

    /// <summary>
    /// Отдаёт собранную базу параметров по операторам текстом: разработчик забирает её
    /// и вшивает в следующую сборку. Wi-Fi SSID не экспортируются (личные данные).
    /// </summary>
    private void ShareProviderPresets()
    {
        try
        {
            var json = ProviderPresetStore.ExportJson();
            AppLog.Write("presets", "Экспорт базы параметров обходов: " + json.ReplaceLineEndings(" "));
            var intent = new Intent(Intent.ActionSend).SetType("text/plain");
            intent.PutExtra(Intent.ExtraSubject, "VieNLink: параметры обхода по операторам");
            intent.PutExtra(Intent.ExtraText, json);
            StartActivity(Intent.CreateChooser(intent, "Поделиться подборками"));
        }
        catch (Exception exception)
        {
            AppLog.Write("presets", "Не удалось поделиться подборками", exception);
            Ui.Toast(this, "Не удалось поделиться: " + exception.Message);
        }
    }

    private void SetAutotuneRunning(bool running, AutotuneMode mode = AutotuneMode.Quick)
    {
        if (_autotuneQuick is null || _autotuneBest is null)
        {
            return;
        }

        if (running)
        {
            var cancel = mode == AutotuneMode.Best ? _autotuneBest : _autotuneQuick;
            var other = mode == AutotuneMode.Best ? _autotuneQuick : _autotuneBest;
            cancel.Text = "Остановить подбор";
            cancel.Enabled = true;
            cancel.Alpha = 1f;
            other.Enabled = false;
            other.Alpha = 0.5f;
            return;
        }

        _autotuneQuick.Text = "Первая рабочая";
        _autotuneQuick.Enabled = true;
        _autotuneQuick.Alpha = 1f;
        _autotuneBest.Text = "Подобрать лучшую";
        _autotuneBest.Enabled = true;
        _autotuneBest.Alpha = 1f;
    }

    protected override void OnDestroy()
    {
        _autotuneCts?.Cancel();
        base.OnDestroy();
    }

    private void RefreshStatus()
    {
        if (_status is null)
        {
            return;
        }

        var running = TunnelService.State == TunnelState.Running;
        var bypass = TunnelService.IsBypassMode;
        _status.Text = bypass ? "TUN-обход (бета) работает" : running ? "Подключён прокси-сервер" : "VPN не подключён";
        _status.SetTextColor(bypass || running ? P.Positive : P.TextStrong);

        if (_strategyHeader is not null)
        {
            // Сеть могла смениться (Wi-Fi ↔ LTE): заголовок показывает, чей пресет загружен.
            _strategyHeader.Text = "Стратегия фрагментации · " + NetworkPresets.Label(this);
        }

        // Смена сети: подхватываем сохранённый пресет уже этой сети (или реально запущенную
        // стратегию), чтобы экран не показывал выбор прошлого оператора.
        var snapshot = NetworkPresets.Snapshot(this);
        RefreshStats(snapshot);
        if (!string.Equals(snapshot.Key, _lastNetworkKey, StringComparison.Ordinal))
        {
            _lastNetworkKey = snapshot.Key;
            var target = ForwarderHost.IsRunning
                ? _strategies.FindIndex(strategy => strategy.Mode == ForwarderHost.Mode)
                : _strategies.FindIndex(strategy => strategy.Mode.ToString()
                    .Equals(snapshot.Page, StringComparison.OrdinalIgnoreCase));
            if (target >= 0 && target != _selected)
            {
                _selected = target;
                RenderStrategies();
            }

            AppLog.Write("tun-bypass", "Сеть: " + snapshot.Describe);
        }

        if (_forwarder is not null)
        {
            _forwarder.Text = ForwarderHost.IsRunning
                ? $"Форвардер 127.0.0.1:{ForwarderHost.Port} · {ForwarderHost.Mode} · " +
                  $"соединений {ForwarderHost.AcceptedConnections}, разрезано {ForwarderHost.FragmentedConnections}" +
                  (ForwarderHost.UdpAssociations > 0
                      ? $", QUIC-фейков {ForwarderHost.FakeQuicPackets}"
                      : string.Empty)
                : "Форвардер не запущен";
        }

        UpdateButtons();
    }

    /// <summary>Все данные, участвующие в выборе обхода, — простыми словами на экране.</summary>
    private void RefreshStats(NetworkSnapshot snapshot)
    {
        if (_stats is null)
        {
            return;
        }

        var lines = new List<string>
        {
            $"Сеть: {snapshot.Label}",
            $"Сети устройства: {HumanizeNetworks(snapshot.Details)}",
            $"Страница YouTube: {Value(snapshot.Page)} — {Humanize(snapshot.PageSource)}",
            $"Видео: {(string.IsNullOrWhiteSpace(snapshot.Video) ? "как для страниц" : snapshot.Video)} — {Humanize(snapshot.VideoSource)}",
            $"QUIC: {(snapshot.BlockQuic ? "заблокирован (YouTube идёт по TCP)" : "разрешён (через релей с фейком)")} — {Humanize(snapshot.BlockQuicSource)}",
            $"Какие сайты резать: {(SettingsStore.Current.TunBypassAllHosts ? "все" : "только YouTube / Telegram / Discord")}",
            "Чем проверяется: страница www.youtube.com + превью i.ytimg.com + API youtubei; " +
            "видео — подписанный поток rr*.googlevideo.com (≥64 КБ), звук — отдельно; " +
            "QUIC — настоящий Initial на www.google.com",
            $"QUIC-проба: {_lastQuicResult ?? "ещё не проверялась"}",
        };

        var preset = ProviderPresetStore.Find(snapshot.Key);
        lines.Add(preset is null
            ? "Сохранённые подборки: записи для этой сети нет"
            : $"Сохранённые подборки: {Value(preset.Page)}, " +
              (string.Equals(preset.Source, "manual", StringComparison.Ordinal)
                  ? "выбрано вручную"
                  : "подтверждено подбором") +
              $", подтверждений {preset.Successes}" +
              (string.Equals(preset.VideoTransport, "tcp", StringComparison.Ordinal)
                  ? ", поток по TCP"
                  : string.Empty) +
              (preset.LastSuccessUtc == default
                  ? string.Empty
                  : $", последний успех {preset.LastSuccessUtc.ToLocalTime():dd.MM HH:mm}"));

        lines.Add(ForwarderHost.IsRunning
            ? $"Форвардер: работает на порту {ForwarderHost.Port}, режим {ForwarderHost.Mode} · " +
              $"соединений {ForwarderHost.AcceptedConnections}, разрезано {ForwarderHost.FragmentedConnections}, " +
              $"QUIC-сессий {ForwarderHost.UdpAssociations}, фейков {ForwarderHost.FakeQuicPackets}"
            : "Форвардер: не запущен");

        var videoKb = ForwarderHost.VideoBytes / 1024;
        lines.Add(videoKb > 0
            ? $"Видео шло: по TCP через обход — {videoKb} КБ за {ForwarderHost.VideoConnections} соединений"
            : ForwarderHost.UdpAssociations > 0
                ? $"Видео шло: по QUIC через релей (TCP-потока от медиасервера нет), сессий {ForwarderHost.UdpAssociations}"
                : "Видео шло: пока не наблюдалось");

        lines.Add("Последний подбор: " + (_lastPageResult ?? "ещё не запускался"));
        if (_lastVideoResult is not null)
        {
            lines.Add("Видео: " + _lastVideoResult);
        }

        _stats.Text = string.Join("\n", lines);
    }

    private static string Value(string value) => string.IsNullOrWhiteSpace(value) ? "не задано" : value;

    /// <summary>Названия источников — простыми словами, без технических ключей.</summary>
    private static string Humanize(string source) => source switch
    {
        "локальный пресет сети" => "сохранено для этой сети",
        "база операторов (зашитая)" => "из базы операторов (вшита в сборку)",
        "база операторов (на устройстве)" => "из базы операторов (собрана на телефоне)",
        "пресет типа сети" => "сохранено для всех сетей этого типа",
        "общая стратегия" => "последняя использованная",
        "общая настройка" => "общая настройка",
        "по умолчанию (Oob)" => "по умолчанию",
        _ => source,
    };

    private static string HumanizeNetworks(string details) => details
        .Replace("CELLULAR", "мобильная", StringComparison.Ordinal)
        .Replace("WIFI", "Wi-Fi", StringComparison.Ordinal)
        .Replace("ETHERNET", "Ethernet", StringComparison.Ordinal)
        .Replace(" VALIDATED", " — интернет есть", StringComparison.Ordinal)
        .Replace("(не проверена)", "— интернет не проверен", StringComparison.Ordinal)
        .Replace("VPN (пропущен)", "VPN не считается", StringComparison.Ordinal);

    private void UpdateButtons()
    {
        var bypass = TunnelService.IsBypassMode;
        if (_start is not null)
        {
            _start.Enabled = !bypass;
            _start.Alpha = bypass ? 0.5f : 1f;
        }

        if (_stop is not null)
        {
            _stop.Enabled = TunnelService.State == TunnelState.Running;
            _stop.Alpha = _stop.Enabled ? 1f : 0.5f;
        }
    }
}
