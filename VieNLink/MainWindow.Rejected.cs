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

        RejectedNodeStore.Add(node);
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
        RejectedSummaryText.Text =
            $"Всего в отсеве: {total} · YouTube: {youtube} · Telegram: {telegram} · " +
            $"Discord: {discord} · Универсальный: {universal}";
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
            // Ничего не возвращаем автоматически: пользователь сам нажмёт
            // «Вернуть (В рабочие)». Иначе сервер снова уедет в отсев после проверки.

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
                "Нажмите «Вернуть (В рабочие)», чтобы перенести сервер в список.");
            AppendLog($"Отсев: перепроверено {rows.Count}, рабочих {recovered.Count}, " +
                      "автоматически в рабочие не возвращаются");
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

    private async void OnRejectConnectClick(object sender, RoutedEventArgs e)
    {
        if (RejectedGrid?.SelectedItem is not RejectedNodeRow row)
        {
            SetStatus("Ничего не выбрано", "Выберите сервер в отсеве");
            return;
        }

        var node = row.Entry.Node;
        await ConnectAsync(node);
        if (_manager.IsConnected &&
            _manager.CurrentNode is { } current &&
            current.Id.Equals(node.Id, StringComparison.OrdinalIgnoreCase))
        {
            // В рабочие переводит только кнопка «Вернуть (В рабочие)».
            SetStatus("VPN подключён", "Сервер остаётся в отсеве — нажмите «Вернуть (В рабочие)»");
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

        var nodes = rows.Select(row => row.Entry.Node).ToList();
        foreach (var node in nodes)
        {
            // Ручное возвращение: пользователь сам решил, что сервер нужно вернуть.
            node.Status = NodeStatus.Working;
            node.FailCount = 0;
        }

        MergeNodes(nodes);
        RejectedNodeStore.Remove(nodes.Select(node => node.Id));
        ReloadRejected();
        SetStatus("Возвращено в рабочие", nodes.Count.ToString());
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
        SetStatus("Удалено из отсева", rows.Count.ToString());
    }
}
