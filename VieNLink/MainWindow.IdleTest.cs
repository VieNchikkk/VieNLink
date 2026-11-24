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
    // ---------- проверка серверов в фоне ----------

    private readonly HiddenProbeService _probe = new();

    /// <summary>Автоперепроверка отсева раз в минуту — тем же путём, что и кнопка.</summary>
    private DateTime _lastRejectedAutoCheckUtc = DateTime.MinValue;

    /// <summary>
    /// Фоновая проверка: работает и в простое, и во время работы. Серверы проверяются
    /// через скрытое ядро на отдельных портах, поэтому не мешают текущему подключению.
    /// </summary>
    private async void OnIdleTick(object? sender, EventArgs e)
    {
        if (_closing || _settings.AutomationPaused)
        {
            return;
        }

        try
        {
            // MTProto обслуживается автономно: чистка старых, проверка и сбор через RSS.
            // Проверка идёт первой, чтобы сбор каналов не откладывал её.
            PruneExpiredMtProto();
            await AutoCheckMtProtoAsync();
            await AutoCollectMtProtoAsync();

            // Отсев — тем же путём, что и кнопка «Перепроверить всё», раз в 5 минут.
            if (!_idleTesting)
            {
                await AutoRecheckRejectedAsync();
            }

        if (_idleTesting || !_settings.IdleTestEnabled)
        {
            return;
        }

        var idleMinutes = Math.Max(1, _settings.IdleTestMinutes);
        var userIdle = GetSystemIdleTime() >= TimeSpan.FromMinutes(idleMinutes);

        // Пока не набрана квота — 10 рабочих серверов и по 2 на каждый сервис — ищем чаще:
        // в простое раз в минуту, при работе раз в две. Иначе поиск мог ждать
        // до пяти минут, и кнопки сервисов оставались без живых серверов.
        var quotaMissing = MissingServiceQuota().Count > 0 ||
                           FreshWorkingCount() < NodeLimits.MinWorkingNodes;

        // В простое проходы реже и крупнее; при работе — чаще и маленькими партиями,
        // чтобы проверка не конкурировала с трафиком пользователя.
        var interval = quotaMissing
            ? (userIdle ? 1 : 2)
            : userIdle
                ? Math.Clamp(_settings.IdleTestIntervalMinutes, 1, 5)
                : Math.Clamp(_settings.IdleTestIntervalMinutes / 5, 1, 2);
        var searchDue = _lastIdleTestUtc is not { } lastSearch ||
                        DateTime.UtcNow - lastSearch >= TimeSpan.FromMinutes(interval);
        // Рабочие серверы перепроверяем отдельно от поиска новых: раз в минуту смотрим,
        // у кого истекает окно свежести, и берём их в работу на минуту раньше срока.
        // Так счётчик не «мигает» между редкими проходами поиска.
        var recheckDue = HasWorkingNodesDue(DateTime.UtcNow) &&
                         (_lastRecheckUtc is not { } lastRecheck ||
                          DateTime.UtcNow - lastRecheck >= TimeSpan.FromSeconds(45));
        if (!searchDue && !recheckDue)
        {
            return;
        }

        await RunIdleTestAsync(userIdle, searchNew: searchDue);
        }
        catch (Exception exception)
        {
            // Фоновый такт не должен ронять приложение: любая ошибка источника,
            // MTProto или проверки серверов просто переносится на следующий такт.
            AppLog.Write("probe", "Фоновый такт не удался", exception);
        }
    }

    /// <summary>Есть ли рабочий сервер, у которого заканчивается окно свежести.</summary>
    private bool HasWorkingNodesDue(DateTime nowUtc)
    {
        var cutoff = NodeAvailability.RecheckCutoffUtc(nowUtc);
        return _workingRows.Any(row =>
            row.Node.Status == NodeStatus.Working &&
            (row.Node.CheckedAtUtc is null || row.Node.CheckedAtUtc < cutoff) &&
            NodeAvailability.RetryAllowed(row.Node, nowUtc));
    }

    /// <summary>
    /// Автоперепроверка отсева: раз в минуту берём все записи и прогоняем тем же
    /// RecheckRejectedAsync, что и кнопка «Перепроверить всё». Успешные возвращаются
    /// в рабочие, остальные остаются. При выключенной автоматике/занятости — пропуск.
    /// </summary>
    private async Task AutoRecheckRejectedAsync()
    {
        if (!_settings.AutoRecheckRejected ||
            _settings.AutomationPaused ||
            !_settings.IdleTestEnabled)
        {
            return;
        }

        if (DateTime.UtcNow - _lastRejectedAutoCheckUtc < TimeSpan.FromMinutes(1))
        {
            return;
        }

        _lastRejectedAutoCheckUtc = DateTime.UtcNow;
        if (_busy || _probe.IsRunning)
        {
            return;
        }

        var items = RejectedNodeStore.Load();
        if (items.Count == 0)
        {
            return;
        }

        var rows = items.Select(item => new RejectedNodeRow(item)).ToList();
        await RecheckRejectedAsync(rows);
    }

    private async Task RunIdleTestAsync(bool userIdle, bool searchNew = true)
    {
        if (_settings.AutomationPaused)
        {
            return;
        }

        var connected = _manager.IsConnected;

        // TUN-обход перехватывает весь трафик, включая скрытое ядро, — в этом режиме
        // проверяем только загруженные серверы. zapret и свой обход DPI ограничены целевыми
        // доменами (zapret — hostlist, десинк — TlsHello), поэтому соединения к серверам
        // серверов они не трогают и проверке не мешают.
        if (_tunBypass.IsRunning)
        {
            return;
        }

        // При активном TUN трафик скрытого ядра тоже уйдёт в туннель, поэтому
        // в этом случае проверяем только уже загруженные серверы основным ядром.
        var hidden = !(connected && _settings.TunnelMode == TunnelMode.Tun);

        // Пока компьютер занят, берём половину партии (но не больше 60): при запуске
        // пробного ядра на каждую партию это единственный способ проверять список
        // не по 30 серверов за раз. Реальную скорость задаёт параллельность ниже.
        var baseBatch = Math.Clamp(_settings.IdleTestBatch, 5, 500);
        var batchSize = userIdle ? baseBatch : Math.Clamp(baseBatch / 2, 5, 60);
        if (_busy)
        {
            // Пользователь что-то делает в приложении — совсем маленькая партия.
            batchSize = Math.Min(batchSize, 10);
        }

        // Пока работает zapret/свой обход, видео и так идёт через WinDivert:
        // проверяем совсем небольшими партиями, чтобы не отнимать канал.
        var bypassActive = _zapret.IsRunning || _desync.IsRunning;
        if (bypassActive)
        {
            batchSize = Math.Min(batchSize, 5);
        }

        // Рабочих серверов должно быть не меньше десяти, а для YouTube, Telegram, Discord
        // и универсального доступа — не меньше двух на каждый сервис. Считаем только
        // свежие проверки: устаревшая запись в кэше не должна изображать готовый сервер.
        // Пока этого нет, не ждём следующего такта, а проверяем несколько партий подряд.
        var missing = MissingServiceQuota();
        var needQuota = searchNew &&
                        (FreshWorkingCount() < NodeLimits.MinWorkingNodes || missing.Count > 0);
        var maxRounds = needQuota ? 8 : 1;

        // Рабочие серверы перепроверяем чаще окна свежести — по
        // NodeLimits.WorkingRecheckMinutes (5 мин), начиная за пару минут до истечения.
        // Бесплатный сервер живёт минуты, и «рабочий» из кэша без перепроверки — главная
        // причина, почему значок сервиса обещал сервер, которого нет.
        // После провала выдерживаем паузу: иначе следующие партии подряд добивают
        // живой сервер до RemoveAfterFailures и отправляют его в отсев.
        static bool NeedsCheck(ProxyNode node, DateTime threshold, DateTime nowUtc) =>
            (node.CheckedAtUtc is null || node.CheckedAtUtc < threshold) &&
            NodeAvailability.RetryAllowed(node, nowUtc);

        // Проход только для перепроверки: ищем не новые серверы, а живые. Партия
        // меньше поисковой, но достаточно крупная, чтобы загрузить пробное ядро.
        if (!searchNew)
        {
            batchSize = Math.Min(batchSize, 40);
        }

        _idleTesting = true;
        _idleCts = new CancellationTokenSource();
        var token = _idleCts.Token;
        var roundStartedUtc = DateTime.UtcNow;
        const int roundBudgetMinutes = 6;
        try
        {
            for (var round = 1; round <= maxRounds; round++)
            {
                // Один такт не должен занимать полчаса: после нескольких партий отдаём
                // управление следующему такту. Иначе счётчики и интерфейс долго висят
                // на «Проверяю серверы», а новые результаты не успевают примениться.
                if (round > 1 &&
                    DateTime.UtcNow - roundStartedUtc >= TimeSpan.FromMinutes(roundBudgetMinutes))
                {
                    AppendLog($"Фон: прошло {roundBudgetMinutes} мин — проход продолжится в следующем такте");
                    break;
                }

                // Границу свежести считаем заново на каждом проходе: один вызов может
                // идти до восьми партий (десятки минут), и замороженная граница
                // исключала бы серверы, проверенные другими операциями уже после её
                // расчёта. Живой сервер выпадал из счётчиков до конца цикла, хотя его
                // давно пора было перепроверить.
                var recheckCutoff = NodeAvailability.RecheckCutoffUtc(DateTime.UtcNow);

                // Если какого-то сервиса не хватает, сначала проверяем серверы из источников,
                // которые уже давали доступ именно к нему. Фокус меняется по кругу:
                // иначе первый недостающий сервис забирал все раунды, а остальные ждали.
                var focusService = missing.Count == 0 ? null : missing[(round - 1) % missing.Count];
                var serviceScores = focusService is null ? null : BuildServiceSourceScores(focusService);
                var focusName = focusService switch
                {
                    "youtube" => "YouTube",
                    "telegram" => "Telegram",
                    "discord" => "Discord",
                    "universal" => "универсальный доступ",
                    _ => null,
                };

                List<ProxyNode> candidates;
                if (hidden)
                {
                    var checkNow = DateTime.UtcNow;
                    // Источники без единого рабочего сервера не проверяем: партия уходит на
                    // заведомо мёртвые адреса. Рабочие серверы из них перепроверяем всегда.
                    var barren = searchNew
                        ? NodeSourceStats.BarrenSources(AllRows.Select(row => row.Node))
                        : new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    var ordered = AllRows
                        .Select(row => row.Node)
                        .Where(node => searchNew
                            ? node.Status == NodeStatus.Unknown ||
                              (node.Status == NodeStatus.Working && NeedsCheck(node, recheckCutoff, checkNow))
                            : node.Status == NodeStatus.Working && NeedsCheck(node, recheckCutoff, checkNow))
                        .Where(node => node.Status == NodeStatus.Working ||
                                       !NodeSourceStats.FromBarrenOnly(node, barren))
                        .Distinct()
                        // Сначала перепроверяем тех, кто прямо сейчас показан в счётчиках:
                        // их смерть важнее, чем поиск среди ещё не проверенных. Иначе
                        // «рабочие» из кэша висят часами и кнопки ведут в пустоту.
                        .OrderBy(node => node.Status == NodeStatus.Working ? 0 : 1)
                        .ThenByDescending(node => serviceScores is not null
                            ? NodeFailover.BestSourceScore(serviceScores, node.SourceName)
                            : 0)
                        .ThenBy(node => node.CheckedAtUtc ?? DateTime.MinValue)
                        .ToList();

                    // Рабочие — первыми. Новые серверы перемежаем по источникам: иначе
                    // первые 200 позиций могут целиком прийти из одного мёртвого списка,
                    // и партия тратит минуты на заведомо нерабочие серверы.
                    var working = ordered.Where(node => node.Status == NodeStatus.Working).ToList();
                    var rest = ordered.Where(node => node.Status != NodeStatus.Working).ToList();

                    // Ограничение на источник: большой мёртвый список получает 2–3 слота,
                    // источник с подтверждённым доступом к нужному сервису — до 10.
                    int SourceCap(ProxyNode node)
                    {
                        var score = serviceScores is null
                            ? 0
                            : NodeFailover.BestSourceScore(serviceScores, node.SourceName);
                        return score <= 0 ? 3 : score < 50 ? 6 : 10;
                    }

                    candidates = working
                        .Concat(NodeFailover.TakeWithSourceCap(
                            rest, Math.Max(0, batchSize - working.Count), SourceCap))
                        .Take(batchSize)
                        .ToList();
                }
                else
                {
                    var checkNow = DateTime.UtcNow;
                    candidates = _manager.LoadedNodes
                        .Where(node => NeedsCheck(node, recheckCutoff, checkNow) &&
                                       (!searchNew || node.Status != NodeStatus.Dead))
                        .Take(batchSize)
                        .ToList();
                }

                if (candidates.Count == 0)
                {
                    break;
                }

                var mode = hidden
                    ? (userIdle ? "скрытое ядро, простой" : "скрытое ядро, фон") +
                      (bypassActive ? ", обход включён" : string.Empty) + ", напрямую"
                    : "через работающее ядро (TUN), только загруженные";
                var quotaNote = needQuota
                    ? $", свежих {FreshWorkingCount()} из {NodeLimits.MinWorkingNodes}" +
                      (missing.Count > 0 ? $", нет по {NodeLimits.MinWorkingPerService} для: {string.Join(", ", missing.Select(ServiceLabel))}" : string.Empty) +
                      (focusName is not null ? $", ищу под {focusName}" : string.Empty)
                    : string.Empty;
                AppendLog($"Фон: серверов {candidates.Count} ({mode}{quotaNote})");
                // В шапке — только суть: что идёт проверка и сколько серверов.
                // Разбивка по сервисам и квоты остаются в журнале.
                ShowBackgroundDetail($"Проверяю серверы: {candidates.Count} ({mode})");
                void OnTested(ProxyNode node) => Dispatcher.BeginInvoke(() => ApplyTestedNode(node));
                var progress = new Progress<string>(line =>
                {
                    AppLog.Write("probe", line);
                    ShowBackgroundDetail(line);
                });

                var summary = hidden
                    ? await _probe.TestAsync(
                        candidates, _settings, progress, OnTested, token,
                        // Партия из мёртвых серверов — это почти одни таймауты, поэтому
                        // скорость прохода задаёт именно параллельность. 24 в простое
                        // и 16 при работе проверяют список в разы быстрее; рабочие
                        // серверы дополнительно защищены таймаутом 6 с и повторной
                        // проверкой. При активном обходе и работе пользователя
                        // держим запас по каналу.
                        concurrency: bypassActive ? 3 : userIdle ? 24 : connected ? 12 : 16,
                        timeoutMs: 3000,
                        waitForGate: false)
                    : await _manager.TestLoadedNodesAsync(
                        candidates, progress, OnTested, token,
                        userIdle ? 6 : 4, 2500,
                        "https://www.youtube.com/generate_204");

                if (summary.Total == 0)
                {
                    AppendLog("Фон: пробное ядро занято, такт пропущен");
                    break;
                }

                await RepartitionAndSaveAsync();
                AppendLog($"Фон: серверов проверено {summary.Total}, рабочих {summary.Working}, " +
                          $"лучший открывает доменов {summary.BestDomains}");
                ShowBackgroundDetail($"Фон: проверено {summary.Total} серверов, свежих рабочих " +
                                     $"{summary.Working} (всего {FreshWorkingCount()})");

                missing = MissingServiceQuota();
                if (FreshWorkingCount() >= NodeLimits.MinWorkingNodes && missing.Count == 0)
                {
                    break;
                }
            }

            missing = MissingServiceQuota();
            if (FreshWorkingCount() < NodeLimits.MinWorkingNodes || missing.Count > 0)
            {
                var parts = new List<string>();
                if (FreshWorkingCount() < NodeLimits.MinWorkingNodes)
                {
                    parts.Add($"свежих рабочих {FreshWorkingCount()} из {NodeLimits.MinWorkingNodes}");
                }

                if (missing.Count > 0)
                {
                    parts.Add("нет по " + NodeLimits.MinWorkingPerService + " для: " + string.Join(", ", missing.Select(ServiceLabel)));
                }

                AppLog.Write("probe", "Квота ещё не набрана: " + string.Join("; ", parts) +
                                      " — поиск продолжится в фоне");
            }

            _lastRecheckUtc = DateTime.UtcNow;
            if (searchNew)
            {
                _lastIdleTestUtc = DateTime.UtcNow;
            }
        }
        catch (OperationCanceledException)
        {
            await RepartitionAndSaveAsync();
            AppendLog("Фон: проверка остановлена");
        }
        catch (Exception exception)
        {
            // Если сработал откат (ни один сервер не открылся), отметки могли разъехаться с таблицей.
            await RepartitionAndSaveAsync();
            AppLog.Write("probe", "Фоновая проверка серверов не удалась", exception);
        }
        finally
        {
            _idleTesting = false;
            _idleCts?.Dispose();
            _idleCts = null;
        }
    }

    /// <summary>Сколько времени пользователь не трогал клавиатуру и мышь.</summary>
    private static TimeSpan GetSystemIdleTime()
    {
        var info = new LastInputInfo { cbSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf<LastInputInfo>() };
        if (!GetLastInputInfo(ref info))
        {
            return TimeSpan.Zero;
        }

        var idle = unchecked((uint)Environment.TickCount - info.dwTime);
        return TimeSpan.FromMilliseconds(idle);
    }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct LastInputInfo
    {
        public uint cbSize;
        public uint dwTime;
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool GetLastInputInfo(ref LastInputInfo plii);

    /// <summary>
    /// Сервер проверен: рабочий переносим на подвкладку «Рабочие», не открывший
    /// ни один адрес остаётся в «Непроверенных», пока не наберёт
    /// <see cref="NodeLimits.RemoveAfterFailures"/> провалов подряд, — тогда удаляется.
    /// </summary>
    private void ApplyTestedNode(ProxyNode node)
    {
        var row = _rowIndex.TryGetValue(node.Id, out var indexed) ? indexed : null;
        if (row is null)
        {
            return;
        }

        // Пока шла проверка, список могли пересобрать из источников (MergeNodes
        // заменяет объекты серверов новыми). Результат тогда нужно перенести на
        // актуальный объект: иначе партия проверена, а значки сервисов остались
        // прежними — сервер выглядит рабочим, но без свежей проверки.
        if (!ReferenceEquals(row.Node, node))
        {
            row.Node.Status = node.Status;
            row.Node.DelayMs = node.DelayMs;
            row.Node.CheckedAtUtc = node.CheckedAtUtc;
            row.Node.TriedAtUtc = node.TriedAtUtc;
            row.Node.YoutubeOk = node.YoutubeOk;
            row.Node.TelegramOk = node.TelegramOk;
            row.Node.UniversalOk = node.UniversalOk;
            row.Node.DiscordOk = node.DiscordOk;
            row.Node.OpenDomains = node.OpenDomains;
            row.Node.FailCount = node.FailCount;
            node = row.Node;
        }

        if (node.Status == NodeStatus.Dead)
        {
            // Сервер, через который прямо сейчас идёт трафик, не убираем: пользователь
            // может смотреть видео, а скрытая проверка в этот момент не открыла адрес
            // (сервер занят, лимит соединений, разовый сбой). Реальную смерть заметит
            // health-check через активный прокси и переключит на запасной.
            if (_manager.CurrentNode is { } connected &&
                connected.Id.Equals(node.Id, StringComparison.OrdinalIgnoreCase))
            {
                node.Status = NodeStatus.Working;
                node.FailCount = 0;
                node.TriedAtUtc = DateTime.UtcNow;
                row.Refresh();
                AppendLog($"«{node.Name}»: проверка не открыла адрес, но трафик идёт через него — оставляю в списке");
                return;
            }

            // В отсев переносим только после завершения проверки (RepartitionAndSaveAsync):
            // при откате партии «ни один сервер не открылся» статус возвращается, и сервер
            // не должен остаться в отсеве как рабочий.
            DropNode(row);
            return;
        }

        row.Refresh();

        if (node.Status == NodeStatus.Working)
        {
            RegisterWorkingNode(node);
        }

        var target = node.Status == NodeStatus.Working ? _workingRows : _rows;
        if (_rowOwner.TryGetValue(node.Id, out var current) && !ReferenceEquals(current, target))
        {
            current.Remove(row);
            target.Add(row);
            _rowOwner[node.Id] = target;
        }

        UpdateTestedSummary();

        // Проверка нашла сервер выбранного сервиса, а работаем на запасном из другого.
        // Возврат делаем в фоне, чтобы не ждать следующего падения сервера.
        if (node.Status == NodeStatus.Working &&
            _activeService is { } service &&
            NodeAvailability.OpenedFor(node, service))
        {
            _ = TryUpgradeToActiveServiceAsync();
        }
    }

    /// <summary>Удаляет строку из таблицы: нерабочие серверы не держим в списке.</summary>
    private void DropNode(NodeRow row)
    {
        if (_rowOwner.TryGetValue(row.Id, out var owner))
        {
            owner.Remove(row);
        }

        _rowOwner.Remove(row.Id);
        _rowIndex.Remove(row.Id);
        _droppedNodes.Add(row.Node);
        UpdateTestedSummary();
    }

    /// <summary>Счётчики в заголовках обновляем не чаще 4 раз в секунду: иначе проверка 500 серверов тормозит UI.</summary>
    private void UpdateTestedSummary()
    {
        var now = DateTime.UtcNow;
        if (now - _lastTestedSummaryUtc < TimeSpan.FromMilliseconds(250))
        {
            return;
        }

        _lastTestedSummaryUtc = now;
        UpdateSummary();
    }

    /// <summary>Проверяем сначала непроверенные и давно проверенные серверы.</summary>
    /// <summary>
    /// Партия для кнопки «Партия 500»: ровно <see cref="TestBatchSize"/> серверов, независимо от
    /// лимита конфига. Лимит «серверов в конфиге» ограничивает только список для sing-box.
    /// </summary>
    private List<ProxyNode> SelectBatch(IEnumerable<ProxyNode> nodes)
    {
        var ordered = nodes
            .OrderBy(n => n.DelayMs is not null)
            .ThenBy(n => n.CheckedAtUtc ?? DateTime.MinValue)
            .ToList();

        // Перемешиваем по источникам: иначе первая партия может целиком прийти
        // из одного мёртвого списка, и проверка 150–500 серверов не находит живых.
        return NodeFailover.InterleaveBySource(ordered, TestBatchSize);
    }

    /// <summary>
    /// Сколько серверов с доступом к сервису можно взять прямо сейчас: только свежая
    /// проверка. Устаревшее «рабочий» из кэша не считается — иначе квота выглядела бы
    /// набранной, а кнопка не находила сервер.
    /// </summary>
    private int CountVerifiedFor(string service)
    {
        var now = DateTime.UtcNow;
        return _workingRows.Count(row =>
            NodeAvailability.IsUsable(
                row.Node, NodeLimits.WorkingRecheckMinutes, _settings.ConnectOnlyMine, now) &&
            NodeAvailability.OpenedFor(row.Node, service));
    }

    /// <summary>Рабочие серверы со свежей проверкой — те же, что видят счётчики в шапке.</summary>
    private int FreshWorkingCount()
    {
        var now = DateTime.UtcNow;
        return _workingRows.Count(row => NodeAvailability.IsUsable(
            row.Node, NodeLimits.WorkingRecheckMinutes, _settings.ConnectOnlyMine, now));
    }

    /// <summary>
    /// Ход фоновой проверки в строке состояния: пользователь должен видеть, что серверы
    /// проверяются, а не думать, что приложение зависло. Операции пользователя важнее —
    /// их строку не перетираем.
    /// </summary>
    private void ShowBackgroundDetail(string text)
    {
        if (_busy || _closing || StatusDetail is null)
        {
            return;
        }

        StatusDetail.Text = text;
    }

    private static bool ServiceOpened(ProxyNode node, string service) => service switch
    {
        "youtube" => node.YoutubeOk == true,
        "telegram" => node.TelegramOk == true,
        "discord" => node.DiscordOk == true,
        _ => node.UniversalOk == true,
    };

    /// <summary>
    /// Доля серверов источника, которые открывают нужный сервис (0–1000). Источники
    /// у сервера склеены строкой, поэтому считаем каждый отдельно, а не всю строку целиком:
    /// иначе оценка терялась и партия снова шла в первый попавшийся (часто мёртвый) список.
    /// </summary>
    private Dictionary<string, int> BuildServiceSourceScores(string service)
    {
        var totals = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var opened = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in _workingRows)
        {
            var reached = ServiceOpened(row.Node, service);
            foreach (var source in NodeFailover.SplitSources(row.Node.SourceName))
            {
                totals[source] = totals.GetValueOrDefault(source) + 1;
                if (reached)
                {
                    opened[source] = opened.GetValueOrDefault(source) + 1;
                }
            }
        }

        var scores = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in totals)
        {
            scores[pair.Key] = opened.GetValueOrDefault(pair.Key) * 1000 / pair.Value;
        }

        return scores;
    }

    /// <summary>
    /// Сервисы, для которых проверенных рабочих серверов меньше
    /// <see cref="NodeLimits.MinWorkingPerService"/>: их надо искать дальше.
    /// Ключи — строчные, как в <see cref="ServiceOpened"/>.
    /// </summary>
    private List<string> MissingServiceQuota()
    {
        var missing = new List<string>();

        void Require(string service)
        {
            if (CountVerifiedFor(service) < NodeLimits.MinWorkingPerService)
            {
                missing.Add(service);
            }
        }

        Require("youtube");
        Require("telegram");
        Require("discord");
        Require("universal");
        return missing;
    }

    /// <summary>Подпись сервиса для журнала и строки состояния.</summary>
    private static string ServiceLabel(string service) => service switch
    {
        "youtube" => "YouTube",
        "telegram" => "Telegram",
        "discord" => "Discord",
        _ => "универсальный",
    };

}
