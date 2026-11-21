using Android.App;
using Android.Content;
using Android.Graphics;
using Android.OS;
using Android.Provider;
using Android.Views;
using Android.Widget;
using System.Net;
using System.Net.Http;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Text;

namespace VieNLink.Mobile;

/// <summary>
/// Диагностика: состояние туннеля, ядра, выбранный узел, размеры списков и проверка
/// реального IP через mixed-порт по сравнению с прямым выходом.
/// </summary>
#if DEBUG
[Activity(Label = "Диагностика", Theme = "@android:style/Theme.Material.Light.NoActionBar", Exported = true)]
#else
[Activity(Label = "Диагностика", Theme = "@android:style/Theme.Material.Light.NoActionBar")]
#endif
public sealed class DiagnosticsActivity : Activity
{
    private TextView? _view;
    private TextView? _status;
    private EditText? _domain;
    private bool _busy;

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);

        var root = new LinearLayout(this) { Orientation = Android.Widget.Orientation.Vertical };
        root.SetPadding(
            UiMetrics.Dp(this, 16),
            UiMetrics.StatusBar(this) + UiMetrics.Dp(this, 8),
            UiMetrics.Dp(this, 16),
            UiMetrics.NavigationBar(this) + UiMetrics.Dp(this, 8));

        var title = new TextView(this) { Text = "Диагностика", TextSize = 24f };
        title.SetTypeface(null, TypefaceStyle.Bold);

        _view = new TextView(this) { TextSize = 13f };
        _view.SetTextIsSelectable(true);

        _status = new TextView(this) { TextSize = 13f };
        _status.SetTextColor(Color.DarkGray);

        var actions = new LinearLayout(this) { Orientation = Android.Widget.Orientation.Horizontal };
        var refresh = new Button(this) { Text = "Обновить", TextSize = 13f };
        refresh.Click += async (_, _) => await LoadAsync();
        var check = new Button(this) { Text = "Проверить выход", TextSize = 13f };
        check.Click += async (_, _) => await CheckExitAsync();
        actions.AddView(refresh, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f));
        actions.AddView(check, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f));

        var vpnSettings = new Button(this) { Text = "Настройки VPN Android", TextSize = 13f };
        vpnSettings.Click += (_, _) =>
        {
            try
            {
                StartActivity(new Intent(Settings.ActionVpnSettings));
            }
            catch (Exception exception)
            {
                SetStatus("Не удалось открыть настройки: " + exception.Message);
            }
        };

        var blockRow = new LinearLayout(this) { Orientation = Android.Widget.Orientation.Horizontal };
        _domain = new EditText(this) { Hint = "домен, например youtube.com" };
        _domain.SetSingleLine(true);
        var block = new Button(this) { Text = "Тип блокировки", TextSize = 13f };
        block.Click += async (_, _) => await CheckBlockTypeAsync();
        blockRow.AddView(_domain, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f));
        blockRow.AddView(block, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent));

        var scroll = new ScrollView(this);
        scroll.AddView(_view);

        root.AddView(title);
        root.AddView(_status);
        root.AddView(actions);
        root.AddView(vpnSettings);
        root.AddView(blockRow);
        root.AddView(scroll, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, 0, 1f));

        SetContentView(root);
    }

    protected override void OnResume()
    {
        base.OnResume();
        _ = LoadAsync();
    }

    private async Task LoadAsync()
    {
        var text = await Task.Run(() =>
        {
            var builder = new StringBuilder();
            try
            {
                NodeStore.EnsureLoaded();
            }
            catch (Exception exception)
            {
                builder.AppendLine("Узлы не прочитаны: " + exception.Message);
            }

            var selected = NodeStore.Selected;
            var working = NodeStore.Nodes.Count(node => node.Status == NodeStatus.Working);

            builder.AppendLine("Туннель: " + VpnStarter.StateLabel);
            builder.AppendLine("Узел: " + (selected is null
                ? "не выбран"
                : $"{selected.Name} · {selected.DisplayDelay} · {selected.Server}:{selected.Port}"));
            builder.AppendLine("Узлов: " + NodeStore.Nodes.Count + ", рабочих: " + working);
            builder.AppendLine("Источников: " + NodeStore.Sources.Count);
            builder.AppendLine("Mixed-порт: 127.0.0.1:" + CoreRuntime.MixedPort);
            builder.AppendLine("Clash API: 127.0.0.1:" + CoreRuntime.ApiPort);
            builder.AppendLine("Ядро запущено: " + (CoreRuntime.IsRunning ? "да" : "нет"));

            try
            {
                builder.AppendLine("libbox: " + Libbox.Libbox.Version());
            }
            catch (Exception exception)
            {
                builder.AppendLine("libbox: недоступен (" + exception.Message + ")");
            }

            builder.AppendLine("Данные: " + AppPaths.DataDirectory);
            builder.AppendLine("Журнал: " + AppPaths.LogsDirectory);
            return builder.ToString();
        });

        if (_view is not null)
        {
            _view.Text = text;
        }
    }

    private async Task CheckExitAsync()
    {
        if (_busy)
        {
            return;
        }

        _busy = true;
        SetStatus("Проверяю выход…");
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));

            string throughNode;
            try
            {
                throughNode = await ThroughMixedAsync("icanhazip.com", timeout.Token);
            }
            catch (Exception exception)
            {
                throughNode = "ошибка: " + exception.Message;
            }

            string direct;
            try
            {
                direct = await DirectAsync("http://icanhazip.com/", timeout.Token);
            }
            catch (Exception exception)
            {
                direct = "ошибка: " + exception.Message;
            }

            var text = (_view?.Text ?? string.Empty).TrimEnd();
            text += System.Environment.NewLine + System.Environment.NewLine +
                    "Выход через узел (mixed " + CoreRuntime.MixedPort + "): " + throughNode +
                    System.Environment.NewLine + "Прямой IP: " + direct;
            if (_view is not null)
            {
                _view.Text = text;
            }

            SetStatus("Готово");
        }
        catch (Exception exception)
        {
            SetStatus("Проверка не удалась: " + exception.Message);
        }
        finally
        {
            _busy = false;
        }
    }

    /// <summary>HTTP-запрос через локальный mixed-порт sing-box (то есть через узел).</summary>
    private static async Task<string> ThroughMixedAsync(string host, CancellationToken cancellationToken)
    {
        using var client = new TcpClient();
        await client.ConnectAsync("127.0.0.1", CoreRuntime.MixedPort, cancellationToken);

        using var stream = client.GetStream();
        var request = "GET http://" + host + "/ HTTP/1.0\r\nHost: " + host +
                      "\r\nConnection: close\r\n\r\n";
        await stream.WriteAsync(Encoding.ASCII.GetBytes(request), cancellationToken);

        using var reader = new StreamReader(stream, Encoding.UTF8);
        var response = await reader.ReadToEndAsync(cancellationToken);
        var split = response.IndexOf("\r\n\r\n", StringComparison.Ordinal);
        return (split >= 0 ? response[(split + 4)..] : response).Trim();
    }

    /// <summary>Прямой запрос мимо туннеля (наш процесс исключён из VPN).</summary>
    private static async Task<string> DirectAsync(string url, CancellationToken cancellationToken)
    {
        using var handler = new SocketsHttpHandler { UseProxy = false };
        using var client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        var text = await client.GetStringAsync(url, cancellationToken);
        return text.Trim();
    }

    /// <summary>
    /// Определяет, чем блокируется домен: не открывается TCP (IP-блок) или открывается,
    /// но не проходит TLS (SNI/TLS-блок). Второй случай наш user-space обход может исправить.
    /// Запросы идут напрямую: наш процесс исключён из VPN.
    /// </summary>
    private async Task CheckBlockTypeAsync()
    {
        if (_busy)
        {
            return;
        }

        _busy = true;
        var host = _domain?.Text?.Trim();
        if (string.IsNullOrWhiteSpace(host))
        {
            host = "youtube.com";
        }

        SetStatus("Проверяю " + host + "…");
        try
        {
            var (tcp, tls, ip, error) = await Task.Run(() => ProbeBlockAsync(host));

            var dnsFailed = error is not null &&
                            error.StartsWith("DNS:", StringComparison.Ordinal);
            var verdict = dnsFailed
                ? "DNS-блок: имя не разрешается — фрагментация SNI не поможет, нужен DoH/DoT или узел"
                : !tcp
                    ? "IP-блок: TCP :443 не открывается — фрагментация SNI не поможет, нужен узел"
                    : !tls
                        ? "SNI/TLS-блок: TCP открыт, TLS не проходит — фрагментация может помочь"
                        : "соединение проходит — обход не нужен";

            var line = "Проверка " + host + (string.IsNullOrEmpty(ip) ? string.Empty : " (" + ip + ")") +
                       ": TCP " + (tcp ? "да" : "нет") + ", TLS " + (tls ? "да" : "нет") + " — " + verdict;
            if (!string.IsNullOrEmpty(error))
            {
                line += System.Environment.NewLine + error;
            }

            var text = (_view?.Text ?? string.Empty).TrimEnd();
            text += System.Environment.NewLine + System.Environment.NewLine + line;
            if (_view is not null)
            {
                _view.Text = text;
            }

            SetStatus("Готово");
        }
        catch (Exception exception)
        {
            SetStatus("Проверка не удалась: " + exception.Message);
        }
        finally
        {
            _busy = false;
        }
    }

    private static async Task<(bool Tcp, bool Tls, string Ip, string? Error)> ProbeBlockAsync(string host)
    {
        try
        {
            var addresses = await Dns.GetHostAddressesAsync(host);
            var address = addresses.FirstOrDefault(item => item.AddressFamily == AddressFamily.InterNetwork)
                          ?? addresses.FirstOrDefault();
            if (address is null)
            {
                return (false, false, string.Empty, "DNS: не вернул адрес");
            }

            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            using var tcp = new TcpClient();
            try
            {
                await tcp.ConnectAsync(address, 443, timeout.Token);
            }
            catch (Exception exception)
            {
                return (false, false, address.ToString(), "TCP: " + exception.Message);
            }

            try
            {
                using var ssl = new SslStream(tcp.GetStream(), false, (_, _, _, _) => true);
                await ssl.AuthenticateAsClientAsync(
                    new SslClientAuthenticationOptions { TargetHost = host }, timeout.Token);
                return (true, true, address.ToString(), null);
            }
            catch (Exception exception)
            {
                return (true, false, address.ToString(), "TLS: " + exception.Message);
            }
        }
        catch (Exception exception)
        {
            return (false, false, string.Empty, "DNS: " + exception.Message);
        }
    }

    private void SetStatus(string text) => RunOnUiThread(() =>
    {
        if (_status is not null)
        {
            _status.Text = text;
        }
    });
}
