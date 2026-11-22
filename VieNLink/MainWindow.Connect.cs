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
                concurrency: 8,
                timeoutMs: 3000,
                waitForGate: false,
                precheck: !(_manager.IsConnected && _settings.TunnelMode == TunnelMode.Tun));
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
            // Без явного сервиса проверяем то, чем сервер уже подтвердил работу: универсальный
            // Cloudflare и один открытый сервис. Проверка только по YouTube браковала сервер,
            // который на этом провайдере открывает лишь Telegram/Cloudflare, — после нескольких
            // таких подключений рабочий сервер удалялся.
            var explicitUrls = probeUrls;
            var targetLabel = explicitUrls is null
                ? "проверочные адреса сервера"
                : explicitUrls.Count == 1
                    ? explicitUrls[0]
                    : explicitUrls[0] + $" и ещё {explicitUrls.Count - 1}";
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
                var targetUrls = explicitUrls ?? ProxyManager.VerificationUrls(candidate);

                try
                {
                    await _manager.ConnectAsync(candidate, progress, _operation.Token, targetUrls);
                    StatusDetail.Text = explicitUrls is null
                        ? "Проверяю, открывается ли сайт через сервер"
                        : "Проверяю, открывается ли " + targetLabel;
                    if (await _manager.VerifyAnyThroughProxyAsync(_operation.Token, targetUrls))
                    {
                        StatusDetail.Text = explicitUrls is null
                            ? "Сайт открылся через сервер"
                            : "Открылось: " + targetLabel;
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
                        if (_settings.AutoMtProto &&
                            _sources.Any(s => s.Enabled && SourceKinds.IsProxy(s)))
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
        // Сервис сохраняем: если упал «Универсальный», переключаемся на следующий
        // универсальный, а не на первый попавшийся.
        // Проверка живости идёт через сам туннель, и её провал — повод переключиться,
        // но не доказательство смерти сервера: один заторможенный домен (YouTube у
        // провайдера) или занятый канал дают тот же результат. Статус и FailCount
        // меняет только прямая проверка в скрытом ядре, иначе рабочие серверы
        // удалялись бы именно во время работы VPN.
        AppLog.Write("proxy",
            $"Сервер «{nodeName}» перестал открывать проверочный адрес — переключаюсь, статус не меняю");
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
    /// Возврат на выбранный сервис: очередная проверка нашла свежий сервер, который его
    /// открывает, а текущее подключение идёт через запасной сервер другого сервиса.
    /// Сначала пробуем тёплое переключение (без перезапуска ядра), иначе — обычное
    /// подключение. При неудаче остаёмся на прежнем рабочем сервере.
    /// </summary>
    private async Task TryUpgradeToActiveServiceAsync()
    {
        if (_closing || _busy || _settings.AutomationPaused || !_settings.AutoFailover ||
            !_manager.IsConnected || _activeService is not { } service)
        {
            return;
        }

        var current = _manager.CurrentNode;
        if (current is null || NodeAvailability.OpenedFor(current, service))
        {
            return;
        }

        // Фоновая проверка обновляет десятки серверов подряд: без паузы приложение
        // дёргало бы подключение после каждой партии.
        if (DateTime.UtcNow - _lastServiceUpgradeUtc < TimeSpan.FromMinutes(1))
        {
            return;
        }

        if (Interlocked.Exchange(ref _serviceUpgradeBusy, 1) == 1)
        {
            return;
        }

        try
        {
            var now = DateTime.UtcNow;
            var preferred = NodeFailover.Rank(
                    _workingRows.Select(row => row.Node),
                    service,
                    now,
                    _settings.ConnectOnlyMine,
                    filter: node => !_failedForSession.Contains(node.Id) &&
                                    (!_upgradeRetryAfter.TryGetValue(node.Id, out var retryAt) ||
                                     retryAt <= now))
                .FirstOrDefault(node => NodeAvailability.OpenedFor(node, service));
            if (preferred is null || preferred.Id == current.Id)
            {
                return;
            }

            _lastServiceUpgradeUtc = now;
            var name = ServiceDisplayName(service);
            var serviceUrls = ProbeUrlsForService(service);
            AppLog.Write("proxy",
                $"«{name}» снова доступен: «{preferred.Name}» ({preferred.DisplayDelay}) — возвращаюсь");

            // Тёплое переключение: сервер уже в конфиге, TUN и прокси не перезапускаются.
            if (_manager.IsNodeLoaded(preferred) && BeginOperation("возврат на " + name))
            {
                try
                {
                    var progress = new Progress<string>(line => StatusDetail.Text = line);
                    SetStatus("Возвращаюсь на " + name, preferred.Name);
                    if (await _manager.SwitchToAsync(preferred, progress, _operation!.Token) &&
                        await _manager.VerifyAnyThroughProxyAsync(_operation.Token, serviceUrls))
                    {
                        _warmSwitchCount++;
                        _lastFailoverUtc = DateTime.UtcNow;
                        _lastFailoverNote = "возврат на " + name;
                        FooterText.Text = $"Прокси 127.0.0.1:{_manager.ActiveMixedPort} — сервер {preferred.Name}";
                        AppendLog($"Возврат на {name}: «{preferred.Name}»");
                        _ = RefreshExitInfoAsync();
                        UpdateDiagnostics();
                        return;
                    }

                    // Новый сервер не открыл сервис — возвращаем прежний рабочий,
                    // чтобы не оставить пользователя без прокси.
                    _upgradeRetryAfter[preferred.Id] = DateTime.UtcNow.AddMinutes(15);
                    try
                    {
                        if (await _manager.SwitchToAsync(current, progress, _operation.Token))
                        {
                            AppendLog($"«{preferred.Name}» не открыл {name} — оставил «{current.Name}»");
                        }
                    }
                    catch (Exception exception) when (exception is not OperationCanceledException)
                    {
                        AppLog.Write("proxy", "Не удалось вернуться на прежний сервер", exception);
                    }

                    return;
                }
                finally
                {
                    EndOperation();
                    UpdateConnectionUi();
                }
            }

            // Сервер не в текущем конфиге: обычное подключение с проверкой выбранного
            // сервиса, после него конфиг пересобран. Провал улучшения не должен
            // засчитываться серверам как проверка: это не фоновая проверка.
            SetStatus("Возвращаюсь на " + name, preferred.Name);
            await ConnectAsync(preferred, automatic: true, markFailures: false, probeUrls: serviceUrls);
            if (_manager.IsConnected)
            {
                _coldSwitchCount++;
                _lastFailoverUtc = DateTime.UtcNow;
                _lastFailoverNote = "возврат на " + name;
                UpdateDiagnostics();
            }
            else
            {
                _upgradeRetryAfter[preferred.Id] = DateTime.UtcNow.AddMinutes(15);
                // Неудачная попытка улучшения не должна оставить без прокси.
                await ConnectAsync(current, automatic: true, markFailures: false,
                    probeUrls: ProxyManager.VerificationUrls(current));
            }
        }
        catch (OperationCanceledException)
        {
            // Отмена — штатное завершение: остаёмся на текущем сервере.
        }
        catch (Exception exception)
        {
            AppLog.Write("proxy", "Возврат на выбранный сервис не удался", exception);
        }
        finally
        {
            Volatile.Write(ref _serviceUpgradeBusy, 0);
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
        _ = Task.Run(() => NodeCacheWriter.Save(snapshot));
        AppendLog(removed
            ? $"Сервер удалён после {node.FailCount} провалов — {reason}"
            : $"Сервер не отвечает ({node.FailCount} из {NodeLimits.RemoveAfterFailures}) — {reason}");
    }

    /// <summary>
    /// Кнопка «Дальше»: следующий проверенный сервер в порядке приоритета.
    /// Сначала серверы выбранного сервиса (по возрастанию пинга), затем остальные —
    /// поэтому при «Универсальном» переключение не уходит на сервер без универсального доступа.
    /// </summary>
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

        // Ручное переключение остаётся в выбранном сервисе: «Универсальный» и «Дальше»
        // должны по-прежнему искать универсальные серверы. Сервис сбрасывается только
        // при отключении или выборе конкретного сервера в списке.
        try
        {
            var progress = new Progress<string>(line => StatusDetail.Text = line);
            SetStatus("Переключаюсь", $"{next.Name} · {next.DisplayDelay}");
            if (_manager.IsNodeLoaded(next) &&
                await _manager.SwitchToAsync(next, progress, _operation!.Token))
            {
                StatusDetail.Text = "Проверяю новый сервер…";
                if (await _manager.VerifyAnyThroughProxyAsync(
                        _operation.Token, ProxyManager.VerificationUrls(next)))
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
    /// Очередь «Дальше»: следующий сервер в порядке приоритета, с переходом по кругу.
    /// Выбранный сервис важнее пинга, поэтому «Универсальный» не переключится на сервер
    /// без универсального доступа, пока есть живые универсальные.
    /// </summary>
    private ProxyNode? PickNextNode()
    {
        var ordered = NodeFailover.Rank(
            _workingRows.Select(row => row.Node),
            _activeService,
            DateTime.UtcNow,
            _settings.ConnectOnlyMine,
            filter: node => !_failedForSession.Contains(node.Id));
        if (ordered.Count == 0)
        {
            return null;
        }

        var currentId = _manager.CurrentNode?.Id;
        var currentIndex = currentId is null
            ? -1
            : ordered.FindIndex(node => string.Equals(node.Id, currentId, StringComparison.OrdinalIgnoreCase));
        if (currentIndex < 0)
        {
            return ordered[0];
        }

        // Идём по кругу от текущего: все серверы участвуют, а свежие и лучшие —
        // первыми в своей группе приоритета.
        for (var step = 1; step <= ordered.Count; step++)
        {
            var candidate = ordered[(currentIndex + step) % ordered.Count];
            if (!string.Equals(candidate.Id, currentId, StringComparison.OrdinalIgnoreCase))
            {
                return candidate;
            }
        }

        return null;
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
    private ProxyNode? PickFailoverNode(string? failedFamily)
    {
        // Выбранный сервис — главный приоритет: сначала серверы, которые его открывают,
        // затем следующий лучший из остальных. Свежесть обязательна: переключаться
        // на «рабочий» из старого кэша нельзя.
        var ordered = NodeFailover.Rank(
            _workingRows.Select(row => row.Node),
            _activeService,
            DateTime.UtcNow,
            _settings.ConnectOnlyMine,
            failedFamily,
            preferTelegram: _sources.Any(s => s.Enabled && SourceKinds.IsProxy(s)),
            filter: node => !_failedForSession.Contains(node.Id));
        return ordered.FirstOrDefault();
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
