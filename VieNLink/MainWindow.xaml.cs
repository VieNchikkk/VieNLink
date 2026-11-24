using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Threading;

namespace VieNLink;

public partial class MainWindow : Window
{
    /// <summary>Сколько серверов проверяем за один запуск: бесплатные списки большие, но проверка не бесконечная.</summary>
    private const int TestBatchSize = 500;

    /// <summary>Сколько серверов проверять сразу после первого заполнения списка.</summary>
    private const int StartupProbeBatch = 150;

    private readonly AppSettings _settings = App.Settings;
    private readonly ProxyManager _manager;
    private readonly ZapretService _zapret = new();
    private readonly DesyncService _desync = new();
    private readonly TunBypassService _tunBypass = new();
    private bool _tunBypassStartedDesync;
    private readonly ObservableCollection<NodeRow> _rows = new();
    private readonly ObservableCollection<NodeRow> _workingRows = new();
    private readonly ObservableCollection<SourceRow> _sourceRows = new();
    private bool _sourceTotalsDirty;
    private readonly ObservableCollection<MtProtoRow> _mtprotoRows = new();
    private readonly ObservableCollection<MtProtoRow> _mtprotoDeadRows = new();
    private List<SourceDefinition> _sources = new();
    private ICollectionView? _view;
    private ICollectionView? _workingView;
    private TrayIconService? _tray;
    private ZapretWidget? _zapretWidget;
    private DispatcherTimer? _idleTimer;
    private CancellationTokenSource? _idleCts;
    private bool _idleTesting;
    private DateTime? _lastIdleTestUtc;
    private DateTime? _lastRecheckUtc;
    private CancellationTokenSource? _mtprotoCollectCts;
    private CancellationTokenSource? _mtprotoCheckCts;
    private bool _mtprotoCollecting;
    private bool _mtprotoChecking;
    private DateTime? _lastMtProtoCollectUtc;
    private DispatcherTimer? _autoTimer;
    private DispatcherTimer? _counterTimer;
    private CancellationTokenSource? _operation;
    private bool _busy;
    private string _operationLabel = "текущее действие";
    private ProxyNode? _headerConnectingNode;
    private TrafficSample? _lastTrafficSample;
    private bool _bypassSpeedMeasuring;
    private string _bypassSpeedMode = string.Empty;
    private double? _bypassSpeedMbps;
    private DateTime _bypassSpeedMeasuredUtc;
    private bool _exitRequested;
    private bool _closing;
    private bool _loadingSettings;
    private bool _trayHintShown;
    private readonly HashSet<string> _failedForSession = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Сервис, к которому подключались кнопкой: приоритет при переключении
    /// (сначала его серверы, затем остальные) и исключение текущего сервера при смене
    /// сервиса, чтобы было реальное переподключение. Сбрасывается при отключении
    /// и выборе конкретного сервера в списке.</summary>
    private string? _activeService;
    private DateTime _lastServiceUpgradeUtc;
    private int _serviceUpgradeBusy;

    /// <summary>Сервер, на который уже не удалось вернуться: не долбим его каждую минуту.</summary>
    private readonly Dictionary<string, DateTime> _upgradeRetryAfter = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, NodeRow> _rowIndex = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, ObservableCollection<NodeRow>> _rowOwner = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Убранные из таблицы серверы: нужны, чтобы откат проверки вернул их обратно.</summary>
    private readonly List<ProxyNode> _droppedNodes = new();
    private Task _initialLoad = Task.CompletedTask;
    private DateTime _lastTestedSummaryUtc;
    private int _failoverCount;
    private int _warmSwitchCount;
    private int _coldSwitchCount;
    private DateTime? _lastFailoverUtc;
    private string _lastFailoverNote = "—";

    /// <summary>Сбой сервера пришёл во время другой операции: переключимся сразу после неё.</summary>
    private ProxyNode? _pendingFailoverNode;

    /// <summary>
    /// Возврат в окно после простоя: если свежих серверов нет, сразу запускаем
    /// короткую перепроверку, не дожидаясь минутного такта.
    /// </summary>
    private void EnsureCountersFresh()
    {
        if (_closing || _busy || _idleTesting || !_settings.IdleTestEnabled ||
            _settings.AutomationPaused || _workingRows.Count == 0 ||
            FreshWorkingCount() >= NodeLimits.MinWorkingNodes)
        {
            return;
        }

        if (_lastRecheckUtc is { } last &&
            DateTime.UtcNow - last < TimeSpan.FromSeconds(30))
        {
            return;
        }

        _ = RunIdleTestAsync(userIdle: false, searchNew: false);
    }

    public MainWindow()
    {
        InitializeComponent();

        // Текущая версия видна и в заголовке окна, и в шапке: пользователь сразу
        // понимает, какая сборка установлена.
        Title = $"{AppInfo.Name} {AppInfo.Version} — свободные серверы";
        HeaderVersionText.Text = "v" + AppInfo.Version;

        SourceInitialized += (_, _) => WindowChromeTheme.Apply(this);
        Activated += (_, _) => EnsureCountersFresh();
        InitializeWindowGeometry();

        // Если прошлый запуск завершился аварийно, снимаем оставшееся правило
        // блокировки QUIC — оно не должно работать без включённого обхода.
        ZapretService.ClearStaleQuicBlock();

        _manager = new ProxyManager(_settings);
        _manager.StateChanged += OnManagerStateChanged;
        _manager.NodeUnhealthy += OnNodeUnhealthy;
        _manager.TrafficUpdated += OnTrafficUpdated;
        AppLog.LineWritten += OnLogLine;

        _view = CollectionViewSource.GetDefaultView(_rows);
        _view.Filter = FilterNode;
        NodesGrid.ItemsSource = _view;

        _workingView = CollectionViewSource.GetDefaultView(_workingRows);
        _workingView.Filter = FilterWorkingNode;
        WorkingGrid.ItemsSource = _workingView;

        // По умолчанию — от меньшего пинга к большему; непроверенные (null) в конце.
        _view.SortDescriptions.Add(new SortDescription(nameof(NodeRow.PingSortValue), ListSortDirection.Ascending));
        _workingView.SortDescriptions.Add(new SortDescription(nameof(NodeRow.PingSortValue), ListSortDirection.Ascending));
        SourcesList.ItemsSource = _sourceRows;
        MtProtoGrid.ItemsSource = _mtprotoRows;
        MtProtoDeadGrid.ItemsSource = _mtprotoDeadRows;

        // Источники делятся на «Мои подписки» и «Публичные», чтобы не путаться.
        if (CollectionViewSource.GetDefaultView(_sourceRows) is { } sourcesView)
        {
            sourcesView.GroupDescriptions.Add(new PropertyGroupDescription(nameof(SourceRow.GroupName)));
            sourcesView.SortDescriptions.Add(new SortDescription(nameof(SourceRow.GroupName), ListSortDirection.Ascending));
            sourcesView.SortDescriptions.Add(new SortDescription(nameof(SourceRow.Name), ListSortDirection.Ascending));
            SourcesList.ItemsSource = sourcesView;
        }

        InitializeRejectedRows();
        BuildZapretPresetList();
        BuildDesyncStrategyList();
        BuildTunBypassStrategyList();
        LoadSettingsToUi();
        // Кэш на десятки тысяч серверов читается и разбирается в фоне: окно появляется сразу.
        _initialLoad = LoadSourcesAndCacheAsync();
        InitializeTray();
        _idleTimer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(1) };
        _idleTimer.Tick += OnIdleTick;
        _idleTimer.Start();
        UpdateConnectionUi();
        UpdateCoreStatus();
        UpdateZapretUi();
        UpdateDesyncUi();
        UpdateTunBypassUi();
        AppendLog(AppInfo.DisplayName + " запущен. Обновите списки, проверьте серверы и подключитесь.");
        Loaded += async (_, _) =>
        {
            var arguments = Environment.GetCommandLineArgs();

            // Автозапуск из ключа Run: стартуем в трее, не выкидывая окно поверх работы.
            // Прячем до загрузки кэша, иначе окно успевает мигнуть.
            if (arguments.Any(arg => arg.Equals("--minimized", StringComparison.OrdinalIgnoreCase)))
            {
                Hide();
            }

            await _initialLoad;

            // Запуск после повышения прав из виджета: показываем только виджет,
            // главное окно остаётся в трее.
            var widgetRequested = arguments.Any(arg =>
                arg.Equals("--widget", StringComparison.OrdinalIgnoreCase));
            if (widgetRequested)
            {
                ToggleZapretWidget();
                Hide();
            }
            else if (_settings.WidgetVisible)
            {
                // Виджет включён по умолчанию: пользователь сразу видит переключатели
                // обхода. Закрытие крестиком сохраняет выбор и больше его не открывает.
                ToggleZapretWidget();
            }

            // Ранее рабочие серверы перепроверяем ДО обновления источников: иначе
            // скачивание списков (а оно бывает долгим) держало значки сервисов
            // на нулях — рабочие серверы в кэше есть, а значки обещали обратное.
            // Пока идёт проверка, обновление списков уже можно запускать параллельно.
            var recheckOnStart = _settings.IdleTestEnabled && !_settings.AutomationPaused &&
                                 HasWorkingNodesDue(DateTime.UtcNow);
            if (recheckOnStart)
            {
                SetStatus("Проверяю серверы", "После запуска обновляю счётчики…");
                _ = RunIdleTestAsync(userIdle: false, searchNew: false);
            }

            await AutoRefreshIfStaleAsync();
            await AutoTickAsync();

            // Telegram-прокси собираются сами, без ручных кнопок: RSS не зависит от t.me.
            // При первом запуске сразу проверяем большую партию настоящим рукопожатием.
            _ = CollectAndCheckMtProtoOnStartupAsync();

            // Запуск после повышения прав: включаем обход DPI, свой обход или подключаемся в TUN-режиме.
            if (arguments.Any(arg => arg.Equals("--store-loopback", StringComparison.OrdinalIgnoreCase)))
            {
                var loopback = await WindowsIntegration.EnableStoreAppLoopbackAsync(CancellationToken.None);
                StoreLoopbackText.Text = loopback.Message;
                SetStatus("Совместимость с приложениями Магазина", loopback.Message);
            }
            else if (arguments.Any(arg => arg.Equals("--tun-bypass-tune", StringComparison.OrdinalIgnoreCase)))
            {
                await StartTunBypassAsync(forceTune: true);
            }
            else if (arguments.Any(arg => arg.Equals("--tun-bypass", StringComparison.OrdinalIgnoreCase)))
            {
                await StartTunBypassAsync();
            }
            else if (arguments.Any(arg => arg.Equals("--desync-tune", StringComparison.OrdinalIgnoreCase)))
            {
                await StartDesyncAsync(true);
            }
            else if (arguments.Any(arg => arg.Equals("--desync", StringComparison.OrdinalIgnoreCase)))
            {
                await StartDesyncAsync(false);
            }
            else if (arguments.Any(arg => arg.Equals("--zapret-tune", StringComparison.OrdinalIgnoreCase)))
            {
                await StartZapretAsync(true);
            }
            else if (arguments.Any(arg => arg.Equals("--zapret", StringComparison.OrdinalIgnoreCase)))
            {
                await StartZapretAsync(false);
            }
            else if (arguments.Any(arg => arg.Equals("--tunnel", StringComparison.OrdinalIgnoreCase)) &&
                     !_manager.IsConnected)
            {
                var node = PickBestNode();
                if (node is null)
                {
                    SetStatus("Нет серверов для TUN", "Обновите списки и проверьте серверы");
                }
                else
                {
                    await ConnectAsync(node);
                }
            }

            // Ядро sing-box проверяется и обновляется само при каждом запуске.
            _ = AutoUpdateCoreOnStartupAsync();
            // Обновление самого VieNLink с GitHub: если версия новее — предложить установку.
            _ = AutoCheckAppUpdateAsync();
        };

        _autoTimer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(1) };
        _autoTimer.Tick += async (_, _) => await AutoTickAsync();
        _autoTimer.Start();

        // Автоматика могла быть выключена пользователем в прошлый раз.
        if (_settings.AutomationPaused)
        {
            _idleTimer.Stop();
            _autoTimer.Stop();
        }

        // Счётчики в шапке и на вкладке «Серверы» живут отдельно от проверок: сервер,
        // у которого истекло окно свежести (5 минут), должен исчезнуть из числа
        // сам, даже если в этот момент ничего не проверяется. Обновляем каждые
        // 3 секунды: числа должны успевать за проверкой, а не раз в минуту.
        _counterTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        _counterTimer.Tick += (_, _) =>
        {
            if (!_closing)
            {
                UpdateSummary();
                UpdateSourceWorkingStats();
            }
        };
        _counterTimer.Start();
    }

    /// <summary>Раз в минуту проверяем, не пора ли обновить списки и поискать новые источники.</summary>
    private async Task AutoTickAsync()
    {
        if (_busy || _closing || _settings.AutomationPaused)
        {
            return;
        }

        var now = DateTime.UtcNow;
        var emptyList = NodeCount == 0;

        // Живых серверов мало (квота не набрана) — списки обновляем чаще: бесплатные
        // серверы вымирают, и старый список уже не даёт рабочих адресов.
        var poorList = !emptyList &&
                       (FreshWorkingCount() < NodeLimits.MinWorkingNodes ||
                        MissingServiceQuota().Count > 0);

        var refreshDue = _settings.AutoCollectServers && _settings.AutoRefreshMinutes > 0 &&
                         now - _settings.LastAutoRefreshUtc >= TimeSpan.FromMinutes(_settings.AutoRefreshMinutes);
        if (!refreshDue && poorList && _settings.AutoCollectServers &&
            _settings.AutoRefreshMinutes > 0 &&
            now - _settings.LastAutoRefreshUtc >= TimeSpan.FromMinutes(30))
        {
            refreshDue = true;
        }

        if (!refreshDue && emptyList && _settings.AutoCollectServers &&
            _settings.AutoRefreshMinutes > 0 &&
            now - _settings.LastAutoRefreshUtc >= TimeSpan.FromMinutes(15))
        {
            // Пустой список — повторяем чаще: источник мог быть временно недоступен.
            refreshDue = true;
        }

        var discoveryDue = _settings.AutoDiscoverSources &&
                           now - _settings.LastDiscoveryUtc >= TimeSpan.FromHours(Math.Max(1, _settings.AutoDiscoverHours));
        if (!discoveryDue && emptyList && _settings.AutoDiscoverSources &&
            now - _settings.LastDiscoveryUtc >= TimeSpan.FromMinutes(30))
        {
            discoveryDue = true;
        }

        // Список заполнен: не тянем новые серверы и не ищем источники, пока накопленные не проверены.
        if ((refreshDue || discoveryDue) && AtNodeLimit)
        {
            NotifyNodeLimit();
            return;
        }

        if ((!refreshDue && !discoveryDue) || !BeginOperation("автообновление"))
        {
            return;
        }

        try
        {
            if (refreshDue)
            {
                _settings.LastAutoRefreshUtc = now;
                SaveSettings();
                AppendLog("Автообновление списков…");
                var count = await RefreshSourcesCoreAsync(testAfter: _settings.TestAfterRefresh, testLimit: 150);
                AppendLog($"Автообновление: серверов {count}");
            }

            if (discoveryDue && !_operation!.IsCancellationRequested)
            {
                _settings.LastDiscoveryUtc = now;
                SaveSettings();
                await DiscoverSourcesCoreAsync(includeNodes: _settings.AutoCollectServers);
            }
        }
        catch (OperationCanceledException)
        {
            AppendLog("Автоцикл остановлен");
        }
        catch (Exception exception)
        {
            AppLog.Write("auto", "Автоцикл не удался", exception);
        }
        finally
        {
            EndOperation();
            UpdateConnectionUi();
        }
    }

    private async void OnDiscoverClick(object sender, RoutedEventArgs e)
    {
        if (AtNodeLimit)
        {
            NotifyNodeLimit();
            SetStatus("Достигнут лимит серверов",
                $"В списке {NodeCount} из {_settings.MaxTotalNodes}. Сначала проверьте накопленные: «Серверы» → «Все серверы»");
            return;
        }

        if (!BeginOperation("поиск источников"))
        {
            return;
        }

        try
        {
            await DiscoverSourcesCoreAsync();
            _settings.LastDiscoveryUtc = DateTime.UtcNow;
            SaveSettings();
        }
        catch (OperationCanceledException)
        {
            SetStatus("Поиск источников остановлен", string.Empty);
        }
        catch (Exception exception)
        {
            ReportError("Не удалось найти новые источники", exception);
        }
        finally
        {
            EndOperation();
            UpdateConnectionUi();
        }
    }

    private async Task DiscoverSourcesCoreAsync(bool includeNodes = true)
    {
        if (AtNodeLimit)
        {
            NotifyNodeLimit();
            return;
        }

        SetStatus("Ищу новые источники", "GitHub и публичные сайты: свежие списки серверов…");
        var progress = new Progress<string>(line =>
        {
            StatusDetail.Text = line;
            AppendLog(line);
        });

        var result = await SourceDiscoveryService.DiscoverAsync(
            _sources, progress, _operation!.Token, _settings.SourceFreshnessHours);
        foreach (var source in result.Sources)
        {
            _sources.Add(source);
            _sourceRows.Add(new SourceRow(source));
        }

        if (result.Sources.Count > 0)
        {
            if (includeNodes)
            {
                MergeNodes(result.Nodes);
            }

            var removed = PruneAutoSources();
            NodeSourceService.SaveSources(_sources);
            var nodesNote = includeNodes ? $", серверов {result.Nodes.Count}" : ", серверы не добавлялись (авто-сбор выключен)";
            AppendLog($"Найдено источников: {result.Sources.Count}{nodesNote}" +
                      (removed > 0 ? $", старых автоисточников удалено: {removed}" : string.Empty));
            SetStatus("Найдены новые источники", $"Добавлено {result.Sources.Count}{nodesNote}");
            _tray?.ShowBalloon(
                "Найдены новые источники",
                string.Join(Environment.NewLine, result.Sources.Select(s => s.Name)) + Environment.NewLine +
                (includeNodes ? $"Серверов: {result.Nodes.Count}" : "Серверы не добавлялись: авто-сбор выключен"));
        }
        else
        {
            var details = $"Проверено файлов: {result.FilesChecked}";
            if (!string.IsNullOrWhiteSpace(result.Error))
            {
                details += " · " + result.Error;
            }

            SetStatus("Новых источников нет", details);
        }
    }

    /// <summary>
    /// Дневные подписки публичных сайтов меняют адрес каждый день: держим по 5 свежих
    /// автоисточников на сайт (на GitHub — на репозиторий), иначе список превращается в свалку.
    /// </summary>
    private int PruneAutoSources()
    {
        const int keepPerSite = 5;
        var removed = 0;
        var groups = _sources
            .Where(s => s.IsAutoDiscovered && !s.IsMine)
            .GroupBy(SourceGroupKey)
            .ToList();

        foreach (var group in groups)
        {
            foreach (var source in group
                         .OrderByDescending(s => s.AddedAtUtc)
                         .ThenByDescending(s => s.LastCheckedUtc)
                         .Skip(keepPerSite)
                         .ToList())
            {
                _sources.Remove(source);
                var row = _sourceRows.FirstOrDefault(r => ReferenceEquals(r.Source, source));
                if (row is not null)
                {
                    _sourceRows.Remove(row);
                }

                removed++;
            }
        }

        return removed;
    }

    /// <summary>Ключ группы источников: сайт, а для GitHub — конкретный репозиторий.</summary>
    private static string SourceGroupKey(SourceDefinition source)
    {
        if (!Uri.TryCreate(source.Url, UriKind.Absolute, out var uri))
        {
            return source.Name;
        }

        var host = uri.Host.ToLowerInvariant();
        if (host != "raw.githubusercontent.com")
        {
            return host;
        }

        var parts = uri.AbsolutePath.Trim('/').Split('/');
        return parts.Length >= 2 ? host + "/" + parts[0] + "/" + parts[1] : host;
    }

    /// <summary>При запуске подтягиваем устаревшие подписки, чтобы список не был «вчерашним».</summary>
    private async Task AutoRefreshIfStaleAsync()
    {
        if (_busy || _sources.Count == 0 || _settings.AutomationPaused ||
            !_settings.AutoCollectServers)
        {
            return;
        }

        // Пустой список — это первый запуск или данные вычистили: обновляем без
        // ожидания интервала, иначе пользователь видит пустое приложение.
        var empty = NodeCount == 0;
        if (!_settings.AutoUpdateSubscriptions && !empty)
        {
            return;
        }

        if (AtNodeLimit)
        {
            NotifyNodeLimit();
            return;
        }

        var threshold = TimeSpan.FromHours(Math.Clamp(_settings.AutoUpdateHours, 1, 168));
        var stale = empty ||
                    _sources.Any(s => s.Enabled && (s.LastCheckedUtc is null || DateTime.UtcNow - s.LastCheckedUtc.Value > threshold));
        if (!stale || !BeginOperation("автообновление подписок"))
        {
            return;
        }

        try
        {
            var count = await RefreshSourcesCoreAsync(testAfter: false, testLimit: 0);
            _settings.LastAutoRefreshUtc = DateTime.UtcNow;
            SaveSettings();

            if (empty && NodeCount > 0 && _settings.IdleTestEnabled)
            {
                // Сразу проверяем партию скрытым ядром: без этого «Рабочие» остаются
                // пустыми до первого пятиминутного тика.
                AppendLog($"Первый запуск: проверяю {Math.Min(NodeCount, StartupProbeBatch)} серверов");
                var batch = SelectBatch(AllRows.Select(row => row.Node)).Take(StartupProbeBatch).ToList();
                await RunTestAsync(batch, "Стартовая проверка серверов");
            }

            SetStatus("Подписки обновлены", $"Серверов: {NodeCount}, MTProto: {_mtprotoRows.Count}");
        }
        catch (OperationCanceledException)
        {
            SetStatus("Автообновление отменено", string.Empty);
        }
        catch (Exception exception)
        {
            AppLog.Write("sources", "Автообновление не удалось", exception);
            StatusDetail.Text = exception.Message;
        }
        finally
        {
            EndOperation();
            UpdateConnectionUi();
        }
    }

    /// <summary>
    /// Браузер — отдельная установка (`VieNLink.Browser.exe`): запускаем его как отдельный процесс.
    /// </summary>
    private void OnOpenBrowserClick(object sender, RoutedEventArgs e) => LaunchBrowser();

    /// <summary>Ищет и запускает VieNLink.Browser.exe (рядом с приложением или в своей установке).</summary>
    private static void LaunchBrowser()
    {
        var candidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "VieNLink.Browser.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Programs", "VieNLink Browser", "VieNLink.Browser.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                "VieNLink Browser", "VieNLink.Browser.exe"),
        };

        foreach (var candidate in candidates)
        {
            if (!File.Exists(candidate))
            {
                continue;
            }

            try
            {
                Process.Start(new ProcessStartInfo(candidate) { UseShellExecute = true });
                return;
            }
            catch (Exception exception)
            {
                AppLog.Write("browser", "Не удалось запустить браузер", exception);
            }
        }

        MessageBox.Show(
            "Браузер VieNLink не найден. Он ставится отдельно (VieNLink Браузер) и обновляется " +
            "независимо от VPN-приложения.",
            "VieNLink", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    /// <summary>Ctrl+Shift+B — быстрый доступ к браузеру из любого места окна.</summary>
    private void OnMainWindowPreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == System.Windows.Input.Key.B &&
            (System.Windows.Input.Keyboard.Modifiers &
             (System.Windows.Input.ModifierKeys.Control | System.Windows.Input.ModifierKeys.Shift)) ==
            (System.Windows.Input.ModifierKeys.Control | System.Windows.Input.ModifierKeys.Shift))
        {
            LaunchBrowser();
            e.Handled = true;
        }
    }

}
