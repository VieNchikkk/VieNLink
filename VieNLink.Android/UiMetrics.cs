using Android.Content;

namespace VieNLink.Mobile;

/// <summary>
/// Отступы системных панелей. Тема без ActionBar не добавляет их сама:
/// без явного отступа заголовок уезжает под строку состояния.
/// </summary>
internal static class UiMetrics
{
    public static int StatusBar(Context context) => Dimension(context, "status_bar_height");

    public static int NavigationBar(Context context) => Dimension(context, "navigation_bar_height");

    public static int Dp(Context context, int value) =>
        (int)Math.Round(value * context.Resources!.DisplayMetrics!.Density);

    private static int Dimension(Context context, string name)
    {
        try
        {
            var id = context.Resources!.GetIdentifier(name, "dimen", "android");
            return id > 0 ? context.Resources.GetDimensionPixelSize(id) : 0;
        }
        catch (Exception)
        {
            return 0;
        }
    }
}
