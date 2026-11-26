using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;

namespace VieNLink;

public partial class MainWindow
{
    private readonly ObservableCollection<RejectedNodeRow> _rejectedRows = new();
    private ICollectionView? _rejectedView;
    private string? _rejectedService;

    /// <summary>Загружает файл отсева и подключает его к вкладке.</summary>
    private void InitializeRejectedRows()
    {
        _rejectedView = CollectionViewSource.GetDefaultView(_rejectedRows);
        _rejectedView.Filter = FilterRejected;
        if (RejectedGrid is not null)
        {
            RejectedGrid.ItemsSource = _rejectedView;
        }

        ReloadRejected();
        UpdateRejectedAutoButton();
    }

    private void ReloadRejected()
    {
        var items = RejectedNodeStore.Load();
        _rejectedRows.Clear();
        foreach (var item in items)
        {
            _rejectedRows.Add(new RejectedNodeRow(item));
        }

        _rejectedView?.Refresh();
        UpdateRejectedSummary();
    }

    /// <summary>Кладёт переставший работать сервер в отдельный файл для ручной перепроверки.</summary>
    private void ArchiveRejectedNode(ProxyNode node)
    {
        // В отсев попадают только те, кто раньше реально работал. Мусорные
        // непроверенные серверы просто удаляются и не занимают место.
        if (!NodeAvailability.HasSuccess(node))
        {
            return;
        }

        RejectedNodeStore.Add(node, RejectedNode.Describe(node));
        ReloadRejected();
    }

    /// <summary>
    /// Убирает из отсева «фантомные» записи: сервер уже лежит в рабочем списке.
    /// Старый откат партии сначала писал сервер в отсев, а потом возвращал статус,
    /// из-за чего рабочий сервер годами висел в отсеве. Настоящий отсев — это только
    /// те, кого в рабочем списке нет.
    /// </summary>
    private void DropWorkingFromRejected()
    {
        var working = new HashSet<string>(
            _workingRows.Select(row => row.Id), StringComparer.OrdinalIgnoreCase);
        if (working.Count == 0)
        {
            return;
        }

        var items = RejectedNodeStore.Load();
        var ghosts = items
            .Where(item => item.Node is not null && working.Contains(item.Node.Id))
            .Select(item => item.Node.Id)
            .ToList();
        if (ghosts.Count == 0)
        {
            return;
        }

        RejectedNodeStore.Remove(ghosts);
        AppLog.Write("nodes", $"Из отсева убрано {ghosts.Count}: сервер уже в рабочем списке");
        ReloadRejected();
    }

    private bool FilterRejected(object item)
    {
        if (item is not RejectedNodeRow row)
        {
            return false;
        }

        return _rejectedService switch
        {
            "youtube" => row.Node.YoutubeOk == true,
            "telegram" => row.Node.TelegramOk == true,
            "discord" => row.Node.DiscordOk == true,
            "universal" => row.Node.UniversalOk == true,
            _ => true,
        };
    }

    private void OnRejectedServiceChecked(object sender, RoutedEventArgs e)
    {
        _rejectedService = ((sender as RadioButton)?.Tag as string) switch
        {
            "youtube" => "youtube",
            "telegram" => "telegram",
            "discord" => "discord",
            "universal" => "universal",
            _ => null,
        };
        _rejectedView?.Refresh();
    }

    private void UpdateRejectedSummary()
    {
        if (RejectedSummaryText is null)
        {
            return;
        }

        var total = _rejectedRows.Count;
        var youtube = _rejectedRows.Count(row => row.Node.YoutubeOk == true);
        var telegram = _rejectedRows.Count(row => row.Node.TelegramOk == true);
        var discord = _rejectedRows.Count(row => row.Node.DiscordOk == true);
        var universal = _rejectedRows.Count(row => row.Node.UniversalOk == true);
        var working = _rejectedRows.Count(row => row.Node.Status == NodeStatus.Working);
        RejectedSummaryText.Text =
            $"Всего в отсеве: {total} · проверка прошла: {working} · YouTube: {youtube} · " +
            $"Telegram: {telegram} · Discord: {discord} · Универсальный: {universal}";
    }

    private async void OnRecheckRejectedClick(object sender, RoutedEventArgs e)
    {
        var rows = RejectedGrid?.SelectedItems?.OfType<RejectedNodeRow>().ToList() ?? new List<RejectedNodeRow>();
        if (rows.Count == 0)
        {
            SetStatus("Ничего не выбрано", "Выберите сервер в отсеве");
            return;
        }

        await RecheckRejectedAsync(rows);
    }

    private async void OnRecheckAllRejectedClick(object sender, RoutedEventArgs e)
    {
        var rows = (_rejectedView?.Cast<RejectedNodeRow>() ?? _rejectedRows)
            .Take(200)
            .ToList();
        if (rows.Count == 0)
        {
            SetStatus("Отсев пуст", "Перепроверять нечего");
            return;
        }

        await RecheckRejectedAsync(rows);
    }

    private async Task RecheckRejectedAsync(IReadOnlyList<RejectedNodeRow> rows)
    {
        if (!BeginOperation("перепроверку отсева"))
        {
            return;
        }

        try
        {
            SetStatus("Перепроверяю отсев", $"{rows.Count} серверов…");
            var nodes = rows.Select(row => row.Entry.Node).ToList();
            var progress = new Progress<string>(line => StatusDetail.Text = line);
            var summary = await _probe.TestAsync(
                nodes,
                _settings,
                progress,
                null,
                _operation!.Token,
                concurrency: 3,
                timeoutMs: 4000);

            var recovered = nodes
                .Where(node => node.Status == NodeStatus.Working)
                .ToList();

            // Успешные сразу возвращаем в рабочие: это та же проверка, что и кнопка,
            // а не отдельное ручное решение. Остальные остаются в отсеве.
            if (recovered.Count > 0)
            {
                RestoreRejectedNodes(recovered, "Перепроверка отсева",
                    $"В рабочие возвращено: {recovered.Count}");
            }

            var remaining = RejectedNodeStore.Load();
            var testedById = nodes.ToDictionary(node => node.Id, StringComparer.OrdinalIgnoreCase);
            foreach (var item in remaining)
            {
                if (testedById.TryGetValue(item.Node.Id, out var tested))
                {
                    item.Node = tested;
                    item.RejectedAtUtc = DateTime.UtcNow;
                }
            }

            RejectedNodeStore.Save(remaining);
            ReloadRejected();
            SetStatus("Перепроверка отсева",
                $"Рабочих: {recovered.Count} из {rows.Count}, доменов: {summary.BestDomains}. " +
                $"Возвращено в рабочие: {recovered.Count}.");
            AppendLog($"Отсев: перепроверено {rows.Count}, рабочих {recovered.Count}, " +
                      $"возвращено в рабочие {recovered.Count}");
        }
        catch (OperationCanceledException)
        {
            SetStatus("Перепроверка отсева отменена", string.Empty);
        }
        catch (Exception exception)
        {
            ReportError("Не удалось перепроверить отсев", exception);
        }
        finally
        {
            EndOperation();
        }
    }

    /// <summary>Кнопка «Авто-перепроверка» на вкладке отсева.</summary>
    private void OnToggleRejectedAutoClick(object sender, RoutedEventArgs e)
    {
        _settings.AutoRecheckRejected = !_settings.AutoRecheckRejected;
        SaveSettings();
        UpdateRejectedAutoButton();
        SetStatus("Авто-перепроверка отсева",
            _settings.AutoRecheckRejected ? "включена — раз в 6 часов, только просроченные" : "выключена");
    }

    private void UpdateRejectedAutoButton()
    {
        if (RejectedAutoButton is null)
        {
            return;
        }

        RejectedAutoButton.Content = _settings.AutoRecheckRejected
            ? "● Авто-перепроверка: вкл"
            : "○ Авто-перепроверка: выкл";
    }

    private async void OnRejectConnectClick(object sender, RoutedEventArgs e)
    {
        if (RejectedGrid?.SelectedItem is not RejectedNodeRow row)
        {
            SetStatus("Ничего не выбрано", "Выберите сервер в отсеве");
            return;
        }

        var node = row.Entry.Node;
        await ConnectAsync(node);
        if (_manager.IsConnected)
        {
            if (_manager.CurrentNode is { } current &&
                current.Id.Equals(node.Id, StringComparison.OrdinalIgnoreCase))
            {
                // Реальный трафик через сервер — лучшее доказательство, что он рабочий.
                // Возвращаем в рабочий список сразу; скрытая проверка могла ошибиться.
                RestoreRejectedNodes(new[] { node }, "VPN подключён",
                    $"«{node.Name}» возвращён в рабочие: через него идёт трафик");
            }
            else
            {
                // Подключился запасной: иначе статус «Сервер не подключился» висел,
                // хотя прокси уже работает и видео идёт через другой сервер.
                var backup = _manager.CurrentNode?.Name ?? "запасной сервер";
                SetStatus("Подключён запасной сервер",
                    $"«{node.Name}» не открыл сайт — работает «{backup}». Сервер остался в отсеве");
            }
        }
        else
        {
            SetStatus("Сервер не подключился", "Остался в отсеве для следующей попытки");
        }
    }

    private void OnRejectCancelClick(object sender, RoutedEventArgs e)
    {
        _operation?.Cancel();
        SetStatus("Отмена", "Останавливаю текущую перепроверку/подключение");
    }

    private void OnRejectNextClick(object sender, RoutedEventArgs e)
    {
        if (RejectedGrid is null || RejectedGrid.Items.Count == 0)
        {
            return;
        }

        var index = RejectedGrid.SelectedIndex;
        index = index < 0 || index + 1 >= RejectedGrid.Items.Count ? 0 : index + 1;
        RejectedGrid.SelectedIndex = index;
        RejectedGrid.ScrollIntoView(RejectedGrid.SelectedItem);
        if (RejectedGrid.SelectedItem is RejectedNodeRow row)
        {
            SetStatus("Следующий в отсеве", row.Name + " · " + row.ServerPort);
        }
    }

    private void OnRejectReturnClick(object sender, RoutedEventArgs e)
    {
        var rows = RejectedGrid?.SelectedItems?.OfType<RejectedNodeRow>().ToList() ?? new List<RejectedNodeRow>();
        if (rows.Count == 0)
        {
            SetStatus("Ничего не выбрано", "Выберите сервер в отсеве");
            return;
        }

        RestoreRejectedNodes(rows.Select(row => row.Entry.Node).ToList(),
            "Возвращено в рабочие", string.Empty);
    }

    /// <summary>
    /// Возвращает серверы из отсева в рабочий список. Используется кнопкой «Вернуть»
    /// и успешным подключением: если через сервер реально пошёл трафик, он рабочий,
    /// даже если скрытая проверка его забраковала.
    /// </summary>
    private void RestoreRejectedNodes(IReadOnlyList<ProxyNode> nodes, string title, string detail)
    {
        var now = DateTime.UtcNow;
        foreach (var node in nodes)
        {
            // Ручное возвращение: пользователь сам решил, что сервер нужно вернуть.
            node.Status = NodeStatus.Working;
            node.FailCount = 0;
            // Свежая отметка проверки — иначе IsCountable/IsUsable не засчитают сервер
            // (последний успех старше часа) и он «пропадёт» из счётчиков.
            node.CheckedAtUtc = now;
            // Возвращённый сервер снова в списке — не должен выпасть
            // по сроку давности при следующей загрузке кэша.
            node.LastSeenUtc = now;
        }

        MergeNodes(nodes);
        var ids = nodes.Select(node => node.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var present = AllRows
            .Select(row => row.Node.Id)
            .Where(ids.Contains)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (present.Count > 0)
        {
            RejectedNodeStore.Remove(present);
        }

        ReloadRejected();
        SetStatus(title, string.IsNullOrEmpty(detail)
            ? $"{present.Count} из {nodes.Count}"
            : detail);
    }

    private void OnRemoveRejectedClick(object sender, RoutedEventArgs e)
    {
        var rows = RejectedGrid?.SelectedItems?.OfType<RejectedNodeRow>().ToList() ?? new List<RejectedNodeRow>();
        if (rows.Count == 0)
        {
            SetStatus("Ничего не выбрано", "Выберите сервер в отсеве");
            return;
        }

        RejectedNodeStore.Remove(rows.Select(row => row.Node.Id));
        ReloadRejected();
        SetStatus("Удалено из отсева",
            $"удалено {rows.Count}, в отсеве осталось {_rejectedRows.Count}");
    }
}
