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
                ? "Нет проверенных серверов в ваших подписках.\n\nДобавьте свой сервер на вкладке «Источники», проверьте его — и подключитесь снова."
                : "Нет проверенных серверов для подключения.\n\nОбновите списки, откройте «Серверы → Непроверенные» и нажмите «Все серверы». К непроверенным серверам приложение подключаться не будет.";
            MessageBox.Show(this, text, "VieNLink", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        await ConnectAsync(node);
    }

    private async void OnDisconnectClick(object sender, RoutedEventArgs e)
    {
        await DisconnectEverythingAsync();
    }

    /// <summary>Кнопка шапки «Отключить»: снимает сервер и все включённые обходы.</summary>
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
                stopped.Add("прокси-сервер");
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
    /// Быстрое подключение к сервису: берём уже проверенный сервер с нужным доступом.
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

        // Если уже подключены через другой сервис (например, «Универсальный»),
        // нажатие «YouTube» должно именно переподключить: текущий сервер исключаем,
        // чтобы кнопка не оставила старое подключение «как есть».
        var avoidId = _activeService is { } active && active != service
            ? _manager.CurrentNode?.Id
            : null;
        var node = PickNodeForService(service, avoidId);
        if (node is null)
        {
            // Свежих серверов нет: проверяем тех, кто раньше открывал этот сервис.
            SetStatus("Ищу сервер", "Нет свежего сервера: " + ServiceDisplayName(service));
            node = await FindNodeForServiceAsync(service, avoidId);
        }

        if (node is null)
        {
            MessageBox.Show(this,
                $"Сейчас нет серверов с подтверждённым доступом: {ServiceDisplayName(service)}.\n\n" +
                "Проверенные ранее серверы не отвечают. Фоновая проверка продолжает искать замену — " +
                "попробуйте через минуту или нажмите «Все серверы» на вкладке «Серверы».",
                "VieNLink", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        // Проверяем сервер именно тем сервисом, ради которого нажата кнопка: иначе
        // «Discord» мог подключиться к серверу, открывающему только YouTube.
        await ConnectAsync(node, probeUrls: ProbeUrlsForService(service));
        if (_manager.IsConnected)
        {
            _activeService = service;
        }
    }

    /// <summary>Проверочные адреса кнопки сервиса — те же, по которым ставится отметка доступа.</summary>
    private static IReadOnlyList<string> ProbeUrlsForService(string service) => service switch
    {
        "youtube" => new[] { ProxyManager.YoutubeTestUrl },
        "telegram" => ProxyManager.TelegramTestUrls,
        "discord" => new[] { ProxyManager.DiscordTestUrl },
        _ => new[] { ProxyManager.UniversalTestUrl },
    };

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

    /// <summary>
    /// Сервер для сервиса: только свежая проверка, сначала самый быстрый (по пингу).
    /// Правило то же, что у счётчиков в шапке: число на значке и результат кнопки
    /// не должны расходиться.
    /// </summary>
    private ProxyNode? PickNodeForService(string service, string? avoidId = null)
    {
        var now = DateTime.UtcNow;
        return _workingRows
            .Select(row => row.Node)
            .Where(node => (avoidId is null || node.Id != avoidId) &&
                           NodeAvailability.IsUsable(
                node, NodeLimits.WorkingRecheckMinutes, _settings.ConnectOnlyMine, now) &&
                           NodeAvailability.OpenedFor(node, service))
            .OrderBy(node => node.DelayMs)
            .ThenByDescending(node => node.OpenDomains)
            .ThenByDescending(node => node.IsMine)
            .FirstOrDefault();
    }

    /// <summary>Название сервиса для сообщений.</summary>
    private static string ServiceDisplayName(string service) => service switch
    {
        "youtube" => "YouTube",
        "telegram" => "Telegram",
        "discord" => "Discord",
        _ => "универсальный доступ",
    };

    /// <summary>
    /// Свежих серверов для сервиса нет: проверяем скрытым ядром тех, кто раньше открывал
    /// этот сервис (данные устарели), и снова выбираем. null — живых не нашлось
    /// или пробное ядро занято фоновой проверкой.
    /// </summary>
    private async Task<ProxyNode?> FindNodeForServiceAsync(string service, string? avoidId = null)
    {
        var now = DateTime.UtcNow;
        var candidates = AllRows
            .Select(row => row.Node)
            .Where(node => node.Status != NodeStatus.Dead &&
                           (avoidId is null || node.Id != avoidId) &&
                           NodeAvailability.OpenedFor(node, service) &&
                           (!_settings.ConnectOnlyMine || node.IsMine) &&
                           !NodeAvailability.IsUsable(
                               node, NodeLimits.WorkingRecheckMinutes, _settings.ConnectOnlyMine, now))
            .OrderBy(node => node.DelayMs ?? int.MaxValue)
            .Take(Math.Clamp(_settings.IdleTestBatch, 5, 40))
            .ToList();
        if (candidates.Count == 0)
        {
            return null;
        }

        if (!BeginOperation("поиск сервера"))
        {
            return null;
        }

        try
        {
            SetStatus("Ищу сервер", $"Проверяю {candidates.Count} серверов: {ServiceDisplayName(service)}");
            var progress = new Progress<string>(line => StatusDetail.Text = line);
            var summary = await _probe.TestAsync(
                candidates,
                _settings,
                progress,
                node => Dispatcher.BeginInvoke(() => ApplyTestedNode(node)),
                _operation!.Token,
                concurrency: 4,
                timeoutMs: 3000,
                waitForGate: false);
            await RepartitionAndSaveAsync();
            if (summary.Total == 0)
            {
                return null;
            }

            AppendLog($"Поиск сервера: проверено {summary.Total}, рабочих {summary.Working}");
            return PickNodeForService(service, avoidId);
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch (Exception exception)
        {
            AppLog.Write("probe", "Поиск сервера перед подключением не удался", exception);
            return null;
        }
        finally
        {
            EndOperation();
            UpdateConnectionUi();
        }
    }

    private async Task ConnectAsync(ProxyNode node, bool automatic = false, IReadOnlyList<string>? probeUrls = null,
        bool markFailures = true)
    {
        if (!automatic && !await TryTakeOverAutoAsync())
        {
            return;
        }

        if (_zapret.IsRunning || _desync.IsRunning || _tunBypass.IsRunning)
        {
            if (!automatic)
            {
                var mode = _tunBypass.IsRunning
                    ? "TUN-обход без прокси-сервера"
                    : _desync.IsRunning
                        ? "свой обход DPI (WinDivert)"
                        : "обход DPI (zapret)";
                var answer = MessageBox.Show(
                    this,
                    $"Сейчас включён {mode}.\n\n" +
                    "Подключение к серверу остановит обход. Продолжить?",
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
                AppendLog("Обход DPI остановлен: подключаемся через сервер");
                UpdateZapretUi();
            }

            if (_desync.IsRunning)
            {
                _desync.Stop();
                _tunBypassStartedDesync = false;
                AppendLog("Свой обход DPI остановлен: подключаемся через сервер");
                UpdateDesyncUi();
            }

            if (_tunBypass.IsRunning)
            {
                await _tunBypass.StopAsync();
                AppendLog("TUN-обход остановлен: подключаемся через сервер");
                UpdateTunBypassUi();
            }
        }

        if (_settings.ConnectOnlyVerified && node.DelayMs is null)
        {
            SetStatus("Сервер не проверен", "Сначала проверьте серверы: «Все серверы» на вкладке «Серверы»");
            return;
        }

        if (_settings.ConnectOnlyMine && !node.IsMine)
        {
            SetStatus("Публичные серверы отключены", "Включено «Подключаться только к моим подпискам»");
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
            // Задержка ещё не гарантирует доступ к сайту: пробуем сервер, затем следующие по скорости.
            // Адрес проверки зависит от режима: Telegram — несколько его доменов, видео — YouTube.
            IReadOnlyList<string> targetUrls = probeUrls ?? new[] { _settings.TestUrl };
            var targetLabel = targetUrls.Count == 1
                ? targetUrls[0]
                : targetUrls[0] + $" и ещё {targetUrls.Count - 1}";
            var telegramNeeded = _sources.Any(s => s.Enabled && SourceKinds.IsProxy(s));
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
                .ThenByDescending(n => n.RankScore)
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
                        StatusDetail.Text = "Открылось: " + targetLabel;
                        FooterText.Text = $"Прокси 127.0.0.1:{_manager.ActiveMixedPort} — сайт открылся через «{candidate.Name}»";
                        var backup = PickBackupNode(candidate);
                        if (backup is not null)
                        {
                            FooterText.Text += $" · запасной камуфляж: {NodeRanking.CamouflageLabel(backup)} ({backup.DelayMs} мс)";
                            AppendLog($"Запасной сервер: {backup.Name} — {NodeRanking.CamouflageLabel(backup)}"); 
                        }
                        _tray?.ShowBalloon("Прокси включён", candidate.Name + Environment.NewLine + "127.0.0.1:" + _manager.ActiveMixedPort);
                        _headerConnectingNode = null;
                        UpdateConnectionUi();
                        _ = RefreshExitInfoAsync();

                        // t.me напрямую недоступен, но теперь канал можно прочитать через прокси.
                        if (_sources.Any(s => s.Enabled && SourceKinds.IsProxy(s)))
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

                    AppLog.Write("proxy", $"Сервер «{candidate.Name}» не открыл проверочный адрес — пробую следующий");
                    if (markFailures)
                    {
                        DropFailedNode(candidate, "не открыл " + targetLabel);
                    }
                    else
                    {
                        // Ручное переключение («Дальше») не повод удалять рабочий сервер:
                        // разовый провал проверки может быть из-за смены маршрута,
                        // а не потому, что сервер умер. Оставляем задержку и отметки,
                        // фоновый проход всё равно перепроверит его в ближайшие минуты.
                        AppendLog($"Сервер «{candidate.Name}» не открыл {targetLabel} — оставляю в списке (ручное переключение)");
                    }

                    lastError = new InvalidOperationException("Сервер не открыл " + targetLabel);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    lastError = exception;
                    AppLog.Write("proxy", $"Сервер «{candidate.Name}» не подключился", exception);
                }
            }

            await _manager.DisconnectAsync();
            throw new InvalidOperationException(
                "Ни один сервер не открыл проверочный адрес. Обновите списки и проверьте серверы ещё раз. " + lastError?.Message,
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
                SetStatus("Не удалось переключиться", "Проверьте серверы и попробуйте снова");
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
        _activeService = null;

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
        // Только проверенные серверы со свежей проверкой: «рабочий» из старого кэша
        // мог умереть, пока приложение было закрыто. Первым — самый быстрый.
        var now = DateTime.UtcNow;
        var pool = _workingRows
            .Select(r => r.Node)
            .Where(n => NodeAvailability.IsUsable(
                n, NodeLimits.WorkingRecheckMinutes, _settings.ConnectOnlyMine, now))
            .OrderBy(n => n.DelayMs ?? int.MaxValue)
            .ThenByDescending(n => n.IsMine)
            .ThenByDescending(n => n.OpenDomains)
            .ThenByDescending(n => n.RankScore)
            .ToList();

        if (pool.Count == 0 && !_settings.ConnectOnlyVerified)
        {
            // Пользователь явно разрешил непроверенные — берём из общего списка.
            pool = _rows
                .Select(r => r.Node)
                .Where(n => !_settings.ConnectOnlyMine || n.IsMine)
                .OrderByDescending(n => n.IsMine)
                .ThenByDescending(n => n.RankScore)
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

        _activeService = null;
        await ConnectAsync(node);
    }

    /// <summary>Сервер перестал отвечать: молча переключаемся на следующий рабочий.</summary>
    private void OnNodeUnhealthy(ProxyNode node)
    {
        if (!_settings.AutoFailover)
        {
            AppendLog("Сервер перестал отвечать, автопереключение выключено");
            SetStatus("Сервер перестал отвечать", "Автопереключение выключено — нажмите «Отключиться» и выберите другой сервер");
            return;
        }

        Dispatcher.BeginInvoke(new Action(() =>
        {
            // Пока шла проверка, пользователь мог нажать «Дальше» и выбрать другой сервер.
            // Событие относится к прежнему серверу — текущий не трогаем и не удаляем.
            if (_manager.CurrentNode is not { } current || current.Id != node.Id)
            {
                AppendLog("Сбой сервера пришёл после ручного переключения — пропускаю");
                return;
            }

            // Проверка серверов или другая операция: событие не теряем, вернёмся после неё.
            if (_busy)
            {
                _pendingFailoverNode = node;
                AppendLog("Сбой сервера замечен во время операции — переключусь после её окончания");
                return;
            }

            _ = HandleUnhealthyNodeAsync(node);
        }));
    }

    private async Task HandleUnhealthyNodeAsync(ProxyNode unhealthy)
    {
        if (_busy || _closing || !_manager.IsConnected || _manager.CurrentNode is not { } current)
        {
            return;
        }

        // Сервер уже сменили вручную — обрабатывать сбой нечего.
        if (current.Id != unhealthy.Id)
        {
            return;
        }

        var nodeName = unhealthy.Name;
        var stopwatch = Stopwatch.StartNew();
        _failedForSession.Add(current.Id);
        _activeService = null;
        DropFailedNode(current, "перестал открывать проверочный адрес");
        AppendLog($"Сервер «{nodeName}» перестал отвечать — ищу замену");

        var failedFamily = NodeRanking.Family(current);
        var next = PickFailoverNode(failedFamily);

        if (next is null)
        {
            SetStatus("Сервер перестал отвечать", "Рабочих серверов для замены нет — обновите списки и проверьте серверы");
            await DisconnectAsync();
            return;
        }

        // Если запасной сервер уже в конфиге — переключаемся без перезапуска ядра.
        if (_manager.IsNodeLoaded(next) && BeginOperation("переключение сервера"))
        {
            try
            {
                SetStatus("Переключаюсь", $"«{next.Name}» вместо «{nodeName}»");
                var progress = new Progress<string>(line => StatusDetail.Text = line);
                if (await _manager.SwitchToAsync(next, progress, _operation!.Token))
                {
                    StatusDetail.Text = "Проверяю новый сервер…";
                    if (await _manager.VerifyThroughProxyAsync(_operation.Token))
                    {
                        _failoverCount++;
                        _warmSwitchCount++;
                        _lastFailoverUtc = DateTime.UtcNow;
                        _lastFailoverNote =
                            $"тёплое: {NodeRanking.CamouflageLabel(current)} → {NodeRanking.CamouflageLabel(next)}, {stopwatch.ElapsedMilliseconds} мс";
                        FooterText.Text = $"Прокси 127.0.0.1:{_manager.ActiveMixedPort} — сервер заменён на «{next.Name}»";
                        _tray?.ShowBalloon("Сервер заменён", next.Name + Environment.NewLine + "127.0.0.1:" + _manager.ActiveMixedPort);
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
                SetStatus("Сервер перестал отвечать", "Рабочих серверов для замены нет — обновите списки и проверьте серверы");
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
    /// Сервер перестал открывать проверочный адрес: счётчик провалов растёт, из списка
    /// сервер убирается только после <see cref="NodeLimits.RemoveAfterFailures"/> провалов подряд —
    /// разовый сбой живого сервера не должен стирать его из списка.
    /// </summary>
    private void DropFailedNode(ProxyNode node, string reason)
    {
        var removed = NodeHealth.RegisterFailure(node);
        ApplyTestedNode(node);
        // Кэш пишется в фоне: список может быть десятки тысяч серверов, не тормозим UI.
        var snapshot = AllRows.Select(r => r.Node).ToList();
        _ = Task.Run(() => NodeSourceService.SaveCache(snapshot));
        AppendLog(removed
            ? $"Сервер удалён после {node.FailCount} провалов — {reason}"
            : $"Сервер не отвечает ({node.FailCount} из {NodeLimits.RemoveAfterFailures}) — {reason}");
    }

    /// <summary>Кнопка «Дальше»: следующий проверенный сервер по возрастанию пинга.</summary>
    private async void OnNextNodeClick(object sender, RoutedEventArgs e)
    {
        if (!_manager.IsConnected || _closing)
        {
            return;
        }

        var next = PickNextNode();
        if (next is null)
        {
            SetStatus("Нет других серверов", "Проверенных серверов больше нет — нажмите «Все серверы» на вкладке «Серверы»");
            return;
        }

        if (!BeginOperation("переключение сервера"))
        {
            return;
        }

        // Ручное переключение — сервис больше не определён однозначно.
        _activeService = null;

        try
        {
            var progress = new Progress<string>(line => StatusDetail.Text = line);
            SetStatus("Переключаюсь", $"{next.Name} · {next.DisplayDelay}");
            if (_manager.IsNodeLoaded(next) &&
                await _manager.SwitchToAsync(next, progress, _operation!.Token))
            {
                StatusDetail.Text = "Проверяю новый сервер…";
                if (await _manager.VerifyThroughProxyAsync(_operation.Token))
                {
                    _warmSwitchCount++;
                    _lastFailoverUtc = DateTime.UtcNow;
                    FooterText.Text = $"Прокси 127.0.0.1:{_manager.ActiveMixedPort} — сервер {next.Name}";
                    _tray?.ShowBalloon("Сервер переключён", next.Name + Environment.NewLine +
                                                          "127.0.0.1:" + _manager.ActiveMixedPort);
                    _ = RefreshExitInfoAsync();
                    return;
                }
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            AppLog.Write("proxy", "Переключение на следующий сервер не удалось", exception);
        }
        finally
        {
            EndOperation();
            UpdateConnectionUi();
        }

        await ConnectAsync(next, automatic: true, markFailures: false);
    }

    /// <summary>
    /// Очередь «Дальше»: текущий сервер уходит в конец, следующий берётся из начала.
    /// Так все проверенные серверы участвуют по кругу и рабочий сервер не теряется
    /// из-за ручного переключения.
    /// </summary>
    private ProxyNode? PickNextNode()
    {
        var now = DateTime.UtcNow;
        var usable = _workingRows
            .Select(row => row.Node)
            .Where(node => NodeAvailability.IsUsable(
                node, NodeLimits.WorkingRecheckMinutes, _settings.ConnectOnlyMine, now))
            .OrderBy(node => node.DelayMs ?? int.MaxValue)
            .ThenByDescending(node => node.IsMine)
            .ThenByDescending(node => node.OpenDomains)
            .ToList();
        if (usable.Count == 0)
        {
            return null;
        }

        var byId = usable.ToDictionary(node => node.Id, StringComparer.OrdinalIgnoreCase);
        // Убираем из очереди то, что перестало быть пригодным, и добавляем новые:
        // порядок уже набранной очереди сохраняется, а не сбрасывается каждый раз.
        _nodeRotation.RemoveAll(id => !byId.ContainsKey(id));
        foreach (var id in byId.Keys)
        {
            if (!_nodeRotation.Contains(id))
            {
                _nodeRotation.Add(id);
            }
        }

        // Текущий сервер — в конец очереди, чтобы следующий раз он был последним.
        var currentId = _manager.CurrentNode?.Id;
        if (currentId is not null)
        {
            var index = _nodeRotation.IndexOf(currentId);
            if (index >= 0)
            {
                _nodeRotation.RemoveAt(index);
                _nodeRotation.Add(currentId);
            }
        }

        if (_nodeRotation.Count < 2)
        {
            return null;
        }

        return byId[_nodeRotation[0]];
    }

    /// <summary>Поделиться подключённым сервером: ссылка копируется в буфер обмена.</summary>
    private void OnShareNodeClick(object sender, RoutedEventArgs e)
    {
        var node = _manager.CurrentNode;
        if (node is null)
        {
            SetStatus("Нечего отправить", "Сначала подключитесь к серверу");
            return;
        }

        // Владелец просил делиться только адресом сервера: IP (или домен) и порт.
        var endpoint = string.IsNullOrWhiteSpace(node.Server)
            ? node.Name
            : $"{node.Server}:{node.Port}";
        if (string.IsNullOrWhiteSpace(endpoint))
        {
            SetStatus("Нечего отправить", "У сервера нет адреса");
            return;
        }

        try
        {
            System.Windows.Clipboard.SetText(endpoint);
            SetStatus("Адрес сервера скопирован", endpoint);
            _tray?.ShowBalloon("Адрес сервера скопирован", endpoint);
        }
        catch (Exception exception)
        {
            ReportError("Не удалось скопировать адрес сервера", exception);
        }
    }

    /// <summary>Следующий рабочий сервер, по возможности из другого семейства камуфляжа.</summary>
    private ProxyNode? PickFailoverNode(string? avoidFamily)    {
        var telegramNeeded = _sources.Any(s => s.Enabled && SourceKinds.IsProxy(s));
        return _workingRows
            .Select(r => r.Node)
            .Where(n => n.DelayMs is not null && !_failedForSession.Contains(n.Id))
            .OrderBy(n => n.DelayMs ?? int.MaxValue)
            .ThenBy(n => avoidFamily is not null && NodeRanking.Family(n) == avoidFamily ? 1 : 0)
            .ThenByDescending(n => telegramNeeded && n.TelegramOk == true)
            .ThenByDescending(n => n.OpenDomains)
            .ThenByDescending(n => n.IsMine)
            .ThenByDescending(n => n.RankScore)
            .FirstOrDefault();
    }

    /// <summary>Запасной сервер другого камуфляжного семейства — показываем его после подключения.</summary>
    private ProxyNode? PickBackupNode(ProxyNode current) =>
        _workingRows
            .Select(r => r.Node)
            .Where(n => n.DelayMs is not null && n.Id != current.Id &&
                        NodeRanking.Family(n) != NodeRanking.Family(current))
            .OrderByDescending(n => n.IsMine)
            .ThenByDescending(n => n.OpenDomains)
            .ThenByDescending(n => n.RankScore)
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
            ? "Подключено · " + (delay is { } ms ? ms + " мс" : "проверенный сервер")
            : held
                ? "Трафик заблокирован (kill switch)"
                : "Отключено";
        StatusDot.Fill = (Brush)FindResource(connected ? "AccentBrush" : held ? "DangerBrush" : "MutedBrush");
        _tray?.UpdateStatus(connected, null, held);
        Title = connected
            ? $"{AppInfo.Name} {AppInfo.Version} — подключено"
            : $"{AppInfo.Name} {AppInfo.Version} — свободные серверы";
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
            $"Активный сервер: проверенный" + (node.DelayMs is { } delay ? $" · {delay} мс" : string.Empty),
            $"Маскировка: {NodeRanking.Label(node)} · камуфляж: {NodeRanking.CamouflageLabel(node)}",
            backup is null
                ? "Запасной: нет сервера другого камуфляжа"
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
            $"Кэш: {nodes} серверов · MTProto {mtproto} (нерабочих {_mtprotoDeadRows.Count}). " +
            $"Непроверенные серверы удаляются при первом провале, уже рабочие — после {NodeLimits.RemoveAfterFailures} провалов подряд; " +
            $"нерабочие MTProto: лимит {MtProtoRetention.MaxDead}, старше {MtProtoRetention.DeadHours} ч удаляются автоматически.";
    }

    /// <summary>Ручная очистка: убирает нерабочие MTProto, серверы остаются.</summary>
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
            "Серверы и рабочие MTProto останутся.",
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
