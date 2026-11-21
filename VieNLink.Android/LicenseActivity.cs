using Android.App;
using Android.Graphics;
using Android.OS;
using Android.Views;
using Android.Widget;

namespace VieNLink.Mobile;

/// <summary>
/// Показывает текст лицензии или список сторонних компонентов из assets APK.
/// </summary>
#if DEBUG
[Activity(Label = "Лицензия", Theme = "@android:style/Theme.Material.Light.NoActionBar", Exported = true)]
#else
[Activity(Label = "Лицензия", Theme = "@android:style/Theme.Material.Light.NoActionBar")]
#endif
public sealed class LicenseActivity : Activity
{
    public const string ExtraAsset = "asset";
    public const string ExtraTitle = "assetTitle";

    private TextView? _view;

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);

        var root = new LinearLayout(this) { Orientation = Android.Widget.Orientation.Vertical };
        root.SetPadding(
            UiMetrics.Dp(this, 16),
            UiMetrics.StatusBar(this) + UiMetrics.Dp(this, 8),
            UiMetrics.Dp(this, 16),
            UiMetrics.NavigationBar(this) + UiMetrics.Dp(this, 8));

        var title = new TextView(this)
        {
            Text = Intent?.GetStringExtra(ExtraTitle) ?? "Лицензия",
            TextSize = 22f,
        };
        title.SetTypeface(null, TypefaceStyle.Bold);

        _view = new TextView(this) { TextSize = 12f, Typeface = Typeface.Monospace };
        _view.SetTextIsSelectable(true);

        var scroll = new ScrollView(this);
        scroll.AddView(_view);

        root.AddView(title);
        root.AddView(scroll, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, 0, 1f));
        SetContentView(root);

        Load();
    }

    private void Load()
    {
        var asset = Intent?.GetStringExtra(ExtraAsset) ?? "LICENSE.txt";
        _ = Task.Run(() =>
        {
            var text = "Не удалось прочитать " + asset;
            try
            {
                using var stream = Assets!.Open(asset);
                using var reader = new StreamReader(stream);
                text = reader.ReadToEnd();
            }
            catch (Exception exception)
            {
                AppLog.Write("android", "Не удалось прочитать лицензию " + asset, exception);
                text += ": " + exception.Message;
            }

            RunOnUiThread(() =>
            {
                if (_view is not null)
                {
                    _view.Text = text;
                }
            });
        });
    }
}
