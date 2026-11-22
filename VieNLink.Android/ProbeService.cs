using Android.App;
using Android.Content;
using Android.OS;
using System.Text;
using System.Text.Json;

namespace VieNLink.Mobile;

/// <summary>
/// Проверка серверов в отдельном процессе :probe. Своё ядро libbox, свои порты и папка
/// кэша: основной VPN и его конфиг не перезапускаются, даже если туннель поднят.
/// Результаты пишутся в файл, основной процесс применяет их к NodeStore.
/// </summary>
[Service(Process = ":probe", Exported = false,
    ForegroundServiceType = Android.Content.PM.ForegroundService.TypeDataSync)]
public sealed class ProbeService : Service
{
    private const string Tag = "probe";
    private const int NotificationId = 1002;
    private const string ChannelId = "vienlink-probe";

    private readonly object _progressLock = new();
    private readonly CancellationTokenSource _workCts = new();
    private DateTime _lastProgressWrite = DateTime.MinValue;
    private int _progressTested;
    private int _progressWorking;
    private int _progressTotal;

    public override void OnCreate()
    {
        base.OnCreate();
        CoreRuntime.ConfigureProbe(this);
        // Уведомление показываем сразу: Android даёт ~5 секунд на startForeground
        // после StartForegroundService, а Libbox.Setup может занять больше.
        StartProbeForeground();
    }

    public override StartCommandResult OnStartCommand(Intent? intent, StartCommandFlags flags, int startId)
    {
        if (string.Equals(intent?.Action, ProbeProtocol.StopAction, StringComparison.Ordinal))
        {
            // Отмена из основного процесса: не ждём конца партии и не тратим трафик.
            StopNow();
            return StartCommandResult.NotSticky;
        }

        var requestPath = intent?.GetStringExtra(ProbeProtocol.ExtraRequest);
        _ = Task.Run(() => RunAsync(requestPath));
        return StartCommandResult.NotSticky;
    }

    /// <summary>Останавливает проверку и сразу завершает процесс :probe.</summary>
    private void StopNow()
    {
        try
        {
            _workCts.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // Проверка уже завершилась.
        }

        try
        {
            StopSelf();
        }
        catch (Exception)
        {
            // Служба могла уже остановиться.
        }

        Android.OS.Process.KillProcess(Android.OS.Process.MyPid());
    }

    public override void OnDestroy()
    {
        try
        {
            _workCts.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // Источник отмены уже освобождён.
        }

        base.OnDestroy();
    }

    /// <summary>
    /// Проверка может стартовать из фона по будильнику: foreground-служба не даёт
    /// Android убить `:probe` до записи результатов.
    /// </summary>
    private void StartProbeForeground()
    {
        try
        {
            if (Build.VERSION.SdkInt >= BuildVersionCodes.O &&
                GetSystemService(NotificationService) is NotificationManager manager)
            {
                var channel = new NotificationChannel(ChannelId, "Проверка серверов", NotificationImportance.Low)
                {
                    Description = "Фоновая проверка прокси-серверов VieNLink",
                };
                manager.CreateNotificationChannel(channel);
            }

            var builder = Build.VERSION.SdkInt >= BuildVersionCodes.O
                ? new Notification.Builder(this, ChannelId)
                : new Notification.Builder(this);
            builder
                .SetContentTitle("VieNLink")
                .SetContentText("Проверка серверов")
                .SetSmallIcon(Resource.Drawable.ic_notification)
                .SetOngoing(true)
                .SetShowWhen(false);

            var notification = builder.Build();
            if (Build.VERSION.SdkInt >= BuildVersionCodes.Q)
            {
                StartForeground(NotificationId, notification, Android.Content.PM.ForegroundService.TypeDataSync);
            }
            else
            {
                StartForeground(NotificationId, notification);
            }
        }
        catch (Exception exception)
        {
            // Без foreground-режима проверка всё равно попробует выполниться.
            AppLog.Write(Tag, "Не удалось показать уведомление проверки", exception);
        }
    }

    public override IBinder? OnBind(Intent? intent) => null;

    /// <summary>
    /// Пишет ход проверки в файл: основной процесс читает его и показывает
    /// «проверено N из M, рабочих K». Частые записи не нужны — не чаще двух в секунду.
    /// </summary>
    private void WriteProgress(string stage, int tested, int total, int working, bool force = false)
    {
        lock (_progressLock)
        {
            if (!force && DateTime.UtcNow - _lastProgressWrite < TimeSpan.FromMilliseconds(500))
            {
                return;
            }

            _lastProgressWrite = DateTime.UtcNow;
            try
            {
                Directory.CreateDirectory(AppPaths.DataDirectory);
                var file = new ProbeProgressFile
                {
                    Stage = stage,
                    Tested = tested,
                    Total = total,
                    Working = working,
                };
                File.WriteAllText(ProbeProtocol.ProgressPath,
                    JsonSerializer.Serialize(file, ProbeProtocol.Json), new UTF8Encoding(false));
            }
            catch (Exception exception)
            {
                AppLog.Write(Tag, "Не удалось записать ход проверки", exception);
            }
        }
    }

    private async Task RunAsync(string? requestPath)
    {
        var result = new ProbeResultFile();
        var token = _workCts.Token;
        // Первая отметка пишется сразу: по её появлению основной процесс понимает,
        // что служба действительно запустилась, а не пропала без ответа.
        WriteProgress("Запускаю проверку…", 0, 0, 0, force: true);
        try
        {
            CoreRuntime.EnsureSetup(this);
            if (string.IsNullOrWhiteSpace(requestPath) || !File.Exists(requestPath))
            {
                result.Error = "Нет файла запроса проверки";
                return;
            }

            var request = JsonSerializer.Deserialize<ProbeRequest>(
                await File.ReadAllTextAsync(requestPath, token), ProbeProtocol.Json);
            var requested = request?.Nodes ?? new List<ProbeRequestNode>();
            if (requested.Count == 0)
            {
                result.Error = "Список серверов для проверки пуст";
                return;
            }

            // Разбираем ссылки в этом процессе: NodeStore основного процесса не трогаем.
            var parsed = new List<(string Id, ProxyNode Node)>();
            foreach (var item in requested)
            {
                var node = LinkParser.Parse(item.RawLink, "probe")
                           ?? SubscriptionParser.Parse(item.RawLink, "probe").FirstOrDefault();
                if (node is not null)
                {
                    parsed.Add((item.Id, node));
                }
            }

            if (parsed.Count == 0)
            {
                result.Error = "Ни одна ссылка не разобралась";
                return;
            }

            _progressTotal = parsed.Count;
            WriteProgress("Готовлю пробное ядро…", 0, _progressTotal, 0, force: true);

            // Лимит конфига: не тащим в пробное ядро больше 200 серверов.
            var nodes = parsed.Select(pair => pair.Node).Take(200).ToList();
            var settings = SettingsStore.Current.Clone();
            settings.TunnelMode = TunnelMode.SystemProxy;
            settings.SiteRouteMode = SiteRouteMode.All;
            settings.OnlySelectedApps = false;
            settings.AppRouteMode = AppRouteMode.All;
            settings.AppList = string.Empty;

            var config = SingBoxConfigBuilder.Build(
                nodes,
                nodes[0].Tag,
                CoreRuntime.MixedPort,
                CoreRuntime.ApiPort,
                CoreRuntime.ApiSecret,
                settings);

            AppLog.Write(Tag, $"Отдельный процесс: серверов {nodes.Count}, mixed {CoreRuntime.MixedPort}");
            if (!CoreRuntime.Start(config))
            {
                result.Error = "Пробное ядро не создало сервер";
                return;
            }

            using var api = new ClashApiClient(CoreRuntime.ApiPort, CoreRuntime.ApiSecret);
            var ready = false;
            for (var attempt = 0; attempt < 40; attempt++)
            {
                if (await api.PingAsync(token))
                {
                    ready = true;
                    break;
                }

                await Task.Delay(150, token);
            }

            if (!ready)
            {
                result.Error = "Clash API пробного ядра не ответил";
                return;
            }

            var loaded = CoreRuntime.LoadedTags;
            var accepted = parsed
                .Where(pair => loaded.Contains(pair.Node.Tag))
                .ToList();
            _progressTotal = accepted.Count;
            Interlocked.Exchange(ref _progressTested, 0);
            Interlocked.Exchange(ref _progressWorking, 0);
            WriteProgress("Проверяю серверы…", 0, _progressTotal, 0, force: true);

            var summary = await NodeTester.TestBatchAsync(
                api,
                accepted.Select(pair => pair.Node).ToList(),
                progress: null,
                onTested: node =>
                {
                    var tested = Interlocked.Increment(ref _progressTested);
                    if (node.Status == NodeStatus.Working)
                    {
                        Interlocked.Increment(ref _progressWorking);
                    }

                    WriteProgress("Проверяю серверы…", tested, _progressTotal,
                        Volatile.Read(ref _progressWorking));
                },
                token,
                concurrency: 8,
                timeoutMs: 3000,
                fullDomains: request?.FullDomains ?? true);

            foreach (var (id, node) in accepted)
            {
                result.Nodes.Add(new ProbeResultNode
                {
                    Id = id,
                    Status = node.Status,
                    DelayMs = node.DelayMs,
                    CheckedAtUtc = node.CheckedAtUtc,
                    TriedAtUtc = node.TriedAtUtc,
                    YoutubeOk = node.YoutubeOk,
                    TelegramOk = node.TelegramOk,
                    UniversalOk = node.UniversalOk,
                    DiscordOk = node.DiscordOk,
                    OpenDomains = node.OpenDomains,
                    FailCount = node.FailCount,
                });
            }

            if (summary.Error is not null)
            {
                result.Error = summary.Error;
            }

            AppLog.Write(Tag, $"Отдельный процесс: рабочих {summary.Working} из {summary.Tested}");
        }
        catch (System.OperationCanceledException)
        {
            result.Error = "Проверка остановлена";
            AppLog.Write(Tag, "Проверка остановлена пользователем");
        }
        catch (Exception exception)
        {
            result.Error = exception.Message;
            AppLog.Write(Tag, "Проверка в отдельном процессе не удалась", exception);
        }
        finally
        {
            WriteProgress(result.Error is null ? "Записываю результаты…" : "Проверка не удалась",
                Volatile.Read(ref _progressTested), _progressTotal, Volatile.Read(ref _progressWorking),
                force: true);
            try
            {
                CoreRuntime.Stop();
            }
            catch (Exception)
            {
                // Ядро могло уже остановиться.
            }

            try
            {
                Directory.CreateDirectory(AppPaths.DataDirectory);
                await File.WriteAllTextAsync(
                    ProbeProtocol.ResultPath,
                    JsonSerializer.Serialize(result, ProbeProtocol.Json),
                    new UTF8Encoding(false));
            }
            catch (Exception exception)
            {
                AppLog.Write(Tag, "Не удалось записать результаты проверки", exception);
            }

            try
            {
                if (Build.VERSION.SdkInt >= BuildVersionCodes.O)
                {
                    StopForeground(StopForegroundFlags.Remove);
                }
            }
            catch (Exception)
            {
                // Уведомление могло быть уже снято.
            }

            StopSelf();

            // Процесс :probe нужен только на время проверки: после записи результата
            // завершаем его, чтобы Go-куча не висела в памяти телефона.
            _ = Task.Run(async () =>
            {
                await Task.Delay(500);
                Android.OS.Process.KillProcess(Android.OS.Process.MyPid());
            });
        }
    }
}
