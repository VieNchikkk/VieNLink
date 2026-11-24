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
    private Button? _start;
    private Button? _stop;
    private Button? _autotuneButton;
    private TextView? _autotuneStatus;
    private CancellationTokenSource? _autotuneCts;
    private Spinner? _videoSpinner;
    private bool _videoSpinnerReady;
    private int _selected;
    private Handler? _handler;
    private Action? _tick;

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        BuildUi();

        var saved = NetworkPresets.GetPage(this);
        if (string.IsNullOrWhiteSpace(saved))
        {
            saved = AndroidSettingsStore.Current.TunBypassStrategy;
        }

        _selected = Math.Max(0, _strategies.FindIndex(strategy =>
            strategy.Mode.ToString().Equals(saved, StringComparison.OrdinalIgnoreCase)));

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

        var actions = Ui.Row(this);
        _start = MakePrimary("Включить обход", StartBypass);
        _stop = MakeSecondary("Выключить", () => VpnStarter.Stop(this));
        actions.AddView(_start, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f));
        actions.AddView(new Space(this), new LinearLayout.LayoutParams(Ui.Dp(this, 8), 1));
        actions.AddView(_stop, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f));
        Ui.Add(page, actions, 12);

        var autotune = Ui.Card(P, this);
        var autotuneTop = Ui.Row(this);
        autotuneTop.AddView(Ui.Text(this, "Автоподбор стратегии", 14.5f, P.TextStrong, bold: true),
            new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f));
        autotuneTop.AddView(Ui.Badge(this, P, "5–10 МИН", P.Accent));
        autotune.AddView(autotuneTop);
        _autotuneStatus = Ui.Hint(this,
            "Одна кнопка: сначала перебирает все стратегии для страницы (youtube.com, youtubei, " +
            "ytimg) и сохраняет рабочую, затем — для видео (googlevideo, ggpht) и тоже сохраняет. " +
            "Затем обе применяются вместе. Обход должен быть выключен; результат — для текущей сети.", P);
        _autotuneStatus.SetPadding(0, Ui.Dp(this, 4), 0, Ui.Dp(this, 8));
        autotune.AddView(_autotuneStatus);
        _autotuneButton = MakeSecondary("Подобрать всё", RunAutotune);
        autotune.AddView(_autotuneButton);
        Ui.Add(page, autotune, 12);

        var videoCard = Ui.Card(P, this);
        videoCard.AddView(Ui.Text(this, "Стратегия для видео (googlevideo)", 14.5f, P.TextStrong, bold: true));
        videoCard.AddView(Ui.Hint(this,
            "Для приложения YouTube обычно работает та же OOB, что и для страниц. " +
            "Если видео тормозит — подберите отдельную стратегию для googlevideo/ytimg.", P));
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
            NetworkPresets.SetVideo(this, position <= 0
                ? string.Empty
                : _strategies[position - 1].Mode.ToString());
        };
        _videoSpinnerReady = true;
        videoCard.AddView(_videoSpinner);
        Ui.Add(page, videoCard, 10);

        var quic = new CheckBox(this)
        {
            Text = "Блокировать QUIC (UDP/443), чтобы браузер шёл по TCP",
            Checked = SettingsStore.Current.TunBypassBlockQuic,
        };
        quic.SetTextColor(P.TextSoft());
        Ui.StyleSystemText(quic);
        quic.CheckedChange += (_, args) =>
        {
            SettingsStore.Current.TunBypassBlockQuic = args.IsChecked;
            SettingsStore.Save();
        };
        page.AddView(quic);
        var quicHint = Ui.Hint(this,
            "QUIC обходит TCP-фрагментацию. Блокировка заставляет Chrome и YouTube перейти на TCP.", P);
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

        page.AddView(Ui.Section(this, "Стратегия фрагментации · " + NetworkPresets.Label(this), P));
        _list = Ui.Column(this);
        page.AddView(_list);

        var dev = Ui.Card(P, this);
        var devTop = Ui.Row(this);
        devTop.AddView(Ui.Text(this, "Автоподбор стратегии", 14.5f, P.TextStrong, bold: true),
            new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f));
        devTop.AddView(Ui.Badge(this, P, "В РАЗРАБОТКЕ", P.Warning));
        dev.AddView(devTop);
        dev.AddView(Ui.Hint(this,
            "В Windows автоподбор перезапускает TUN и проверяет сайты через curl.exe. На Android " +
            "сам процесс приложения исключён из TUN (иначе запросы ядра ушли бы в него же), поэтому " +
            "проверить результат изнутри нельзя. В разработке: отдельный процесс проверки, как :probe " +
            "для серверов. Пока выберите стратегию вручную: начните с рекомендуемой «Разрез TLS-записей», " +
            "затем пробуйте «Разрез полосы SNI» и «Медленный разрез SNI». Если провайдер режет сайты " +
            "вне встроенного списка — включите «Резать все сайты».", P));
        Ui.Add(page, dev, 14);

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
        AndroidSettingsStore.Current.TunBypassStrategy = strategy.Mode.ToString();
        AndroidSettingsStore.Save();

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
            BlockQuic = SettingsStore.Current.TunBypassBlockQuic,
            Uses = 1,
        });

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

    private async void RunAutotune()
    {
        if (TunnelService.State == TunnelState.Running || ForwarderHost.IsRunning)
        {
            Ui.Toast(this, "Сначала выключите обход");
            return;
        }

        if (_autotuneCts is not null)
        {
            _autotuneCts.Cancel();
            return;
        }

        _autotuneCts = new CancellationTokenSource();
        SetAutotuneEnabled(false);

        IProgress<string> progress = new Progress<string>(line =>
        {
            if (_autotuneStatus is not null)
            {
                _autotuneStatus.Text = line;
            }
        });

        try
        {
            progress.Report("Страница: перебираю стратегии…");
            var page = await StrategyAutotune.RunAsync(AutotuneScope.Page, progress, _autotuneCts.Token);
            if (page.Strategy is not null)
            {
                var index = _strategies.IndexOf(page.Strategy);
                if (index >= 0)
                {
                    _selected = index;
                    NetworkPresets.SetPage(this, page.Strategy.Mode.ToString());
                    RenderStrategies();
                }
            }

            progress.Report("Видео: перебираю стратегии…");
            var video = await StrategyAutotune.RunAsync(AutotuneScope.Video, progress, _autotuneCts.Token);
            var videoStrategy = video.Strategy ?? page.Strategy;
            if (videoStrategy is not null)
            {
                NetworkPresets.SetVideo(this, videoStrategy.Mode.ToString());
                if (_videoSpinner is not null)
                {
                    var index = _strategies.FindIndex(item => item.Mode == videoStrategy.Mode);
                    _videoSpinner.SetSelection(index >= 0 ? index + 1 : 0, false);
                }
            }

            if (page.Strategy is not null || videoStrategy is not null)
            {
                Ui.Toast(this,
                    $"{NetworkPresets.Label(this)}: страница {page.Strategy?.Name ?? "—"} " +
                    $"({page.Score}/{page.Total}), видео {videoStrategy?.Name ?? "—"} " +
                    $"({video.Score}/{video.Total})");

                // Сразу применяем: обе стратегии сохранены, включаем обход.
                if (page.Strategy is not null)
                {
                    StartBypass();
                }
            }
            else
            {
                Ui.Toast(this, "Ни одна стратегия не открыла домены YouTube");
            }
        }
        catch (System.OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            Ui.Toast(this, "Подбор не удался: " + exception.Message);
        }
        finally
        {
            _autotuneCts.Dispose();
            _autotuneCts = null;
            SetAutotuneEnabled(true);
        }
    }

    private void SetAutotuneEnabled(bool enabled)
    {
        if (_autotuneButton is not null)
        {
            _autotuneButton.Enabled = enabled;
            _autotuneButton.Alpha = enabled ? 1f : 0.5f;
        }
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

        if (_forwarder is not null)
        {
            _forwarder.Text = ForwarderHost.IsRunning
                ? $"Форвардер 127.0.0.1:{ForwarderHost.Port} · {ForwarderHost.Mode} · " +
                  $"соединений {ForwarderHost.AcceptedConnections}, разрезано {ForwarderHost.FragmentedConnections}"
                : "Форвардер не запущен";
        }

        UpdateButtons();
    }

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
