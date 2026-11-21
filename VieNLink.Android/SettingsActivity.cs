using Android.App;
using Android.Content;
using Android.Graphics;
using Android.OS;
using Android.Views;
using Android.Widget;

namespace VieNLink.Mobile;

/// <summary>
/// Настройки Android-версии: автозапуск, плитка и split-tunnel (какие приложения
/// пускать через туннель). Общие настройки ядра остаются в settings.json.
/// </summary>
#if DEBUG
// В отладочной сборке экран открыт наружу: так его запускают adb-команды из §0 плана.
[Activity(Label = "Настройки", Theme = "@android:style/Theme.Material.Light.NoActionBar", Exported = true)]
#else
[Activity(Label = "Настройки", Theme = "@android:style/Theme.Material.Light.NoActionBar")]
#endif
public sealed class SettingsActivity : Activity
{
    private CheckBox? _boot;
    private CheckBox? _split;
    private Button? _apps;
    private TextView? _hint;

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);

        var root = new LinearLayout(this) { Orientation = Android.Widget.Orientation.Vertical };
        root.SetPadding(
            UiMetrics.Dp(this, 16),
            UiMetrics.StatusBar(this) + UiMetrics.Dp(this, 8),
            UiMetrics.Dp(this, 16),
            UiMetrics.NavigationBar(this) + UiMetrics.Dp(this, 8));

        var title = new TextView(this) { Text = "Настройки", TextSize = 24f };
        title.SetTypeface(null, TypefaceStyle.Bold);

        var settings = AndroidSettingsStore.Current;

        _boot = new CheckBox(this)
        {
            Text = "Подключаться после загрузки устройства",
            Checked = settings.AutoConnectOnBoot,
        };
        _boot.CheckedChange += (_, args) =>
        {
            AndroidSettingsStore.Current.AutoConnectOnBoot = args.IsChecked;
            AndroidSettingsStore.Save();
        };

        _split = new CheckBox(this)
        {
            Text = "Туннель только для выбранных приложений",
            Checked = settings.SplitTunnel,
        };
        _split.CheckedChange += (_, args) =>
        {
            AndroidSettingsStore.Current.SplitTunnel = args.IsChecked;
            AndroidSettingsStore.Save();
            UpdateHint();
        };

        _apps = new Button(this) { Text = "Приложения" };
        _apps.Click += (_, _) => StartActivity(typeof(AppPickerActivity));

        _hint = new TextView(this) { TextSize = 13f };
        _hint.SetTextColor(Color.DarkGray);

        var tileHint = new TextView(this) { TextSize = 13f };
        tileHint.SetTextColor(Color.DarkGray);
        tileHint.Text = "Плитка VieNLink: добавьте её в шторку быстрых настроек " +
                        "через меню редактирования плиток Android.";

        var about = new Button(this) { Text = "О программе и лицензии" };
        about.Click += (_, _) => StartActivity(typeof(AboutActivity));

        root.AddView(title);
        root.AddView(_boot);
        root.AddView(_split);
        root.AddView(_apps);
        root.AddView(_hint);
        root.AddView(tileHint);
        root.AddView(about);

        SetContentView(root);
        UpdateHint();
    }

    protected override void OnResume()
    {
        base.OnResume();
        UpdateHint();
    }

    private void UpdateHint()
    {
        if (_hint is null || _apps is null)
        {
            return;
        }

        var settings = AndroidSettingsStore.Current;
        var count = settings.AllowedPackages.Count;
        _apps.Text = "Приложения: " + count;

        if (!settings.SplitTunnel)
        {
            _hint.Text = "Сейчас через туннель идут все приложения, кроме самого VieNLink.";
            _apps.Enabled = false;
            return;
        }

        _apps.Enabled = true;
        _hint.Text = count == 0
            ? "Ни одно приложение не выбрано — через туннель пойдут все."
            : $"Через туннель пойдут только выбранные приложения ({count}).";
    }
}
