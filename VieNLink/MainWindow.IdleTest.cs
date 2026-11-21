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

        // В простое проходы реже и крупнее; при работе — чаще и маленькими партиями,
        // чтобы проверка не конкурировала с трафиком пользователя.
        var interval = userIdle
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
        var batch = userIdle ? baseBatch : Math.Clamp(baseBatch / 4, 5, 30);
        if (_busy)
        {
            // Пользователь что-то делает в приложении — совсем маленькая партия.
            batch = Math.Min(batch, 10);
        }

        // Пока работает zapret/свой обход, видео и так идёт через WinDivert:
        // проверяем совсем небольшими партиями, чтобы не отнимать канал.
        var bypassActive = _zapret.IsRunning || _desync.IsRunning;
        if (bypassActive)
        {
            batch = Math.Min(batch, 5);
        }

        var cutoff = DateTime.UtcNow.AddHours(-Math.Clamp(_settings.IdleRecheckHours, 1, 168));
        static bool NeedsCheck(ProxyNode node, DateTime threshold) =>
            node.CheckedAtUtc is null || node.CheckedAtUtc < threshold;

        List<ProxyNode> candidates;
        if (hidden)
        {
            candidates = AllRows
                .Select(row => row.Node)
                .Where(node => node.Status == NodeStatus.Unknown ||
                               (node.Status == NodeStatus.Working && NeedsCheck(node, cutoff)))
                .Distinct()
                .OrderBy(node => node.Status == NodeStatus.Working)
                .ThenBy(node => node.CheckedAtUtc ?? DateTime.MinValue)
                .Take(batch)
                .ToList();
        }
        else
        {
            candidates = _manager.LoadedNodes
                .Where(node => NeedsCheck(node, cutoff))
                .Take(batch)
                .ToList();
        }

        if (candidates.Count == 0)
        {
            _lastIdleTestUtc = DateTime.UtcNow;
            return;
        }

        _idleTesting = true;
        _idleCts = new CancellationTokenSource();
        var token = _idleCts.Token;
        try
        {
            var mode = hidden
                ? (userIdle ? "скрытое ядро, простой" : "скрытое ядро, фон") +
                  (bypassActive ? ", обход включён" : string.Empty)
                : "через работающий туннель";
            AppendLog($"Фон: узлов {candidates.Count} ({mode})");
            void OnTested(ProxyNode node) => Dispatcher.BeginInvoke(() => ApplyTestedNode(node));
            var progress = new Progress<string>(line => AppLog.Write("probe", line));

            if (candidates.Count > 0)
            {
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
                }
                else
                {
                    await RepartitionAndSaveAsync();
                    AppendLog($"Фон: узлов проверено {summary.Total}, рабочих {summary.Working}, " +
                              $"лучший открывает доменов {summary.BestDomains}");
                }
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

}
