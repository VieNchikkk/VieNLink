using Android.App;
using Android.Content;
using Android.Graphics;
using Android.OS;
using Android.Views;
using Android.Widget;

namespace VieNLink.Mobile;

/// <summary>
/// Экран узлов без списка: только сводка (сколько всего, рабочих, доступ по сервисам)
/// и действия «Обновить», «Проверить», «Подключить лучший». Сам узел выбирается скрыто.
/// </summary>
#if DEBUG
// В отладочной сборке экран открыт наружу: так его запускают adb-команды из §0 плана.
[Activity(Label = "Узлы", Theme = "@android:style/Theme.Material.Light.NoActionBar", Exported = true)]
#else
[Activity(Label = "Узлы", Theme = "@android:style/Theme.Material.Light.NoActionBar")]
#endif
public sealed class NodesActivity : Activity
{
    private const int VpnRequestCode = 2002;

    private TextView? _summary;
    private TextView? _detail;
    private TextView? _progress;
    private Button? _refresh;
    private Button? _test;
    private Button? _connect;

    private CancellationTokenSource? _work;
    private bool _busy;
    private string? _pendingConfig;

    // Автозапуск для проверки через adb (см. §0 плана): -e refresh true -e test true.
    private bool _autoRefresh;
    private bool _autoTest;
    private bool _autoConnect;
    private string? _autoSelect;

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);

        _autoRefresh = Intent?.GetBooleanExtra("refresh", false) ?? false;
        _autoTest = Intent?.GetBooleanExtra("test", false) ?? false;
        _autoConnect = Intent?.GetBooleanExtra("connect", false) ?? false;
        _autoSelect = Intent?.GetStringExtra("selectTag");

        BuildUi();
        _ = LoadAsync();
    }

    protected override void OnResume()
    {
        base.OnResume();
        UpdateSummary();
    }

    protected override void OnDestroy()
    {
        _work?.Cancel();
        base.OnDestroy();
    }

    private void BuildUi()
    {
        var root = new LinearLayout(this) { Orientation = Android.Widget.Orientation.Vertical };
        root.SetPadding(
            UiMetrics.Dp(this, 16),
            UiMetrics.StatusBar(this) + UiMetrics.Dp(this, 8),
            UiMetrics.Dp(this, 16),
            UiMetrics.NavigationBar(this) + UiMetrics.Dp(this, 8));

        var title = new TextView(this) { Text = "Узлы", TextSize = 24f };
        title.SetTypeface(null, TypefaceStyle.Bold);
        root.AddView(title);

        _summary = new TextView(this) { TextSize = 16f };
        _summary.SetTypeface(null, TypefaceStyle.Bold);
        _detail = new TextView(this) { TextSize = 14f };
        _detail.SetTextColor(Color.DarkGray);
        _progress = new TextView(this) { TextSize = 13f };
        _progress.SetTextColor(Color.DarkGray);

        var actions = new LinearLayout(this) { Orientation = Android.Widget.Orientation.Horizontal };
        _refresh = MakeButton("Обновить", async () => await RunRefreshAsync());
        _test = MakeButton("Проверить", async () => await RunTestAsync());
        var sources = MakeButton("Источники", () => StartActivity(typeof(SourcesActivity)));
        actions.AddView(_refresh, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f));
        actions.AddView(_test, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f));
        actions.AddView(sources, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f));

        _connect = new Button(this) { Text = "Подключить лучший узел" };
        _connect.Click += (_, _) => ConnectSelected();

        root.AddView(_summary);
        root.AddView(_detail);
        root.AddView(_progress);
        root.AddView(actions);
        root.AddView(new TextView(this)
        {
            Text = "Список узлов скрыт: приложение само выбирает проверенный узел " +
                   "по доступу к сервисам и задержке.",
            TextSize = 13f,
        });
        root.AddView(_connect);

        SetContentView(root);
    }

    private Button MakeButton(string text, Action action)
    {
        var button = new Button(this) { Text = text, TextSize = 13f };
        button.Click += (_, _) => action();
        return button;
    }

    private async Task LoadAsync()
    {
        try
        {
            await Task.Run(() => NodeStore.EnsureLoaded());
        }
        catch (Exception exception)
        {
            AppLog.Write("nodes", "Не удалось прочитать узлы", exception);
            SetProgress("Ошибка чтения узлов: " + exception.Message);
        }

        ApplyAutoSelect();
        UpdateSummary();

        if (_autoRefresh)
        {
            await RunRefreshAsync();
        }

        if (_autoTest || _autoRefresh)
        {
            await RunTestAsync();
        }

        if (_autoConnect)
        {
            ConnectSelected();
        }
    }

    /// <summary>Отладочный выбор узла по тегу: только через adb, в интерфейсе списка нет.</summary>
    private void ApplyAutoSelect()
    {
        if (string.IsNullOrWhiteSpace(_autoSelect))
        {
            return;
        }

        var wanted = NodeStore.Snapshot().FirstOrDefault(node =>
            node.Tag.Equals(_autoSelect, StringComparison.OrdinalIgnoreCase) ||
            node.Id.Equals(_autoSelect, StringComparison.OrdinalIgnoreCase));
        if (wanted is not null)
        {
            NodeStore.Select(wanted);
        }

        _autoSelect = null;
    }

    private void UpdateSummary()
    {
        var nodes = NodeStore.Snapshot();
        var working = nodes.Count(node => node.Status == NodeStatus.Working);
        var mine = nodes.Count(node => node.IsMine);
        var universal = nodes.Count(node => node.UniversalOk == true || node.DelayMs is not null);
        var youtube = nodes.Count(node => node.YoutubeOk == true);
        var telegram = nodes.Count(node => node.TelegramOk == true);
        var discord = nodes.Count(node => node.DiscordOk == true);
        var bestDomains = working == 0
            ? 0
            : nodes.Where(node => node.Status == NodeStatus.Working)
                   .Max(node => node.OpenDomains);

        var lastChecked = nodes
            .Where(node => node.CheckedAtUtc is not null)
            .Select(node => node.CheckedAtUtc!.Value)
            .DefaultIfEmpty()
            .Max();

        if (_summary is not null)
        {
            _summary.Text = $"В списке: {nodes.Count} узлов · рабочих: {working}";
        }

        if (_detail is not null)
        {
            var checkedText = lastChecked == default
                ? "проверок ещё не было"
                : "проверено " + lastChecked.ToLocalTime().ToString("dd.MM HH:mm");
            _detail.Text = $"Доступ: универс. {universal}, YouTube {youtube}, " +
                           $"Telegram {telegram}, Discord {discord}\n" +
                           $"Мои подписки: {mine} · лимит {NodeStore.Settings.MaxTotalNodes} · " +
                           $"макс. доменов {bestDomains} · {checkedText}";
        }

        if (_connect is not null)
        {
            _connect.Text = "Подключить лучший узел";
        }
    }

    private async Task RunRefreshAsync()
    {
        if (_busy)
        {
            Toast.MakeText(this, "Дождитесь окончания операции", ToastLength.Short)?.Show();
            return;
        }

        _busy = true;
        _work = new CancellationTokenSource();
        SetProgress("Обновляю источники…");

        try
        {
            var progress = new Progress<string>(SetProgress);
            var summary = await NodeStore.RefreshAsync(progress, _work.Token);
            SetProgress(summary);
        }
        catch (System.OperationCanceledException)
        {
            SetProgress("Обновление отменено");
        }
        catch (Exception exception)
        {
            AppLog.Write("sources", "Ошибка обновления источников", exception);
            SetProgress("Ошибка обновления: " + exception.Message);
        }
        finally
        {
            _busy = false;
            UpdateSummary();
        }
    }

    private async Task RunTestAsync()
    {
        if (_busy)
        {
            Toast.MakeText(this, "Дождитесь окончания операции", ToastLength.Short)?.Show();
            return;
        }

        _busy = true;
        _work = new CancellationTokenSource();
        SetProgress("Проверяю узлы в отдельном процессе…");

        try
        {
            // Отдельный процесс :probe: основное ядро и VPN не перезапускаются.
            var summary = await ProbeClient.RunBatchAsync(NodeTester.MaxBatch, _work.Token);

            SetProgress(summary.Error is null
                ? $"Проверено {summary.Tested}: рабочих {summary.Working} " +
                  $"(универс. {summary.Universal}, YouTube {summary.Youtube}, " +
                  $"Telegram {summary.Telegram}, Discord {summary.Discord}), " +
                  $"лучший открывает доменов {summary.BestDomains}, удалено {summary.Removed}"
                : summary.Error);
        }
        catch (Exception exception)
        {
            AppLog.Write("test", "Ошибка проверки узлов", exception);
            SetProgress("Ошибка проверки: " + exception.Message);
        }
        finally
        {
            _busy = false;
            UpdateSummary();
        }
    }

    private void ConnectSelected()
    {
        var selected = NodeStore.Selected;
        if (selected is not null &&
            TunnelService.State == TunnelState.Running &&
            CoreRuntime.IsRunning)
        {
            // Туннель уже поднят: узел из его конфига переключаем через Clash selector —
            // без перезапуска ядра и без пересоздания TUN.
            if (CoreRuntime.LoadedTags.Contains(selected.Tag))
            {
                _ = SwitchNodeAsync(selected);
                return;
            }

            SetProgress("Проверенный узел не в текущем конфиге — переподключаю туннель");
        }

        var config = NodeStore.BuildTunnelConfig(out var error);
        if (config is null)
        {
            Toast.MakeText(this, error ?? "Нет узлов для подключения", ToastLength.Long)?.Show();
            SetProgress(error ?? "Нет узлов для подключения");
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
        Toast.MakeText(this, "Туннель запускается", ToastLength.Short)?.Show();
        Finish();
    }

    /// <summary>Переключение на уже загруженный узел: соединение не рвётся, ядро не перезапускается.</summary>
    private async Task SwitchNodeAsync(ProxyNode node)
    {
        SetProgress("Переключаюсь на проверенный узел…");
        try
        {
            using var api = new ClashApiClient(CoreRuntime.ApiPort, CoreRuntime.ApiSecret);
            await api.SelectAsync(SingBoxConfigBuilder.SelectorTag, node.Tag, CancellationToken.None);
            TunnelService.UpdateNodeLabel(node.DisplayDelay);
            SetProgress("Подключено · " + node.DisplayDelay);
            Toast.MakeText(this, "Подключено · " + node.DisplayDelay, ToastLength.Short)?.Show();
            Finish();
        }
        catch (Exception exception)
        {
            AppLog.Write("nodes", "Не удалось переключить узел", exception);
            SetProgress("Ядро не приняло узел: " + exception.Message);
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
            Toast.MakeText(this, "Туннель запускается", ToastLength.Short)?.Show();
            Finish();
        }
        else
        {
            SetProgress("Разрешение VPN не выдано");
        }

        _pendingConfig = null;
    }

    private void SetProgress(string text) => RunOnUiThread(() =>
    {
        if (_progress is not null)
        {
            _progress.Text = text;
        }
    });
}
