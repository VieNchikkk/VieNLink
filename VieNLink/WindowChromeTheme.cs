using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace VieNLink;

/// <summary>
/// Красит системный заголовок окна под текущую тему, чтобы серая полоса Windows
/// не выбивалась из тёмного интерфейса. На Windows 11 задаются точные цвета фона,
/// текста и рамки; на Windows 10 работает только тёмный режим заголовка.
/// </summary>
internal static class WindowChromeTheme
{
    private const int DwmwaBorderColor = 34;
    private const int DwmwaCaptionColor = 35;
    private const int DwmwaTextColor = 36;
    private const int DwmwaUseImmersiveDarkMode = 20;

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr windowHandle, int attribute, ref int value, int valueSize);

    /// <summary>Применяет цвета темы к заголовку уже созданного окна.</summary>
    public static void Apply(Window window)
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero)
        {
            return;
        }

        var background = GetColor("AppBackgroundBrush", Color.FromRgb(0x18, 0x19, 0x1B));
        var text = GetColor("TextBrush", Color.FromRgb(0xF2, 0xF3, 0xF5));
        var border = GetColor("BorderBrush", Color.FromRgb(0x34, 0x38, 0x3D));
        var isDark = 0.299 * background.R + 0.587 * background.G + 0.114 * background.B < 128;

        var immersive = isDark ? 1 : 0;
        _ = DwmSetWindowAttribute(handle, DwmwaUseImmersiveDarkMode, ref immersive, sizeof(int));

        // Windows 10 атрибуты 34–36 игнорирует, поэтому вызываем без проверки результата.
        var caption = ToColorRef(background);
        var captionText = ToColorRef(text);
        var captionBorder = ToColorRef(border);
        _ = DwmSetWindowAttribute(handle, DwmwaCaptionColor, ref caption, sizeof(int));
        _ = DwmSetWindowAttribute(handle, DwmwaTextColor, ref captionText, sizeof(int));
        _ = DwmSetWindowAttribute(handle, DwmwaBorderColor, ref captionBorder, sizeof(int));
    }

    private static Color GetColor(string key, Color fallback) =>
        Application.Current?.TryFindResource(key) is SolidColorBrush brush ? brush.Color : fallback;

    /// <summary>COLORREF: 0x00BBGGRR.</summary>
    private static int ToColorRef(Color color) => color.R | (color.G << 8) | (color.B << 16);
}
