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
    // ---------- подключение ----------

    private async void OnConnectSelectedClick(object sender, RoutedEventArgs e)
    {
        ApplyRoutingSettingsFromUi();
        SaveSettings();

        if (_settings.TunnelMode == TunnelMode.Tun && !IsAdministrator())
        {
            AskElevation();
            return;
        }

        var node = SelectedNode() ?? PickBestNode();
        if (node is null)
        {
            var text = _settings.ConnectOnlyMine
                ? "Нет проверенных узлов в ваших подписках.\n\nДобавьте свой сервер на вкладке «Источники», проверьте его — и подключитесь снова."
                : "Нет проверенных узлов для подключения.\n\nОбновите списки, откройте «Узлы → Непроверенные» и нажмите «Все узлы». К непроверенным серверам приложение подключаться не будет.";
            MessageBox.Show(this, text, "VieNLink", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        await ConnectAsync(node);
    }

    private async void OnDisconnectClick(object sender, RoutedEventArgs e)
    {
        await DisconnectEverythingAsync();
    }

    /// <summary>Кнопка шапки «Отключить»: снимает узел и все включённые обходы.</summary>
    private async Task DisconnectEverythingAsync()
    {
        if (!BeginOperation("отключение"))
        {
            return;
        }

        _failedForSession.Clear();
        var stopped = new List<string>();
        try
        {
            if (_tunBypass.IsRunning || _tunBypassStartedDesync)
            {
                await StopTunBypassAsync();
                stopped.Add("TUN-обход");
            }

            if (_zapret.IsRunning)
            {
                _zapret.Stop();
                UpdateZapretUi();
                stopped.Add("обход DPI (zapret)");
            }

            if (_desync.IsRunning)
            {
                _desync.Stop();
                UpdateDesyncUi();
                stopped.Add("свой обход DPI");
            }

            if (_manager.IsConnected || _manager.IsKillSwitchHeld)
            {
                SetStatus("Отключаюсь", "Возвращаю системный прокси…");
                await _manager.DisconnectAsync();
                stopped.Add("прокси-узел");
            }

            SetStatus("Отключено", stopped.Count == 0
                ? "Активных подключений не было"
                : "Остановлено: " + string.Join(", ", stopped));
            FooterText.Text = "Отключено";
        }
        catch (Exception exception)
        {
            ReportError("Ошибка при отключении", exception);
        }
        finally
        {
            EndOperation();
            UpdateConnectionUi();
        }
    }

    /// <summary>
    /// Быстрое подключение к сервису: берём уже проверенный узел с нужным доступом.
    /// Сортировка скрыта — сначала больше открытых заблокированных доменов, затем быстрее.
    /// </summary>
    private async void OnQuickServiceClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string service })
        {
            return;
        }

        // Любая кнопка быстрого подключения возвращает со страницы Telegram.
        if (TelegramTab.IsChecked == true)
        {
            TelegramTab.IsChecked = false;
        }

        ApplyRoutingSettingsFromUi();
        SaveSettings();

        if (_settings.TunnelMode == TunnelMode.Tun && !IsAdministrator())
        {
            AskElevation();
            return;
        }

        var node = PickNodeForService(service);
        if (node is null)
        {
            var name = service switch
            {
                "youtube" => "YouTube",
                "telegram" => "Telegram",
                "discord" => "Discord",
                _ => "универсальным доступом",
            };
            MessageBox.Show(this,
                $"Нет проверенных узлов с доступом: {name}.\n\n" +
                "Обновите списки и нажмите «Все узлы» на вкладке «Узлы».",
                "VieNLink", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        await ConnectAsync(node);
    }

    private void OnTelegramTabChecked(object sender, RoutedEventArgs e)
    {
        if (TelegramPage is null || QuickConnectPage is null)
        {
            return;
        }

        TelegramPage.Visibility = Visibility.Visible;
        QuickConnectPage.Visibility = Visibility.Collapsed;
    }

    private void OnTelegramTabUnchecked(object sender, RoutedEventArgs e)
    {
        if (TelegramPage is null || QuickConnectPage is null)
        {
            return;
        }

        TelegramPage.Visibility = Visibility.Collapsed;
        QuickConnectPage.Visibility = Visibility.Visible;
    }

    /// <summary>Узел для сервиса: только проверенные, по числу открытых доменов и задержке.</summary>
    private ProxyNode? PickNodeForService(string service)
    {
        var pool = _workingRows
            .Select(row => row.Node)
            .Where(node => node.DelayMs is not null &&
                           (!_settings.ConnectOnlyMine || node.IsMine));

        Func<ProxyNode, bool> opened = service switch
        {
            "youtube" => node => node.YoutubeOk == true,
            "telegram" => node => node.TelegramOk == true,
            "discord" => node => node.DiscordOk == true,
            _ => node => node.UniversalOk == true || node.DelayMs is not null,
        };

        return pool
            .Where(opened)
            .OrderByDescending(node => node.OpenDomains)
            .ThenBy(node => node.DelayMs)
            .ThenByDescending(node => node.IsMine)
            .FirstOrDefault();
    }

    private async Task ConnectAsync(ProxyNode node, bool automatic = false, IReadOnlyList<string>? probeUrls = null)
    {
        if (_zapret.IsRunning || _desync.IsRunning || _tunBypass.IsRunning)
        {
            if (!automatic)
            {
                var mode = _tunBypass.IsRunning
                    ? "TUN-обход без прокси-узла"
                    : _desync.IsRunning
                        ? "свой обход DPI (WinDivert)"
                        : "обход DPI (zapret)";
                var answer = MessageBox.Show(
                    this,
                    $"Сейчас включён {mode}.\n\n" +
                    "Подключение к узлу остановит обход. Продолжить?",
                    "VieNLink",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Question);
                if (answer != MessageBoxResult.Yes)
                {
                    return;
                }
            }

            if (_zapret.IsRunning)
            {
                _zapret.Stop();
                AppendLog("Обход DPI остановлен: подключаемся через узел");
                UpdateZapretUi();
            }

            if (_desync.IsRunning)
            {
                _desync.Stop();
                _tunBypassStartedDesync = false;
                AppendLog("Свой обход DPI остановлен: подключаемся через узел");
                UpdateDesyncUi();
            }

            if (_tunBypass.IsRunning)
            {
                await _tunBypass.StopAsync();
                AppendLog("TUN-обход остановлен: подключаемся через узел");
                UpdateTunBypassUi();
            }
        }

        if (_settings.ConnectOnlyVerified && node.DelayMs is null)
        {
            SetStatus("Узел не проверен", "Сначала проверьте узлы: «Все узлы» на вкладке «Узлы»");
            return;
        }

        if (_settings.ConnectOnlyMine && !node.IsMine)
        {
            SetStatus("Публичные узлы отключены", "Включено «Подключаться только к моим подпискам»");
            return;
        }

        if (!BeginOperation("подключение"))
        {
            return;
        }

        if (!automatic)
        {
            _failedForSession.Clear();
        }

        try
        {
            // Задержка ещё не гарантирует доступ к сайту: пробуем узел, затем следующие по скорости.
            // Адрес проверки зависит от режима: Telegram — несколько его доменов, видео — YouTube.
            IReadOnlyList<string> targetUrls = probeUrls ?? new[] { _settings.TestUrl };
            var targetLabel = targetUrls.Count == 1
                ? targetUrls[0]
                : targetUrls[0] + $" и ещё {targetUrls.Count - 1}";
            var telegramNeeded = _sources.Any(s => s.Enabled && s.Kind == SourceKind.Telegram);
            var firstFamily = NodeRanking.Family(node);
            var candidates = new List<ProxyNode> { node };
            candidates.AddRange(_workingRows
                .Select(r => r.Node)
                .Where(n => n.DelayMs is not null && n.Id != node.Id &&
                            !(automatic && _failedForSession.Contains(n.Id)))
                .OrderBy(n => NodeRanking.Family(n) == firstFamily ? 1 : 0)
                .ThenByDescending(n => telegramNeeded && n.TelegramOk == true)
                .ThenByDescending(n => n.OpenDomains)
                .ThenByDescending(n => n.IsMine)
                .ThenByDescending(NodeRanking.Score)
                .ThenBy(n => n.DelayMs)
                .Take(3));

            Exception? lastError = null;
            foreach (var candidate in candidates)
            {
                _operation!.Token.ThrowIfCancellationRequested();
                _headerConnectingNode = candidate;
                UpdateHeaderConnectionInfo();
                SetStatus("Подключаюсь", candidate.Name + " · " + candidate.Protocol);
                var progress = new Progress<string>(line => StatusDetail.Text = line);

                try
                {
                    await _manager.ConnectAsync(candidate, progress, _operation.Token, targetUrls);
                    StatusDetail.Text = "Проверяю, открывается ли " + targetLabel;
                    if (await _manager.VerifyAnyThroughProxyAsync(_operation.Token, targetUrls))
                    {
                        FooterText.Text = $"Прокси 127.0.0.1:{_manager.ActiveMixedPort} — сайт открылся через «{candidate.Name}»";
                        var backup = PickBackupNode(candidate);
                        if (backup is not null)
                        {
                            FooterText.Text += $" · запасной камуфляж: {NodeRanking.CamouflageLabel(backup)} ({backup.DelayMs} мс)";
                            AppendLog($"Запасной узел: {backup.Name} — {NodeRanking.CamouflageLabel(backup)}"); 
                        }
                        _tray?.ShowBalloon("Прокси включён", candidate.Name + Environment.NewLine + "127.0.0.1:" + _manager.ActiveMixedPort);
                        _headerConnectingNode = null;
                        UpdateConnectionUi();
                        _ = RefreshExitInfoAsync();

                        // t.me напрямую недоступен, но теперь канал можно прочитать через прокси.
                        if (_sources.Any(s => s.Enabled && s.Kind == SourceKind.Telegram))
                        {
                            try
                            {
                                StatusDetail.Text = "Читаю каналы Telegram через прокси…";
                                var found = await CollectMtProtoCoreAsync(_operation!.Token);
                                AppendLog($"После подключения собрано MTProto-ссылок: {found}");
                                SetStatus("Подключено: " + candidate.Name, $"MTProto в списке: {_mtprotoRows.Count}");
                            }
                            catch (Exception exception) when (exception is not OperationCanceledException)
                            {
                                AppLog.Write("mtproto", "Не удалось прочитать каналы после подключения", exception);
                            }
                        }

                        return;
                    }

                    AppLog.Write("proxy", $"Узел «{candidate.Name}» не открыл проверочный адрес — пробую следующий");
                    DropFailedNode(candidate, "не открыл " + targetLabel);
                    lastError = new InvalidOperationException("Узел не открыл " + targetLabel);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    lastError = exception;
                    AppLog.Write("proxy", $"Узел «{candidate.Name}» не подключился", exception);
                }
            }

            await _manager.DisconnectAsync();
            throw new InvalidOperationException(
                "Ни один узел не открыл проверочный адрес. Обновите списки и проверьте узлы ещё раз. " + lastError?.Message,
                lastError);
        }
        catch (OperationCanceledException)
        {
            SetStatus("Подключение отменено", string.Empty);
        }
        catch (Exception exception)
        {
            if (automatic)
            {
                AppLog.Write("proxy", "Автопереключение не удалось", exception);
                SetStatus("Не удалось переключиться", "Проверьте узлы и попробуйте снова");
            }
            else
            {
                ReportError("Не удалось подключиться", exception);
            }
        }
        finally
        {
            _headerConnectingNode = null;
            EndOperation();
            UpdateConnectionUi();
        }
    }

    private async Task DisconnectAsync()
    {
        if (!BeginOperation("отключение"))
        {
            return;
        }

        _failedForSession.Clear();

        try
        {
            SetStatus("Отключаюсь", "Возвращаю системный прокси…");
            await _manager.DisconnectAsync();
            StatusDetail.Text = "Прокси отключён, настройки Windows восстановлены";
            FooterText.Text = "Отключено";
        }
        catch (Exception exception)
        {
            ReportError("Ошибка при отключении", exception);
        }
        finally
        {
            EndOperation();
            UpdateConnectionUi();
        }
    }

    private ProxyNode? SelectedNode()
    {
        // Кнопки в шапке работают с той подвкладкой, которая открыта сейчас.
        if (ReferenceEquals(MainTabs.SelectedItem, ToolsTab) &&
            ReferenceEquals(ToolsTabs.SelectedItem, NodesTab))
        {
            if (ReferenceEquals(NodesTabs.SelectedItem, NodesWorkingTab))
            {
                return (WorkingGrid.SelectedItem as NodeRow)?.Node;
            }

            return (NodesGrid.SelectedItem as NodeRow)?.Node;
        }

        return (NodesGrid.SelectedItem as NodeRow)?.Node;
    }

    private ProxyNode? PickBestNode()
    {
        // Только проверенные узлы: никаких попыток подключения «наугад» к мёртвым серверам.
        var telegramNeeded = _sources.Any(s => s.Enabled && s.Kind == SourceKind.Telegram);
        var pool = _workingRows
            .Select(r => r.Node)
            .Where(n => n.DelayMs is not null && (!_settings.ConnectOnlyMine || n.IsMine))
            .OrderByDescending(n => telegramNeeded && n.TelegramOk == true)
            .ThenByDescending(n => n.IsMine)
            .ThenByDescending(n => n.OpenDomains)
            .ThenByDescending(NodeRanking.Score)
            .ThenBy(n => n.DelayMs)
            .ToList();

        if (pool.Count == 0 && !_settings.ConnectOnlyVerified)
        {
            // Пользователь явно разрешил непроверенные — берём из общего списка.
            pool = _rows
                .Select(r => r.Node)
                .Where(n => !_settings.ConnectOnlyMine || n.IsMine)
                .OrderByDescending(n => n.IsMine)
                .ThenByDescending(NodeRanking.Score)
                .ThenBy(n => n.DelayMs ?? int.MaxValue)
                .ToList();
        }

        var quick = pool.FirstOrDefault(n => n.DelayMs <= _settings.MaxDelayMs);
        return quick ?? pool.FirstOrDefault();
    }

    private async void OnNodesGridDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (_manager.IsConnected || SelectedNode() is not { } node)
        {
            return;
        }

        await ConnectAsync(node);
    }

    /// <summary>Узел перестал отвечать: молча переключаемся на следующий рабочий.</summary>
    private void OnNodeUnhealthy(string nodeName)
    {
        if (!_settings.AutoFailover)
        {
            AppendLog("Узел перестал отвечать, автопереключение выключено");
            SetStatus("Узел перестал отвечать", "Автопереключение выключено — нажмите «Отключиться» и выберите другой узел");
            return;
        }

        Dispatcher.BeginInvoke(new Action(() =>
        {
            // Проверка узлов или другая операция: событие не теряем, вернёмся после неё.
            if (_busy)
            {
                _pendingFailoverName = nodeName;
                AppendLog("Сбой узла замечен во время операции — переключусь после её окончания");
                return;
            }

            _ = HandleUnhealthyNodeAsync(nodeName);
        }));
    }

    private async Task HandleUnhealthyNodeAsync(string nodeName)
    {
        if (_busy || _closing || !_manager.IsConnected || _manager.CurrentNode is not { } current)
        {
            return;
        }

        var stopwatch = Stopwatch.StartNew();
        _failedForSession.Add(current.Id);
        DropFailedNode(current, "перестал открывать проверочный адрес");
        AppendLog($"Узел «{nodeName}» перестал отвечать — ищу замену");

        var failedFamily = NodeRanking.Family(current);
        var next = PickFailoverNode(failedFamily);

        if (next is null)
        {
            SetStatus("Узел перестал отвечать", "Рабочих узлов для замены нет — обновите списки и проверьте узлы");
            await DisconnectAsync();
            return;
        }

        // Если запасной узел уже в конфиге — переключаемся без перезапуска ядра.
        if (_manager.IsNodeLoaded(next) && BeginOperation("переключение узла"))
        {
            try
            {
                SetStatus("Переключаюсь", $"«{next.Name}» вместо «{nodeName}»");
                var progress = new Progress<string>(line => StatusDetail.Text = line);
                if (await _manager.SwitchToAsync(next, progress, _operation!.Token))
                {
                    StatusDetail.Text = "Проверяю новый узел…";
                    if (await _manager.VerifyThroughProxyAsync(_operation.Token))
                    {
                        _failoverCount++;
                        _warmSwitchCount++;
                        _lastFailoverUtc = DateTime.UtcNow;
                        _lastFailoverNote =
                            $"тёплое: {NodeRanking.CamouflageLabel(current)} → {NodeRanking.CamouflageLabel(next)}, {stopwatch.ElapsedMilliseconds} мс";
                        FooterText.Text = $"Прокси 127.0.0.1:{_manager.ActiveMixedPort} — узел заменён на «{next.Name}»";
                        _tray?.ShowBalloon("Узел заменён", next.Name + Environment.NewLine + "127.0.0.1:" + _manager.ActiveMixedPort);
                        _ = RefreshExitInfoAsync();
                        return;
                    }
                }
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                AppLog.Write("proxy", "Тёплое переключение не удалось", exception);
            }
            finally
            {
                EndOperation();
                UpdateConnectionUi();
            }

            _failedForSession.Add(next.Id);
            next = PickFailoverNode(null);
            if (next is null)
            {
                SetStatus("Узел перестал отвечать", "Рабочих узлов для замены нет — обновите списки и проверьте узлы");
                await DisconnectAsync();
                return;
            }
        }

        SetStatus("Переключаюсь", $"«{next.Name}» вместо «{nodeName}»");
        await ConnectAsync(next, automatic: true);
        if (_manager.IsConnected)
        {
            _failoverCount++;
            _coldSwitchCount++;
            _lastFailoverUtc = DateTime.UtcNow;
            _lastFailoverNote =
                $"полное: {NodeRanking.CamouflageLabel(current)} → {NodeRanking.CamouflageLabel(next)}, {stopwatch.ElapsedMilliseconds} мс";
            UpdateDiagnostics();
        }
    }

    /// <summary>
    /// Узел перестал открывать проверочный адрес: счётчик провалов растёт, из списка
    /// узел убирается только после <see cref="NodeLimits.RemoveAfterFailures"/> провалов подряд —
    /// разовый сбой живого узла не должен стирать его из списка.
    /// </summary>
    private void DropFailedNode(ProxyNode node, string reason)
    {
        var removed = NodeHealth.RegisterFailure(node);
        ApplyTestedNode(node);
        // Кэш пишется в фоне: список может быть десятки тысяч узлов, не тормозим UI.
        var snapshot = AllRows.Select(r => r.Node).ToList();
        _ = Task.Run(() => NodeSourceService.SaveCache(snapshot));
        AppendLog(removed
            ? $"Узел удалён после {node.FailCount} провалов — {reason}"
            : $"Узел не отвечает ({node.FailCount} из {NodeLimits.RemoveAfterFailures}) — {reason}");
    }

    /// <summary>Следующий рабочий узел, по возможности из другого семейства камуфляжа.</summary>
    private ProxyNode? PickFailoverNode(string? avoidFamily)    {
        var telegramNeeded = _sources.Any(s => s.Enabled && s.Kind == SourceKind.Telegram);
        return _workingRows
            .Select(r => r.Node)
            .Where(n => n.DelayMs is not null && !_failedForSession.Contains(n.Id))
            .OrderBy(n => avoidFamily is not null && NodeRanking.Family(n) == avoidFamily ? 1 : 0)
            .ThenByDescending(n => telegramNeeded && n.TelegramOk == true)
            .ThenByDescending(n => n.OpenDomains)
            .ThenByDescending(n => n.IsMine)
            .ThenByDescending(NodeRanking.Score)
            .ThenBy(n => n.DelayMs)
            .FirstOrDefault();
    }

    /// <summary>Запасной узел другого камуфляжного семейства — показываем его после подключения.</summary>
    private ProxyNode? PickBackupNode(ProxyNode current) =>
        _workingRows
            .Select(r => r.Node)
            .Where(n => n.DelayMs is not null && n.Id != current.Id &&
                        NodeRanking.Family(n) != NodeRanking.Family(current))
            .OrderByDescending(n => n.IsMine)
            .ThenByDescending(n => n.OpenDomains)
            .ThenByDescending(NodeRanking.Score)
            .ThenBy(n => n.DelayMs)
            .FirstOrDefault();

    private void OnManagerStateChanged(string message)
    {
        Dispatcher.BeginInvoke(() =>
        {
            UpdateConnectionUi();
            if (!_manager.IsConnected && message.Contains("остановилось", StringComparison.Ordinal))
            {
                _tray?.ShowBalloon("Прокси отключён", message, warning: true);
            }
        });
    }

    private void UpdateConnectionUi()
    {
        var connected = _manager.IsConnected;
        var held = _manager.IsKillSwitchHeld;
        var delay = _manager.CurrentNode?.DelayMs;
        StatusText.Text = connected
            ? "Подключено · " + (delay is { } ms ? ms + " мс" : "проверенный узел")
            : held
                ? "Трафик заблокирован (kill switch)"
                : "Отключено";
        StatusDot.Fill = (Brush)FindResource(connected ? "AccentBrush" : held ? "DangerBrush" : "MutedBrush");
        _tray?.UpdateStatus(connected, null, held);
        Title = connected
            ? AppInfo.DisplayName + " — подключено"
            : AppInfo.DisplayName + " — свободные узлы";
        if (!connected)
        {
            _lastTrafficSample = null;
        }

        UpdateHeaderState();
        UpdateHeaderConnectionInfo();
        UpdateTrayNodes();
        UpdateDiagnostics();
    }

    /// <summary>Показывает, что именно делает наша логика: активный камуфляж, запасной и статистику переключений.</summary>
    private void UpdateDiagnostics()
    {
        if (DiagnosticsText is null)
        {
            return;
        }

        if (!_manager.IsConnected || _manager.CurrentNode is not { } node)
        {
            DiagnosticsText.Text = "Не подключено. Подключитесь, чтобы увидеть активный и запасной камуфляж.";
            return;
        }

        var backup = PickBackupNode(node);
        var last = _lastFailoverUtc is null
            ? "Последнее переключение: —"
            : $"Последнее: {_lastFailoverUtc.Value.ToLocalTime():HH:mm:ss} — {_lastFailoverNote}";

        DiagnosticsText.Text = string.Join(Environment.NewLine,
            $"Активный узел: проверенный" + (node.DelayMs is { } delay ? $" · {delay} мс" : string.Empty),
            $"Маскировка: {NodeRanking.Label(node)} · камуфляж: {NodeRanking.CamouflageLabel(node)}",
            backup is null
                ? "Запасной: нет узла другого камуфляжа"
                : $"Запасной: другой камуфляж ({NodeRanking.CamouflageLabel(backup)}, {backup.DelayMs} мс)",
            $"Проверенных рабочих: {_workingRows.Count(r => r.Node.DelayMs is not null)} · " +
            $"моих: {_workingRows.Count(r => r.Node.IsMine && r.Node.DelayMs is not null)}",
            $"Переключений: {_failoverCount} · тёплых: {_warmSwitchCount} · полных: {_coldSwitchCount}",
            last);
    }

    /// <summary>Сводка кэша и лимитов — чтобы было видно, что и когда удаляется.</summary>
    private void UpdateCacheSummary()
    {
        if (CacheSummaryText is null)
        {
            return;
        }

        var nodes = _rows.Count + _workingRows.Count;
        var mtproto = _mtprotoRows.Count + _mtprotoDeadRows.Count;
        CacheSummaryText.Text =
            $"Кэш: {nodes} узлов · MTProto {mtproto} (нерабочих {_mtprotoDeadRows.Count}). " +
            $"Непроверенные узлы удаляются при первом провале, уже рабочие — после {NodeLimits.RemoveAfterFailures} провалов подряд; " +
            $"нерабочие MTProto: лимит {MtProtoRetention.MaxDead}, старше {MtProtoRetention.DeadHours} ч удаляются автоматически.";
    }

    /// <summary>Ручная очистка: убирает нерабочие MTProto, узлы остаются.</summary>
    private void OnClearCacheClick(object sender, RoutedEventArgs e)
    {
        var dead = _mtprotoDeadRows.Count;
        if (dead == 0)
        {
            MessageBox.Show(this, "Лишнего в кэше нет: нерабочие MTProto уже пусты.",
                "VieNLink", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var answer = MessageBox.Show(
            this,
            $"Удалить из кэша:\n• нерабочих MTProto: {dead}\n\n" +
            "Узлы и рабочие MTProto останутся.",
            "VieNLink",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);
        if (answer != MessageBoxResult.Yes)
        {
            return;
        }

        var keepMtProto = _mtprotoRows.Select(r => r.Proxy).ToList();
        RebuildMtProtoRows(keepMtProto);
        MtProtoService.SaveCache(keepMtProto);

        AppendLog($"Кэш очищен: удалено нерабочих MTProto {dead}");
        SetStatus("Кэш очищен", $"Удалено нерабочих MTProto: {dead}");
        UpdateCacheSummary();
    }

    /// <summary>Ведёт на вкладку «Источники» для добавления своего сервера/подписки.</summary>
    private void OnAddPersonalServerClick(object sender, RoutedEventArgs e)
    {
        MainTabs.SelectedItem = ToolsTab;
        ToolsTabs.SelectedItem = SourcesTab;
        NewSourceUrl.Focus();
        FooterText.Text = "Вставьте ссылку на свой сервер (vless://, trojan://…) или личную подписку";
    }

}
