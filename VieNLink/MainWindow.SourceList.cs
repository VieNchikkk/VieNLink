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
    // ---------- источники ----------

    private async void OnAddSourceClick(object sender, RoutedEventArgs e)
    {
        var lines = NewSourceUrl.Text
            .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(line => line.Length > 0)
            .ToList();
        if (lines.Count == 0)
        {
            MessageBox.Show(
                this,
                "Вставьте ссылку на подписку, список, канал t.me или ссылку на сервер (vless://, vmess://, trojan://, ss://, hysteria2://).",
                "VieNLink",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        var baseName = NewSourceName.Text.Trim();
        var added = 0;
        var errors = new List<string>();
        for (var index = 0; index < lines.Count; index++)
        {
            var name = baseName.Length == 0
                ? string.Empty
                : lines.Count == 1
                    ? baseName
                    : $"{baseName} {index + 1}";
            var (ok, error) = await TryAddSourceAsync(lines[index], name);
            if (ok)
            {
                added++;
            }
            else if (!string.IsNullOrWhiteSpace(error))
            {
                errors.Add(error!);
            }
        }

        if (added > 0)
        {
            NewSourceName.Clear();
            NewSourceUrl.Clear();
            FooterText.Text = added == 1 ? "Источник добавлен" : $"Добавлено источников: {added}";
        }

        if (errors.Count > 0)
        {
            MessageBox.Show(
                this,
                "Не удалось добавить:\n" + string.Join("\n", errors.Take(5)) +
                (errors.Count > 5 ? $"\n… и ещё {errors.Count - 5}" : string.Empty),
                "VieNLink",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
    }

    /// <summary>Добавляет один источник: подписку, список, канал или ссылку на сервер.</summary>
    private async Task<(bool Ok, string? Error)> TryAddSourceAsync(string rawUrl, string name)
    {
        var url = rawUrl.Trim();

        // Happ умеет импортировать подписку ссылкой happ://add/… — поддержим тот же формат.
        if (url.StartsWith("happ://add/", StringComparison.OrdinalIgnoreCase))
        {
            var payload = Uri.UnescapeDataString(url["happ://add/".Length..]);
            if (!payload.StartsWith("http", StringComparison.OrdinalIgnoreCase) &&
                Base64Util.TryDecode(payload, out var decoded) &&
                decoded.TrimStart().StartsWith("http", StringComparison.OrdinalIgnoreCase))
            {
                payload = decoded.Trim();
            }

            url = payload;
        }

        if (_sources.Any(s => s.Url.Equals(url, StringComparison.OrdinalIgnoreCase)))
        {
            return (false, $"уже добавлен: {url}");
        }

        // Можно вставить ссылку на сервер (например, скопированную из Happ).
        var scheme = url.Contains("://", StringComparison.Ordinal)
            ? url[..url.IndexOf("://", StringComparison.Ordinal)].ToLowerInvariant()
            : string.Empty;
        if (scheme is "vless" or "vmess" or "trojan" or "ss" or "hysteria2" or "hy2")
        {
            var singleSource = new SourceDefinition
            {
                Name = name.Length == 0 ? "Мой узел" : name,
                Url = url,
                Kind = SourceKind.SingleLink,
                IsMine = true,
            };
            AddSource(singleSource);
            await LoadSingleSourceAsync(singleSource);
            return (true, null);
        }

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
            (uri.Scheme != "http" && uri.Scheme != "https"))
        {
            return (false, $"не похоже на ссылку: {url}");
        }

        if (name.Length == 0)
        {
            name = uri.Host.Equals("t.me", StringComparison.OrdinalIgnoreCase)
                ? "Telegram " + uri.AbsolutePath.Trim('/')
                : uri.Host;
        }

        var source = new SourceDefinition
        {
            Name = name,
            Url = url,
            Kind = uri.Host.Equals("t.me", StringComparison.OrdinalIgnoreCase) ? SourceKind.Telegram : SourceKind.Auto,
            IsMine = true,
        };
        AddSource(source);
        await LoadSingleSourceAsync(source);
        return (true, null);
    }

    private void AddSource(SourceDefinition source)
    {
        _sources.Add(source);
        _sourceRows.Add(new SourceRow(source));
        NodeSourceService.SaveSources(_sources);
    }

    /// <summary>Сразу загружает одну подписку, чтобы после вставки ссылки появились узлы.</summary>
    private async Task LoadSingleSourceAsync(SourceDefinition source)
    {
        if (!BeginOperation("загрузку источника"))
        {
            return;
        }

        try
        {
            SetStatus("Загружаю подписку", source.Name + "…");
            var outcome = await NodeSourceService.FetchAsync(source, _operation!.Token);
            source.LastCheckedUtc = DateTime.UtcNow;
            source.LastError = outcome.Error;
            source.LastNodeCount = outcome.Nodes.Count;
            _sourceRows.FirstOrDefault(r => ReferenceEquals(r.Source, source))?.Refresh();
            NodeSourceService.SaveSources(_sources);

            if (outcome.Nodes.Count == 0)
            {
                SetStatus("Подписка пуста", outcome.Error ?? "ссылки на узлы не найдены — проверьте ссылку");
                AppendLog($"{source.Name}: узлов не найдено");
                return;
            }

            MergeNodes(outcome.Nodes);
            var info = string.IsNullOrWhiteSpace(source.SubscriptionInfo) ? string.Empty : " · " + source.SubscriptionInfo;
            SetStatus("Подписка загружена", $"{source.Name}: {outcome.Nodes.Count} узлов{info}");
            AppendLog($"{source.Name}: {outcome.Nodes.Count} узлов");

            if (_settings.TestAfterRefresh)
            {
                await RunTestAsync(SelectBatch(outcome.Nodes), "Проверка подписки");
            }
        }
        catch (OperationCanceledException)
        {
            SetStatus("Загрузка подписки отменена", string.Empty);
        }
        catch (Exception exception)
        {
            source.LastError = exception.Message;
            _sourceRows.FirstOrDefault(r => ReferenceEquals(r.Source, source))?.Refresh();
            ReportError("Не удалось загрузить подписку", exception);
        }
        finally
        {
            EndOperation();
            UpdateConnectionUi();
        }
    }

    /// <summary>Добавляет узлы к уже собранным, сохраняя задержки и статусы.</summary>
    private void MergeNodes(IEnumerable<ProxyNode> nodes)
    {
        var now = DateTime.UtcNow;
        var map = new Dictionary<string, ProxyNode>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in AllRows)
        {
            map[row.Id] = row.Node;
        }

        foreach (var node in nodes)
        {
            // Узел только что пришёл из источника — отмечаем его свежим.
            node.LastSeenUtc ??= now;

            if (map.TryGetValue(node.Id, out var existing))
            {
                node.DelayMs ??= existing.DelayMs;
                node.CheckedAtUtc ??= existing.CheckedAtUtc;

                // Свежий разбор не знает результатов проверок — переносим их из кэша.
                if (node.Status == NodeStatus.Unknown)
                {
                    node.YoutubeOk = existing.YoutubeOk;
                    node.TelegramOk = existing.TelegramOk;
                    node.Status = existing.Status;
                }

                if (existing.LastSeenUtc is { } seen &&
                    (node.LastSeenUtc is null || seen > node.LastSeenUtc))
                {
                    node.LastSeenUtc = seen;
                }
            }

            map[node.Id] = node;
        }

        // Один сервер:порт — одна строка; проверки с дубля переходят на оставшийся узел.
        var merged = NodeDedup.Collapse(map.Values);
        ReplaceNodes(merged);
        // ReplaceNodes применяет общий лимит — в кэш пишем уже подрезанный список.
        NodeSourceService.SaveCache(AllRows.Select(r => r.Node).ToList());
    }

    private async void OnRefreshSourceClick(object sender, RoutedEventArgs e)
    {
        if (SourcesList.SelectedItem is not SourceRow row)
        {
            MessageBox.Show(this, "Выберите источник в списке.", "VieNLink", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        await LoadSingleSourceAsync(row.Source);
    }

    private void OnRemoveSourceClick(object sender, RoutedEventArgs e)
    {
        if (SourcesList.SelectedItem is not SourceRow row)
        {
            return;
        }

        if (MessageBox.Show(this, "Удалить источник «" + row.Name + "»?", "VieNLink", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
        {
            return;
        }

        _sources.Remove(row.Source);
        _sourceRows.Remove(row);
        NodeSourceService.SaveSources(_sources);
    }

    private void OnSourceToggled(object sender, RoutedEventArgs e) => NodeSourceService.SaveSources(_sources);

    private async void OnCheckSourcesClick(object sender, RoutedEventArgs e)
    {
        if (!BeginOperation("проверку источников"))
        {
            return;
        }

        try
        {
            SetStatus("Проверяю источники", "Загрузка без проверки узлов…");
            var progress = new Progress<string>(line =>
            {
                StatusDetail.Text = line;
                AppendLog(line);
            });

            var results = new List<FetchOutcome>();
            foreach (var source in _sources.Where(s => s.Enabled))
            {
                try
                {
                    var outcome = await NodeSourceService.FetchAsync(source, _operation!.Token);
                    source.LastCheckedUtc = DateTime.UtcNow;
                    source.LastError = outcome.Error;
                    source.LastNodeCount = outcome.Nodes.Count;
                    results.Add(outcome);
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    source.LastCheckedUtc = DateTime.UtcNow;
                    source.LastError = exception.Message;
                    ((IProgress<string>)progress).Report($"{source.Name}: {exception.Message}");
                }

                _sourceRows.FirstOrDefault(r => ReferenceEquals(r.Source, source))?.Refresh();
            }

            NodeSourceService.SaveSources(_sources);
            StatusDetail.Text = $"Источников доступно: {results.Count(o => o.Error is null)} из {_sources.Count(s => s.Enabled)}";
        }
        catch (OperationCanceledException)
        {
            SetStatus("Проверка источников отменена", string.Empty);
        }
        finally
        {
            EndOperation();
        }
    }

    private void OnSearchChanged(object sender, TextChangedEventArgs e)
    {
        if (SearchPlaceholder is not null)
        {
            SearchPlaceholder.Visibility = string.IsNullOrEmpty(SearchBox.Text) ? Visibility.Visible : Visibility.Collapsed;
        }

        _view?.Refresh();
        _workingView?.Refresh();
        UpdateSummary();
    }

    private void OnFilterChanged(object sender, RoutedEventArgs e)
    {
        _view?.Refresh();
        _workingView?.Refresh();
        UpdateSummary();
    }

    /// <summary>Счётчик рядом с «Только маскированные»: сколько узлов останется в открытой подвкладке.</summary>
    private void UpdateMaskedCount()
    {
        if (MaskedCountText is null)
        {
            return;
        }

        if (OnlyMaskedCheck.IsChecked != true)
        {
            MaskedCountText.Visibility = Visibility.Collapsed;
            return;
        }

        IEnumerable<NodeRow> source;
        if (ReferenceEquals(NodesTabs.SelectedItem, NodesWorkingTab))
        {
            source = _workingRows;
        }
        else if (ReferenceEquals(NodesTabs.SelectedItem, NodesUntestedTab))
        {
            source = _rows;
        }
        else
        {
            MaskedCountText.Visibility = Visibility.Collapsed;
            return;
        }

        var count = source.Count(r => NodeRanking.IsMasked(r.Node) && MatchesCommonFilters(r));
        MaskedCountText.Text = $"маскированных: {count}";
        MaskedCountText.Visibility = Visibility.Visible;
    }

    private void OnNodesTabChanged(object sender, SelectionChangedEventArgs e) => UpdateMaskedCount();

    private bool MatchesCommonFilters(NodeRow row)
    {
        if (OnlyMineCheck.IsChecked == true && !row.Node.IsMine)
        {
            return false;
        }

        var query = SearchBox.Text.Trim();
        if (query.Length == 0)
        {
            return true;
        }

        return row.Node.Name.Contains(query, StringComparison.OrdinalIgnoreCase)
               || row.Node.Server.Contains(query, StringComparison.OrdinalIgnoreCase)
               || row.Node.SourceName.Contains(query, StringComparison.OrdinalIgnoreCase)
               || row.Node.Protocol.Contains(query, StringComparison.OrdinalIgnoreCase);
    }

    private bool FilterNode(object item)
    {
        if (item is not NodeRow row || row.Node.Status != NodeStatus.Unknown)
        {
            return false;
        }

        return (OnlyMaskedCheck.IsChecked != true || NodeRanking.IsMasked(row.Node)) &&
               MatchesCommonFilters(row);
    }

    private bool FilterWorkingNode(object item)
    {
        if (item is not NodeRow row || row.Node.Status != NodeStatus.Working)
        {
            return false;
        }

        return (OnlyMaskedCheck.IsChecked != true || NodeRanking.IsMasked(row.Node)) &&
               MatchesCommonFilters(row);
    }

    private void OnCopyLinkClick(object sender, RoutedEventArgs e)
    {
        if (SelectedNode() is { } node)
        {
            Clipboard.SetText(node.RawLink);
            FooterText.Text = "Ссылка скопирована";
        }
    }

    /// <summary>Короткая памятка: как пользоваться маскировкой и автопереключением.</summary>
    private void OnMaskingHelpClick(object sender, RoutedEventArgs e)
    {
        MessageBox.Show(
            this,
            "Как пользоваться\n\n" +
            "1. Нажмите «Обновить списки».\n" +
            "2. Включите «Только маскированные» — останутся узлы, чей трафик похож на обычный HTTPS.\n" +
            "3. Нажмите «Все узлы». Узлы с ✓ появятся в подвкладке «Рабочие».\n" +
            "4. Нажмите «Подключиться» — приложение само выберет быстрый и маскированный узел.\n\n" +
            "Что означают колонки\n" +
            "• Маскировка — REALITY · Vision, WS · TLS · uTLS, Hysteria2 · obfs, TLS или «без TLS».\n" +
            "• Камуфляж — под какой сайт маскируется рукопожатие: YouTube/Google, Yandex, Cloudflare.\n\n" +
            "Если узел перестанет отвечать, приложение само переключится на другой камуфляж\n" +
            "(YouTube → Yandex → Cloudflare), без перезапуска и без диалогов.\n\n" +
            "Как убедиться, что это работает\n" +
            "1. Подключитесь и откройте вкладку «Диагностика»:\n" +
            "   там видно активный и запасной камуфляж и счётчики переключений.\n" +
            "2. Нажмите «Проверить туннель» — YouTube и t.me должны открыться.\n" +
            "3. Смоделируйте сбой: заблокируйте IP:порт активного узла в брандмауэре Windows\n" +
            "   (или дождитесь, когда умрёт бесплатный узел).\n" +
            "4. Через 45–60 секунд приложение переключится само: в журнале появится\n" +
            "   «Переключено без перезапуска ядра», а счётчик «тёплых» вырастет.\n" +
            "5. Для сравнения выключите «Автопереключение при сбое узла» и повторите:\n" +
            "   соединение просто упадёт — это и есть разница между сервером и нашей логикой.\n\n" +
            "Проверенные узлы и свой сервер\n" +
            "• По умолчанию приложение подключается только к узлам с задержкой.\n" +
            "• Свой сервер: «Диагностика → Добавить свой сервер», затем включите\n" +
            "  «Подключаться только к моим подпискам» в настройках.\n" +
            "• Шифрование до сервера не создаёт выход в интернет само по себе —\n" +
            "  нужен сервер, до которого ТСПУ пропускает.\n\n" +
            "Узлы, которые уже были рабочими, не стираются с первого провала:\n" +
            "  они получают до 10 попыток подряд, а непроверенные удаляются сразу.\n" +
            "Telegram: «Собрать из каналов»; если t.me недоступен, приложение попробует RSS-зеркала.",
            "VieNLink — как пользоваться",
            MessageBoxButton.OK,
            MessageBoxImage.Information);
    }

}
