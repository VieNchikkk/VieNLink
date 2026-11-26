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

public partial class MainWindow
{
    // ---------- загрузка ----------

    private void LoadSettingsToUi()
    {
        _loadingSettings = true;
        try
        {
            PortBox.Text = _settings.MixedPort.ToString();
            ApiPortBox.Text = _settings.ApiPort.ToString();
            MaxNodesBox.Text = _settings.MaxNodesInConfig.ToString();
            MaxTotalNodesBox.Text = _settings.MaxTotalNodes.ToString();
            MaxDelayBox.Text = _settings.MaxDelayMs.ToString();
            TestUrlBox.Text = _settings.TestUrl;
            BypassBox.Text = _settings.ProxyBypass;
            SniBox.Text = _settings.DefaultSni;
            TlsFragmentCheck.IsChecked = _settings.TlsFragment;
            TlsRecordFragmentCheck.IsChecked = _settings.TlsRecordFragment;
            AutoUpdateCheck.IsChecked = _settings.AutoUpdateSubscriptions;
            AutoUpdateHoursBox.Text = _settings.AutoUpdateHours.ToString();
            KeepUnseenDaysBox.Text = _settings.KeepUnseenNodeDays.ToString();
            AutoRefreshBox.Text = _settings.AutoRefreshMinutes.ToString();
            SourceFreshnessHoursBox.Text = _settings.SourceFreshnessHours.ToString();
            AutoCollectServersCheck.IsChecked = _settings.AutoCollectServers && _settings.AutoRefreshMinutes > 0;
            AutoDiscoverCheck.IsChecked = _settings.AutoDiscoverSources;
            AutoDiscoverHoursBox.Text = _settings.AutoDiscoverHours.ToString();
            AutoMtProtoCheck.IsChecked = _settings.AutoMtProto;
            AutoCoreUpdateCheck.IsChecked = _settings.AutoUpdateCore;
            AutoCheckAppUpdatesCheck.IsChecked = _settings.AutoCheckAppUpdates;
            ApplyProxyCheck.IsChecked = _settings.ApplySystemProxy;
            AutoFailoverCheck.IsChecked = _settings.AutoFailover;
            OnlyVerifiedConnectCheck.IsChecked = _settings.ConnectOnlyVerified;
            OnlyMineConnectCheck.IsChecked = _settings.ConnectOnlyMine;
            ZapretAutoStartCheck.IsChecked = _settings.ZapretAutoStart;
            DesyncAutoStartCheck.IsChecked = _settings.DesyncAutoStart;
            ZapretBlockQuicCheck.IsChecked = _settings.ZapretBlockQuic;
            DesyncBlockQuicCheck.IsChecked = _settings.DesyncBlockQuic;
            UpdateDesyncStrategyInfo(_settings.DesyncStrategy);
            TunBypassBlockQuicCheck.IsChecked = _settings.TunBypassBlockQuic;
            TunBypassAutoStartCheck.IsChecked = _settings.TunBypassAutoStart;
            SelectTunBypassStrategyRadio(_settings.TunBypassStrategy);
            UpdateZapretStrategyInfo(_settings.ZapretPreset);
        TestAfterRefreshCheck.IsChecked = _settings.TestAfterRefresh;
            IdleTestCheck.IsChecked = _settings.IdleTestEnabled;
            IdleMinutesBox.Text = _settings.IdleTestMinutes.ToString();
            IdleBatchBox.Text = _settings.IdleTestBatch.ToString();
            IdleIntervalBox.Text = _settings.IdleTestIntervalMinutes.ToString();
            MinimizeTrayCheck.IsChecked = _settings.MinimizeToTray;
            WidgetTopmostCheck.IsChecked = _settings.WidgetTopmost;
            UpdateAutomationUi();
            LightThemeCheck.IsChecked = string.Equals(_settings.Theme, "Light", StringComparison.OrdinalIgnoreCase);
            // Автозапуск читаем из реестра: это фактическое состояние, а не пожелание.
            AutostartCheck.IsChecked = AutostartService.IsEnabled;
            KillSwitchCheck.IsChecked = _settings.KillSwitch;
            ShowTrafficCheck.IsChecked = _settings.ShowTraffic;
            ShowExitInfoCheck.IsChecked = _settings.ShowExitInfo;
            AppVersionText.Text = "Версия приложения: " + AppInfo.VersionLabel +
                " · ядро: " + SingBoxCore.InstalledVersion;

            TunModeRadio.IsChecked = _settings.TunnelMode == TunnelMode.Tun;
            SystemProxyModeRadio.IsChecked = _settings.TunnelMode != TunnelMode.Tun;
            StrictRouteCheck.IsChecked = _settings.TunStrictRoute;

            RouteAllRadio.IsChecked = _settings.SiteRouteMode == SiteRouteMode.All;
            RouteOnlyRadio.IsChecked = _settings.SiteRouteMode == SiteRouteMode.OnlyListed;
            RouteExceptRadio.IsChecked = _settings.SiteRouteMode == SiteRouteMode.AllExceptListed;
            SiteListBox.Text = _settings.SiteList;

            var appMode = _settings.AppRouteMode;
            if (appMode == AppRouteMode.All && _settings.OnlySelectedApps)
            {
                appMode = AppRouteMode.OnlyListed;
            }

            AppAllRadio.IsChecked = appMode == AppRouteMode.All;
            AppOnlyRadio.IsChecked = appMode == AppRouteMode.OnlyListed;
            AppExceptRadio.IsChecked = appMode == AppRouteMode.AllExceptListed;
            OnlyAppsCheck.IsChecked = appMode == AppRouteMode.OnlyListed;
            AppListBox.Text = _settings.AppList;
        }
        finally
        {
            _loadingSettings = false;
        }

        UpdateRoutingAvailability();
        RefreshRoutingChips();
    }

    /// <summary>
    /// TUN забирает весь трафик через адаптер, системный прокси при этом не используется —
    /// галочку отключаем, чтобы не создавать иллюзию двух одновременных режимов.
    /// </summary>
    private void UpdateRoutingAvailability()
    {
        var tun = TunModeRadio.IsChecked == true;
        ApplyProxyCheck.IsEnabled = !tun;
        ApplyProxyCheck.ToolTip = tun
            ? "В TUN-режиме системный прокси не нужен: трафик всех программ идёт через адаптер sing-box."
            : "Переключать системный прокси Windows при подключении";
    }

    /// <summary>Меню маршрутизации меняет настройки сразу: иначе «Подключиться» возьмёт старые.</summary>
    private void OnRoutingChanged(object sender, RoutedEventArgs e)
    {
        if (_loadingSettings)
        {
            return;
        }

        ApplyRoutingSettingsFromUi();
        UpdateRoutingAvailability();
        SaveSettings();
    }

    private void OnPickProcessClick(object sender, RoutedEventArgs e)
    {
        var picker = new ProcessPickerWindow { Owner = this };
        if (picker.ShowDialog() == true)
        {
            AddAppsToRouting(picker.SelectedNames);
        }
    }

    private void OnPickShortcutClick(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Выберите программу или ярлык",
            Filter = "Программы и ярлыки (*.exe;*.lnk)|*.exe;*.lnk|Все файлы (*.*)|*.*",
            Multiselect = true,
        };
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        var names = dialog.FileNames
            .Select(ResolveProcessName)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Select(name => name!)
            .ToList();
        AddAppsToRouting(names);
    }

    /// <summary>Имя процесса из exe или из ярлыка (.lnk).</summary>
    private static string? ResolveProcessName(string path)
    {
        try
        {
            if (path.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase))
            {
                var shellType = Type.GetTypeFromProgID("WScript.Shell");
                if (shellType is null)
                {
                    return null;
                }

                dynamic shell = Activator.CreateInstance(shellType)!;
                dynamic shortcut = shell.CreateShortcut(path);
                string target = shortcut.TargetPath;
                return string.IsNullOrWhiteSpace(target) ? null : Path.GetFileName(target);
            }

            return Path.GetFileName(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
                                             or System.Runtime.InteropServices.COMException)
        {
            return null;
        }
    }

    private void AddAppsToRouting(IEnumerable<string> names)
    {
        var added = names
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Select(name => name.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (added.Count == 0)
        {
            return;
        }

        var current = AppListBox.Text
            .Split('\n')
            .Select(line => line.Trim())
            .Where(line => line.Length > 0)
            .ToList();
        foreach (var name in added)
        {
            if (!current.Contains(name, StringComparer.OrdinalIgnoreCase))
            {
                current.Add(name);
            }
        }

        AppListBox.Text = string.Join(Environment.NewLine, current);
        AppOnlyRadio.IsChecked = true;
        OnlyAppsCheck.IsChecked = true;
        RefreshAppChips();
        OnRoutingChanged(this, new RoutedEventArgs());
        FooterText.Text = "В список приложений добавлено: " + string.Join(", ", added);
    }

    private void ApplyRoutingSettingsFromUi()
    {
        _settings.TunnelMode = TunModeRadio.IsChecked == true ? TunnelMode.Tun : TunnelMode.SystemProxy;
        _settings.TunStrictRoute = StrictRouteCheck.IsChecked == true;
        _settings.SiteRouteMode = RouteOnlyRadio.IsChecked == true
            ? SiteRouteMode.OnlyListed
            : RouteExceptRadio.IsChecked == true
                ? SiteRouteMode.AllExceptListed
                : SiteRouteMode.All;
        _settings.SiteList = SiteListBox.Text;
        _settings.AppRouteMode = AppOnlyRadio.IsChecked == true
            ? AppRouteMode.OnlyListed
            : AppExceptRadio.IsChecked == true
                ? AppRouteMode.AllExceptListed
                : AppRouteMode.All;
        _settings.OnlySelectedApps = _settings.AppRouteMode == AppRouteMode.OnlyListed;
        _settings.AppList = AppListBox.Text;
    }

    /// <summary>
    /// Чтение источников и кэша серверов. Разбор десятков мегабайт JSON и ссылок идёт
    /// в фоновом потоке, а коллекции UI заполняются уже в диспетчере.
    /// </summary>
    private async Task LoadSourcesAndCacheAsync()
    {
        CacheSnapshot snapshot;
        try
        {
            snapshot = await Task.Run(() => ReadCacheSnapshot(_settings));
        }
        catch (Exception exception)
        {
            AppLog.Write("cache", "Не удалось прочитать кэш", exception);
            return;
        }

        // Окно могли закрыть, пока читался кэш: диспетчер уже не принимает работу.
        if (_closing)
        {
            return;
        }

        _sources = snapshot.Sources;
        _sourceRows.Clear();
        foreach (var source in _sources)
        {
            _sourceRows.Add(new SourceRow(source));
        }

        // «Всего зарегистрировано» для старых источников считаем по уже собранному кэшу:
        // иначе колонка была бы пустой до следующего обновления.
        if (NodeSourceStats.SeedTotals(_sources, snapshot.Nodes))
        {
            NodeSourceService.SaveSources(_sources);
        }

        ReplaceNodes(snapshot.Nodes);
        // Рабочий сервер не может одновременно лежать в отсеве: такие «фантомы»
        // оставлял старый откат партии проверки.
        DropWorkingFromRejected();

        if (snapshot.DroppedNodes > 0)
        {
            AppendLog($"Из кэша удалено {snapshot.DroppedNodes} нерабочих/старых серверов — они не хранятся");
        }

        if (snapshot.DuplicateNodes > 0)
        {
            AppendLog($"Склеено повторов: {snapshot.DuplicateNodes} — одинаковые серверы оставлены одной строкой");
        }

        if (snapshot.TrimmedNodes > 0)
        {
            AppendLog($"Список ограничен {_settings.MaxTotalNodes} серверами: из кэша убрано {snapshot.TrimmedNodes} лишних. " +
                      "Новые появятся после проверки накопленных");
        }

        if (snapshot.ExpiredMtProto > 0)
        {
            AppendLog($"Из «Нерабочих» MTProto удалено {snapshot.ExpiredMtProto} прокси старше {MtProtoRetention.DeadHours} ч");
        }

        if (snapshot.TrimmedMtProto > 0)
        {
            AppendLog($"Из «Нерабочих» MTProto удалено {snapshot.TrimmedMtProto} самых старых (лимит {MtProtoRetention.MaxDead})");
        }

        RebuildMtProtoRows(snapshot.MtProto);

        if (snapshot.Nodes.Count > 0)
        {
            StatusDetail.Text = "Кэш прочитан — можно проверять серверы";
            FooterText.Text = $"Кэш от {snapshot.CacheTime:dd.MM HH:mm}";
        }

        // После разбора большого кэша остаётся много мусора в LOH — возвращаем память системе.
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: false, compacting: true);
    }

    /// <summary>Данные кэша, подготовленные в фоне: UI-коллекции создаются только в диспетчере.</summary>
    private sealed record CacheSnapshot(
        List<SourceDefinition> Sources,
        List<ProxyNode> Nodes,
        List<MtProtoProxy> MtProto,
        int ExpiredMtProto,
        int TrimmedMtProto,
        int DroppedNodes,
        int DuplicateNodes,
        int TrimmedNodes,
        DateTime CacheTime);

    private static CacheSnapshot ReadCacheSnapshot(AppSettings settings)
    {
        var sources = NodeSourceService.LoadSources();
        var freshAfter = DateTime.UtcNow.AddDays(-Math.Clamp(settings.KeepUnseenNodeDays, 1, 365));
        // Рабочие лежат отдельным файлом: он маленький и всегда свежий.
        var nodes = NodeCacheWriter.Load(freshAfter, out var droppedNodes, out var duplicateNodes, out _);

        // Первый запуск без кэша: подгружаем встроенный стартовый набор серверов,
        // чтобы не ждать первый сбор и проверку. Файл кладёт установщик рядом с exe.
        if (nodes.Count == 0)
        {
            var seed = NodeSourceService.LoadSeed(Path.Combine(AppContext.BaseDirectory, "seed.txt"));
            if (seed.Count > 0)
            {
                nodes = seed;
                AppLog.Write("seed", $"Встроенный набор: {seed.Count} серверов");
            }
        }

        // Кэш мог вырасти до десятков тысяч: жёстко ограничиваем список и оставляем
        // личные и проверенные серверы, а не первые попавшиеся.
        var (keptNodes, trimmedNodes) = NodeLimits.Trim(nodes, settings.MaxTotalNodes);
        nodes = keptNodes;
        if (droppedNodes > 0 || duplicateNodes > 0 || trimmedNodes > 0)
        {
            // «Мусор», устаревшие серверы, повторы и лишнее сверх лимита сразу убираем из кэша.
            NodeCacheWriter.Save(nodes, force: true);
        }

        var mtproto = MtProtoService.LoadCache();
        var expired = mtproto.RemoveAll(p => MtProtoRetention.IsExpired(p, DateTime.UtcNow));
        var trimmed = MtProtoRetention.TrimDead(mtproto);
        if (expired > 0 || trimmed > 0)
        {
            MtProtoService.SaveCache(mtproto);
        }

        var cacheTime = File.Exists(AppPaths.NodesPath)
            ? File.GetLastWriteTime(AppPaths.NodesPath)
            : DateTime.Now;
        return new CacheSnapshot(
            sources, nodes, mtproto, expired, trimmed, droppedNodes, duplicateNodes, trimmedNodes, cacheTime);
    }

    /// <summary>Все строки: непроверенные и рабочие.</summary>
    private IEnumerable<NodeRow> AllRows => _rows.Concat(_workingRows);

    private int NodeCount => _rows.Count + _workingRows.Count;

    /// <summary>Список заполнен: новые серверы не добавляем, пока накопленные не проверены.</summary>
    private bool AtNodeLimit => NodeCount >= Math.Max(1000, _settings.MaxTotalNodes);

    private DateTime _limitNoticeUtc;

    private void NotifyNodeLimit()
    {
        if (DateTime.UtcNow - _limitNoticeUtc < TimeSpan.FromMinutes(30))
        {
            return;
        }

        _limitNoticeUtc = DateTime.UtcNow;
        AppendLog($"Достигнут лимит {_settings.MaxTotalNodes} серверов: новые не добавляются, источники не ищутся. " +
                  "Проверьте накопленные — нерабочие удалятся, и место освободится");
    }

    private void ReplaceNodes(IEnumerable<ProxyNode> nodes)
    {
        // Повторы не должны попадать в таблицу ни из кэша, ни после проверки.
        var now = DateTime.UtcNow;
        var unique = NodeDedup.Collapse(nodes);
        var (kept, trimmed) = NodeLimits.Trim(unique, _settings.MaxTotalNodes);
        unique = kept;
        if (trimmed > 0)
        {
            AppLog.Write("nodes", $"Список ограничен {_settings.MaxTotalNodes} серверами: отброшено {trimmed}");
            AppendLog($"Список ограничен {_settings.MaxTotalNodes} серверами: отброшено {trimmed} лишних. " +
                      "Новые появятся после проверки накопленных");
        }
        _rows.Clear();
        _workingRows.Clear();
        _rowIndex.Clear();
        _rowOwner.Clear();
        foreach (var node in unique)
        {
            node.LastSeenUtc ??= now;
            var row = new NodeRow(node);
            _rowIndex[node.Id] = row;
            switch (node.Status)
            {
                case NodeStatus.Working:
                    _workingRows.Add(row);
                    _rowOwner[node.Id] = _workingRows;
                    break;
                default:
                    _rows.Add(row);
                    _rowOwner[node.Id] = _rows;
                    break;
            }
        }

        _view?.Refresh();
        _workingView?.Refresh();
        UpdateSummary();
        UpdateTrayNodes();
    }

    /// <summary>Пересобрать вкладки по текущим статусам серверов и сохранить кэш.</summary>
    private async Task RepartitionAndSaveAsync()
    {
        // Отсев пополняем здесь, а не по одному серверу в ApplyTestedNode: если партия
        // откатилась (нет прямого интернета), статусы уже восстановлены, и рабочие
        // серверы не должны оставаться в отсеве.
        foreach (var node in _droppedNodes.Where(node => node.Status == NodeStatus.Dead))
        {
            // Негативный кэш: сервер подтверждённо не работает — не проверяем его снова
            // при следующем сборе того же источника (24 ч, для рабочего — 48 ч).
            NodeNegativeCache.RecordDead(node);
            ArchiveRejectedNode(node);
        }

        NodeNegativeCache.Flush();

        // Удалённые (10 провалов) серверы в кэш не попадают: их просто выбрасываем.
        var all = AllRows.Select(r => r.Node)
            .Concat(_droppedNodes.Where(n => n.Status != NodeStatus.Dead))
            .ToList();
        _droppedNodes.Clear();
        ReplaceNodes(all);

        // Сервер мог вернуться из источника и снова стать рабочим: не держим его
        // одновременно в рабочем списке и в отсеве.
        DropWorkingFromRejected();

        // Список в таблице уже без повторов — именно его и сохраняем.
        var unique = AllRows.Select(r => r.Node).ToList();
        // Кэш на 20 МБ пишется в фоне: после каждой партии проверки UI не должен ждать диск.
        await Task.Run(() => NodeCacheWriter.Save(unique));
    }

    /// <summary>
    /// Сводка вместо списка серверов: сколько всего, рабочих и с доступом к сервисам.
    /// Сам список скрыт, сортировка и выбор сервера идут внутри.
    /// </summary>
    private void UpdateNodesSummary()
    {
        if (NodesSummaryText is null)
        {
            return;
        }

        var now = DateTime.UtcNow;
        var recheckMinutes = NodeLimits.WorkingRecheckMinutes;
        var onlyMine = _settings.ConnectOnlyMine;
        var total = 0;
        var working = 0;
        var countable = 0;
        var fresh = 0;
        var fastFresh = 0;
        var fastCountable = 0;
        var fastUniversal = 0;
        var fastYoutube = 0;
        var fastTelegram = 0;
        var fastDiscord = 0;
        var universal = 0;
        var youtube = 0;
        var telegram = 0;
        var discord = 0;
        var bestDomains = 0;

        // Один проход без копии списка: сводка обновляется каждые 250 мс во время
        // проверки 200+ серверов, и аллокация List на 12 000 строк была лишней.
        foreach (var row in AllRows)
        {
            var node = row.Node;
            total++;
            if (node.Status == NodeStatus.Working)
            {
                working++;
            }

            // Значки сервисов и щит считают по одному мягкому правилу
            // NodeAvailability.IsCountable: последняя удачная проверка не стирается
            // по таймеру 5 минут (партия перепроверки занимает минуты, и значки иначе
            // обнулялись бы на ровном месте), а уменьшают их подтверждённый провал
            // и давность больше часа. Подключение предпочитает свежие, а если их нет —
            // проверяет этих же серверов скрытым ядром на месте.
            if (!NodeAvailability.IsCountable(node, onlyMine, now))
            {
                continue;
            }

            countable++;
            var fast = NodeSpeedPolicy.IsFast(node);
            if (fast)
            {
                fastCountable++;
            }

            // «Свежие» считаем среди доступных же серверов: иначе строка «доступных 52,
            // свежих 60» выглядела как ошибка (в свежие попадали медленные и ждущие
            // повтора после провала).
            if (NodeAvailability.IsFresh(node, recheckMinutes, now))
            {
                fresh++;
                if (fast)
                {
                    fastFresh++;
                }
            }

            if (node.OpenDomains > bestDomains)
            {
                bestDomains = node.OpenDomains;
            }

            if (NodeAvailability.OpenedFor(node, "universal"))
            {
                universal++;
                if (fast)
                {
                    fastUniversal++;
                }
            }

            if (NodeAvailability.OpenedFor(node, "youtube"))
            {
                youtube++;
                if (fast)
                {
                    fastYoutube++;
                }
            }

            if (NodeAvailability.OpenedFor(node, "telegram"))
            {
                telegram++;
                if (fast)
                {
                    fastTelegram++;
                }
            }

            if (NodeAvailability.OpenedFor(node, "discord"))
            {
                discord++;
                if (fast)
                {
                    fastDiscord++;
                }
            }
        }

        // Медленные серверы (задержка выше порога) в значки не берём, пока быстрых
        // хватает квоты: иначе значок обещает сервер, с которым всё открывается
        // за 3–5 секунд. Сами серверы остаются в списке и участвуют в подключении.
        if (fastCountable >= NodeLimits.MinWorkingNodes)
        {
            countable = fastCountable;
            fresh = fastFresh;
            universal = fastUniversal;
            youtube = fastYoutube;
            telegram = fastTelegram;
            discord = fastDiscord;
        }

        UniversalNodeCount = universal;
        YoutubeNodeCount = youtube;
        TelegramNodeCount = telegram;
        DiscordNodeCount = discord;

        NodesSummaryText.Text = $"В списке: {total} серверов · рабочих: {working} · " +
                                $"доступных: {countable} · из них свежих: {fresh} " +
                                $"(проверены за {recheckMinutes} мин)";
        if (NodesAccessText is not null)
        {
            NodesAccessText.Text = $"Доступ: универс. {universal}, YouTube {youtube}, " +
                                   $"Telegram {telegram}, Discord {discord} · " +
                                   $"лучший открывает доменов {bestDomains}";
        }

        // Постоянная строка в шапке: её не перетирают ход проверки и сообщения операций.
        // Щит и числа под иконками сервисов — рабочие серверы с последней удачной проверкой.
        if (HeaderWorkingText is not null)
        {
            HeaderWorkingText.Text = countable.ToString();
        }

        if (HeaderNodesText is not null)
        {
            // «из N в списке» рядом со щитом: щит показывает доступные, а не все записи.
            HeaderNodesText.Text = $"из {total} в списке";
        }

        if (HeaderYoutubeText is not null)
        {
            HeaderYoutubeText.Text = YoutubeNodeCount.ToString();
        }

        if (HeaderDiscordText is not null)
        {
            HeaderDiscordText.Text = DiscordNodeCount.ToString();
        }

        if (HeaderTelegramText is not null)
        {
            HeaderTelegramText.Text = TelegramNodeCount.ToString();
        }

        if (HeaderUniversalText is not null)
        {
            HeaderUniversalText.Text = UniversalNodeCount.ToString();
        }
    }

    private void UpdateSummary()
    {
        // Количество серверов показываем прямо в заголовках подвкладок.
        // IsEmpty дешевле полного перебора 30 000 строк с фильтром при каждой проверке.
        var hasVisible = _view is null ? _rows.Count > 0 : !_view.IsEmpty;
        NodesUntestedTab.Header = $"Непроверенные ({_rows.Count})";
        NodesWorkingTab.Header = $"Рабочие ({_workingRows.Count})";
        UpdateMaskedCount();
        UpdateCacheSummary();
        UpdateNodesSummary();

        if (WorkingEmpty is not null)
        {
            WorkingEmpty.Visibility = _workingRows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        if (EmptyHint is null)
        {
            return;
        }

        EmptyHint.Visibility = hasVisible ? Visibility.Collapsed : Visibility.Visible;
        if (_rows.Count == 0)
        {
            if (_workingRows.Count > 0)
            {
                EmptyTitle.Text = "Непроверенных серверов нет";
                EmptyText.Text = "Проверенные серверы — на подвкладке «Рабочие». Обновите списки, чтобы добавить новые.";
            }
            else
            {
                EmptyTitle.Text = "Список пуст";
                EmptyText.Text = "Нажмите «Обновить списки» — приложение соберёт серверы из открытых источников";
            }
        }
        else
        {
            EmptyTitle.Text = "Ничего не найдено";
            EmptyText.Text = "Измените запрос или снимите фильтр «Только мои»";
        }
    }

}
