using Android.App;
using Android.Content;
using Android.OS;
using System.Text;
using System.Text.Json;

namespace VieNLink.Mobile;

/// <summary>
/// Проверка узлов в отдельном процессе :probe. Своё ядро libbox, свои порты и папка
/// кэша: основной VPN и его конфиг не перезапускаются, даже если туннель поднят.
/// Результаты пишутся в файл, основной процесс применяет их к NodeStore.
/// </summary>
[Service(Process = ":probe", Exported = false)]
public sealed class ProbeService : Service
{
    private const string Tag = "probe";

    public override void OnCreate()
    {
        base.OnCreate();
        CoreRuntime.ConfigureProbe(this);
        CoreRuntime.EnsureSetup(this);
    }

    public override StartCommandResult OnStartCommand(Intent? intent, StartCommandFlags flags, int startId)
    {
        var requestPath = intent?.GetStringExtra(ProbeProtocol.ExtraRequest);
        _ = Task.Run(() => RunAsync(requestPath));
        return StartCommandResult.NotSticky;
    }

    public override IBinder? OnBind(Intent? intent) => null;

    private async Task RunAsync(string? requestPath)
    {
        var result = new ProbeResultFile();
        try
        {
            if (string.IsNullOrWhiteSpace(requestPath) || !File.Exists(requestPath))
            {
                result.Error = "Нет файла запроса проверки";
                return;
            }

            var request = JsonSerializer.Deserialize<ProbeRequest>(
                await File.ReadAllTextAsync(requestPath), ProbeProtocol.Json);
            var requested = request?.Nodes ?? new List<ProbeRequestNode>();
            if (requested.Count == 0)
            {
                result.Error = "Список узлов для проверки пуст";
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

            // Лимит конфига: не тащим в пробное ядро больше 200 узлов.
            var nodes = parsed.Select(pair => pair.Node).Take(200).ToList();
            var settings = SettingsStore.Current.Clone();
            settings.TunnelMode = TunnelMode.SystemProxy;
            settings.SiteRouteMode = SiteRouteMode.All;
            settings.OnlySelectedApps = false;

            var config = SingBoxConfigBuilder.Build(
                nodes,
                nodes[0].Tag,
                CoreRuntime.MixedPort,
                CoreRuntime.ApiPort,
                CoreRuntime.ApiSecret,
                settings);

            AppLog.Write(Tag, $"Отдельный процесс: узлов {nodes.Count}, mixed {CoreRuntime.MixedPort}");
            if (!CoreRuntime.Start(config))
            {
                result.Error = "Пробное ядро не создало сервер";
                return;
            }

            using var api = new ClashApiClient(CoreRuntime.ApiPort, CoreRuntime.ApiSecret);
            var ready = false;
            for (var attempt = 0; attempt < 40; attempt++)
            {
                if (await api.PingAsync(CancellationToken.None))
                {
                    ready = true;
                    break;
                }

                await Task.Delay(150);
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
            var summary = await NodeTester.TestBatchAsync(
                api,
                accepted.Select(pair => pair.Node).ToList(),
                progress: null,
                onTested: null,
                CancellationToken.None,
                concurrency: 6,
                timeoutMs: 3500);

            foreach (var (id, node) in accepted)
            {
                result.Nodes.Add(new ProbeResultNode
                {
                    Id = id,
                    Status = node.Status,
                    DelayMs = node.DelayMs,
                    CheckedAtUtc = node.CheckedAtUtc,
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
        catch (Exception exception)
        {
            result.Error = exception.Message;
            AppLog.Write(Tag, "Проверка в отдельном процессе не удалась", exception);
        }
        finally
        {
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
