using Android.App;
using Android.Content;
using Android.Graphics;
using Android.OS;
using Android.Views;
using Android.Widget;

namespace VieNLink.Mobile;

/// <summary>
/// TUN-обход без прокси-узла: TUN → встроенный SOCKS5-форвардер, который режет TLS ClientHello.
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
        .OrderByDescending(strategy => strategy.Mode == StreamFragmentMode.RecordSplit)
        .ToList();

    private LinearLayout? _list;
    private TextView? _status;
    private TextView? _forwarder;
    private Button? _start;
    private Button? _stop;
    private int _selected;
    private Handler? _handler;
    private Action? _tick;

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        BuildUi();

        var saved = AndroidSettingsStore.Current.TunBypassStrategy;
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

        var info = Ui.Banner(P, this, P.Accent, "Без узла и без root",
            "sing-box поднимает TUN, TCP идёт в собственный SOCKS5-форвардер, который режет TLS " +
            "ClientHello. На SNI-блоке страницы открываются; при блоке по IP (часто видео YouTube) " +
            "нужен прокси-узел.");
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

        var quic = new CheckBox(this)
        {
            Text = "Блокировать QUIC (UDP/443), чтобы браузер шёл по TCP",
            Checked = SettingsStore.Current.TunBypassBlockQuic,
        };
        quic.SetTextColor(P.TextSoft());
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

        page.AddView(Ui.Section(this, "Стратегия фрагментации", P));
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
            "для узлов. Пока выберите стратегию вручную — «Разрез TLS-записей» обычно лучший.", P));
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
        button.SetTextSize(Android.Util.ComplexUnitType.Sp, 13.5f);
        button.SetTypeface(Typeface.Create("sans-serif-medium", TypefaceStyle.Normal), TypefaceStyle.Normal);
        button.Background = Ui.Pressable(this, P.Accent, Ui.Blend(P.Accent, P.White, 0.15f), 14);
        button.Click += (_, _) => click();
        return button;
    }

    private Button MakeSecondary(string text, Action click)
    {
        var button = new Button(this) { Text = text };
        button.SetTextColor(P.Text);
        button.SetTextSize(Android.Util.ComplexUnitType.Sp, 13.5f);
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
            var card = Ui.Column(this);
            card.SetPadding(Ui.Dp(this, 14), Ui.Dp(this, 11), Ui.Dp(this, 14), Ui.Dp(this, 11));
            card.Background = Ui.Rounded(selected ? P.AccentSurface : P.Card, 14, this,
                selected ? P.AccentBorder : P.Border);
            card.Clickable = true;
            card.Click += (_, _) =>
            {
                _selected = index;
                AndroidSettingsStore.Current.TunBypassStrategy = strategy.Mode.ToString();
                AndroidSettingsStore.Save();
                RenderStrategies();
            };

            var row = Ui.Row(this);
            row.AddView(Ui.Text(this, strategy.Name, 14f, selected ? P.Accent : P.TextStrong, bold: true),
                new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f));
            if (strategy.Mode == StreamFragmentMode.RecordSplit)
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
            Launch(_strategies[_selected]);
        }
        else
        {
            Ui.Toast(this, "Разрешение VPN не выдано");
        }
    }

    private void RefreshStatus()
    {
        if (_status is null)
        {
            return;
        }

        var running = TunnelService.State == TunnelState.Running;
        var bypass = TunnelService.IsBypassMode;
        _status.Text = bypass ? "TUN-обход (бета) работает" : running ? "Подключён прокси-узел" : "VPN не подключён";
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
