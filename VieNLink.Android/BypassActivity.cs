using Android.App;
using Android.Content;
using Android.Graphics;
using Android.OS;
using Android.Views;
using Android.Widget;

namespace VieNLink.Mobile;

/// <summary>
/// TUN-обход без прокси-узла: трафик идёт в TUN, ядро отправляет TCP в наш форвардер,
/// который режет TLS ClientHello. Работает без root только user-space методами.
/// Лучший результат на SNI-блоках даёт «Разрез TLS-записей» (аналог tlsrec из ByeDPI):
/// страница YouTube открывается, но видео с googlevideo требует узел (блок по IP).
/// </summary>
#if DEBUG
[Activity(Label = "TUN-обход", Theme = "@android:style/Theme.Material.Light.NoActionBar", Exported = true)]
#else
[Activity(Label = "TUN-обход", Theme = "@android:style/Theme.Material.Light.NoActionBar")]
#endif
public sealed class BypassActivity : Activity
{
    private const int VpnRequestCode = 3003;
    private const int StatusIntervalMs = 2000;

    private readonly List<StreamFragmentStrategy> _strategies = TlsFragmentForwarder.Strategies
        .OrderByDescending(strategy => strategy.Mode == StreamFragmentMode.RecordSplit)
        .ToList();
    private readonly List<string> _labels = new();

    private ListView? _list;
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

        var root = new LinearLayout(this) { Orientation = Android.Widget.Orientation.Vertical };
        root.SetPadding(
            UiMetrics.Dp(this, 16),
            UiMetrics.StatusBar(this) + UiMetrics.Dp(this, 8),
            UiMetrics.Dp(this, 16),
            UiMetrics.NavigationBar(this) + UiMetrics.Dp(this, 8));

        var title = new TextView(this) { Text = "TUN-обход (без узла)", TextSize = 24f };
        title.SetTypeface(null, TypefaceStyle.Bold);

        var info = new TextView(this) { TextSize = 13f };
        info.SetTextColor(Color.DarkGray);
        info.Text = "Трафик идёт в TUN, ядро отправляет TCP в локальный форвардер, " +
                    "который режет TLS. Прокси-узел не нужен. Лучше всего работает «Разрез " +
                    "TLS-записей»: страница YouTube открывается, видео с googlevideo требует узел.";

        _status = new TextView(this) { TextSize = 14f };
        _forwarder = new TextView(this) { TextSize = 13f };
        _forwarder.SetTextColor(Color.DarkGray);

        foreach (var strategy in _strategies)
        {
            var note = strategy.Mode == StreamFragmentMode.RecordSplit ? " · рекомендуется" : string.Empty;
            _labels.Add(strategy.Name + note + "\n" + strategy.Description);
        }

        _list = new ListView(this) { ChoiceMode = ChoiceMode.Single };
        _list.Adapter = new ArrayAdapter<string>(
            this, Android.Resource.Layout.SimpleListItem1, _labels.ToList());
        _list.ItemClick += (_, args) =>
        {
            _selected = args.Position;
            RefreshStatus();
        };

        var actions = new LinearLayout(this) { Orientation = Android.Widget.Orientation.Horizontal };
        _start = new Button(this) { Text = "Включить", TextSize = 13f };
        _start.Click += (_, _) => StartBypass();
        _stop = new Button(this) { Text = "Выключить", TextSize = 13f };
        _stop.Click += (_, _) => VpnStarter.Stop(this);
        actions.AddView(_start, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f));
        actions.AddView(_stop, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f));

        root.AddView(title);
        root.AddView(info);
        root.AddView(_status);
        root.AddView(_forwarder);
        root.AddView(_list, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, 0, 1f));
        root.AddView(actions);

        SetContentView(root);

        // Автозапуск для проверки с adb: --ez start true -e strategy RecordSplit
        var autoStart = Intent?.GetBooleanExtra("start", false) ?? false;
        _selected = Math.Clamp(Intent?.GetIntExtra("strategy", 0) ?? 0, 0, _strategies.Count - 1);
        _list.SetItemChecked(_selected, true);

        if (autoStart)
        {
            _ = _list.Post(StartBypass);
        }

        UpdateButtons();
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
        var request = VpnStarter.PrepareRequest(this);
        if (request is not null)
        {
            StartActivityForResult(request, VpnRequestCode);
            return;
        }

        VpnStarter.StartBypass(this, _strategies[_selected].Mode);
        Toast.MakeText(this, "Запускаю TUN-обход…", ToastLength.Short)?.Show();
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
            VpnStarter.StartBypass(this, _strategies[_selected].Mode);
            Toast.MakeText(this, "Запускаю TUN-обход…", ToastLength.Short)?.Show();
        }
        else
        {
            Toast.MakeText(this, "Разрешение VPN не выдано", ToastLength.Long)?.Show();
        }
    }

    private void RefreshStatus()
    {
        if (_status is null)
        {
            return;
        }

        var running = TunnelService.State == TunnelState.Running;
        _status.Text = (running ? "Туннель работает" : "VPN не подключён") +
                       " · выбрано: " + _strategies[_selected].Name;

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
        var running = TunnelService.State == TunnelState.Running;
        if (_start is not null)
        {
            _start.Enabled = !running;
        }

        if (_stop is not null)
        {
            _stop.Enabled = running;
        }
    }
}
