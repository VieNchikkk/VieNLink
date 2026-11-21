using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Threading;

namespace VieNLink;

public partial class MainWindow
{
    // ---------- шапка, статистика, обновление ядра и интеграция с Windows ----------

    /// <summary>Отсчёт приходит из фонового потока: в UI переносим через диспетчер.</summary>
    private void OnTrafficUpdated(TrafficSample sample)
    {
        if (_closing)
        {
            return;
        }

        Dispatcher.BeginInvoke(() =>
        {
            _lastTrafficSample = sample;
            if (!_manager.IsConnected)
            {
                return;
            }

            HeaderStatsText.Text = _settings.ShowTraffic
                ? $"↓ {FormatSpeed(sample.DownBytesPerSecond)} · ↑ {FormatSpeed(sample.UpBytesPerSecond)}" +
                  $"  ·  {FormatBytes(sample.TotalDown + sample.TotalUp)}"
                : string.Empty;
            UpdateHeaderConnectionInfo();
        });
    }

    /// <summary>Кнопки шапки сами определяют, что отменять и что отключать.</summary>
    private void UpdateHeaderState()
    {
        if (CancelButton is null || HeaderDisconnectButton is null)
        {
            return;
        }

        var connected = _manager.IsConnected;
        var held = _manager.IsKillSwitchHeld;

        var active = new List<string>();
        if (connected)
        {
            active.Add("прокси-узел");
        }
        else if (held)
        {
            active.Add("kill switch");
        }

        if (_zapret.IsRunning)
        {
            active.Add("обход DPI (zapret)");
        }

        if (_desync.IsRunning)
        {
            active.Add("свой обход DPI");
        }

        if (_tunBypass.IsRunning)
        {
            active.Add("TUN-обход");
        }

        HeaderDisconnectButton.Visibility = active.Count > 0 && !_busy
            ? Visibility.Visible
            : Visibility.Collapsed;
        HeaderDisconnectButton.IsEnabled = !_busy;
        HeaderDisconnectButton.ToolTip = active.Count > 0
            ? "Отключить всё: " + string.Join(", ", active)
            : "Отключить все активные подключения";
        CancelButton.ToolTip = _busy
            ? "Прервать: " + _operationLabel
            : "Прервать текущую операцию";

        if (!connected)
        {
            HeaderStatsText.Text = string.Empty;
            HeaderExitText.Text = string.Empty;
        }

        UpdateHeaderConnectionInfo();
    }

    /// <summary>Карточка в шапке: узел при подключении или активный обход без узла.</summary>
    private void UpdateHeaderConnectionInfo()
    {
        if (HeaderConnectionPanel is null || HeaderConnectionText is null || HeaderConnectionQualityText is null)
        {
            return;
        }

        ProxyNode? node = null;
        var connecting = false;
        if (_headerConnectingNode is { } pending)
        {
            node = pending;
            connecting = true;
        }
        else if (_manager.IsConnected && _manager.CurrentNode is { } current)
        {
            node = current;
        }

        if (node is null)
        {
            if (!TryShowBypassHeader())
            {
                HeaderConnectionPanel.Visibility = Visibility.Collapsed;
                _bypassSpeedMode = string.Empty;
                _bypassSpeedMbps = null;
            }

            return;
        }

        var name = string.IsNullOrWhiteSpace(node.Name) ? node.Server : node.Name;
        var endpoint = string.IsNullOrWhiteSpace(node.Server)
            ? name
            : $"{node.Server}:{node.Port}";
        var (flag, countryCode) = ExtractCountryFlag(node.Name);
        var country = CountryNames.FromCode(countryCode);
        var location = flag.Length == 0
            ? string.Empty
            : (country.Length > 0 ? $"{flag} {country}" : flag) + " · ";
        var title = connecting ? $"Подключаюсь: {location}{endpoint}" : $"Сервер: {location}{endpoint}";

        var domain = NodeRanking.CamouflageHost(node);
        if (string.IsNullOrWhiteSpace(domain))
        {
            domain = "—";
        }

        var ping = node.DelayMs is { } ms ? $"{ms} мс" : "—";
        string speed;
        if (!connecting && _lastTrafficSample is { DownBytesPerSecond: > 0 } sample)
        {
            speed = FormatSpeed(sample.DownBytesPerSecond);
        }
        else if (node.SpeedMbps is { } mbps)
        {
            speed = $"{mbps:0.0} Мбит/с";
        }
        else
        {
            speed = "—";
        }

        var (stars, label, brushKey) = RateConnection(node);
        var rating = stars == 0
            ? label
            : new string('★', stars) + new string('☆', 5 - stars) + " " + label;

        var details = new List<string> { title, "Узел: " + name };
        if (country.Length > 0)
        {
            details.Add("Страна: " + country);
        }

        details.Add("Домен: " + domain);
        details.Add($"Пинг: {ping} · Скорость: {speed} · Качество: {rating}");
        if (!string.IsNullOrWhiteSpace(HeaderExitText.Text))
        {
            details.Add(HeaderExitText.Text);
        }

        if (!string.IsNullOrWhiteSpace(HeaderStatsText.Text))
        {
            details.Add(HeaderStatsText.Text);
        }

        ApplyHeaderCard(
            title,
            $"Домен: {domain}",
            $"Пинг: {ping} · Скорость: {speed}",
            $"Качество: {rating}",
            brushKey,
            string.Join(Environment.NewLine, details));
    }

    /// <summary>Обход блокировок без прокси-узла: показываем режим, стратегию и статус.</summary>
    private bool TryShowBypassHeader()
    {
        if (_tunBypass.IsRunning || (_tunBypassStartedDesync && _desync.IsRunning))
        {
            var desyncOnly = !_tunBypass.IsRunning;
            var strategy = desyncOnly
                ? _desync.CurrentStrategy?.Name ?? "WinDivert"
                : TlsFragmentForwarder.Strategies
                      .FirstOrDefault(item => item.Mode == _tunBypass.Mode)?.Name
                  ?? (_settings.TunBypassStrategy >= 0 &&
                      _settings.TunBypassStrategy < TlsFragmentForwarder.Strategies.Count
                          ? TlsFragmentForwarder.Strategies[_settings.TunBypassStrategy].Name
                          : "фрагментация");
            EnsureBypassSpeed(desyncOnly ? "tun-desync" : "tun");
            var speed = _bypassSpeedMbps is { } mbps ? $"{mbps:0.0} Мбит/с" : "—";
            var (stars, qualityLabel, qualityBrush) = RateBypassSpeed(_bypassSpeedMbps);
            var quality = FormatQuality(stars, qualityLabel);
            var details = new List<string>
            {
                desyncOnly ? "Обход: TUN (движок WinDivert)" : "Обход: TUN-обход (Beta)",
                "Стратегия: " + strategy,
                "Прокси-узел не нужен",
                "Скорость: " + speed,
                "Качество: " + quality,
                "Статус: активен",
            };
            if (desyncOnly)
            {
                details.Add("Режим: WinDivert");
            }
            else
            {
                details.Add($"TUN-адаптер VieNLink-Bypass · форвардер 127.0.0.1:{_tunBypass.ForwarderPort}");
            }

            if (_settings.TunBypassBlockQuic)
            {
                details.Add("QUIC: блокируется");
            }

            ApplyHeaderCard(
                desyncOnly ? "Обход: TUN · WinDivert" : "Обход: TUN (Beta)",
                "Стратегия: " + strategy,
                (desyncOnly ? "WinDivert" : "TUN") + " · " + speed,
                "Качество: " + quality,
                qualityBrush,
                string.Join(Environment.NewLine, details));
            return true;
        }

        if (_zapret.IsRunning)
        {
            var preset = _settings.ZapretPreset >= 0 && _settings.ZapretPreset < ZapretService.Presets.Count
                ? ZapretService.Presets[_settings.ZapretPreset].Name
                : "подберётся автоматически";
            EnsureBypassSpeed("zapret");
            var speed = _bypassSpeedMbps is { } mbps ? $"{mbps:0.0} Мбит/с" : "—";
            var (stars, qualityLabel, qualityBrush) = RateBypassSpeed(_bypassSpeedMbps);
            var quality = FormatQuality(stars, qualityLabel);
            var details = new List<string>
            {
                "Обход: Zapret",
                $"Стратегия: {preset}",
                "Режим: WinDivert, без прокси-узла",
                "Правила: только целевые домены",
                "Скорость канала: " + speed,
                "Качество: " + quality,
                "Статус: активен",
            };
            if (_settings.ZapretBlockQuic)
            {
                details.Add("QUIC: блокируется");
            }

            ApplyHeaderCard(
                "Обход: Zapret",
                $"Стратегия: {preset}",
                "WinDivert · " + speed,
                "Качество: " + quality,
                qualityBrush,
                string.Join(Environment.NewLine, details));
            return true;
        }

        if (_desync.IsRunning)
        {
            var strategy = _desync.CurrentStrategy?.Name ?? "текущая";
            EnsureBypassSpeed("desync");
            var speed = _bypassSpeedMbps is { } mbps ? $"{mbps:0.0} Мбит/с" : "—";
            var (stars, qualityLabel, qualityBrush) = RateBypassSpeed(_bypassSpeedMbps);
            var quality = FormatQuality(stars, qualityLabel);
            var details = new List<string>
            {
                "Обход: свой DPI",
                "Стратегия: " + strategy,
                "Режим: WinDivert, без прокси-узла",
                "Правила: только целевые домены",
                "Скорость канала: " + speed,
                "Качество: " + quality,
                "Статус: активен",
            };
            if (_settings.DesyncBlockQuic)
            {
                details.Add("QUIC: блокируется");
            }

            ApplyHeaderCard(
                "Обход: свой DPI",
                "Стратегия: " + strategy,
                "WinDivert · " + speed,
                "Качество: " + quality,
                qualityBrush,
                string.Join(Environment.NewLine, details));
            return true;
        }

        return false;
    }

    /// <summary>Заполняет карточку и общую подсказку.</summary>
    private void ApplyHeaderCard(
        string title,
        string subtitle,
        string stats,
        string badge,
        string brushKey,
        string tooltip)
    {
        HeaderConnectionText.Text = title;
        HeaderConnectionText.ToolTip = tooltip;
        HeaderConnectionQualityText.Text = subtitle;
        HeaderConnectionQualityText.ToolTip = tooltip;
        HeaderConnectionStatsText.Text = stats;
        HeaderConnectionStatsText.ToolTip = tooltip;
        HeaderConnectionRatingText.Text = badge;
        HeaderConnectionRatingText.ToolTip = tooltip;

        var brush = (Brush)FindResource(brushKey);
        HeaderConnectionRatingText.Foreground = brush;
        HeaderConnectionAccent.Background = brush;
        HeaderConnectionPanel.ToolTip = tooltip;
        HeaderConnectionPanel.Visibility = Visibility.Visible;
    }

    /// <summary>Замер скорости обхода в фоне: у TUN — через curl, у zapret/desync — напрямую.</summary>
    private void EnsureBypassSpeed(string mode)
    {
        if (_closing || _bypassSpeedMeasuring)
        {
            return;
        }

        if (_bypassSpeedMode == mode && _bypassSpeedMbps is not null &&
            DateTime.UtcNow - _bypassSpeedMeasuredUtc < TimeSpan.FromMinutes(5))
        {
            return;
        }

        if (_bypassSpeedMode != mode)
        {
            _bypassSpeedMbps = null;
        }

        _bypassSpeedMode = mode;
        _bypassSpeedMeasuring = true;
        _ = MeasureBypassSpeedAsync(mode);
    }

    private async Task MeasureBypassSpeedAsync(string mode)
    {
        try
        {
            var mbps = mode is "tun" or "tun-desync"
                ? await TunBypassService.MeasureSpeedAsync(CancellationToken.None)
                : await MeasureDirectSpeedAsync(CancellationToken.None);
            if (mbps is { } value && _bypassSpeedMode == mode)
            {
                _bypassSpeedMbps = value;
                _bypassSpeedMeasuredUtc = DateTime.UtcNow;
            }
        }
        catch (Exception exception)
        {
            AppLog.Write("speed", "Не удалось замерить скорость обхода", exception);
        }
        finally
        {
            _bypassSpeedMeasuring = false;
            if (!_closing)
            {
                _ = Dispatcher.BeginInvoke(() => UpdateHeaderConnectionInfo());
            }
        }
    }

    /// <summary>Скорость канала для zapret/desync: замер идёт напрямую, без прокси.</summary>
    private static async Task<double?> MeasureDirectSpeedAsync(CancellationToken cancellationToken)
    {
        var urls = new[]
        {
            "https://speed.cloudflare.com/__down?bytes=2000000",
            "https://proof.ovh.net/files/1Mb.dat",
            "https://speedtest.selectel.ru/10MB",
        };

        using var handler = new SocketsHttpHandler
        {
            UseProxy = false,
            ConnectTimeout = TimeSpan.FromSeconds(5),
        };
        using var client = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(15),
        };

        foreach (var url in urls)
        {
            try
            {
                var stopwatch = Stopwatch.StartNew();
                using var response = await client.GetAsync(
                    url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
                response.EnsureSuccessStatusCode();
                await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
                var buffer = new byte[64 * 1024];
                long total = 0;
                while (stopwatch.Elapsed < TimeSpan.FromSeconds(8))
                {
                    var read = await stream.ReadAsync(buffer, cancellationToken);
                    if (read == 0)
                    {
                        break;
                    }

                    total += read;
                    if (total >= 2_000_000)
                    {
                        break;
                    }
                }

                if (total >= 200_000 && stopwatch.Elapsed.TotalSeconds > 0.1)
                {
                    return Math.Round(total * 8.0 / 1_000_000 / stopwatch.Elapsed.TotalSeconds, 1);
                }
            }
            catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
            {
            }
        }

        return null;
    }

    /// <summary>Флаг и код страны из названия узла: настоящий эмодзи или экранированный \U0001F1XX.</summary>
    private static (string Flag, string Code) ExtractCountryFlag(string? source)
    {
        if (string.IsNullOrWhiteSpace(source))
        {
            return (string.Empty, string.Empty);
        }

        var text = Regex.Replace(source, @"\\U0001F1([0-9A-Fa-f]{2})", match =>
            char.ConvertFromUtf32(0x1F1E6 + Convert.ToInt32(match.Groups[1].Value, 16) - 0xE6));
        text = Regex.Replace(text, @"\\u([0-9A-Fa-f]{4})", match =>
            ((char)Convert.ToInt32(match.Groups[1].Value, 16)).ToString());

        for (var i = 0; i + 3 < text.Length; i++)
        {
            if (!char.IsHighSurrogate(text[i]) || !char.IsLowSurrogate(text[i + 1]))
            {
                continue;
            }

            var first = char.ConvertToUtf32(text[i], text[i + 1]);
            if (first < 0x1F1E6 || first > 0x1F1FF)
            {
                continue;
            }

            if (!char.IsHighSurrogate(text[i + 2]) || !char.IsLowSurrogate(text[i + 3]))
            {
                continue;
            }

            var second = char.ConvertToUtf32(text[i + 2], text[i + 3]);
            if (second >= 0x1F1E6 && second <= 0x1F1FF)
            {
                var code = new string(new[]
                {
                    (char)('A' + first - 0x1F1E6),
                    (char)('A' + second - 0x1F1E6),
                });
                return (text.Substring(i, 4), code);
            }
        }

        return (string.Empty, string.Empty);
    }

    /// <summary>Оценка связи по задержке, открытым доменам, скорости и проверке сертификата.</summary>
    private static (int Stars, string Label, string BrushKey) RateConnection(ProxyNode node)
    {
        if (node.DelayMs is not { } ping)
        {
            return (0, "не оценено", "MutedStrongBrush");
        }

        var stars = 5;
        if (ping > 80)
        {
            stars--;
        }

        if (ping > 200)
        {
            stars--;
        }

        if (ping > 500)
        {
            stars--;
        }

        if (node.OpenDomains <= 0)
        {
            stars -= 2;
        }
        else if (node.OpenDomains < 5)
        {
            stars--;
        }

        if (node.AllowInsecure)
        {
            stars--;
        }

        if (node.SpeedMbps is { } speed && speed < 5)
        {
            stars--;
        }

        stars = Math.Clamp(stars, 1, 5);
        var label = stars switch
        {
            5 => "отличное",
            4 => "хорошее",
            3 => "среднее",
            2 => "слабое",
            _ => "плохое",
        };
        var brush = stars >= 4 ? "AccentBrush" : stars == 3 ? "WarningBrush" : "DangerBrush";
        return (stars, label, brush);
    }

    /// <summary>Оценка обхода по измеренной скорости канала.</summary>
    private static (int Stars, string Label, string BrushKey) RateBypassSpeed(double? speedMbps)
    {
        if (speedMbps is not { } speed)
        {
            return (0, "оценивается", "MutedStrongBrush");
        }

        var stars = speed switch
        {
            >= 50 => 5,
            >= 20 => 4,
            >= 8 => 3,
            >= 3 => 2,
            _ => 1,
        };
        var label = stars switch
        {
            5 => "отличное",
            4 => "хорошее",
            3 => "среднее",
            2 => "слабое",
            _ => "плохое",
        };
        var brush = stars >= 4 ? "AccentBrush" : stars == 3 ? "WarningBrush" : "DangerBrush";
        return (stars, label, brush);
    }

    private static string FormatQuality(int stars, string label) =>
        stars == 0 ? label : new string('★', stars) + new string('☆', 5 - stars) + " " + label;

    /// <summary>Страна и IP выхода: один запрос через тот же узел, что и браузер.</summary>
    private async Task RefreshExitInfoAsync()
    {
        if (!_settings.ShowExitInfo)
        {
            HeaderExitText.Text = string.Empty;
            UpdateHeaderConnectionInfo();
            return;
        }

        HeaderExitText.Text = "Выход: определение…";
        UpdateHeaderConnectionInfo();
        try
        {
            var info = await ExitInfoService.LookupAsync(_manager.ActiveMixedPort, CancellationToken.None);
            if (!_manager.IsConnected || _closing)
            {
                return;
            }

            HeaderExitText.Text = info is null ? "Выход: не определён" : "Выход: " + info.Label;
            UpdateHeaderConnectionInfo();
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            AppLog.Write("exit", "Не удалось определить выходной IP", exception);
            HeaderExitText.Text = "Выход: не определён";
            UpdateHeaderConnectionInfo();
        }
    }

    /// <summary>Пресеты сайтов: добавляют домены в список и включают режим «только сайты из списка».</summary>
    private void OnSitePresetClick(object sender, RoutedEventArgs e)
    {
        if (sender is not CheckBox { Tag: string domains } box)
        {
            return;
        }

        var preset = SplitDomains(domains);
        var list = SiteListBox.Text
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();

        if (box.IsChecked == true)
        {
            foreach (var domain in preset)
            {
                if (!list.Contains(domain, StringComparer.OrdinalIgnoreCase))
                {
                    list.Add(domain);
                }
            }

            RouteOnlyRadio.IsChecked = true;
            FooterText.Text = $"«{box.Content}» добавлен: через узел пойдут только эти домены";
        }
        else
        {
            // Домен мог остаться нужен другому включённому пресету — его не убираем.
            var stillNeeded = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (box.Parent is Panel panel)
            {
                foreach (var other in panel.Children.OfType<CheckBox>())
                {
                    if (!ReferenceEquals(other, box) && other.IsChecked == true && other.Tag is string otherDomains)
                    {
                        foreach (var domain in SplitDomains(otherDomains))
                        {
                            stillNeeded.Add(domain);
                        }
                    }
                }
            }

            list.RemoveAll(domain => preset.Contains(domain, StringComparer.OrdinalIgnoreCase) &&
                                     !stillNeeded.Contains(domain));
            FooterText.Text = $"«{box.Content}» убран из списка";
        }

        SiteListBox.Text = string.Join(Environment.NewLine, list);
        OnRoutingChanged(this, new RoutedEventArgs());
    }

    private static List<string> SplitDomains(string text) =>
        text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();

    private void OnAutostartChanged(object sender, RoutedEventArgs e)
    {
        if (_loadingSettings)
        {
            return;
        }

        var enabled = AutostartCheck.IsChecked == true;
        try
        {
            AutostartService.SetEnabled(enabled);
            _settings.RunAtStartup = enabled;
            SaveSettings();
            FooterText.Text = enabled ? "VieNLink будет запускаться при входе в Windows" : "Автозапуск выключен";
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException
                                             or System.Security.SecurityException
                                             or InvalidOperationException)
        {
            ReportError("Не удалось изменить автозапуск", exception);
        }
    }

    /// <summary>Замер скорости: выбранный узел или 10 лучших рабочих. Требует запущенного ядра.</summary>
    private async void OnSpeedTestClick(object sender, RoutedEventArgs e)
    {
        if (!BeginOperation("замер скорости"))
        {
            return;
        }

        try
        {
            var selected = SelectedNode();
            List<ProxyNode> targets;
            if (_manager.IsConnected)
            {
                // Переключать узел под живым соединением нельзя: замеряем только текущий.
                if (_manager.CurrentNode is not { } current)
                {
                    SetStatus("Замер скорости", "Нет активного узла");
                    return;
                }

                if (selected is not null && selected.Id != current.Id)
                {
                    SetStatus("Замер скорости", "При активном соединении можно замерить только текущий узел");
                }

                targets = new List<ProxyNode> { current };
            }
            else
            {
                targets = selected is not null
                    ? new List<ProxyNode> { selected }
                    : _workingRows
                        .Select(row => row.Node)
                        .Where(node => node.DelayMs is not null)
                        .OrderByDescending(node => node.OpenDomains)
                        .ThenBy(node => node.DelayMs)
                        .Take(10)
                        .ToList();
            }

            if (targets.Count == 0)
            {
                SetStatus("Нет узлов для замера", "Сначала проверьте узлы: «Все узлы» на вкладке «Узлы»");
                return;
            }

            if (!_manager.IsConnected)
            {
                SetStatus("Замер скорости", "Поднимаю ядро для замера…");
                await _manager.PrepareTestCoreAsync(targets, null, _operation!.Token);
            }

            var done = 0;
            foreach (var node in targets)
            {
                _operation!.Token.ThrowIfCancellationRequested();
                done++;
                SetStatus("Замер скорости", $"{node.Name}: {done} из {targets.Count}");
                double? mbps = null;
                try
                {
                    mbps = await _manager.MeasureSpeedAsync(node, _operation.Token);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    // Один проблемный узел не должен срывать замер остальных.
                    AppLog.Write("speed", $"Замер «{node.Name}» не удался", exception);
                }

                _rowIndex.TryGetValue(node.Id, out var row);
                row?.Refresh();
                AppendLog(mbps is null
                    ? $"Скорость «{node.Name}»: замер не удался"
                    : $"Скорость «{node.Name}»: {mbps:0.0} Мбит/с");
            }

            NodeSourceService.SaveCache(AllRows.Select(r => r.Node).ToList());
            SetStatus("Замер скорости завершён", $"Замерено узлов: {targets.Count}");
        }
        catch (OperationCanceledException)
        {
            SetStatus("Замер скорости отменён", string.Empty);
        }
        catch (Exception exception)
        {
            ReportError("Не удалось замерить скорость", exception);
        }
        finally
        {
            EndOperation();
            UpdateConnectionUi();
        }
    }

    /// <summary>Проверка и обновление ядра sing-box до последнего релиза.</summary>
    private async void OnCheckCoreUpdateClick(object sender, RoutedEventArgs e)
    {
        if (!BeginOperation("обновление ядра"))
        {
            return;
        }

        try
        {
            CoreUpdateText.Text = "Проверяю последнюю версию sing-box…";
            var latest = await SingBoxCore.CheckLatestVersionAsync(_operation!.Token);
            if (string.IsNullOrWhiteSpace(latest))
            {
                CoreUpdateText.Text = "Не удалось проверить: GitHub и зеркала не ответили.";
                return;
            }

            var installed = SingBoxCore.InstalledVersion;
            if (Version.TryParse(latest, out var latestVersion) &&
                Version.TryParse(installed, out var installedVersion) &&
                latestVersion <= installedVersion)
            {
                CoreUpdateText.Text = $"Установлена актуальная версия {installed}.";
                return;
            }

            var answer = MessageBox.Show(
                this,
                $"Доступна версия {latest}, установлена {installed}.\n\n" +
                "Скачать и заменить ядро? Соединение будет разорвано.",
                "VieNLink",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);
            if (answer != MessageBoxResult.Yes)
            {
                CoreUpdateText.Text = $"Обновление до {latest} отменено.";
                return;
            }

            if (_manager.IsConnected)
            {
                await _manager.DisconnectAsync();
                UpdateConnectionUi();
                await Task.Delay(500);
            }

            var progress = new Progress<string>(line => CoreUpdateText.Text = line);
            await SingBoxCore.UpdateAsync(latest, progress, _operation.Token);
            CoreUpdateText.Text = $"Ядро обновлено до {latest}. Подключитесь заново.";
            UpdateCoreStatus();
        }
        catch (OperationCanceledException)
        {
            CoreUpdateText.Text = "Обновление ядра отменено.";
        }
        catch (Exception exception)
        {
            ReportError("Не удалось обновить ядро", exception);
            CoreUpdateText.Text = exception.Message;
        }
        finally
        {
            EndOperation();
        }
    }

    /// <summary>
    /// При запуске сама проверяет последнюю версию sing-box и ставит её без вопросов.
    /// Скрытая секция «Ядро sing-box» остаётся только для совместимости.
    /// </summary>
    private async Task AutoUpdateCoreOnStartupAsync()
    {
        if (_closing)
        {
            return;
        }

        try
        {
            CoreUpdateText.Text = "Проверяю обновление ядра sing-box…";

            if (!SingBoxCore.IsInstalled)
            {
                var installProgress = new Progress<string>(line =>
                {
                    CoreUpdateText.Text = line;
                    StatusDetail.Text = line;
                });
                await SingBoxCore.EnsureAsync(installProgress, CancellationToken.None);
                UpdateCoreStatus();
                CoreUpdateText.Text = "Ядро sing-box установлено автоматически.";
                AppendLog("Ядро sing-box установлено автоматически.");
                return;
            }

            var latest = await SingBoxCore.CheckLatestVersionAsync(CancellationToken.None);
            if (string.IsNullOrWhiteSpace(latest))
            {
                AppLog.Write("core", "Автопроверка ядра: GitHub и зеркала не ответили.");
                return;
            }

            var installed = SingBoxCore.InstalledVersion;
            if (Version.TryParse(latest, out var latestVersion) &&
                Version.TryParse(installed, out var installedVersion) &&
                latestVersion <= installedVersion)
            {
                CoreUpdateText.Text = $"Ядро актуально: {installed}.";
                AppLog.Write("core", $"Автопроверка ядра: установлена актуальная версия {installed}.");
                return;
            }

            if (_manager.IsConnected || _zapret.IsRunning || _desync.IsRunning || _tunBypass.IsRunning)
            {
                AppendLog($"Ядро: доступна версия {latest}, обновление отложено до отключения обхода/узла.");
                return;
            }

            AppendLog($"Ядро: автообновление {installed} → {latest}");
            var progress = new Progress<string>(line =>
            {
                CoreUpdateText.Text = line;
                StatusDetail.Text = line;
            });
            await SingBoxCore.UpdateAsync(latest, progress, CancellationToken.None);
            UpdateCoreStatus();
            CoreUpdateText.Text = $"Ядро обновлено до {latest}.";
            AppendLog($"Ядро sing-box обновлено до {latest}.");
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            AppLog.Write("core", "Автоматическое обновление ядра не удалось", exception);
            CoreUpdateText.Text = "Не удалось автоматически проверить/обновить ядро.";
        }
    }

    /// <summary>Приложения Магазина не видят 127.0.0.1 без снятия loopback-изоляции.</summary>
    private async void OnStoreAppsLoopbackClick(object sender, RoutedEventArgs e)
    {
        if (!IsAdministrator())
        {
            AskElevation(
                "Снятие loopback-изоляции для приложений Магазина требует прав администратора.\n\n" +
                "Перезапустить VieNLink с повышением прав?",
                "--store-loopback");
            return;
        }

        var (ok, message) = await WindowsIntegration.EnableStoreAppLoopbackAsync(CancellationToken.None);
        StoreLoopbackText.Text = message;
        FooterText.Text = message;
        if (!ok)
        {
            AppLog.Write("ui", "CheckNetIsolation: " + message);
        }
    }

    /// <summary>Выбор узла из меню трея.</summary>
    private void OnTrayNodeSelected(string nodeId)
    {
        var node = AllRows.Select(row => row.Node).FirstOrDefault(candidate => candidate.Id == nodeId);
        if (node is null)
        {
            return;
        }

        Dispatcher.BeginInvoke(new Action(async () =>
        {
            try
            {
                if (_busy)
                {
                    return;
                }

                Show();
                WindowState = WindowState.Normal;
                if (_manager.IsConnected && _manager.CurrentNode?.Id == node.Id)
                {
                    return;
                }

                await ConnectAsync(node);
            }
            catch (Exception exception)
            {
                AppLog.Write("tray", "Не удалось подключиться к узлу из трея", exception);
            }
        }));
    }

    /// <summary>Верхние узлы для меню трея: личные, быстрые и по скорости.</summary>
    private void UpdateTrayNodes()
    {
        if (_tray is null)
        {
            return;
        }

        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(UpdateTrayNodes);
            return;
        }

        var nodes = _workingRows
            .Select(row => row.Node)
            .Where(node => node.DelayMs is not null)
            .OrderByDescending(node => node.IsMine)
            .ThenByDescending(node => node.SpeedMbps ?? -1)
            .ThenBy(node => node.DelayMs)
            .Take(8)
            .ToList();
        _tray.UpdateNodes(nodes, _manager.CurrentNode?.Id);
    }

    private static string FormatSpeed(long bytesPerSecond) => bytesPerSecond switch
    {
        < 1024 => bytesPerSecond + " Б/с",
        < 1024 * 1024 => (bytesPerSecond / 1024.0).ToString("0") + " КБ/с",
        _ => (bytesPerSecond / 1048576.0).ToString("0.0") + " МБ/с",
    };

    private static string FormatBytes(long bytes) => bytes switch
    {
        < 1024 => bytes + " Б",
        < 1024 * 1024 => (bytes / 1024.0).ToString("0") + " КБ",
        < 1024L * 1024 * 1024 => (bytes / 1048576.0).ToString("0.0") + " МБ",
        _ => (bytes / 1073741824.0).ToString("0.00") + " ГБ",
    };
}
