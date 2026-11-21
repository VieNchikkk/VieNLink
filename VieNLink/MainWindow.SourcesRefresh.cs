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
    // ---------- обновление списков ----------

    private async void OnRefreshClick(object sender, RoutedEventArgs e)
    {
        if (AtNodeLimit)
        {
            NotifyNodeLimit();
            SetStatus("Достигнут лимит узлов",
                $"В списке {NodeCount} из {_settings.MaxTotalNodes}. Проверьте накопленные: «Узлы» → «Все узлы» — " +
                "нерабочие удалятся, и обновление разблокируется");
            return;
        }

        if (!BeginOperation("обновление списков"))
        {
            return;
        }

        try
        {
            var count = await RefreshSourcesCoreAsync(_settings.TestAfterRefresh, testLimit: 0);
            SetStatus("Списки обновлены", $"Узлов: {count}, MTProto: {_mtprotoRows.Count}");
            _settings.LastAutoRefreshUtc = DateTime.UtcNow;
            SaveSettings();
        }
        catch (OperationCanceledException)
        {
            SetStatus("Обновление отменено", "Нажмите «Обновить списки», чтобы повторить");
        }
        catch (Exception exception)
        {
            ReportError("Не удалось обновить списки", exception);
        }
        finally
        {
            EndOperation();
            UpdateConnectionUi();
        }
    }

    /// <summary>Общая загрузка всех включённых источников: используется кнопкой, автозапуском и таймером.</summary>
    private async Task<int> RefreshSourcesCoreAsync(bool testAfter, int testLimit)
    {
        SetStatus("Обновляю списки", "Загрузка источников…");
        var progress = new Progress<string>(line =>
        {
            StatusDetail.Text = line;
            AppendLog(line);
        });

        var collected = await NodeSourceService.CollectAsync(
            _sources,
            progress,
            _operation!.Token,
            outcome => _sourceRows.FirstOrDefault(r => ReferenceEquals(r.Source, outcome.Source))?.Refresh());

        var nodes = collected.Nodes;
        MergeMtProto(collected.MtProto);

        // Переносим результаты проверок: свежий разбор источников их не знает.
        // Ключ — стабильный Id (тот же outbound). Один сервер:порт может держать
        // разные аккаунты: успех одного не доказывает доступ другого.
        var oldById = new Dictionary<string, ProxyNode>(StringComparer.OrdinalIgnoreCase);
        var oldByEndpoint = new Dictionary<string, ProxyNode>(StringComparer.OrdinalIgnoreCase);
        foreach (var old in NodeDedup.Collapse(AllRows.Select(r => r.Node)))
        {
            oldById[old.Id] = old;
            oldByEndpoint[old.EndpointKey] = old;
        }

        foreach (var node in nodes)
        {
            if (oldById.TryGetValue(node.Id, out var old))
            {
                node.DelayMs = old.DelayMs;
                node.CheckedAtUtc = old.CheckedAtUtc;
                node.TriedAtUtc = old.TriedAtUtc;
                node.YoutubeOk = old.YoutubeOk;
                node.TelegramOk = old.TelegramOk;
                node.UniversalOk = old.UniversalOk;
                node.DiscordOk = old.DiscordOk;
                node.OpenDomains = old.OpenDomains;
                node.FailCount = old.FailCount;
                node.SpeedMbps = old.SpeedMbps;
                node.Status = old.Status;
                node.IsMine |= old.IsMine;
            }
            else if (oldByEndpoint.TryGetValue(node.EndpointKey, out old))
            {
                // Тот же адрес, другой аккаунт: только метка «моя»,
                // проверки остаются за этим узлом.
                node.IsMine |= old.IsMine;
            }
        }

        _sources = _sources.OrderBy(s => s.Name).ToList();
        ReplaceNodes(nodes);
        NodeSourceService.SaveSources(_sources);

        // ReplaceNodes применил общий лимит: дальше работаем с тем, что реально в списке.
        var kept = AllRows.Select(r => r.Node).ToList();
        // Разбор крупных списков оставляет десятки мегабайт мусора в LOH, а сам кэш
        // пишется в фоне: окно не должно ждать ни диск, ни сборку мусора.
        await Task.Run(() =>
        {
            NodeSourceService.SaveCache(kept);
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: false, compacting: true);
        });

        var trimmedNote = nodes.Count > kept.Count ? $" (лимит {_settings.MaxTotalNodes}, отброшено {nodes.Count - kept.Count})" : string.Empty;
        var nodeSourceCount = _sources.Count(s => s.Enabled && !SourceKinds.IsProxy(s));
        var summary = $"Загружено {kept.Count} узлов и {_mtprotoRows.Count} MTProto из {nodeSourceCount} источников узлов{trimmedNote}";
        StatusDetail.Text = summary;
        AppendLog(summary);

        if (testAfter && kept.Count > 0)
        {
            var batch = SelectBatch(kept);
            if (testLimit > 0 && batch.Count > testLimit)
            {
                batch = batch.Take(testLimit).ToList();
            }

            await RunTestAsync(batch, "Автопроверка");
        }

        return nodes.Count;
    }

    private async void OnTestClick(object sender, RoutedEventArgs e)
    {
        if (!BeginOperation("проверку партии узлов"))
        {
            return;
        }

        try
        {
            var candidates = _rows.Select(r => r.Node).ToList();
            var batch = SelectBatch(candidates);
            if (batch.Count == 0)
            {
                SetStatus("Нет узлов", "Сначала обновите списки");
                return;
            }

            await RunTestAsync(batch, "Проверка узлов");
        }
        catch (OperationCanceledException)
        {
            SetStatus("Проверка отменена", "Результаты уже проверенных узлов сохранены");
        }
        catch (Exception exception)
        {
            ReportError("Не удалось проверить узлы", exception);
        }
        finally
        {
            EndOperation();
            UpdateConnectionUi();
        }
    }

    private async void OnTestAllClick(object sender, RoutedEventArgs e)
    {
        if (!BeginOperation("проверку всех узлов"))
        {
            return;
        }

        try
        {
            var all = _rows
                .Concat(_workingRows)
                .Select(r => r.Node)
                .OrderBy(n => n.DelayMs is not null)
                .ThenBy(n => n.CheckedAtUtc ?? DateTime.MinValue)
                .ToList();
            if (all.Count == 0)
            {
                SetStatus("Нет узлов", "Сначала обновите списки");
                return;
            }

            var batchSize = Math.Max(50, _settings.MaxNodesInConfig);
            var tested = 0;
            for (var index = 0; index < all.Count; index += batchSize)
            {
                _operation!.Token.ThrowIfCancellationRequested();
                var batch = all.Skip(index).Take(batchSize).ToList();
                await RunTestAsync(batch, $"Проверка всех узлов ({tested + batch.Count} из {all.Count})");
                tested += batch.Count;
                var working = AllRows.Count(row => NodeAvailability.IsUsable(
                    row.Node, NodeLimits.WorkingRecheckMinutes, _settings.ConnectOnlyMine, DateTime.UtcNow));
                SetStatus("Проверка узлов", $"Готово {tested} из {all.Count}, рабочих {working}");
            }
        }
        catch (OperationCanceledException)
        {
            SetStatus("Проверка остановлена", "Результаты сохранены");
        }
        catch (Exception exception)
        {
            ReportError("Не удалось проверить узлы", exception);
        }
        finally
        {
            EndOperation();
            UpdateConnectionUi();
        }
    }

    private async void OnTestSelectedClick(object sender, RoutedEventArgs e)
    {
        var node = SelectedNode();
        if (node is null)
        {
            MessageBox.Show(this, "Выберите узел в таблице.", "VieNLink", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (!BeginOperation("проверку узла"))
        {
            return;
        }

        try
        {
            await RunTestAsync(new[] { node }, "Проверка узла");
        }
        catch (OperationCanceledException)
        {
            SetStatus("Проверка узла отменена", string.Empty);
        }
        catch (Exception exception)
        {
            ReportError("Не удалось проверить узел", exception);
        }
        finally
        {
            EndOperation();
            UpdateConnectionUi();
        }
    }

    private async Task RunTestAsync(IReadOnlyList<ProxyNode> nodes, string title)
    {
        var connected = _manager.IsConnected;
        var tunConnected = connected && _settings.TunnelMode == TunnelMode.Tun;

        // При активном TUN трафик скрытого ядра ушёл бы в туннель — проверяем
        // только уже загруженные узлы. В системном прокси основное ядро не трогаем:
        // проверка идёт через скрытое ядро на отдельных портах.
        if (tunConnected)
        {
            var loaded = nodes.Where(node => _manager.IsNodeLoaded(node)).ToList();
            if (loaded.Count == 0)
            {
                SetStatus("Проверка недоступна", "При активном TUN проверяются только загруженные узлы. Отключитесь, чтобы проверить остальные.");
                return;
            }

            nodes = loaded;
        }

        SetStatus(title, $"0 из {nodes.Count}…");
        var progress = new Progress<string>(line => StatusDetail.Text = line);

        // TUN-обход перехватывает весь трафик, включая скрытое ядро: на время ручной
        // проверки снимаем его и возвращаем после. zapret и свой обход DPI не трогаем —
        // они ограничены целевыми доменами и соединения к серверам узлов не затрагивают.
        var tunBypassMode = !connected && _tunBypass.IsRunning ? _tunBypass.Mode : (StreamFragmentMode?)null;
        if (tunBypassMode is not null)
        {
            await _tunBypass.StopAsync();
            UpdateTunBypassUi();
            AppendLog("TUN-обход приостановлен на время проверки узлов");
        }

        // Проверка идёт в фоне, а списки трогаем только в UI-потоке:
        // узел с провалами: рабочий остаётся с отметкой, непроверенный убирается сразу.
        void OnNodeTested(ProxyNode node) => Dispatcher.BeginInvoke(() => ApplyTestedNode(node));

        TestSummary summary;
        try
        {
            summary = tunConnected
                ? await _manager.TestLoadedNodesAsync(
                    nodes, progress, OnNodeTested, _operation!.Token, concurrency: 8, timeoutMs: 3000)
                : await _probe.TestAsync(
                    nodes, _settings, progress, OnNodeTested, _operation!.Token,
                    concurrency: _zapret.IsRunning || _desync.IsRunning ? 3 : 8,
                    timeoutMs: 3000);
        }
        finally
        {
            // Проверку могли отменить или прервать ошибкой — обновляем строки,
            // чтобы в таблице не осталось устаревших отметок.
            foreach (var row in AllRows.ToList())
            {
                row.Refresh();
            }

            await RepartitionAndSaveAsync();

            if (tunBypassMode is { } bypassMode && !_closing)
            {
                try
                {
                    // Тестовое ядро могло остаться запущенным: освобождаем порты и адаптер.
                    await _manager.DisconnectAsync();
                    await _tunBypass.StartAsync(bypassMode, _settings, null, CancellationToken.None);
                    AppendLog("TUN-обход снова включён после проверки");
                }
                catch (Exception exception)
                {
                    AppLog.Write("tun-bypass", "Не удалось вернуть TUN-обход после проверки", exception);
                }

                UpdateTunBypassUi();
            }
        }

        var working = summary.Working;
        // Только итоги последней партии: общие счётчики сервисов уже показаны
        // иконками в шапке, и дублировать их нулями из одной партии нельзя.
        StatusDetail.Text = $"Последняя партия: проверено {summary.Total} · рабочих {working} · " +
                            $"лучший открывает доменов {summary.BestDomains}";
        FooterText.Text = working > 0
            ? $"Рабочих узлов: {working}. Подключение — 127.0.0.1:{_manager.ActiveMixedPort}"
            : "Рабочих узлов не найдено: нерабочие узлы удалены из списка";
        if (working == 0)
        {
            _tray?.ShowBalloon("Узлы не отвечают", "Среди проверенных узлов нет рабочих. Попробуйте обновить списки.", warning: true);
        }
    }

}
