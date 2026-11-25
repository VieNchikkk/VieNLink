using System.Diagnostics;
using System.Globalization;
using System.Net.NetworkInformation;
using System.Text;

namespace VieNLink;

/// <summary>
/// Сеть, в которой работает ПК: «wifi:&lt;SSID&gt;» или «eth:&lt;адаптер&gt;». Нужна, чтобы
/// запоминать рабочую стратегию обхода для конкретной сети (дом, работа, раздача с телефона)
/// и пробовать её первой, а не перебирать всё заново на каждой новой сети.
/// </summary>
internal static class NetworkIdentity
{
    private static readonly object Sync = new();
    private static readonly TimeSpan CacheFor = TimeSpan.FromSeconds(30);

    private static DateTime _stamp = DateTime.MinValue;
    private static bool _cached;
    private static volatile bool _refreshing;
    private static string _key = string.Empty;
    private static string _label = "сеть не определена";

    /// <summary>Ключ сети для записей профилей; пустой — сеть не определена.</summary>
    public static string Key => Current().Key;

    /// <summary>Название сети для интерфейса и журнала.</summary>
    public static string Label => Current().Label;

    /// <summary>Список сетей для записи профиля (пустой, если сеть не определена).</summary>
    public static List<string> Networks() =>
        Key is { Length: > 0 } key ? new List<string> { key } : new List<string>();

    /// <summary>
    /// Отдаём кэш и обновляем его в фоне: интерфейс обновляется часто, а netsh — отдельный
    /// процесс, который может отвечать сотни миллисекунд. Синхронно считаем только один раз,
    /// когда данных ещё нет.
    /// </summary>
    private static (string Key, string Label) Current()
    {
        lock (Sync)
        {
            if (_cached)
            {
                if (DateTime.UtcNow - _stamp >= CacheFor && !_refreshing)
                {
                    StartRefresh();
                }

                return (_key, _label);
            }
        }

        Refresh();
        lock (Sync)
        {
            return (_key, _label);
        }
    }

    private static void StartRefresh()
    {
        _refreshing = true;
        _ = Task.Run(() =>
        {
            try
            {
                Refresh();
            }
            finally
            {
                _refreshing = false;
            }
        });
    }

    private static void Refresh()
    {
        var key = string.Empty;
        var label = "сеть не определена";
        try
        {
            var ssid = WifiSsid();
            if (ssid.Length > 0)
            {
                key = "wifi:" + ssid;
                label = $"Wi-Fi «{ssid}»";
            }
            else
            {
                var wired = WiredName();
                if (wired.Length > 0)
                {
                    key = "eth:" + wired;
                    label = $"Ethernet «{wired}»";
                }
            }
        }
        catch (Exception exception)
        {
            AppLog.Write("bypass", "Не удалось определить сеть", exception);
        }

        lock (Sync)
        {
            _key = key;
            _label = label;
            _stamp = DateTime.UtcNow;
            _cached = true;
        }
    }

    /// <summary>
    /// SSID подключённого Wi-Fi. В RU-сборках Windows netsh печатает в кодировке OEM,
    /// поэтому вывод читаем той же кодировкой, а при неудаче — UTF-8.
    /// </summary>
    private static string WifiSsid()
    {
        var output = Run("netsh", "wlan show interfaces");
        if (output is null)
        {
            return string.Empty;
        }

        foreach (var raw in output.Split('\n'))
        {
            var line = raw.Trim().TrimEnd('\r');
            var colon = line.IndexOf(':');
            if (colon <= 0)
            {
                continue;
            }

            // BSSID — MAC точки доступа, нам нужен именно SSID.
            if (!line[..colon].Trim().Equals("SSID", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var value = line[(colon + 1)..].Trim();
            if (value.Length == 0)
            {
                continue;
            }

            return value;
        }

        return string.Empty;
    }

    /// <summary>Проводной адаптер с шлюзом — ключ сети, когда Wi-Fi не подключён.</summary>
    private static string WiredName()
    {
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up ||
                nic.NetworkInterfaceType is not (NetworkInterfaceType.Ethernet
                    or NetworkInterfaceType.GigabitEthernet))
            {
                continue;
            }

            if (nic.GetIPProperties().GatewayAddresses.Count == 0)
            {
                continue;
            }

            return string.IsNullOrWhiteSpace(nic.Name) ? nic.Description : nic.Name;
        }

        return string.Empty;
    }

    private static string? Run(string file, string arguments)
    {
        try
        {
            var encoding = ConsoleEncoding();
            var info = new ProcessStartInfo(file, arguments)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = encoding,
                StandardErrorEncoding = encoding,
            };

            using var process = Process.Start(info);
            if (process is null)
            {
                return null;
            }

            var output = process.StandardOutput.ReadToEnd();
            if (!process.WaitForExit(3000))
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch (Exception)
                {
                    // Процесс уже завершился — ничего не делаем.
                }
            }

            return output;
        }
        catch (Exception exception)
        {
            AppLog.Write("bypass", $"Не удалось запустить {file}", exception);
            return null;
        }
    }

    private static Encoding ConsoleEncoding()
    {
        try
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            return Encoding.GetEncoding(CultureInfo.CurrentCulture.TextInfo.OEMCodePage);
        }
        catch (Exception)
        {
            return Encoding.UTF8;
        }
    }
}
