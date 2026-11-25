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
    // ---------- ядро и журнал ----------

    private void UpdateCoreStatus()
    {
        CoreStatusText.Text = SingBoxCore.IsInstalled
            ? $"Установлено: {SingBoxCore.ExecutablePath} · версия {SingBoxCore.InstalledVersion}"
            : "Ядро не установлено — скачается автоматически при первом подключении (около 33 МБ).";
        if (AppVersionText is not null)
        {
            AppVersionText.Text = "Версия приложения: " + AppInfo.VersionLabel;
        }
    }

    private async void OnEnsureCoreClick(object sender, RoutedEventArgs e)
    {
        if (!BeginOperation("проверку ядра"))
        {
            return;
        }

        try
        {
            SetStatus("Подготовка ядра", "Проверка sing-box…");
            var progress = new Progress<string>(line =>
            {
                StatusDetail.Text = line;
                AppendLog(line);
            });
            await _manager.EnsureCoreAsync(progress, _operation!.Token);
            UpdateCoreStatus();
            StatusDetail.Text = "Ядро готово";
            FooterText.Text = "sing-box " + SingBoxCore.InstalledVersion;
        }
        catch (OperationCanceledException)
        {
            SetStatus("Подготовка ядра отменена", string.Empty);
        }
        catch (Exception exception)
        {
            ReportError("Не удалось подготовить ядро", exception);
        }
        finally
        {
            EndOperation();
        }
    }

    private void OnLogLine(string line)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(() => OnLogLine(line));
            return;
        }

        if (AppLogCategories.Matches(line, _logCategory))
        {
            AppendLog(line);
        }
    }

    /// <summary>Сколько последних строк журнала держим в окне. Старые отбрасываются целиком.</summary>
    private const int LogMaxLines = 500;

    private string _logCategory = AppLogCategories.All;
    private bool _logLoading;

    /// <summary>Категории — фильтр по тегу записи, как на Android.</summary>
    private void RenderLogCategories()
    {
        if (LogCategoryPanel is null)
        {
            return;
        }

        LogCategoryPanel.Children.Clear();
        foreach (var name in AppLogCategories.Names)
        {
            var selected = string.Equals(name, _logCategory, StringComparison.Ordinal);
            var button = new Button
            {
                Content = name,
                Style = (Style)FindResource(selected ? "AccentSmallButton" : "SmallButton"),
                Margin = new Thickness(0, 0, 6, 0),
                Padding = new Thickness(10, 4, 10, 4),
                Tag = name,
            };
            button.Click += (_, _) =>
            {
                _logCategory = (string)button.Tag;
                RenderLogCategories();
                _ = LoadLogAsync();
            };
            LogCategoryPanel.Children.Add(button);
        }
    }

    private async Task LoadLogAsync()
    {
        if (_logLoading)
        {
            return;
        }

        _logLoading = true;
        try
        {
            var (text, source) = await Task.Run(() => AppLog.ReadTail(LogMaxLines, _logCategory));
            LogBox.Text = text;
            LogBox.CaretIndex = LogBox.Text.Length;
            LogBox.ScrollToEnd();
            if (LogInfoText is not null)
            {
                LogInfoText.Text = source;
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            LogBox.Text = "Не удалось прочитать журнал: " + exception.Message;
        }
        finally
        {
            _logLoading = false;
        }
    }

    private void OnRefreshLogClick(object sender, RoutedEventArgs e) => _ = LoadLogAsync();

    private void OnShareLogClick(object sender, RoutedEventArgs e)
    {
        try
        {
            Clipboard.SetText(LogBox.Text);
            FooterText.Text = "Журнал скопирован · " + _logCategory;
        }
        catch (Exception exception) when (exception is System.Runtime.InteropServices.ExternalException)
        {
            AppLog.Write("ui", "Не удалось скопировать журнал", exception);
        }
    }

    private void AppendLog(string line)
    {
        if (!AppLogCategories.Matches(line, _logCategory))
        {
            return;
        }

        try
        {
            LogBox.AppendText(line + Environment.NewLine);
            var lines = LogBox.Text.Split('\n');
            if (lines.Length > LogMaxLines)
            {
                LogBox.Text = string.Join('\n', lines[^LogMaxLines..]);
            }

            LogBox.CaretIndex = LogBox.Text.Length;
            LogBox.ScrollToEnd();
        }
        catch (Exception exception) when (exception is InvalidOperationException or ArgumentException)
        {
            // Пока окно не готово, строка просто потеряется — это не критично.
        }
    }

    private void OnClearLogClick(object sender, RoutedEventArgs e)
    {
        if (AppLog.ClearToday())
        {
            _ = LoadLogAsync();
            FooterText.Text = "Сегодняшний журнал очищен";
        }
    }

    private void OnOpenLogsClick(object sender, RoutedEventArgs e) => OpenFolder(AppPaths.LogsDirectory);

    private static void OpenFolder(string path)
    {
        try
        {
            Directory.CreateDirectory(path);
            Process.Start(new ProcessStartInfo("explorer.exe", "\"" + path + "\"") { UseShellExecute = true });
        }
        catch (Exception exception) when (exception is IOException or System.ComponentModel.Win32Exception)
        {
            AppLog.Write("ui", "Не удалось открыть папку " + path, exception);
        }
    }

}
