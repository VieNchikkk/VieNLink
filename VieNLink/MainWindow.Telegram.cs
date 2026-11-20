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

    private async void OnMtProtoRefreshClick(object sender, RoutedEventArgs e)
    {
        if (!BeginOperation())
        {
            return;
        }

        try
        {
            SetStatus("Собираю MTProto-прокси", "Каналы Telegram…");
            var found = await CollectMtProtoCoreAsync();
            SetStatus("MTProto собраны", $"Новых ссылок: {found}. Всего в списке: {_mtprotoRows.Count}");
        }
        catch (OperationCanceledException)
        {
            SetStatus("Сбор MTProto отменён", string.Empty);
        }
        catch (Exception exception)
        {
            ReportError("Не удалось собрать MTProto-прокси", exception);
        }
        finally
        {
            EndOperation();
            UpdateConnectionUi();
        }
    }

    /// <summary>Принудительно читает Telegram-каналы через RSS-зеркала, не дожидаясь таймаута t.me.</summary>
    private async void OnMtProtoRssClick(object sender, RoutedEventArgs e)
    {
        if (!BeginOperation())
        {
            return;
        }

        try
        {
            SetStatus("Читаю каналы через RSS", "tg.i-c-a.su и RSSHub-зеркала…");
            var found = await CollectMtProtoCoreAsync(forceRss: true);
            SetStatus("MTProto через RSS", $"Новых ссылок: {found}. Всего в списке: {_mtprotoRows.Count}");
        }
        catch (OperationCanceledException)
        {
            SetStatus("Чтение RSS отменено", string.Empty);
        }
        catch (Exception exception)
        {
            ReportError("Не удалось прочитать каналы через RSS", exception);
        }
        finally
        {
            EndOperation();
            UpdateConnectionUi();
        }
    }

    /// <summary>
    /// Каналы t.me доступны только при работающем обходе, поэтому метод вызывается и после подключения.
    /// Возвращает число найденных ссылок.
    /// </summary>
    private async Task<int> CollectMtProtoCoreAsync(bool forceRss = false)
    {
        var sources = _sources.Where(s => s.Enabled && s.Kind == SourceKind.Telegram).ToList();
        if (sources.Count == 0)
        {
            AppendLog("Нет включённых каналов Telegram — включите их на вкладке «Источники»");
            return 0;
        }

        var found = new List<MtProtoProxy>();
        foreach (var source in sources)
        {
            try
            {
                var list = await MtProtoService.FetchAsync(source, _operation!.Token, forceRss);
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

    /// <summary>Раскладывает прокси по подвкладкам «Рабочие» и «Нерабочие».</summary>
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

        var alive = _mtprotoRows.Count(r => r.Proxy.DelayMs is not null);
        MtProtoWorkingTab.Header = $"Список ({_mtprotoRows.Count})";
        MtProtoDeadTab.Header = $"Нерабочие ({_mtprotoDeadRows.Count})";
        MtProtoSummary.Text = $"В списке {_mtprotoRows.Count} · отвечают {alive} · нерабочих {_mtprotoDeadRows.Count}";

        if (MtProtoEmpty is not null)
        {
            MtProtoEmpty.Visibility = _mtprotoRows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        if (MtProtoDeadSummary is not null)
        {
            MtProtoDeadSummary.Text = $"Нерабочих {_mtprotoDeadRows.Count} · хранятся 14 дней, затем удаляются";
        }

        if (MtProtoDeadEmpty is not null)
        {
            MtProtoDeadEmpty.Visibility = _mtprotoDeadRows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        UpdateCacheSummary();
    }

    private async void OnMtProtoTestClick(object sender, RoutedEventArgs e)
    {
        var dead = ReferenceEquals(MtProtoTabs.SelectedItem, MtProtoDeadTab);
        var rows = dead ? _mtprotoDeadRows : _mtprotoRows;
        if (rows.Count == 0)
        {
            MessageBox.Show(
                this,
                dead ? "Нерабочих прокси нет." : "Сначала соберите прокси из каналов.",
                "VieNLink",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        if (!BeginOperation())
        {
            return;
        }

        try
        {
            SetStatus(dead ? "Проверяю нерабочие MTProto" : "Проверяю MTProto", "TCP-подключение к серверам…");
            var proxies = rows.Select(r => r.Proxy).ToList();
            await MtProtoService.TestAsync(
                proxies,
                new Progress<string>(line => StatusDetail.Text = line),
                proxy => Dispatcher.BeginInvoke(() => ApplyTestedMtProto(proxy)),
                _operation!.Token);

            var alive = proxies.Count(p => p.DelayMs is not null);
            StatusDetail.Text = dead
                ? $"Ожило {alive} из {proxies.Count} — они вернулись в «Список»"
                : $"Отвечают {alive} из {proxies.Count}; остальные перенесены в «Нерабочие»";
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
        if (SelectedMtProto() is not { } proxy)
        {
            MessageBox.Show(this, "Выберите прокси в таблице.", "VieNLink", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (!BeginOperation())
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
                ? $"Отвечает: {proxy.Server}:{proxy.Port}"
                : $"Не отвечает: {proxy.Server}:{proxy.Port} — перенесён в «Нерабочие»";
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

    /// <summary>Ручное добавление узлов: вставляются ссылки, источник попадает в «Мои подписки».</summary>
    private async void OnAddNodeManuallyClick(object sender, RoutedEventArgs e)
    {
        var text = TextInputWindow.Show(
            this,
            "Добавить узлы вручную",
            "Вставьте ссылки на серверы — по одной в строке: vless://, vmess://, trojan://, ss://, hysteria2://.\n" +
            "Узлы попадут в группу «Мои подписки» и будут пробоваться первыми.",
            string.Empty);
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        const string manualName = "Мои узлы (вручную)";
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

    /// <summary>Ручное добавление MTProto-прокси по ссылкам tg://proxy.</summary>
    private void OnMtProtoAddManuallyClick(object sender, RoutedEventArgs e)
    {
        var text = TextInputWindow.Show(
            this,
            "Добавить MTProto-прокси",
            "Вставьте ссылки tg://proxy?server=…&port=…&secret=… или https://t.me/proxy?… — по одной в строке.",
            string.Empty);
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        var links = MtProtoService.Extract(text, "вручную");
        if (links.Count == 0)
        {
            MessageBox.Show(
                this,
                "Не нашёл ни одной ссылки tg://proxy. Пример: tg://proxy?server=1.2.3.4&port=443&secret=abcdef",
                "VieNLink",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        MergeMtProto(links);
        SetStatus("MTProto добавлены", $"Новых ссылок: {links.Count}. Всего: {_mtprotoRows.Count}");
        AppendLog($"MTProto добавлены вручную: {links.Count}");
    }

}
