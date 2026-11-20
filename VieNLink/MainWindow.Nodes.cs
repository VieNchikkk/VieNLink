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
    // ---------- рабочие ----------

    private async void OnWorkingConnectClick(object sender, RoutedEventArgs e)
    {
        if (WorkingGrid.SelectedItem is not NodeRow row)
        {
            MessageBox.Show(this, "Выберите узел в таблице.", "VieNLink", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        await ConnectAsync(row.Node);
    }

    private async void OnWorkingTestClick(object sender, RoutedEventArgs e)
    {
        if (WorkingGrid.SelectedItem is not NodeRow row)
        {
            MessageBox.Show(this, "Выберите узел в таблице.", "VieNLink", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (!BeginOperation())
        {
            return;
        }

        try
        {
            await RunTestAsync(new[] { row.Node }, "Проверка рабочего узла");
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

    private async void OnWorkingDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (!_manager.IsConnected && WorkingGrid.SelectedItem is NodeRow row)
        {
            await ConnectAsync(row.Node);
        }
    }

    private void OnWorkingDeleteClick(object sender, RoutedEventArgs e) => DeleteRow(WorkingGrid.SelectedItem as NodeRow);

    private void OnWorkingCopyLinkClick(object sender, RoutedEventArgs e)
    {
        if (WorkingGrid.SelectedItem is NodeRow row)
        {
            Clipboard.SetText(row.Node.RawLink);
            FooterText.Text = "Ссылка скопирована";
        }
    }

    private void DeleteRow(NodeRow? row)
    {
        if (row is null)
        {
            return;
        }

        var all = AllRows.Select(r => r.Node).Where(n => !ReferenceEquals(n, row.Node)).ToList();
        ReplaceNodes(all);
        NodeSourceService.SaveCache(all);
    }

}
