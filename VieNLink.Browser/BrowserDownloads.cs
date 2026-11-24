using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using Microsoft.Web.WebView2.Core;

namespace VieNLink;

/// <summary>Одна загрузка браузера: имя, путь, состояние и прогресс.</summary>
internal sealed class BrowserDownloadEntry : INotifyPropertyChanged
{
    private double _progress;
    private string _state = "Скачивание…";

    public string FileName { get; init; } = string.Empty;

    public string Url { get; init; } = string.Empty;

    public string Path { get; init; } = string.Empty;

    public DateTime StartedUtc { get; init; } = DateTime.UtcNow;

    public string StartedLabel => StartedUtc.ToLocalTime().ToString("HH:mm");

    public string SizeLabel => File.Exists(Path) && Operation?.State == CoreWebView2DownloadState.Completed
        ? Math.Round(new FileInfo(Path).Length / 1024d / 1024d, 1) + " МБ"
        : string.Empty;

    public CoreWebView2DownloadOperation? Operation { get; init; }

    public string State
    {
        get => _state;
        set
        {
            if (_state == value)
            {
                return;
            }

            _state = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(SizeLabel));
        }
    }

    public double Progress
    {
        get => _progress;
        set
        {
            if (Math.Abs(_progress - value) < 0.001)
            {
                return;
            }

            _progress = value;
            OnPropertyChanged();
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>Список загрузок текущего запуска: им пользуются и окно браузера, и окно загрузок.</summary>
internal static class BrowserDownloads
{
    public static ObservableCollection<BrowserDownloadEntry> Items { get; } = new();

    public static BrowserDownloadEntry Track(CoreWebView2DownloadOperation operation, string path, string url)
    {
        var entry = new BrowserDownloadEntry
        {
            FileName = System.IO.Path.GetFileName(path),
            Path = path,
            Url = url,
            Operation = operation,
        };
        operation.StateChanged += (_, _) => Refresh(entry);
        operation.BytesReceivedChanged += (_, _) => Refresh(entry);
        Items.Insert(0, entry);
        Refresh(entry);
        return entry;
    }

    public static void Open(BrowserDownloadEntry entry)
    {
        try
        {
            if (!File.Exists(entry.Path))
            {
                return;
            }

            Process.Start(new ProcessStartInfo(entry.Path) { UseShellExecute = true });
        }
        catch (Exception exception)
        {
            AppLog.Write("browser", "Не удалось открыть скачанный файл", exception);
        }
    }

    public static void ShowInFolder(BrowserDownloadEntry entry)
    {
        try
        {
            var folder = System.IO.Path.GetDirectoryName(entry.Path);
            if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder))
            {
                return;
            }

            if (File.Exists(entry.Path))
            {
                Process.Start("explorer.exe", $"/select,\"{entry.Path}\"");
            }
            else
            {
                Process.Start("explorer.exe", folder);
            }
        }
        catch (Exception exception)
        {
            AppLog.Write("browser", "Не удалось открыть папку загрузки", exception);
        }
    }

    private static void Refresh(BrowserDownloadEntry entry)
    {
        if (entry.Operation is not { } operation)
        {
            return;
        }

        var total = operation.TotalBytesToReceive ?? 0;
        entry.Progress = total > 0 ? (double)operation.BytesReceived / total : 0;
        entry.State = operation.State switch
        {
            CoreWebView2DownloadState.InProgress => $"Скачивание · {entry.Progress:P0}",
            CoreWebView2DownloadState.Interrupted => "Прервано: " + operation.InterruptReason,
            _ => "Готово",
        };
    }
}
