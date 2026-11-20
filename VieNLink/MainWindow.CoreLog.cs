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
            AppVersionText.Text = "Версия приложения: " + AppInfo.VersionLabel +
                " · ядро: " + SingBoxCore.InstalledVersion;
        }
    }

    private async void OnEnsureCoreClick(object sender, RoutedEventArgs e)
    {
        if (!BeginOperation())
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
            Dispatcher.BeginInvoke(() => AppendLog(line));
            return;
        }

        AppendLog(line);
    }

    private void AppendLog(string line)
    {
        try
        {
            LogBox.AppendText(line + Environment.NewLine);
            if (LogBox.Text.Length > 200_000)
            {
                LogBox.Text = LogBox.Text[^120_000..];
                LogBox.CaretIndex = LogBox.Text.Length;
            }
            else
            {
                LogBox.ScrollToEnd();
            }
        }
        catch (Exception exception) when (exception is InvalidOperationException or ArgumentException)
        {
            // Пока окно не готово, строка просто потеряется — это не критично.
        }
    }

    private void OnClearLogClick(object sender, RoutedEventArgs e) => LogBox.Clear();

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
