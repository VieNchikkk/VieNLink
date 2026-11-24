using Android.Content;
using Android.Graphics;
using Android.Graphics.Drawables;
using Android.OS;
using Android.Views;
using Android.Widget;

namespace VieNLink.Mobile;

internal enum UiButtonKind
{
    Primary,
    Accent,
    Secondary,
    Danger,
    Ghost,
}

/// <summary>
/// Небольшой набор визуальных элементов: карточки, плашки, кнопки и строки.
/// Программный UI без разметки — так проще повторять палитру Windows-версии
/// и не тащить в проект лишние зависимости.
/// </summary>
internal static class Ui
{
    /// <summary>Масштаб отступов и размеров: 1.0 телефон, больше планшет/ТВ.</summary>
    public static float LayoutScale { get; set; } = 1f;

    /// <summary>Масштаб шрифтов для 10-foot UI.</summary>
    public static float FontScale { get; set; } = 1f;

    /// <summary>
    /// Поправка на системный «Размер текста»: уважаем его, но не выше предела,
    /// иначе подписи не помещаются в тесные ряды кнопок. 1.0 — системный масштаб
    /// не мешает; меньше 1.0 — Android просит слишком крупный шрифт.
    /// </summary>
    public static float FontLimit { get; set; } = 1f;

    /// <summary>Узкий экран (до 360dp): компактные отступы, короткие подписи.</summary>
    public static bool IsCompactLayout { get; set; }

    /// <summary>Низкий экран (ландшафт, окно в несколько окон).</summary>
    public static bool IsShortLayout { get; set; }

    /// <summary>Телевизор: крупные элементы и подсветка фокуса для пульта.</summary>
    public static bool IsTelevision { get; set; }

    public static int Dp(Context context, double value) =>
        (int)Math.Round(value * LayoutScale * context.Resources!.DisplayMetrics!.Density);

    /// <summary>
    /// Ширина в dp как доля экрана, но не больше <paramref name="maxDp"/>.
    /// Нужна для фиксированных ширин (имя сервера и т. п.): на узком экране они
    /// не должны выдавливать соседние элементы.
    /// </summary>
    public static int Share(Context context, double fraction, double maxDp) =>
        Dp(context, Math.Min(maxDp, Math.Max(1, DeviceProfile.WidthDp) * fraction));

    /// <summary>Высота в dp как доля экрана, но не больше предела (журнал, редакторы).</summary>
    public static int ShareHeight(Context context, double fraction, double maxDp) =>
        Dp(context, Math.Min(maxDp, Math.Max(1, DeviceProfile.HeightDp) * fraction));

    public static float Sp(float value) => value * FontScale * FontLimit;

    /// <summary>
    /// Размер текста для виджетов, созданных не через <see cref="Text"/>
    /// (CheckBox, RadioButton, Button, EditText): иначе системный масштаб
    /// шрифта не ограничен и подписи вылезают из строк.
    /// </summary>
    public static void StyleSystemText(TextView view, float size = 14f)
    {
        view.TextSize = Sp(size);
        view.SetIncludeFontPadding(false);
    }

    public static int StatusBar(Context context) => Dimension(context, "status_bar_height");

    public static int NavigationBar(Context context) => Dimension(context, "navigation_bar_height");

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

    /// <summary>
    /// Отступы системных панелей по реальным WindowInsets (вырез экрана, жестовая
    /// навигация, ландшафт). Если система инсеты не отдала (экран не edge-to-edge),
    /// берётся высота строки состояния/панели навигации из ресурсов — как раньше.
    /// Без этого на разных устройствах содержимое то уезжало под строку состояния,
    /// то получало лишний отступ снизу.
    /// </summary>
    public static void ApplyInsets(
        View view,
        int leftDp,
        int topDp,
        int rightDp,
        int bottomDp,
        bool insetTop = true,
        bool insetBottom = true,
        bool insetSides = true)
    {
        var context = view.Context!;
        var left = Dp(context, leftDp);
        var top = Dp(context, topDp);
        var right = Dp(context, rightDp);
        var bottom = Dp(context, bottomDp);
        var fallbackTop = insetTop ? StatusBar(context) : 0;
        var fallbackBottom = insetBottom ? NavigationBar(context) : 0;
        view.SetPadding(left, top + fallbackTop, right, bottom + fallbackBottom);
        view.SetOnApplyWindowInsetsListener(new InsetsPaddingListener(
            left, top, right, bottom, fallbackTop, fallbackBottom, insetTop, insetBottom, insetSides));
        view.RequestApplyInsets();
    }

    private sealed class InsetsPaddingListener : Java.Lang.Object, View.IOnApplyWindowInsetsListener
    {
        private readonly int _left;
        private readonly int _top;
        private readonly int _right;
        private readonly int _bottom;
        private readonly int _fallbackTop;
        private readonly int _fallbackBottom;
        private readonly bool _insetTop;
        private readonly bool _insetBottom;
        private readonly bool _insetSides;

        public InsetsPaddingListener(
            int left,
            int top,
            int right,
            int bottom,
            int fallbackTop,
            int fallbackBottom,
            bool insetTop,
            bool insetBottom,
            bool insetSides)
        {
            _left = left;
            _top = top;
            _right = right;
            _bottom = bottom;
            _fallbackTop = fallbackTop;
            _fallbackBottom = fallbackBottom;
            _insetTop = insetTop;
            _insetBottom = insetBottom;
            _insetSides = insetSides;
        }

        public WindowInsets OnApplyWindowInsets(View view, WindowInsets insets)
        {
            // DisplayCutout появился в Android 9 (API 28); на 8.0 его нет.
            var cutout = Build.VERSION.SdkInt >= BuildVersionCodes.P ? insets.DisplayCutout : null;
            var insetLeft = _insetSides
                ? Math.Max(insets.SystemWindowInsetLeft, cutout?.SafeInsetLeft ?? 0)
                : 0;
            var insetRight = _insetSides
                ? Math.Max(insets.SystemWindowInsetRight, cutout?.SafeInsetRight ?? 0)
                : 0;
            var insetTop = _insetTop
                ? Math.Max(insets.SystemWindowInsetTop, cutout?.SafeInsetTop ?? 0)
                : 0;
            var insetBottom = _insetBottom
                ? Math.Max(insets.SystemWindowInsetBottom, cutout?.SafeInsetBottom ?? 0)
                : 0;

            // Инсеты не пришли — оставляем запасной отступ из ресурсов.
            if (insetTop <= 0)
            {
                insetTop = _fallbackTop;
            }

            if (insetBottom <= 0)
            {
                insetBottom = _fallbackBottom;
            }

            view.SetPadding(_left + insetLeft, _top + insetTop, _right + insetRight, _bottom + insetBottom);
            return insets;
        }
    }

    public static GradientDrawable Rounded(Color fill, float radiusDp, Context context, Color? border = null, float borderDp = 1f)
    {
        var drawable = new GradientDrawable();
        drawable.SetShape(ShapeType.Rectangle);
        drawable.SetColor(fill);
        drawable.SetCornerRadius(Dp(context, radiusDp));
        if (border is not null)
        {
            drawable.SetStroke(Math.Max(1, Dp(context, borderDp)), border.Value);
        }

        return drawable;
    }

    public static GradientDrawable Gradient(Color from, Color to, float radiusDp, Context context, Color? border = null, float borderDp = 1f)
    {
        var drawable = new GradientDrawable(GradientDrawable.Orientation.TlBr, new[] { from.ToArgb(), to.ToArgb() });
        drawable.SetShape(ShapeType.Rectangle);
        drawable.SetCornerRadius(Dp(context, radiusDp));
        if (border is not null)
        {
            drawable.SetStroke(Math.Max(1, Dp(context, borderDp)), border.Value);
        }

        return drawable;
    }

    public static StateListDrawable Pressable(Context context, Color fill, Color pressed, float radiusDp, Color? border = null)
    {
        var normal = Rounded(fill, radiusDp, context, border);
        var down = Rounded(pressed, radiusDp, context, border);
        // Фокус виден на ТВ и при управлении с клавиатуры: светлее + заметная рамка.
        var focused = Rounded(Blend(fill, Color.White, 0.16f), radiusDp, context,
            border ?? Color.Argb(190, 255, 255, 255), 1.8f);
        var states = new StateListDrawable();
        states.AddState(new[] { Android.Resource.Attribute.StatePressed }, down);
        states.AddState(new[] { Android.Resource.Attribute.StateFocused }, focused);
        states.AddState(new[] { Android.Resource.Attribute.StateEnabled }, normal);
        states.AddState(Array.Empty<int>(), normal);
        return states;
    }

    /// <summary>Кнопка с градиентом: нажатое состояние тоже градиентное.</summary>
    public static StateListDrawable PressableGradient(
        Context context,
        Color from,
        Color to,
        Color pressedFrom,
        Color pressedTo,
        float radiusDp,
        Color? border = null,
        float borderDp = 1f)
    {
        var normal = Gradient(from, to, radiusDp, context, border, borderDp);
        var down = Gradient(pressedFrom, pressedTo, radiusDp, context, border, borderDp);
        var focused = Gradient(Blend(from, Color.White, 0.18f), Blend(to, Color.White, 0.10f),
            radiusDp, context, border ?? Color.Argb(200, 255, 255, 255), 2f);
        var states = new StateListDrawable();
        states.AddState(new[] { Android.Resource.Attribute.StatePressed }, down);
        states.AddState(new[] { Android.Resource.Attribute.StateFocused }, focused);
        states.AddState(new[] { Android.Resource.Attribute.StateEnabled }, normal);
        // Неактивная кнопка тоже должна быть видна, просто без подсветки.
        states.AddState(Array.Empty<int>(), normal);
        return states;
    }

    public static TextView Text(Context context, string text, float size, Color color, bool bold = false)
    {
        var view = new TextView(context)
        {
            Text = text,
            TextSize = Sp(size),
        };
        // Без дополнительного поля шрифта строки одинаковы на всех прошивках,
        // и текст не «съезжает» вниз при смене системного шрифта.
        view.SetIncludeFontPadding(false);
        view.SetTextColor(color);
        if (bold)
        {
            view.SetTypeface(Typeface.Create("sans-serif-medium", TypefaceStyle.Normal), TypefaceStyle.Normal);
        }

        return view;
    }

    public static TextView Title(Context context, string text, Palette palette)
    {
        // Длинное название экрана не переносится на вторую строку и не выдавливает
        // значок справа: обрезается многоточием в конце.
        var view = Text(context, text, 22f, palette.TextStrong, bold: true);
        view.SetSingleLine(true);
        view.Ellipsize = Android.Text.TextUtils.TruncateAt.End;
        return view;
    }

    public static TextView Section(Context context, string text, Palette palette)
    {
        var view = Text(context, text.ToUpperInvariant(), 11.5f, palette.Muted, bold: true);
        view.LetterSpacing = 0.08f;
        view.SetPadding(Dp(context, 2), Dp(context, 18), 0, Dp(context, 8));
        return view;
    }

    public static TextView Hint(Context context, string text, Palette palette, Color? color = null)
    {
        var view = Text(context, text, 13f, color ?? palette.Muted);
        view.SetLineSpacing(0, 1.15f);
        return view;
    }

    public static LinearLayout Column(Context context)
    {
        var layout = new LinearLayout(context) { Orientation = Orientation.Vertical };
        return layout;
    }

    public static LinearLayout Row(Context context)
    {
        var layout = new LinearLayout(context) { Orientation = Orientation.Horizontal };
        layout.SetGravity(GravityFlags.CenterVertical);
        return layout;
    }

    public static LinearLayout Card(Palette palette, Context context, params View[] children)
    {
        var card = Column(context);
        card.SetPadding(Dp(context, 16), Dp(context, 14), Dp(context, 16), Dp(context, 14));
        card.Background = Rounded(palette.Card, 16, context, palette.Border);
        foreach (var child in children)
        {
            card.AddView(child, new LinearLayout.LayoutParams(
                ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent));
        }

        return card;
    }

    public static LinearLayout HeaderCard(Palette palette, Context context, params View[] children)
    {
        var card = Column(context);
        card.SetPadding(Dp(context, 16), Dp(context, 16), Dp(context, 16), Dp(context, 18));
        card.Background = Gradient(palette.Card, palette.Panel, 18, context, palette.AccentBorder, 0.7f);
        foreach (var child in children)
        {
            card.AddView(child, new LinearLayout.LayoutParams(
                ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent));
        }

        return card;
    }

    public static LinearLayout Banner(Palette palette, Context context, Color accent, string title, string body)
    {
        var layout = Column(context);
        layout.SetPadding(Dp(context, 14), Dp(context, 11), Dp(context, 14), Dp(context, 11));
        var surface = Color.Argb(38, accent.R, accent.G, accent.B);
        layout.Background = Rounded(surface, 14, context, Color.Argb(90, accent.R, accent.G, accent.B));
        layout.AddView(Text(context, title, 13.5f, accent, bold: true));
        var hint = Hint(context, body, palette, palette.MutedStrong);
        hint.SetPadding(0, Dp(context, 4), 0, 0);
        layout.AddView(hint);
        return layout;
    }

    public static Drawable? Icon(Context context, int resource)
    {
        try
        {
            return context.GetDrawable(resource)?.Mutate();
        }
        catch (Exception exception)
        {
            Android.Util.Log.Warn("VieNLink", $"иконка {resource}: {exception.Message}");
            return null;
        }
    }

    public static TextView Pill(
        Context context,
        Palette palette,
        string text,
        UiButtonKind kind,
        Action click,
        int? icon = null,
        Color? tone = null)
    {
        var (fill, pressed, textColor, border) = kind switch
        {
            UiButtonKind.Primary => (palette.Accent, Blend(palette.Accent, palette.White, 0.18f), palette.AccentContrast, (Color?)null),
            UiButtonKind.Accent => (palette.AccentSurface, Blend(palette.AccentSurface, palette.Accent, 0.18f), palette.Positive, palette.AccentBorder),
            UiButtonKind.Danger => (palette.DangerSurface, Blend(palette.DangerSurface, palette.Danger, 0.14f), palette.Danger, Color.Argb(90, palette.Danger.R, palette.Danger.G, palette.Danger.B)),
            UiButtonKind.Ghost => (Color.Transparent, palette.Surface, palette.MutedStrong, (Color?)null),
            _ => (palette.Surface, palette.Inner, palette.TextSoft(), palette.Border),
        };

        if (tone is not null)
        {
            fill = Color.Argb(38, tone.Value.R, tone.Value.G, tone.Value.B);
            pressed = Color.Argb(64, tone.Value.R, tone.Value.G, tone.Value.B);
            textColor = tone.Value;
            border = Color.Argb(110, tone.Value.R, tone.Value.G, tone.Value.B);
        }

        var button = Text(context, text, 13.5f, textColor, bold: true);
        button.Gravity = GravityFlags.Center;
        button.SetMinHeight(Dp(context, 44));
        button.SetPadding(Dp(context, 14), 0, Dp(context, 14), 0);
        button.Background = Pressable(context, fill, pressed, 13, border);
        button.Clickable = true;
        button.Focusable = true;
        button.Click += (_, _) => click();
        button.SetTextColor(textColor);

        if (icon is not null)
        {
            var drawable = Icon(context, icon.Value);
            if (drawable is not null)
            {
                drawable.SetTint(textColor);
                var size = Dp(context, 18);
                drawable.SetBounds(0, 0, size, size);
                button.SetCompoundDrawables(drawable, null, null, null);
                button.CompoundDrawablePadding = Dp(context, 7);
            }
        }

        return button;
    }

    public static LinearLayout ServiceButton(
        Context context,
        Palette palette,
        string text,
        int icon,
        Color tone,
        Color surface,
        Action click,
        bool tintIcon = true)
    {
        var layout = Row(context);
        layout.SetPadding(Dp(context, 12), Dp(context, 10), Dp(context, 12), Dp(context, 10));
        layout.Background = Pressable(context, surface, Blend(surface, tone, 0.16f), 14,
            Color.Argb(120, tone.R, tone.G, tone.B));
        layout.Clickable = true;
        layout.Focusable = true;
        layout.Click += (_, _) => click();

        var image = new ImageView(context);
        var drawable = Icon(context, icon);
        if (drawable is not null)
        {
            if (tintIcon)
            {
                drawable.SetTint(tone);
            }

            image.SetImageDrawable(drawable);
        }

        layout.AddView(image, new LinearLayout.LayoutParams(Dp(context, 22), Dp(context, 22)));
        var label = Text(context, text, 13f, tone, bold: true);
        label.SetSingleLine(true);
        label.Ellipsize = Android.Text.TextUtils.TruncateAt.End;
        label.SetPadding(Dp(context, 7), 0, 0, 0);
        layout.AddView(label, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f));
        layout.SetMinimumHeight(Dp(context, 44));
        return layout;
    }

    public static LinearLayout IconTile(
        Context context,
        Palette palette,
        int icon,
        string title,
        string subtitle,
        Action click,
        Color? tone = null)
    {
        var layout = Row(context);
        layout.SetPadding(Dp(context, 14), Dp(context, 12), Dp(context, 14), Dp(context, 12));
        layout.Background = Pressable(context, palette.Card, palette.Surface, 15, palette.Border);
        layout.Clickable = true;
        layout.Focusable = true;
        layout.Click += (_, _) => click();

        var chip = new FrameLayout(context);
        chip.Background = Rounded(palette.Inner, 11, context, palette.Border);
        var image = new ImageView(context);
        var drawable = Icon(context, icon);
        if (drawable is not null)
        {
            drawable.SetTint(tone ?? palette.Accent);
            image.SetImageDrawable(drawable);
        }

        var iconSize = Dp(context, 22);
        chip.AddView(image, new FrameLayout.LayoutParams(iconSize, iconSize) { Gravity = GravityFlags.Center });
        layout.AddView(chip, new LinearLayout.LayoutParams(Dp(context, 42), Dp(context, 42)));

        var texts = Column(context);
        texts.SetPadding(Dp(context, 12), 0, 0, 0);
        texts.AddView(Text(context, title, 14.5f, palette.TextStrong, bold: true));
        if (!string.IsNullOrWhiteSpace(subtitle))
        {
            var hint = Hint(context, subtitle, palette);
            hint.SetPadding(0, Dp(context, 2), 0, 0);
            texts.AddView(hint);
        }

        layout.AddView(texts, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f));
        return layout;
    }

    public static TextView Badge(Context context, Palette palette, string text, Color tone)
    {
        var badge = Text(context, text, 10.5f, tone, bold: true);
        badge.Gravity = GravityFlags.Center;
        badge.SetPadding(Dp(context, 8), Dp(context, 3), Dp(context, 8), Dp(context, 3));
        badge.Background = Rounded(Color.Argb(40, tone.R, tone.G, tone.B), 8, context,
            Color.Argb(110, tone.R, tone.G, tone.B));
        return badge;
    }

    public static TextView Stars(Context context, Palette palette, int rating)
    {
        var value = Math.Clamp(rating, 0, 5);
        var text = Text(context, new string('★', value) + new string('☆', 5 - value), 15f,
            value >= 4 ? palette.Positive : value >= 2 ? palette.Warning : palette.Danger);
        return text;
    }

    public static void AddSpacer(LinearLayout parent, Context context, double dp)
    {
        parent.AddView(new Space(context), new LinearLayout.LayoutParams(
            ViewGroup.LayoutParams.MatchParent, Dp(context, dp)));
    }

    public static void Add(LinearLayout parent, View child, double topDp = 0)
    {
        var layout = new LinearLayout.LayoutParams(
            ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent);
        layout.TopMargin = Dp(parent.Context, topDp);
        parent.AddView(child, layout);
    }

    public static EditText Input(Context context, Palette palette, string hint, string? text = null)
    {
        var input = new EditText(context)
        {
            Hint = hint,
            Text = text,
        };
        input.SetTextColor(palette.Text);
        input.SetHintTextColor(palette.Muted);
        StyleSystemText(input, 13.5f);
        input.SetSingleLine(true);
        input.SetPadding(Dp(context, 12), Dp(context, 10), Dp(context, 12), Dp(context, 10));
        input.Background = Rounded(palette.Inner, 12, context, palette.Border);
        return input;
    }

    public static void Divider(LinearLayout parent, Palette palette)
    {
        var divider = new View(parent.Context);
        divider.SetBackgroundColor(palette.Border);
        var layout = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, Math.Max(1, Dp(parent.Context, 0.7)));
        layout.TopMargin = Dp(parent.Context, 10);
        layout.BottomMargin = Dp(parent.Context, 10);
        parent.AddView(divider, layout);
    }

    public static void Toast(Context context, string text)
    {
        Android.Widget.Toast.MakeText(context, text, ToastLength.Short)?.Show();
    }

    public static Color Blend(Color a, Color b, float amount)
    {
        var value = Math.Clamp(amount, 0f, 1f);
        return Color.Argb(
            Math.Max(a.A, b.A),
            (int)(a.R + (b.R - a.R) * value),
            (int)(a.G + (b.G - a.G) * value),
            (int)(a.B + (b.B - a.B) * value));
    }
}

/// <summary>Удобное чтение цвета текста из палитры (для мест, где нужен «обычный» цвет).</summary>
internal static class PaletteExtensions
{
    public static Color TextSoft(this Palette palette) => palette.Text;
}

