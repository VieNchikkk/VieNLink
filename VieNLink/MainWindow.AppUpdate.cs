using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Windows;

namespace VieNLink;

public partial class MainWindow
{
    private bool _appUpdateChecked;

    /// <summary>
    /// При запуске проверяет релизы VieNLink на GitHub. Если там версия новее —
    /// предлагает скачать установщик и поставить обновление.
    /// </summary>
    private async Task AutoCheckAppUpdateAsync()
    {
        if (_appUpdateChecked || _closing)
        {
            return;
        }

        _appUpdateChecked = true;
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(6));
            if (_closing)
            {
                return;
            }

            var release = await AppUpdateService.FindLatestAsync(CancellationToken.None);
            if (release is null)
            {
                return;
            }

            var current = AppUpdateService.ParseVersion(AppInfo.Version);
            var latest = AppUpdateService.ParseVersion(release.Tag);
            if (current is null || latest is null || latest <= current)
            {
                return;
            }

            AppendLog($"Доступно обновление VieNLink {release.Tag} (установлена {AppInfo.VersionLabel}).");
            var answer = MessageBox.Show(
                this,
                $"Доступна новая версия VieNLink {release.Tag}.\n" +
                $"Установлена {AppInfo.VersionLabel}.\n\n" +
                "Скачать и установить обновление?",
                "VieNLink",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);
            if (answer != MessageBoxResult.Yes)
            {
                return;
            }

            if (string.IsNullOrWhiteSpace(release.SetupUrl))
            {
                Process.Start(new ProcessStartInfo(release.PageUrl) { UseShellExecute = true });
                return;
            }

            SetStatus("Обновление VieNLink", $"Скачиваю {release.Tag}…");
            var target = Path.Combine(Path.GetTempPath(), $"VieNLink-update-{release.Tag}.exe");
            using (var handler = new SocketsHttpHandler
                   {
                       ConnectTimeout = TimeSpan.FromSeconds(20),
                       AllowAutoRedirect = true,
                   })
            using (var client = new HttpClient(handler) { Timeout = TimeSpan.FromMinutes(10) })
            using (var response = await client.GetAsync(
                       release.SetupUrl, HttpCompletionOption.ResponseHeadersRead, CancellationToken.None))
            {
                response.EnsureSuccessStatusCode();
                await using var source = await response.Content.ReadAsStreamAsync(CancellationToken.None);
                await using var file = File.Create(target);
                await source.CopyToAsync(file, CancellationToken.None);
            }

            SetStatus("Обновление VieNLink", "Запускаю установщик…");
            Process.Start(new ProcessStartInfo(target, "/SILENT /CLOSEAPPLICATIONS /NORESTART")
            {
                UseShellExecute = true,
            });
            _exitRequested = true;
            Close();
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            AppLog.Write("update", "Не удалось проверить или установить обновление VieNLink", exception);
            FooterText.Text = "Обновление не удалось: " + exception.Message;
        }
    }
}
