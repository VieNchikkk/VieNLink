using Android.Content;
using Android.Content.Res;

namespace VieNLink.Mobile;

/// <summary>
/// Форм-фактор: телефон, планшет или телевизор. От него зависят ориентация,
/// ширина контента, размеры элементов и фокус для пульта.
/// Дополнительно считается реальная ширина и высота экрана в dp: на узком телефоне
/// (320–359dp) отступы уменьшаются, а на низком экране (ландшафт, окно в несколько
/// окон) — сжимаются ещё сильнее. Иначе фиксированные ширины кнопок и подписей
/// выдавливали соседей за край карточки.
/// </summary>
internal static class DeviceProfile
{
    public static bool IsTelevision { get; private set; }

    public static bool IsTablet { get; private set; }

    public static bool IsLargeScreen => IsTelevision || IsTablet;

    /// <summary>Ширина экрана в dp (уже с учётом системного масштаба дисплея).</summary>
    public static int WidthDp { get; private set; }

    /// <summary>Высота экрана в dp.</summary>
    public static int HeightDp { get; private set; }

    public static int SmallestWidthDp { get; private set; }

    public static bool IsLandscape { get; private set; }

    /// <summary>Узкий телефон: уже 360dp (320–359dp — бюджетные и старые модели).</summary>
    public static bool IsCompact => !IsTelevision && WidthDp > 0 && WidthDp < 360;

    /// <summary>Низкий экран: ландшафт или окно в несколько окон. Меньше вертикальных отступов.</summary>
    public static bool IsShort => HeightDp > 0 && HeightDp < 520;

    /// <summary>Системный масштаб шрифта (настройка Android «Размер текста»).</summary>
    public static float SystemFontScale { get; private set; } = 1f;

    public static void Initialize(Context context)
    {
        var configuration = context.Resources?.Configuration;
        if (configuration is null)
        {
            return;
        }

        var uiMode = configuration.UiMode & UiMode.TypeMask;
        IsTelevision = uiMode == UiMode.TypeTelevision;
        SmallestWidthDp = configuration.SmallestScreenWidthDp;
        IsTablet = !IsTelevision && SmallestWidthDp >= 600;

        WidthDp = configuration.ScreenWidthDp;
        HeightDp = configuration.ScreenHeightDp;
        IsLandscape = WidthDp > HeightDp;
        SystemFontScale = Math.Max(1f, configuration.FontScale);

        // Базовый масштаб отступов по форм-фактору.
        float layout;
        float font;
        if (IsTelevision)
        {
            layout = 1.35f;
            font = 1.22f;
        }
        else if (IsTablet)
        {
            layout = 1.08f;
            font = 1.04f;
        }
        else
        {
            layout = WidthDp > 0 && WidthDp < 360 ? 0.92f : WidthDp >= 400 ? 1f : 0.97f;
            font = 1f;
        }

        // Низкий экран (ландшафт, окно в несколько окон): меньше «воздуха» по вертикали.
        if (IsShort)
        {
            layout *= 0.94f;
        }

        Ui.LayoutScale = layout;
        Ui.FontScale = font;
        // Системный «Размер текста» уважаем, но ограничиваем: при 1.3+ подписи
        // в тесных рядах (статус, счётчики, две кнопки рядом) рвали шапку.
        var fontCap = IsTelevision ? 1.12f : 1.28f;
        Ui.FontLimit = Math.Min(SystemFontScale, fontCap) / SystemFontScale;
        Ui.IsTelevision = IsTelevision;
        Ui.IsCompactLayout = IsCompact;
        Ui.IsShortLayout = IsShort;
    }
}
