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
    // ---------- проверка узлов в фоне ----------

    private readonly HiddenProbeService _probe = new();

    /// <summary>
    /// Фоновая проверка: работает и в простое, и во время работы. Узлы проверяются
    /// через скрытое ядро на отдельных портах, поэтому не мешают текущему подключению.
    /// </summary>
    private async void OnIdleTick(object? sender, EventArgs e)
    {
        if (_closing)
        {
            return;
        }

        // MTProto обслуживается автономно: чистка старых, проверка и сбор через RSS.
        // Проверка идёт первой, чтобы сбор каналов не откладывал её.
        PruneExpiredMtProto();
        await AutoCheckMtProtoAsync();
        await AutoCollectMtProtoAsync();

        if (_idleTesting || !_settings.IdleTestEnabled)
        {
            return;
        }

        var idleMinutes = Math.Max(1, _settings.IdleTestMinutes);
        var userIdle = GetSystemIdleTime() >= TimeSpan.FromMinutes(idleMinutes);

        // Пока не набрана квота — 10 рабочих узлов и по 2 на каждый сервис — ищем чаще:
        // в простое раз в минуту, при работе раз в две. Иначе поиск мог ждать
        // до пяти минут, и кнопки сервисов оставались без живых узлов.
        var quotaMissing = MissingServiceQuota().Count > 0 ||
                           FreshWorkingCount() < NodeLimits.MinWorkingNodes;

        // В простое проходы реже и крупнее; при работе — чаще и маленькими партиями,
        // чтобы проверка не конкурировала с трафиком пользователя.
        var interval = quotaMissing
            ? (userIdle ? 1 : 2)
            : userIdle
                ? Math.Clamp(_settings.IdleTestIntervalMinutes, 1, 5)
                : Math.Clamp(_settings.IdleTestIntervalMinutes / 5, 1, 2);
        if (_lastIdleTestUtc is { } last &&
            DateTime.UtcNow - last < TimeSpan.FromMinutes(interval))
        {
            return;
        }

        await RunIdleTestAsync(userIdle);
    }

    private async Task RunIdleTestAsync(bool userIdle)
    {
        var connected = _manager.IsConnected;

        // TUN-обход перехватывает весь трафик, включая скрытое ядро, — в этом режиме
        // проверяем только загруженные узлы. zapret и свой обход DPI ограничены целевыми
        // доменами (zapret — hostlist, десинк — TlsHello), поэтому соединения к серверам
        // узлов они не трогают и проверке не мешают.
        if (_tunBypass.IsRunning)
        {
            return;
        }

        // При активном TUN трафик скрытого ядра тоже уйдёт в туннель, поэтому
        // в этом случае проверяем только уже загруженные узлы основным ядром.
        var hidden = !(connected && _settings.TunnelMode == TunnelMode.Tun);

        var baseBatch = Math.Clamp(_settings.IdleTestBatch, 5, 500);
        var batchSize = userIdle ? baseBatch : Math.Clamp(baseBatch / 4, 5, 30);
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

        // Рабочих узлов должно быть не меньше десяти, а для YouTube, Telegram, Discord
        // и универсального доступа — не меньше двух на каждый сервис. Считаем только
        // свежие проверки: устаревшая запись в кэше не должна изображать готовый узел.
        // Пока этого нет, не ждём следующего такта, а проверяем несколько партий подряд.
        var missing = MissingServiceQuota();
        var needQuota = FreshWorkingCount() < NodeLimits.MinWorkingNodes || missing.Count > 0;
        var maxRounds = needQuota ? 8 : 1;

        // Рабочие узлы перепроверяем чаще, чем раз в IdleRecheckHours — по
        // NodeLimits.WorkingRecheckMinutes (15 мин): бесплатный узел живёт минуты,
        // и «рабочий» из кэша без перепроверки — главная причина, почему значок
        // сервиса обещал узел, которого нет.
        var recheckCutoff = DateTime.UtcNow.AddMinutes(-NodeLimits.WorkingRecheckMinutes);
        static bool NeedsCheck(ProxyNode node, DateTime threshold) =>
            node.CheckedAtUtc is null || node.CheckedAtUtc < threshold;

        _idleTesting = true;
        _idleCts = new CancellationTokenSource();
        var token = _idleCts.Token;
        try
        {
            for (var round = 1; round <= maxRounds; round++)
            {
                // Если какого-то сервиса не хватает, сначала проверяем узлы из источников,
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
                    candidates = AllRows
                        .Select(row => row.Node)
                        .Where(node => node.Status == NodeStatus.Unknown ||
                                       (node.Status == NodeStatus.Working && NeedsCheck(node, recheckCutoff)))
                        .Distinct()
                        // Сначала перепроверяем тех, кто прямо сейчас показан в счётчиках:
                        // их смерть важнее, чем поиск среди ещё не проверенных. Иначе
                        // «рабочие» из кэша висят часами и кнопки ведут в пустоту.
                        .OrderBy(node => node.Status == NodeStatus.Working ? 0 : 1)
                        .ThenByDescending(node => serviceScores is not null &&
                                                  serviceScores.TryGetValue(
                                                      node.SourceName ?? string.Empty, out var score)
                            ? score
                            : 0)
                        .ThenBy(node => node.CheckedAtUtc ?? DateTime.MinValue)
                        .Take(batchSize)
                        .ToList();
                }
                else
                {
                    candidates = _manager.LoadedNodes
                        .Where(node => NeedsCheck(node, recheckCutoff))
                        .Take(batchSize)
                        .ToList();
                }

                if (candidates.Count == 0)
                {
                    break;
                }

                var mode = hidden
                    ? (userIdle ? "скрытое ядро, простой" : "скрытое ядро, фон") +
                      (bypassActive ? ", обход включён" : string.Empty)
                    : "через работающий туннель";
                var quotaNote = needQuota
                    ? $", свежих {FreshWorkingCount()} из {NodeLimits.MinWorkingNodes}" +
                      (missing.Count > 0 ? $", нет по {NodeLimits.MinWorkingPerService} для: {string.Join(", ", missing.Select(ServiceLabel))}" : string.Empty) +
                      (focusName is not null ? $", ищу под {focusName}" : string.Empty)
                    : string.Empty;
                AppendLog($"Фон: узлов {candidates.Count} ({mode}{quotaNote})");
                // В шапке — только суть: что идёт проверка и сколько узлов.
                // Разбивка по сервисам и квоты остаются в журнале.
                ShowBackgroundDetail($"Проверяю узлы: {candidates.Count} ({mode})");
                void OnTested(ProxyNode node) => Dispatcher.BeginInvoke(() => ApplyTestedNode(node));
                var progress = new Progress<string>(line =>
                {
                    AppLog.Write("probe", line);
                    ShowBackgroundDetail(line);
                });

                var summary = hidden
                    ? await _probe.TestAsync(
                        candidates, _settings, progress, OnTested, token,
                        concurrency: bypassActive ? 1 : (userIdle ? 4 : 2), timeoutMs: 3000,
                        waitForGate: false)
                    : await _manager.TestLoadedNodesAsync(
                        candidates, progress, OnTested, token, 2, 2500,
                        "https://www.youtube.com/generate_204");

                if (summary.Total == 0)
                {
                    AppendLog("Фон: пробное ядро занято, такт пропущен");
                    break;
                }

                await RepartitionAndSaveAsync();
                AppendLog($"Фон: узлов проверено {summary.Total}, рабочих {summary.Working}, " +
                          $"лучший открывает доменов {summary.BestDomains}");
                ShowBackgroundDetail($"Фон: проверено {summary.Total} узлов, свежих рабочих " +
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

            _lastIdleTestUtc = DateTime.UtcNow;
        }
        catch (OperationCanceledException)
        {
            await RepartitionAndSaveAsync();
            AppendLog("Фон: проверка остановлена");
        }
        catch (Exception exception)
        {
            // Если сработал откат (ни один узел не открылся), отметки могли разъехаться с таблицей.
            await RepartitionAndSaveAsync();
            AppLog.Write("probe", "Фоновая проверка узлов не удалась", exception);
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
    /// Узел проверен: рабочий переносим на подвкладку «Рабочие», не открывший
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

        if (node.Status == NodeStatus.Dead)
        {
            DropNode(row);
            return;
        }

        row.Refresh();

        var target = node.Status == NodeStatus.Working ? _workingRows : _rows;
        if (_rowOwner.TryGetValue(node.Id, out var current) && !ReferenceEquals(current, target))
        {
            current.Remove(row);
            target.Add(row);
            _rowOwner[node.Id] = target;
        }

        UpdateTestedSummary();
    }

    /// <summary>Удаляет строку из таблицы: нерабочие узлы не держим в списке.</summary>
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

    /// <summary>Счётчики в заголовках обновляем не чаще 4 раз в секунду: иначе проверка 500 узлов тормозит UI.</summary>
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

    /// <summary>Проверяем сначала непроверенные и давно проверенные узлы.</summary>
    /// <summary>
    /// Партия для кнопки «Партия 500»: ровно <see cref="TestBatchSize"/> узлов, независимо от
    /// лимита конфига. Лимит «узлов в конфиге» ограничивает только список для sing-box.
    /// </summary>
    private List<ProxyNode> SelectBatch(IEnumerable<ProxyNode> nodes)
    {
        return nodes
            .OrderBy(n => n.DelayMs is not null)
            .ThenBy(n => n.CheckedAtUtc ?? DateTime.MinValue)
            .Take(TestBatchSize)
            .ToList();
    }

    /// <summary>
    /// Сколько узлов с доступом к сервису можно взять прямо сейчас: только свежая
    /// проверка. Устаревшее «рабочий» из кэша не считается — иначе квота выглядела бы
    /// набранной, а кнопка не находила узел.
    /// </summary>
    private int CountVerifiedFor(string service)
    {
        var now = DateTime.UtcNow;
        return _workingRows.Count(row =>
            NodeAvailability.IsUsable(
                row.Node, NodeLimits.WorkingRecheckMinutes, _settings.ConnectOnlyMine, now) &&
            NodeAvailability.OpenedFor(row.Node, service));
    }

    /// <summary>Рабочие узлы со свежей проверкой — те же, что видят счётчики в шапке.</summary>
    private int FreshWorkingCount()
    {
        var now = DateTime.UtcNow;
        return _workingRows.Count(row => NodeAvailability.IsUsable(
            row.Node, NodeLimits.WorkingRecheckMinutes, _settings.ConnectOnlyMine, now));
    }

    /// <summary>
    /// Ход фоновой проверки в строке состояния: пользователь должен видеть, что узлы
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
    /// Доля узлов источника, которые открывают нужный сервис (0–1000). Проверяем
    /// сначала источники, которые уже давали доступ к этому сервису.
    /// </summary>
    private Dictionary<string, int> BuildServiceSourceScores(string service)
    {
        var scores = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var group in _workingRows.GroupBy(
                     row => row.Node.SourceName ?? string.Empty,
                     StringComparer.OrdinalIgnoreCase))
        {
            var total = group.Count();
            scores[group.Key] = total == 0
                ? 0
                : group.Count(row => ServiceOpened(row.Node, service)) * 1000 / total;
        }

        return scores;
    }

    /// <summary>
    /// Сервисы, для которых проверенных рабочих узлов меньше
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
