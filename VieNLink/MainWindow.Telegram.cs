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
    // ---------- Telegram (MTProto) ----------

    /// <summary>
    /// Автономный сбор MTProto через RSS: t.me в РФ часто недоступен, а RSS-зеркала
    /// читают канал на своей стороне и не требуют прокси. Запускается по таймеру.
    /// </summary>
    private async Task AutoCollectMtProtoAsync(bool force = false)
    {
        if (_closing || _mtprotoCollecting || _busy || _settings.AutomationPaused ||
            !_settings.AutoMtProto)
        {
            return;
        }

        if (!_sources.Any(source => source.Enabled && SourceKinds.IsProxy(source)))
        {
            return;
        }

        var now = DateTime.UtcNow;
        var collectMinutes = _mtprotoRows.Count(row => row.Proxy.Status == MtProtoStatus.Working) <
                             MtProtoSchedule.MinWorking
            ? MtProtoSchedule.CollectMinutesWhenFew
            : MtProtoSchedule.CollectMinutes;
        if (!force &&
            _lastMtProtoCollectUtc is { } last &&
            now - last < TimeSpan.FromMinutes(collectMinutes))
        {
            return;
        }

        _mtprotoCollecting = true;
        _mtprotoCollectCts = new CancellationTokenSource();
        var token = _mtprotoCollectCts.Token;
        try
        {
            _lastMtProtoCollectUtc = now;
            var found = await CollectMtProtoCoreAsync(token, forceRss: true);
            AppendLog($"Автосбор MTProto через RSS: новых {found}, всего {_mtprotoRows.Count + _mtprotoDeadRows.Count}");
        }
        catch (OperationCanceledException)
        {
            // Приложение закрывается или началась ручная операция.
        }
        catch (Exception exception)
        {
            AppLog.Write("mtproto", "Автосбор MTProto не удался", exception);
        }
        finally
        {
            _mtprotoCollecting = false;
            _mtprotoCollectCts?.Dispose();
            _mtprotoCollectCts = null;
        }
    }

    /// <summary>
    /// Первичное наполнение данных: собираем каналы MTProto и сразу проверяем большую
    /// партию, чтобы на новом устройстве список не оставался пустым и «непроверенным».
    /// </summary>
    private async Task CollectAndCheckMtProtoOnStartupAsync()
    {
        try
        {
            await AutoCollectMtProtoAsync(force: true);
            await AutoCheckMtProtoAsync(batchScale: _mtprotoRows.Count > 0 ? 3 : 1);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            AppLog.Write("mtproto", "Стартовый сбор MTProto не удался", exception);
        }
    }

    /// <summary>
    /// Автопроверка MTProto: рабочие — часто, нерабочие — изредка. За проход
    /// проверяется небольшая партия, чтобы не отнимать канал у пользователя.
    /// <paramref name="batchScale"/> увеличивает партию при первом запуске.
    /// </summary>
    private async Task AutoCheckMtProtoAsync(int batchScale = 1)
    {
        // Проверка идёт настоящим рукопожатием с Telegram, но партии небольшие,
        // чтобы не отнимать канал у видео.
        if (_closing || _mtprotoChecking || _settings.AutomationPaused ||
            !_settings.AutoMtProto)
        {
            return;
        }

        _mtprotoChecking = true;
        _mtprotoCheckCts = new CancellationTokenSource();
        var token = _mtprotoCheckCts.Token;
        try
        {
            // Пока рабочих прокси меньше пяти, проверяем несколько партий подряд,
            // а не ждём следующего такта.
            for (var round = 1; round <= 3; round++)
            {
                var workingCount = _mtprotoRows.Count(row => row.Proxy.Status == MtProtoStatus.Working);
                if (round > 1 && workingCount >= MtProtoSchedule.MinWorking)
                {
                    break;
                }

                var now = DateTime.UtcNow;
                var workingCutoff = now.AddMinutes(-MtProtoSchedule.WorkingRecheckMinutes);
                var deadCutoff = now.AddHours(-MtProtoSchedule.DeadRecheckHours);
                var scale = workingCount < MtProtoSchedule.MinWorking
                    ? Math.Max(batchScale, 3)
                    : Math.Max(1, batchScale);

                var working = _mtprotoRows
                    .Select(row => row.Proxy)
                    .Where(proxy => proxy.CheckedAtUtc is null || proxy.CheckedAtUtc < workingCutoff)
                    .OrderBy(proxy => proxy.CheckedAtUtc ?? DateTime.MinValue)
                    .Take(MtProtoSchedule.WorkingBatch * scale)
                    .ToList();

                var dead = _mtprotoDeadRows
                    .Select(row => row.Proxy)
                    .Where(proxy => proxy.CheckedAtUtc is null ||
                                    proxy.CheckedAtUtc < deadCutoff ||
                                    // Недавно упавший рабочий проверяем быстро: возможно, это была заминка.
                                    (proxy.FailCount <= 1 &&
                                     proxy.CheckedAtUtc < now.AddMinutes(-MtProtoSchedule.QuickRecheckMinutes)))
                    .OrderBy(proxy => proxy.CheckedAtUtc ?? DateTime.MinValue)
                    .Take(MtProtoSchedule.DeadBatch * scale)
                    .ToList();

                var batch = working.Concat(dead).ToList();
                if (batch.Count == 0)
                {
                    break;
                }

                AppendLog($"MTProto: автопроверка {batch.Count} (рабочих {working.Count}, нерабочих {dead.Count})");
                await MtProtoService.TestAsync(
                    batch,
                    null,
                    proxy => Dispatcher.BeginInvoke(() => ApplyTestedMtProto(proxy)),
                    token,
                    concurrency: 6,
                    timeoutMs: MtProtoProbe.DefaultTimeoutMs);
                RepartitionMtProtoAndSave();

                if (_mtprotoRows.Count(row => row.Proxy.Status == MtProtoStatus.Working) >=
                    MtProtoSchedule.MinWorking)
                {
                    break;
                }
            }
        }
        catch (OperationCanceledException)
        {
            RepartitionMtProtoAndSave();
        }
        catch (Exception exception)
        {
            AppLog.Write("mtproto", "Автопроверка MTProto не удалась", exception);
        }
        finally
        {
            _mtprotoChecking = false;
            _mtprotoCheckCts?.Dispose();
            _mtprotoCheckCts = null;
        }
    }

    /// <summary>Удаляет нерабочие MTProto, пролежавшие больше суток.</summary>
    private int PruneExpiredMtProto()
    {
        var all = _mtprotoRows.Concat(_mtprotoDeadRows).Select(row => row.Proxy).ToList();
        var removed = all.RemoveAll(proxy => MtProtoRetention.IsExpired(proxy, DateTime.UtcNow));
        if (removed == 0)
        {
            return 0;
        }

        RebuildMtProtoRows(all);
        MtProtoService.SaveCache(all);
        AppendLog($"MTProto: удалено {removed} нерабочих старше {MtProtoRetention.DeadHours} ч");
        return removed;
    }

    /// <summary>
    /// Читает включённые каналы Telegram. Вызывается автосбором и после подключения.
    /// Возвращает число найденных ссылок.
    /// </summary>
    private async Task<int> CollectMtProtoCoreAsync(CancellationToken cancellationToken, bool forceRss = false)
    {
        var sources = _sources
            .Where(s => s.Enabled && SourceKinds.IsProxy(s))
            .OrderBy(s => s.Kind == SourceKind.Telegram ? 1 : 0)
            .ToList();
        if (sources.Count == 0)
        {
            AppendLog("Нет включённых каналов/сайтов MTProto — включите их на вкладке «Источники»");
            return 0;
        }

        var found = new List<MtProtoProxy>();
        AppLog.Write("mtproto",
            $"Источники MTProto: {sources.Count} " +
            $"(каналов {sources.Count(s => s.Kind == SourceKind.Telegram)}, " +
            $"сайтов {sources.Count(s => s.Kind == SourceKind.MtProto)})");
        foreach (var source in sources)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var list = await MtProtoService.FetchAsync(source, cancellationToken, forceRss);
                source.LastCheckedUtc = DateTime.UtcNow;
                source.LastError = null;
                found.AddRange(list);
                AppendLog($"{source.Name}: MTProto {list.Count}");
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                source.LastError = exception.Message;
                AppendLog($"{source.Name}: ошибка — {exception.Message}");
            }

            _sourceRows.FirstOrDefault(r => ReferenceEquals(r.Source, source))?.Refresh();
        }

        MergeMtProto(found);
        NodeSourceService.SaveSources(_sources);
        return found.Count;
    }

    private void MergeMtProto(IEnumerable<MtProtoProxy> proxies)
    {
        var incoming = proxies.ToList();
        if (incoming.Count == 0)
        {
            return;
        }

        var map = _mtprotoRows.Concat(_mtprotoDeadRows)
            .ToDictionary(r => r.Proxy.Id, r => r.Proxy, StringComparer.OrdinalIgnoreCase);
        foreach (var proxy in incoming)
        {
            if (map.TryGetValue(proxy.Id, out var existing))
            {
                proxy.DelayMs ??= existing.DelayMs;
                proxy.CheckedAtUtc ??= existing.CheckedAtUtc;
                proxy.Status = existing.Status;
                proxy.DeadAtUtc = existing.DeadAtUtc;
            }

            map[proxy.Id] = proxy;
        }

        RebuildMtProtoRows(map.Values);
        MtProtoService.SaveCache(map.Values);
    }

    /// <summary>Раскладывает прокси по подвкладкам «Собранные» и «Нерабочие».</summary>
    private void RebuildMtProtoRows(IEnumerable<MtProtoProxy> proxies)
    {
        var all = proxies.ToList();
        _mtprotoRows.Clear();
        _mtprotoDeadRows.Clear();

        foreach (var proxy in all
                     .Where(p => p.Status != MtProtoStatus.Dead)
                     .OrderByDescending(p => p.DelayMs is not null)
                     .ThenBy(p => p.DelayMs ?? int.MaxValue)
                     .ThenBy(p => p.Server, StringComparer.OrdinalIgnoreCase))
        {
            _mtprotoRows.Add(new MtProtoRow(proxy));
        }

        foreach (var proxy in all
                     .Where(p => p.Status == MtProtoStatus.Dead)
                     .OrderByDescending(p => p.DeadAtUtc ?? DateTime.MinValue)
                     .ThenBy(p => p.Server, StringComparer.OrdinalIgnoreCase))
        {
            _mtprotoDeadRows.Add(new MtProtoRow(proxy));
        }

        UpdateMtProtoSummary();
    }

    private void RepartitionMtProtoAndSave()
    {
        var all = _mtprotoRows.Concat(_mtprotoDeadRows).Select(r => r.Proxy).ToList();
        RebuildMtProtoRows(all);
        MtProtoService.SaveCache(all);
    }

    /// <summary>Прокси проверен: обновляем строку и сразу переносим её на нужную подвкладку.</summary>
    private void ApplyTestedMtProto(MtProtoProxy proxy)
    {
        var row = _mtprotoRows.Concat(_mtprotoDeadRows).FirstOrDefault(r => r.Proxy.Id == proxy.Id);
        if (row is null)
        {
            return;
        }

        row.Refresh();

        var current = _mtprotoRows.Contains(row)
            ? _mtprotoRows
            : _mtprotoDeadRows.Contains(row)
                ? _mtprotoDeadRows
                : null;
        var target = proxy.Status == MtProtoStatus.Dead ? _mtprotoDeadRows : _mtprotoRows;
        if (current is not null && !ReferenceEquals(current, target))
        {
            current.Remove(row);
            target.Add(row);
        }

        UpdateMtProtoSummary();
    }

    private void UpdateMtProtoSummary()
    {
        if (MtProtoSummary is null)
        {
            return;
        }

        var alive = _mtprotoRows.Count(r => r.Proxy.Status == MtProtoStatus.Working);
        MtProtoWorkingTab.Header = $"Собранные ({_mtprotoRows.Count})";
        MtProtoDeadTab.Header = $"Нерабочие ({_mtprotoDeadRows.Count})";
        MtProtoSummary.Text =
            $"Рабочих {alive} · ждут проверки {_mtprotoRows.Count - alive} · нерабочих {_mtprotoDeadRows.Count}";

        if (MtProtoEmpty is not null)
        {
            MtProtoEmpty.Visibility = _mtprotoRows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        if (MtProtoDeadSummary is not null)
        {
            MtProtoDeadSummary.Text = $"Нерабочих {_mtprotoDeadRows.Count} · удаляются через {MtProtoRetention.DeadHours} ч";
        }

        if (MtProtoDeadEmpty is not null)
        {
            MtProtoDeadEmpty.Visibility = _mtprotoDeadRows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        UpdateCacheSummary();
    }

    private async void OnMtProtoTestClick(object sender, RoutedEventArgs e)
    {
        _mtprotoCheckCts?.Cancel();

        var dead = ReferenceEquals(MtProtoTabs.SelectedItem, MtProtoDeadTab);
        var rows = dead ? _mtprotoDeadRows : _mtprotoRows;
        if (rows.Count == 0)
        {
            MessageBox.Show(
                this,
                dead ? "Нерабочих прокси нет." : "Прокси ещё не собраны — приложение собирает их автоматически.",
                "VieNLink",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        if (!BeginOperation("проверку MTProto"))
        {
            return;
        }

        try
        {
            SetStatus(dead ? "Проверяю нерабочие MTProto" : "Проверяю MTProto", "Рукопожатие с прокси и запрос к серверам Telegram…");
            var proxies = rows.Select(r => r.Proxy).ToList();
            await MtProtoService.TestAsync(
                proxies,
                new Progress<string>(line => StatusDetail.Text = line),
                proxy => Dispatcher.BeginInvoke(() => ApplyTestedMtProto(proxy)),
                _operation!.Token);

            var alive = proxies.Count(p => p.Status == MtProtoStatus.Working);
            StatusDetail.Text = dead
                ? $"Заработало {alive} из {proxies.Count} — они вернулись в «Собранные»"
                : $"Работают {alive} из {proxies.Count} (Telegram ответил); остальные перенесены в «Нерабочие»";
        }
        catch (OperationCanceledException)
        {
            SetStatus("Проверка MTProto отменена", string.Empty);
        }
        catch (Exception exception)
        {
            ReportError("Не удалось проверить MTProto-прокси", exception);
        }
        finally
        {
            // Даже при отмене проверенные прокси должны оказаться на своей подвкладке.
            RepartitionMtProtoAndSave();
            EndOperation();
            UpdateConnectionUi();
        }
    }

    /// <summary>Проверка соединения с одним выбранным прокси на открытой подвкладке.</summary>
    private async void OnMtProtoTestSelectedClick(object sender, RoutedEventArgs e)
    {
        _mtprotoCheckCts?.Cancel();

        if (SelectedMtProto() is not { } proxy)
        {
            MessageBox.Show(this, "Выберите прокси в таблице.", "VieNLink", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (!BeginOperation("проверку MTProto"))
        {
            return;
        }

        try
        {
            SetStatus("Проверяю MTProto", $"{proxy.Server}:{proxy.Port}…");
            await MtProtoService.TestAsync(
                new[] { proxy },
                null,
                tested => Dispatcher.BeginInvoke(() => ApplyTestedMtProto(tested)),
                _operation!.Token);

            StatusDetail.Text = proxy.Status == MtProtoStatus.Working
                ? $"Работает: {proxy.Server}:{proxy.Port} — Telegram ответил"
                : $"Не работает: {proxy.Server}:{proxy.Port} — перенесён в «Нерабочие»";
        }
        catch (OperationCanceledException)
        {
            SetStatus("Проверка MTProto отменена", string.Empty);
        }
        catch (Exception exception)
        {
            ReportError("Не удалось проверить MTProto-прокси", exception);
        }
        finally
        {
            RepartitionMtProtoAndSave();
            EndOperation();
            UpdateConnectionUi();
        }
    }

    private MtProtoProxy? SelectedMtProto()
    {
        if (ReferenceEquals(MtProtoTabs.SelectedItem, MtProtoDeadTab))
        {
            return (MtProtoDeadGrid.SelectedItem as MtProtoRow)?.Proxy;
        }

        return (MtProtoGrid.SelectedItem as MtProtoRow)?.Proxy;
    }

    private void OnMtProtoOpenClick(object sender, RoutedEventArgs e) => OpenInTelegram(SelectedMtProto());

    private void OnMtProtoDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e) => OpenInTelegram(SelectedMtProto());

    private void OpenInTelegram(MtProtoProxy? proxy)
    {
        if (proxy is null)
        {
            MessageBox.Show(this, "Выберите прокси в таблице.", "VieNLink", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo(proxy.Link) { UseShellExecute = true });
            FooterText.Text = "Telegram должен предложить добавить прокси";
            AppendLog($"Открыта ссылка: {proxy.Server}:{proxy.Port}");
        }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            AppLog.Write("mtproto", "Не удалось открыть tg://", exception);
            Clipboard.SetText(proxy.Link);
            MessageBox.Show(
                this,
                "Не удалось открыть Telegram — ссылка скопирована в буфер обмена. Вставьте её в чат Telegram.",
                "VieNLink",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }
    }

    private void OnMtProtoCopyClick(object sender, RoutedEventArgs e)
    {
        if (SelectedMtProto() is not { } proxy)
        {
            MessageBox.Show(this, "Выберите прокси в таблице.", "VieNLink", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        Clipboard.SetText(proxy.Link);
        FooterText.Text = "Ссылка tg://proxy скопирована";
    }

    /// <summary>Ручное добавление серверов: вставляются ссылки, источник попадает в «Мои подписки».</summary>
    private async void OnAddNodeManuallyClick(object sender, RoutedEventArgs e)
    {
        var text = TextInputWindow.Show(
            this,
            "Добавить серверы вручную",
            "Вставьте ссылки на серверы — по одной в строке: vless://, vmess://, trojan://, ss://, hysteria2://.\n" +
            "Серверы попадут в группу «Мои подписки» и будут пробоваться первыми.",
            string.Empty);
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        const string manualName = "Мои серверы (вручную)";
        var source = _sources.FirstOrDefault(s => s.Kind == SourceKind.SingleLink && s.Name == manualName);
        if (source is null)
        {
            source = new SourceDefinition
            {
                Name = manualName,
                Url = text.Trim(),
                Kind = SourceKind.SingleLink,
                IsMine = true,
            };
            _sources.Add(source);
            _sourceRows.Add(new SourceRow(source));
        }
        else
        {
            var existing = source.Url.TrimEnd();
            var added = text.Trim();
            source.Url = added.Length == 0 || existing.Contains(added, StringComparison.Ordinal)
                ? existing
                : existing + "\n" + added;
            source.IsMine = true;
        }

        source.LastError = null;
        NodeSourceService.SaveSources(_sources);
        await LoadSingleSourceAsync(source);
    }

}
