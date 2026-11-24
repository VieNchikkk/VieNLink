using System.Windows;
using System.Windows.Input;

namespace VieNLink;

/// <summary>Список загрузок браузера: прогресс, открытие файла и папки, отмена.</summary>
public sealed partial class BrowserDownloadsWindow : Window
{
    public BrowserDownloadsWindow()
    {
        InitializeComponent();
        DownloadList.ItemsSource = BrowserDownloads.Items;
        DownloadList.SelectionChanged += (_, _) => UpdateButtons();
        BrowserDownloads.Items.CollectionChanged += (_, _) => UpdateCount();
        UpdateCount();
        UpdateButtons();
    }

    private BrowserDownloadEntry? Selected => DownloadList.SelectedItem as BrowserDownloadEntry;

    private void UpdateCount()
    {
        var count = BrowserDownloads.Items.Count;
        CountText.Text = count == 0 ? "Пока ничего не скачивалось" : $"Загрузок: {count}";
        UpdateButtons();
    }

    private void UpdateButtons()
    {
        var entry = Selected;
        var ready = entry is not null && System.IO.File.Exists(entry.Path);
        var active = entry?.Operation?.State == Microsoft.Web.WebView2.Core.CoreWebView2DownloadState.InProgress;
        OpenButton.IsEnabled = ready;
        ShowButton.IsEnabled = entry is not null && System.IO.Directory.Exists(entry.Path);
        CancelButton.IsEnabled = active;
    }

    private void OnOpenSelected(object sender, MouseButtonEventArgs e) => OpenSelected();

    private void OnOpenClick(object sender, RoutedEventArgs e) => OpenSelected();

    private void OpenSelected()
    {
        if (Selected is { } entry)
        {
            BrowserDownloads.Open(entry);
            UpdateButtons();
        }
    }

    private void OnShowClick(object sender, RoutedEventArgs e)
    {
        if (Selected is { } entry)
        {
            BrowserDownloads.ShowInFolder(entry);
        }
    }

    private void OnCancelClick(object sender, RoutedEventArgs e)
    {
        try
        {
            Selected?.Operation?.Cancel();
        }
        catch (Exception exception)
        {
            AppLog.Write("browser", "Не удалось отменить загрузку", exception);
        }
    }

    private void OnClearClick(object sender, RoutedEventArgs e) => BrowserDownloads.Items.Clear();

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();
}
