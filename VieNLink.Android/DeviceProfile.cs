using Android.Content;
using Android.Content.Res;

namespace VieNLink.Mobile;

/// <summary>
/// Форм-фактор: телефон, планшет или телевизор. От него зависят ориентация,
/// ширина контента, размеры элементов и фокус для пульта.
/// </summary>
internal static class DeviceProfile
{
    public static bool IsTelevision { get; private set; }

    public static bool IsTablet { get; private set; }

    public static bool IsLargeScreen => IsTelevision || IsTablet;

    public static void Initialize(Context context)
    {
        var configuration = context.Resources?.Configuration;
        if (configuration is null)
        {
            return;
        }

        var uiMode = configuration.UiMode & UiMode.TypeMask;
        IsTelevision = uiMode == UiMode.TypeTelevision;
        IsTablet = !IsTelevision && configuration.SmallestScreenWidthDp >= 600;

        // Телевизор смотрят с 3 метров: крупнее всё — от кнопок до шрифта.
        Ui.LayoutScale = IsTelevision ? 1.35f : IsTablet ? 1.08f : 1f;
        Ui.FontScale = IsTelevision ? 1.22f : IsTablet ? 1.04f : 1f;
        Ui.IsTelevision = IsTelevision;
    }
}
