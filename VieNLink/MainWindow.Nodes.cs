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
            MessageBox.Show(this, "Выберите сервер в таблице.", "VieNLink", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        await ConnectAsync(row.Node);
    }

    private async void OnWorkingTestClick(object sender, RoutedEventArgs e)
    {
        if (WorkingGrid.SelectedItem is not NodeRow row)
        {
            MessageBox.Show(this, "Выберите сервер в таблице.", "VieNLink", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (!BeginOperation("проверку сервера"))
        {
            return;
        }

        try
        {
            await RunTestAsync(new[] { row.Node }, "Проверка рабочего сервера");
        }
        catch (OperationCanceledException)
        {
            SetStatus("Проверка сервера отменена", string.Empty);
        }
        catch (Exception exception)
        {
            ReportError("Не удалось проверить сервер", exception);
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

    /// <summary>
    /// Экспорт профиля для телефона: JSON для sing-box (с TUN) и ссылка в буфер —
    /// Android-клиенты (SFA, Exclave, Karing) импортируют и то, и другое.
    /// </summary>
    private void OnWorkingExportClick(object sender, RoutedEventArgs e)
    {
        if (WorkingGrid.SelectedItem is not NodeRow row)
        {
            return;
        }

        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            FileName = ProfileExport.SuggestFileName(row.Node),
            Filter = "Конфиг sing-box (*.json)|*.json|Все файлы (*.*)|*.*",
            Title = "Экспорт профиля для телефона",
        };

        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        try
        {
            // Без BOM: sing-box не читает JSON с меткой порядка байтов.
            File.WriteAllText(dialog.FileName, ProfileExport.BuildConfig(row.Node, tunnel: true),
                new System.Text.UTF8Encoding(false));
            Clipboard.SetText(ProfileExport.BuildShareLink(row.Node));
            FooterText.Text = "Профиль сохранён, ссылка скопирована — импортируйте в SFA или Exclave";
        }
        catch (Exception exception)
        {
            ReportError("Не удалось сохранить профиль", exception);
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
        // Кэш пишется в фоне, чтобы удаление не подмораживало окно на большом списке.
        _ = Task.Run(() => NodeCacheWriter.Save(all, force: true));
    }

}
