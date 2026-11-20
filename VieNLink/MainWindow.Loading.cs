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
            AutoDiscoverCheck.IsChecked = _settings.AutoDiscoverSources;
            AutoDiscoverHoursBox.Text = _settings.AutoDiscoverHours.ToString();
            ApplyProxyCheck.IsChecked = _settings.ApplySystemProxy;
            AutoBestCheck.IsChecked = _settings.AutoSelectBest;
            AutoFailoverCheck.IsChecked = _settings.AutoFailover;
            OnlyVerifiedConnectCheck.IsChecked = _settings.ConnectOnlyVerified;
            OnlyMineConnectCheck.IsChecked = _settings.ConnectOnlyMine;
            ZapretAutoStartCheck.IsChecked = _settings.ZapretAutoStart;
            ZapretGameModeCheck.IsChecked = _settings.ZapretGameMode;
            DesyncAutoStartCheck.IsChecked = _settings.DesyncAutoStart;
            DesyncBlockQuicCheck.IsChecked = _settings.DesyncBlockQuic;
            SelectDesyncStrategyRadio(_settings.DesyncStrategy);
            TunBypassBlockQuicCheck.IsChecked = _settings.TunBypassBlockQuic;
            TunBypassAutoStartCheck.IsChecked = _settings.TunBypassAutoStart;
            SelectTunBypassStrategyRadio(_settings.TunBypassStrategy);
            SelectZapretPresetRadio(_settings.ZapretPreset);
        TestAfterRefreshCheck.IsChecked = _settings.TestAfterRefresh;
            IdleTestCheck.IsChecked = _settings.IdleTestEnabled;
            IdleMinutesBox.Text = _settings.IdleTestMinutes.ToString();
            IdleBatchBox.Text = _settings.IdleTestBatch.ToString();
            IdleIntervalBox.Text = _settings.IdleTestIntervalMinutes.ToString();
            IdleRecheckBox.Text = _settings.IdleRecheckHours.ToString();
            MinimizeTrayCheck.IsChecked = _settings.MinimizeToTray;
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
            OnlyAppsCheck.IsChecked = _settings.OnlySelectedApps;
            AppListBox.Text = _settings.AppList;
        }
        finally
        {
            _loadingSettings = false;
        }

        UpdateRoutingAvailability();
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
        AppListBox.ScrollToEnd();
        OnlyAppsCheck.IsChecked = true;
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
        _settings.OnlySelectedApps = OnlyAppsCheck.IsChecked == true;
        _settings.AppList = AppListBox.Text;
    }

    /// <summary>
    /// Чтение источников и кэша узлов. Разбор десятков мегабайт JSON и ссылок идёт
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

        ReplaceNodes(snapshot.Nodes);

        if (snapshot.DroppedNodes > 0)
        {
            AppendLog($"Из кэша удалено {snapshot.DroppedNodes} нерабочих/старых узлов — они не хранятся");
        }

        if (snapshot.DuplicateNodes > 0)
        {
            AppendLog($"Склеено повторов: {snapshot.DuplicateNodes} — одинаковые узлы оставлены одной строкой");
        }

        if (snapshot.TrimmedNodes > 0)
        {
            AppendLog($"Список ограничен {_settings.MaxTotalNodes} узлами: из кэша убрано {snapshot.TrimmedNodes} лишних. " +
                      "Новые появятся после проверки накопленных");
        }

        if (snapshot.ExpiredMtProto > 0)
        {
            AppendLog($"Из «Нерабочих» MTProto удалено {snapshot.ExpiredMtProto} прокси старше 14 дней");
        }

        if (snapshot.TrimmedMtProto > 0)
        {
            AppendLog($"Из «Нерабочих» MTProto удалено {snapshot.TrimmedMtProto} самых старых (лимит {MtProtoRetention.MaxDead})");
        }

        RebuildMtProtoRows(snapshot.MtProto);

        if (snapshot.Nodes.Count > 0)
        {
            var working = snapshot.Nodes.Count(n => n.DelayMs is not null);
            StatusDetail.Text = $"Из кэша: {snapshot.Nodes.Count} узлов, рабочих {working}";
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
        var nodes = NodeSourceService.LoadCache(out var droppedNodes, out var duplicateNodes, freshAfter);

        // Кэш мог вырасти до десятков тысяч: жёстко ограничиваем список и оставляем
        // личные и проверенные узлы, а не первые попавшиеся.
        var (keptNodes, trimmedNodes) = NodeLimits.Trim(nodes, settings.MaxTotalNodes);
        nodes = keptNodes;
        if (droppedNodes > 0 || duplicateNodes > 0 || trimmedNodes > 0)
        {
            // «Мусор», устаревшие узлы, повторы и лишнее сверх лимита сразу убираем из кэша.
            NodeSourceService.SaveCache(nodes);
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

    /// <summary>Список заполнен: новые узлы не добавляем, пока накопленные не проверены.</summary>
    private bool AtNodeLimit => NodeCount >= Math.Max(1000, _settings.MaxTotalNodes);

    private DateTime _limitNoticeUtc;

    private void NotifyNodeLimit()
    {
        if (DateTime.UtcNow - _limitNoticeUtc < TimeSpan.FromMinutes(30))
        {
            return;
        }

        _limitNoticeUtc = DateTime.UtcNow;
        AppendLog($"Достигнут лимит {_settings.MaxTotalNodes} узлов: новые не добавляются, источники не ищутся. " +
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
            AppLog.Write("nodes", $"Список ограничен {_settings.MaxTotalNodes} узлами: отброшено {trimmed}");
            AppendLog($"Список ограничен {_settings.MaxTotalNodes} узлами: отброшено {trimmed} лишних. " +
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

    /// <summary>Пересобрать вкладки по текущим статусам узлов и сохранить кэш.</summary>
    private async Task RepartitionAndSaveAsync()
    {
        // Удалённые (10 провалов) узлы в кэш не попадают: их просто выбрасываем.
        var all = AllRows.Select(r => r.Node)
            .Concat(_droppedNodes.Where(n => n.Status != NodeStatus.Dead))
            .ToList();
        _droppedNodes.Clear();
        ReplaceNodes(all);

        // Список в таблице уже без повторов — именно его и сохраняем.
        var unique = AllRows.Select(r => r.Node).ToList();
        // Кэш на 20 МБ пишется в фоне: после каждой партии проверки UI не должен ждать диск.
        await Task.Run(() => NodeSourceService.SaveCache(unique));
    }

    private void UpdateSummary()
    {
        // Количество узлов показываем прямо в заголовках подвкладок.
        // IsEmpty дешевле полного перебора 30 000 строк с фильтром при каждой проверке.
        var hasVisible = _view is null ? _rows.Count > 0 : !_view.IsEmpty;
        NodesUntestedTab.Header = $"Непроверенные ({_rows.Count})";
        NodesWorkingTab.Header = $"Рабочие ({_workingRows.Count})";
        UpdateMaskedCount();
        UpdateCacheSummary();

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
                EmptyTitle.Text = "Непроверенных узлов нет";
                EmptyText.Text = "Проверенные узлы — на подвкладке «Рабочие». Обновите списки, чтобы добавить новые.";
            }
            else
            {
                EmptyTitle.Text = "Список пуст";
                EmptyText.Text = "Нажмите «Обновить списки» — приложение соберёт узлы из открытых источников";
            }
        }
        else
        {
            EmptyTitle.Text = "Ничего не найдено";
            EmptyText.Text = "Измените запрос или снимите фильтр «Только мои»";
        }
    }

}
