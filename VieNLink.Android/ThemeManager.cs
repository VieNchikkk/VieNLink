using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.Content.Res;
using Android.Graphics;
using Android.OS;
using Android.Views;

namespace VieNLink.Mobile;

/// <summary>
/// Палитра Android-версии. Повторяет цвета Windows-клиента: графит и приглушённый
/// зелёный в тёмной теме, бумажный фон в светлой.
/// </summary>
public sealed class Palette
{
    public Color Background = Color.ParseColor("#18191B")!;
    public Color Panel = Color.ParseColor("#1E2023")!;
    public Color Card = Color.ParseColor("#26292C")!;
    public Color Surface = Color.ParseColor("#2A2D31")!;
    public Color Inner = Color.ParseColor("#222528")!;
    public Color Border = Color.ParseColor("#34383D")!;
    public Color BorderSoft = Color.ParseColor("#3F444A")!;
    public Color Text = Color.ParseColor("#F2F3F5")!;
    public Color TextStrong = Color.ParseColor("#F7F8F9")!;
    public Color Muted = Color.ParseColor("#A3A9AF")!;
    public Color MutedStrong = Color.ParseColor("#C9CED4")!;
    public Color Accent = Color.ParseColor("#9CCDB0")!;
    public Color AccentContrast = Color.ParseColor("#09201B")!;
    public Color AccentSurface = Color.ParseColor("#224A3A")!;
    public Color AccentBorder = Color.ParseColor("#59A57A")!;
    public Color Positive = Color.ParseColor("#B3EFCF")!;
    public Color Danger = Color.ParseColor("#F08C9C")!;
    public Color DangerSurface = Color.ParseColor("#3A2028")!;
    public Color Warning = Color.ParseColor("#F2C14E")!;
    public Color WarningSurface = Color.ParseColor("#4D3D18")!;
    public Color YouTube = Color.ParseColor("#FF6B6B")!;
    public Color YouTubeSurface = Color.ParseColor("#3A1E22")!;
    public Color Discord = Color.ParseColor("#8E9BFF")!;
    public Color DiscordSurface = Color.ParseColor("#232745")!;
    public Color Telegram = Color.ParseColor("#6FC3F2")!;
    public Color TelegramSurface = Color.ParseColor("#1C3240")!;
    public Color Universal = Color.ParseColor("#7FD8B5")!;
    public Color UniversalSurface = Color.ParseColor("#203A30")!;
    public Color White = Color.ParseColor("#FFFFFF")!;

    public static Palette Dark { get; } = new();

    public static Palette Light { get; } = new()
    {
        Background = Color.ParseColor("#F4F6F5")!,
        Panel = Color.ParseColor("#E7EBE8")!,
        Card = Color.ParseColor("#FFFFFF")!,
        Surface = Color.ParseColor("#EDF1EE")!,
        Inner = Color.ParseColor("#F1F4F2")!,
        Border = Color.ParseColor("#D5DAD6")!,
        BorderSoft = Color.ParseColor("#C2C9C4")!,
        Text = Color.ParseColor("#1A1D1B")!,
        TextStrong = Color.ParseColor("#0F1210")!,
        Muted = Color.ParseColor("#5F6662")!,
        MutedStrong = Color.ParseColor("#3E4642")!,
        Accent = Color.ParseColor("#2E7D52")!,
        AccentContrast = Color.ParseColor("#FFFFFF")!,
        AccentSurface = Color.ParseColor("#D7EFE1")!,
        AccentBorder = Color.ParseColor("#7FB99A")!,
        Positive = Color.ParseColor("#1F7A4D")!,
        Danger = Color.ParseColor("#B3263D")!,
        DangerSurface = Color.ParseColor("#F7DDE2")!,
        Warning = Color.ParseColor("#8A5A00")!,
        WarningSurface = Color.ParseColor("#F8E9C8")!,
        YouTube = Color.ParseColor("#D7263D")!,
        YouTubeSurface = Color.ParseColor("#FBE2E6")!,
        Discord = Color.ParseColor("#4752C4")!,
        DiscordSurface = Color.ParseColor("#E3E6FB")!,
        Telegram = Color.ParseColor("#1D7FB5")!,
        TelegramSurface = Color.ParseColor("#D9EEF9")!,
        Universal = Color.ParseColor("#2E7D52")!,
        UniversalSurface = Color.ParseColor("#D7EFE1")!,
        White = Color.ParseColor("#FFFFFF")!,
    };
}

/// <summary>
/// Тема: тёмная по умолчанию, светлая — из общей настройки <see cref="AppSettings.Theme"/>.
/// Единая точка, чтобы все окна выглядели одинаково.
/// </summary>
internal static class ThemeManager
{
    public static bool IsLight
    {
        get
        {
            try
            {
                return SettingsStore.Current.Theme.Equals("Light", StringComparison.OrdinalIgnoreCase);
            }
            catch (Exception)
            {
                return false;
            }
        }
    }

    public static Palette Palette => IsLight ? Palette.Light : Palette.Dark;

    public static void ApplyTheme(Activity activity)
    {
        activity.SetTheme(IsLight ? Resource.Style.Theme_VieNLink_Light : Resource.Style.Theme_VieNLink);
    }

    public static void ApplyWindow(Activity activity, Palette palette)
    {
        try
        {
            var window = activity.Window;
            if (window is null)
            {
                return;
            }

            window.SetStatusBarColor(palette.Background);
            window.SetNavigationBarColor(palette.Background);

            if (Build.VERSION.SdkInt >= BuildVersionCodes.O)
            {
                var flags = window.DecorView.SystemUiVisibility;
                if (IsLight)
                {
                    flags |= (StatusBarVisibility)SystemUiFlags.LightStatusBar;
                    flags |= (StatusBarVisibility)SystemUiFlags.LightNavigationBar;
                }
                else
                {
                    flags &= ~(StatusBarVisibility)SystemUiFlags.LightStatusBar;
                    flags &= ~(StatusBarVisibility)SystemUiFlags.LightNavigationBar;
                }

                window.DecorView.SystemUiVisibility = flags;
            }
        }
        catch (Exception exception)
        {
            Android.Util.Log.Warn("VieNLink", "тема окна: " + exception.Message);
        }
    }
}

/// <summary>Базовое окно: тема, системные панели и общий фон.</summary>
public abstract class BaseActivity : Activity
{
    protected Palette P { get; private set; } = Palette.Dark;

    private Android.Content.Res.Orientation _lastOrientation = Android.Content.Res.Orientation.Undefined;

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        // Телефон — портрет; планшет — любая ориентация; телевизор — альбомная.
        // Системную настройку автоповорота приложение не меняет.
        DeviceProfile.Initialize(this);
        ApplyOrientation();
        DeviceProfile.LogRotationState("экран " + GetType().Name);
        ThemeManager.ApplyTheme(this);
        base.OnCreate(savedInstanceState);
        P = ThemeManager.Palette;
        ThemeManager.ApplyWindow(this, P);
        if (Window is not null)
        {
            Window.SetBackgroundDrawable(new Android.Graphics.Drawables.ColorDrawable(P.Background));
        }
    }

    protected override void OnResume()
    {
        base.OnResume();
        // Повторно подтверждаем ориентацию при каждом возврате: после обновления APK
        // или смены сети/окна прошивка иногда сбрасывает запрошенную ориентацию,
        // и экран начинает крутиться вместе с телефоном, хотя выбран портрет.
        DeviceProfile.Initialize(this);
        ApplyOrientation();
    }

    /// <summary>
    /// Запрашивает ориентацию по форм-фактору. Повторный вызов с тем же значением
    /// ничего не перестраивает, поэтому дёргать можно в OnCreate и OnResume.
    /// </summary>
    private void ApplyOrientation()
    {
        var target = DeviceProfile.IsTelevision
            ? ScreenOrientation.Landscape
            : DeviceProfile.IsTablet
                ? ScreenOrientation.FullSensor
                : ScreenOrientation.Portrait;
        if (RequestedOrientation != target)
        {
            RequestedOrientation = target;
        }
    }

    /// <summary>
    /// Поворот планшета, изменение размера окна (несколько окон), смена системного
    /// шрифта. Android не пересоздаёт такие экраны, поэтому метрики пересчитываются
    /// здесь, а экран перестраивает содержимое.
    /// </summary>
    public override void OnConfigurationChanged(Configuration newConfig)
    {
        base.OnConfigurationChanged(newConfig);
        if (newConfig.Orientation != _lastOrientation)
        {
            _lastOrientation = newConfig.Orientation;
            // Если экран всё-таки повернулся — в журнале будет видно, при каком
            // системном автоповороте и каком форм-факторе это случилось.
            DeviceProfile.LogRotationState("поворот " + newConfig.Orientation);
        }

        DeviceProfile.Initialize(this);
        P = ThemeManager.Palette;
        ThemeManager.ApplyWindow(this, P);
        OnLayoutConfigurationChanged();
    }

    /// <summary>Экран перестраивает раскладку под новые размеры. По умолчанию — ничего.</summary>
    protected virtual void OnLayoutConfigurationChanged()
    {
    }

    /// <summary>Корневой экран: фон, отступы системных панелей и прокрутка содержимого.</summary>
    protected void SetPage(View content, bool scroll = true, bool withInsets = true)
    {
        View body = content;
        if (scroll)
        {
            var scrollView = new ScrollView(this)
            {
                FillViewport = true,
                VerticalScrollBarEnabled = false,
            };
            scrollView.AddView(content, new FrameLayout.LayoutParams(
                ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent));
            body = scrollView;
        }

        var root = new FrameLayout(this);
        root.SetBackgroundColor(P.Background);
        // На планшете длинная колонка кнопок во всю ширину выглядит растянутой:
        // содержимое ограничено 900dp и стоит по центру. Телефон и ТВ — во всю ширину.
        var screenWidth = Resources?.DisplayMetrics?.WidthPixels ?? 0;
        var maxContent = Ui.Dp(this, 900);
        var contentWidth = DeviceProfile.IsTablet && screenWidth > maxContent
            ? maxContent
            : ViewGroup.LayoutParams.MatchParent;
        root.AddView(body, new FrameLayout.LayoutParams(contentWidth, ViewGroup.LayoutParams.MatchParent)
        {
            Gravity = GravityFlags.CenterHorizontal,
        });

        if (withInsets)
        {
            // Реальные WindowInsets (вырез, жестовая навигация), а не только высота
            // строки состояния из ресурсов.
            Ui.ApplyInsets(root, 16, 8, 16, 8);
        }

        SetContentView(root);
    }

    protected LinearLayout ColumnPage()
    {
        var page = new LinearLayout(this) { Orientation = Android.Widget.Orientation.Vertical };
        page.SetPadding(0, 0, 0, Ui.Dp(this, Ui.IsShortLayout ? 12 : 24));
        return page;
    }

    protected void Open(Type activity)
    {
        StartActivity(new Intent(this, activity));
    }
}
