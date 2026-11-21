using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace VieNLink;

/// <summary>
/// Значок в трее: показать окно, подключиться/отключиться, выбрать узел, виджет и выход.
/// Цвет точки на значке показывает состояние: зелёная — подключено, серая — нет.
/// </summary>
internal sealed class TrayIconService : IDisposable
{
    private readonly NotifyIcon _notifyIcon;
    private readonly ToolStripMenuItem _toggleItem;
    private readonly ToolStripMenuItem _nodesItem;
    private readonly Icon _baseIcon;
    private readonly Icon _connectedIcon;
    private readonly Icon _disconnectedIcon;
    private readonly Icon _blockedIcon;
    private bool _connected;

    public TrayIconService()
    {
        _baseIcon = LoadIcon();
        _connectedIcon = WithStatusDot(_baseIcon, Color.FromArgb(0x35, 0xC7, 0x59));
        _disconnectedIcon = WithStatusDot(_baseIcon, Color.FromArgb(0x9A, 0xA0, 0xA6));
        _blockedIcon = WithStatusDot(_baseIcon, Color.FromArgb(0xD9, 0x3A, 0x3A));

        _toggleItem = new ToolStripMenuItem("Подключиться", null, (_, _) => ToggleRequested?.Invoke());
        _nodesItem = new ToolStripMenuItem("Автовыбор узла");
        _nodesItem.DropDownItems.Add(new ToolStripMenuItem("Узлы ещё не проверены") { Enabled = false });

        var menu = new ContextMenuStrip();
        menu.Items.Add(new ToolStripMenuItem("Показать VieNLink", null, (_, _) => ShowRequested?.Invoke()));
        menu.Items.Add(_nodesItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(_toggleItem);
        menu.Items.Add(new ToolStripMenuItem("Виджет обхода DPI", null, (_, _) => WidgetRequested?.Invoke()));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem("Выход", null, (_, _) => ExitRequested?.Invoke()));

        _notifyIcon = new NotifyIcon
        {
            Icon = _disconnectedIcon,
            Text = AppInfo.DisplayName,
            Visible = true,
            ContextMenuStrip = menu,
        };
        _notifyIcon.DoubleClick += (_, _) => ShowRequested?.Invoke();
    }

    public event Action? ShowRequested;

    public event Action? ToggleRequested;

    public event Action? WidgetRequested;

    public event Action? ExitRequested;

    /// <summary>Выбран узел в подменю «Узлы»: передаётся Id узла.</summary>
    public event Action<string>? NodeSelected;

    public void UpdateStatus(bool connected, string? nodeName, bool blocked = false)
    {
        _connected = connected;
        _toggleItem.Text = connected || blocked ? "Отключиться" : "Подключиться";
        _notifyIcon.Icon = blocked ? _blockedIcon : connected ? _connectedIcon : _disconnectedIcon;
        _notifyIcon.Text = blocked
            ? Truncate(AppInfo.DisplayName + " — трафик заблокирован")
            : connected
                ? Truncate(AppInfo.DisplayName + " — " + (nodeName ?? "подключено"))
                : AppInfo.DisplayName + " — отключено";
    }

    /// <summary>Быстрое подключение без открытия окна: один лучший узел, без списка адресов.</summary>
    public void UpdateNodes(IReadOnlyList<ProxyNode> nodes, string? currentId)
    {
        _nodesItem.DropDownItems.Clear();
        if (nodes.Count == 0)
        {
            _nodesItem.DropDownItems.Add(new ToolStripMenuItem("Нет проверенных узлов") { Enabled = false });
            return;
        }

        var best = nodes[0];
        var label = best.DelayMs is { } delay
            ? $"Подключить лучший · {delay} мс"
            : "Подключить лучший";
        _nodesItem.DropDownItems.Add(new ToolStripMenuItem(label, null,
            (_, _) => NodeSelected?.Invoke(best.Id)));

        if (_connected && !string.IsNullOrEmpty(currentId))
        {
            _nodesItem.DropDownItems.Add(new ToolStripSeparator());
            _nodesItem.DropDownItems.Add(new ToolStripMenuItem("Отключиться", null, (_, _) => ToggleRequested?.Invoke()));
        }
    }

    public void ShowBalloon(string title, string text, bool warning = false)
    {
        _notifyIcon.BalloonTipTitle = title;
        _notifyIcon.BalloonTipText = text;
        _notifyIcon.BalloonTipIcon = warning ? ToolTipIcon.Warning : ToolTipIcon.Info;
        _notifyIcon.ShowBalloonTip(4000);
    }

    public void Dispose()
    {
        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
        _baseIcon.Dispose();
        _connectedIcon.Dispose();
        _disconnectedIcon.Dispose();
        _blockedIcon.Dispose();
    }

    /// <summary>Тот же значок с цветной точкой в углу — состояние видно, не открывая меню.</summary>
    private static Icon WithStatusDot(Icon source, Color color)
    {
        using var bitmap = source.ToBitmap();
        using var graphics = Graphics.FromImage(bitmap);
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        var size = Math.Max(8, bitmap.Width / 3);
        var x = bitmap.Width - size - 1;
        var y = bitmap.Height - size - 1;
        using var border = new SolidBrush(Color.White);
        using var fill = new SolidBrush(color);
        graphics.FillEllipse(border, x - 2, y - 2, size + 4, size + 4);
        graphics.FillEllipse(fill, x, y, size, size);

        var handle = bitmap.GetHicon();
        try
        {
            using var temporary = Icon.FromHandle(handle);
            return (Icon)temporary.Clone();
        }
        finally
        {
            DestroyIcon(handle);
        }
    }

    private static Icon LoadIcon()
    {
        try
        {
            var resource = System.Windows.Application.GetResourceStream(
                new Uri("pack://application:,,,/Assets/VieNLink.ico"));
            if (resource is not null)
            {
                using var source = resource.Stream;
                using var memory = new MemoryStream();
                source.CopyTo(memory);
                memory.Position = 0;
                return new Icon(memory);
            }
        }
        catch (Exception exception) when (exception is IOException or ArgumentException or NotSupportedException)
        {
            AppLog.Write("ui", "Не удалось загрузить иконку VieNLink", exception);
        }

        // Клон: значок системы общий, его нельзя отдавать в Dispose.
        return (Icon)SystemIcons.Application.Clone();
    }

    private static string Truncate(string text) => text.Length <= 63 ? text : text[..60] + "…";

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr hIcon);
}
