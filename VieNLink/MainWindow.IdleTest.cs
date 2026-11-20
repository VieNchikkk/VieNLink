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
    // ---------- проверка узлов в простое ----------

    /// <summary>Проверка в простое: только когда окно свёрнуто и компьютер не используется.</summary>
    private async void OnIdleTick(object? sender, EventArgs e)
    {
        if (_idleTesting)
        {
            // Пользователь вернулся за компьютер — вежливо останавливаем проход.
            if (GetSystemIdleTime() < TimeSpan.FromMinutes(_settings.IdleTestMinutes))
            {
                _idleCts?.Cancel();
            }

            return;
        }

        if (!_settings.IdleTestEnabled || _busy)
        {
            return;
        }

        if (IsVisible && WindowState != WindowState.Minimized)
        {
            return;
        }

        if (GetSystemIdleTime() < TimeSpan.FromMinutes(Math.Max(1, _settings.IdleTestMinutes)))
        {
            return;
        }

        if (_lastIdleTestUtc is { } last &&
            DateTime.UtcNow - last < TimeSpan.FromMinutes(Math.Max(1, _settings.IdleTestIntervalMinutes)))
        {
            return;
        }

        await RunIdleTestAsync();
    }

    private async Task RunIdleTestAsync()
    {
        var connected = _manager.IsConnected;

        // Пока работает обход DPI, пробы через WinDivert искажаются — в простое не мешаем.
        if (!connected && (_zapret.IsRunning || _desync.IsRunning || _tunBypass.IsRunning))
        {
            return;
        }

        // Без подключения TUN-ядро перехватило бы трафик всех программ — в простое так нельзя.
        if (!connected && _settings.TunnelMode == TunnelMode.Tun)
        {
            return;
        }

        var batch = Math.Clamp(_settings.IdleTestBatch, 5, 500);
        var cutoff = DateTime.UtcNow.AddHours(-Math.Clamp(_settings.IdleRecheckHours, 1, 168));
        static bool NeedsCheck(ProxyNode node, DateTime threshold) =>
            node.CheckedAtUtc is null || node.CheckedAtUtc < threshold;

        var candidates = connected
            ? _manager.LoadedNodes
                .Where(node => NeedsCheck(node, cutoff))
                .Take(batch)
                .ToList()
            : AllRows
                .Select(row => row.Node)
                .Where(node => node.Status == NodeStatus.Unknown ||
                               (node.Status == NodeStatus.Working && NeedsCheck(node, cutoff)))
                .Distinct()
                .Take(batch)
                .ToList();
        var mtproto = _mtprotoRows
            .Select(row => row.Proxy)
            .Where(proxy => proxy.CheckedAtUtc is null || proxy.CheckedAtUtc < cutoff)
            .Take(batch)
            .ToList();
        if (candidates.Count == 0 && mtproto.Count == 0)
        {
            _lastIdleTestUtc = DateTime.UtcNow;
            return;
        }

        _idleTesting = true;
        _idleCts = new CancellationTokenSource();
        var token = _idleCts.Token;
        try
        {
            AppendLog($"Простой: узлов {candidates.Count}, MTProto {mtproto.Count} " +
                      (connected ? "(через работающий туннель)" : "(без подключения)"));
            void OnTested(ProxyNode node) => Dispatcher.BeginInvoke(() => ApplyTestedNode(node));
            var progress = new Progress<string>(line => AppLog.Write("idle", line));

            if (candidates.Count > 0)
            {
                var summary = connected
                    ? await _manager.TestLoadedNodesAsync(
                        candidates, progress, OnTested, token, 2, 2500,
                        "https://www.youtube.com/generate_204")
                    : await _manager.TestNodesAsync(
                        candidates, progress, OnTested, token, 2, 2500,
                        "https://www.youtube.com/generate_204");

                // ApplyTestedNode уже обновил строки, а RepartitionAndSaveAsync пересоберёт списки.
                await RepartitionAndSaveAsync();
                AppendLog($"Простой: узлов проверено {summary.Total}, рабочих {summary.Working}");            }

            if (mtproto.Count > 0)
            {
                await MtProtoService.TestAsync(
                    mtproto,
                    progress,
                    proxy => Dispatcher.BeginInvoke(() => ApplyTestedMtProto(proxy)),
                    token,
                    concurrency: 2,
                    timeoutMs: 2500);
                MtProtoService.SaveCache(_mtprotoRows.Concat(_mtprotoDeadRows).Select(row => row.Proxy));
                AppendLog($"Простой: MTProto проверено {mtproto.Count}, нерабочих {_mtprotoDeadRows.Count}");
            }

            _lastIdleTestUtc = DateTime.UtcNow;
        }
        catch (OperationCanceledException)
        {
            await RepartitionAndSaveAsync();
            AppendLog("Простой: проверка остановлена (пользователь вернулся)");
        }
        catch (Exception exception)
        {
            // Если сработал откат (ни один узел не открылся), отметки могли разъехаться с таблицей.
            await RepartitionAndSaveAsync();
            AppLog.Write("idle", "Проверка узлов в простое не удалась", exception);
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
