using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Windows;

namespace VieNLink;

public partial class MainWindow
{
    private bool _appUpdateChecked;
    private bool _appUpdateChecking;
    private bool _appUpdateDownloading;
    private AppRelease? _appUpdateRelease;
    private string? _appUpdateSetupPath;

    /// <summary>
    /// При запуске проверяет релизы VieNLink на GitHub. Если там версия новее —
    /// предлагает скачать установщик. Ту же проверку можно запустить вручную
    /// кнопкой «Проверить обновление» на вкладке «Общие».
    /// </summary>
    private async Task AutoCheckAppUpdateAsync()
    {
        if (_appUpdateChecked || _closing || _settings.AutomationPaused ||
            !_settings.AutoCheckAppUpdates)
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

            var release = await CheckAppUpdateAsync();
            if (release is null || _closing)
            {
                return;
            }

            AppendLog($"Доступно обновление VieNLink {release.Tag} (установлена {AppInfo.VersionLabel}).");
            var answer = MessageBox.Show(
                this,
                $"Доступна новая версия VieNLink {release.Tag}.\n" +
                $"Установлена {AppInfo.VersionLabel}.\n\n" +
                "Скачать установщик сейчас? После скачивания его нужно будет запустить " +
                "кнопкой «Запустить установщик» на вкладке «Общие».",
                "VieNLink",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);
            if (answer != MessageBoxResult.Yes)
            {
                SetAppUpdateStatus($"Доступна версия {release.Tag}. Скачать можно на вкладке «Общие».");
                return;
            }

            await DownloadAppUpdateAsync(release);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            AppLog.Write("update", "Не удалось проверить или установить обновление VieNLink", exception);
            SetAppUpdateStatus("Обновление не удалось: " + exception.Message);
        }
    }

    private async void OnCheckAppUpdateClick(object sender, RoutedEventArgs e)
    {
        if (_appUpdateChecking)
        {
            return;
        }

        _appUpdateChecked = true;
        _appUpdateChecking = true;
        CheckAppUpdateButton.IsEnabled = false;
        try
        {
            await CheckAppUpdateAsync();
        }
        finally
        {
            _appUpdateChecking = false;
            CheckAppUpdateButton.IsEnabled = true;
        }
    }

    private async void OnInstallAppUpdateClick(object sender, RoutedEventArgs e)
    {
        if (_appUpdateSetupPath is { } path && File.Exists(path))
        {
            PromptAndLaunchAppUpdate();
            return;
        }

        if (_appUpdateRelease is not { } release)
        {
            SetAppUpdateStatus("Сначала нажмите «Проверить обновление».");
            return;
        }

        await DownloadAppUpdateAsync(release);
    }

    /// <summary>
    /// Сравнивает установленную версию с последним релизом. Возвращает релиз,
    /// если он новее; в остальных случаях сам пишет результат в «Общие».
    /// </summary>
    private async Task<AppRelease?> CheckAppUpdateAsync()
    {
        SetAppUpdateStatus("Проверяю обновления на GitHub…");
        AppRelease? release;
        try
        {
            release = await AppUpdateService.FindLatestAsync(CancellationToken.None);
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
        {
            AppLog.Write("update", "Не удалось проверить обновление VieNLink", exception);
            SetAppUpdateStatus("Не удалось связаться с GitHub. Проверьте интернет и повторите.");
            return null;
        }

        if (release is null)
        {
            SetAppUpdateStatus("Не удалось получить список релизов (GitHub недоступен). " +
                               "Проверьте интернет или откройте страницу релизов вручную: " +
                               AppUpdateService.ReleasesPage);
            return null;
        }

        var current = AppUpdateService.ParseVersion(AppInfo.Version);
        var latest = AppUpdateService.ParseVersion(release.Tag);
        if (current is null || latest is null || latest <= current)
        {
            _appUpdateRelease = null;
            _appUpdateSetupPath = null;
            InstallAppUpdateButton.Content = "Скачать и установить";
            InstallAppUpdateButton.Visibility = Visibility.Collapsed;
            SetAppUpdateStatus($"Установлена последняя версия {AppInfo.VersionLabel}.");
            return null;
        }

        // Для другого релиза старый скачанный установщик не подходит.
        if (_appUpdateRelease?.Tag != release.Tag)
        {
            _appUpdateSetupPath = null;
        }

        _appUpdateRelease = release;
        InstallAppUpdateButton.Content = "Скачать и установить";
        InstallAppUpdateButton.Visibility = Visibility.Visible;
        SetAppUpdateStatus(
            $"Доступна версия {release.Tag} (установлена {AppInfo.VersionLabel}). " +
            "Нажмите «Скачать и установить».");
        return release;
    }

    private async Task DownloadAppUpdateAsync(AppRelease release)
    {
        if (_appUpdateDownloading)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(release.SetupUrl))
        {
            Process.Start(new ProcessStartInfo(release.PageUrl) { UseShellExecute = true });
            return;
        }

        _appUpdateDownloading = true;
        if (!BeginOperation("скачивание обновления VieNLink"))
        {
            _appUpdateDownloading = false;
            SetAppUpdateStatus("Скачивание не начато: дождитесь окончания текущей операции " +
                               "и нажмите «Скачать и установить».");
            return;
        }

        InstallAppUpdateButton.IsEnabled = false;
        CheckAppUpdateButton.IsEnabled = false;
        string? target = null;
        try
        {
            SetAppUpdateStatus($"Скачиваю установщик {release.Tag}…");
            target = await DownloadSetupAsync(release, _operation!.Token);
        }
        catch (OperationCanceledException)
        {
            SetAppUpdateStatus("Скачивание отменено. Нажмите «Скачать и установить», чтобы повторить.");
            return;
        }
        catch (Exception exception)
        {
            AppLog.Write("update", "Не удалось скачать установщик VieNLink", exception);
            SetAppUpdateStatus("Не удалось скачать установщик: " + exception.Message);
            MessageBox.Show(
                this,
                "Не удалось скачать установщик ни с GitHub, ни с зеркал.\n\n" +
                exception.Message + "\n\n" +
                "Попробуйте ещё раз позже или скачайте вручную: " + AppUpdateService.ReleasesPage,
                "VieNLink",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }
        finally
        {
            EndOperation();
            _appUpdateDownloading = false;
            InstallAppUpdateButton.IsEnabled = true;
            CheckAppUpdateButton.IsEnabled = true;
        }

        if (target is null)
        {
            return;
        }

        _appUpdateSetupPath = target;
        InstallAppUpdateButton.Content = "Запустить установщик";
        InstallAppUpdateButton.Visibility = Visibility.Visible;
        SetAppUpdateStatus($"Установщик {release.Tag} скачан: {target}");
        AppendLog("Обновление VieNLink: установщик скачан — " + target);

        PromptAndLaunchAppUpdate();
    }

    private void PromptAndLaunchAppUpdate()
    {
        var path = _appUpdateSetupPath;
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            _appUpdateSetupPath = null;
            InstallAppUpdateButton.Content = "Скачать и установить";
            SetAppUpdateStatus("Скачанный установщик не найден — скачайте обновление заново.");
            return;
        }

        var answer = MessageBox.Show(
            this,
            "Установщик скачан:\n" + path + "\n\n" +
            "Запустить установку сейчас? Приложение закроется, установка займёт до минуты; " +
            "в конце установщик сам предложит запустить новую версию.",
            "VieNLink",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);
        if (answer != MessageBoxResult.Yes)
        {
            SetAppUpdateStatus($"Установщик готов: {path}. " +
                               "Нажмите «Запустить установщик», когда будет удобно.");
            return;
        }

        LaunchAppUpdate(path);
    }

    private void LaunchAppUpdate(string path)
    {
        try
        {
            // Обычное окно установщика, а не /SILENT: видно ход установки, и после
            // завершения можно сразу запустить новую версию. /CLOSEAPPLICATIONS
            // закрывает наш процесс, если он ещё не успел выйти.
            Process.Start(new ProcessStartInfo(path, "/CLOSEAPPLICATIONS /NORESTART")
            {
                UseShellExecute = true,
            });
            _exitRequested = true;
            Close();
        }
        catch (Exception exception)
        {
            AppLog.Write("update", "Не удалось запустить установщик VieNLink", exception);
            SetAppUpdateStatus("Не удалось запустить установщик: " + exception.Message +
                               ". Файл: " + path);
        }
    }

    /// <summary>
    /// Скачивает setup.exe с GitHub, затем с зеркал. Пишет процент в «Общие»
    /// и возвращает путь к проверенному файлу.
    /// </summary>
    private async Task<string> DownloadSetupAsync(AppRelease release, CancellationToken token)
    {
        var target = Path.Combine(Path.GetTempPath(), $"VieNLink-update-{release.Tag}.exe");
        // Прямой GitHub, затем зеркала: у операторов редирект на
        // objects.githubusercontent.com часто блокируется.
        var setupUrls = new[]
        {
            release.SetupUrl!,
            "https://ghfast.top/" + release.SetupUrl,
            "https://gh-proxy.com/" + release.SetupUrl,
        };

        Exception? downloadError = null;
        foreach (var setupUrl in setupUrls)
        {
            token.ThrowIfCancellationRequested();
            try
            {
                using var handler = new SocketsHttpHandler
                {
                    ConnectTimeout = TimeSpan.FromSeconds(20),
                    AllowAutoRedirect = true,
                };
                using var client = new HttpClient(handler) { Timeout = TimeSpan.FromMinutes(15) };
                using var response = await client.GetAsync(
                    setupUrl, HttpCompletionOption.ResponseHeadersRead, token);
                response.EnsureSuccessStatusCode();
                var total = response.Content.Headers.ContentLength ?? -1;
                await using var source = await response.Content.ReadAsStreamAsync(token);
                await using (var file = File.Create(target))
                {
                    var buffer = new byte[81920];
                    long received = 0;
                    var lastPercent = -1;
                    int read;
                    while ((read = await source.ReadAsync(buffer, token)) > 0)
                    {
                        await file.WriteAsync(buffer.AsMemory(0, read), token);
                        received += read;
                        if (total > 0)
                        {
                            var percent = (int)Math.Min(100, received * 100 / total);
                            if (percent != lastPercent)
                            {
                                lastPercent = percent;
                                SetAppUpdateStatus($"Скачиваю установщик {release.Tag}… {percent}%");
                            }
                        }
                    }

                    if (total > 0 && received != total)
                    {
                        throw new IOException($"скачано {received} из {total} байт");
                    }
                }

                downloadError = null;
                break;
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                TryDelete(target);
                throw;
            }
            catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException
                                                 or IOException)
            {
                downloadError = exception;
                TryDelete(target);
                AppLog.Write("update", "Скачивание установщика не удалось: " + setupUrl, exception);
            }
        }

        if (downloadError is not null)
        {
            throw new InvalidOperationException("нет доступа к GitHub и зеркалам: " + downloadError.Message,
                downloadError);
        }

        return target;
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    /// <summary>Пишет ход обновления в «Общие» и в строку состояния окна.</summary>
    private void SetAppUpdateStatus(string message)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(new Action(() => SetAppUpdateStatus(message)));
            return;
        }

        if (AppUpdateText is not null)
        {
            AppUpdateText.Text = message;
        }

        SetStatus("Обновление VieNLink", message);
    }
}
