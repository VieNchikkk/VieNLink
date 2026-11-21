using Android.App;
using Android.Content;
using Android.Graphics;
using Android.OS;
using Android.Views;
using Android.Widget;

namespace VieNLink.Mobile;

/// <summary>
/// «О программе»: версия, лицензия GPL-3.0, ссылка на исходники sing-box и сторонние
/// компоненты. Тексты лицензий лежат в APK как assets (см. VieNLink.Android.csproj).
/// </summary>
#if DEBUG
[Activity(Label = "О программе", Theme = "@android:style/Theme.Material.Light.NoActionBar", Exported = true)]
#else
[Activity(Label = "О программе", Theme = "@android:style/Theme.Material.Light.NoActionBar")]
#endif
public sealed class AboutActivity : Activity
{
    private const string SingBoxSource = "https://github.com/SagerNet/sing-box";
    private const string GplUrl = "https://www.gnu.org/licenses/gpl-3.0.html";

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);

        var root = new LinearLayout(this) { Orientation = Android.Widget.Orientation.Vertical };
        root.SetPadding(
            UiMetrics.Dp(this, 16),
            UiMetrics.StatusBar(this) + UiMetrics.Dp(this, 8),
            UiMetrics.Dp(this, 16),
            UiMetrics.NavigationBar(this) + UiMetrics.Dp(this, 8));

        var title = new TextView(this) { Text = "О программе", TextSize = 24f };
        title.SetTypeface(null, TypefaceStyle.Bold);

        var version = new TextView(this) { TextSize = 14f };
        version.SetTextColor(Color.DarkGray);
        version.Text = "VieNLink · версия " + ReadVersion();

        var body = new TextView(this) { TextSize = 14f };
        body.Text = "Клиент для сбора, проверки и подключения прокси-узлов. " +
                    "Android-версия использует ядро sing-box (libbox) через JNI.\n\n" +
                    "Проект распространяется по лицензии GNU GPL-3.0: исходный код открыт, " +
                    "потому что libbox (sing-box) распространяется под GPL-3.0.";

        var gpl = new Button(this) { Text = "Текст GPL-3.0" };
        gpl.Click += (_, _) => OpenLicense("LICENSE.txt", "GPL-3.0");

        var notices = new Button(this) { Text = "Сторонние компоненты" };
        notices.Click += (_, _) => OpenLicense("THIRD-PARTY-NOTICES.md", "Сторонние компоненты");

        var source = new Button(this) { Text = "Исходники sing-box" };
        source.Click += (_, _) => OpenUrl(SingBoxSource);

        var license = new Button(this) { Text = "Лицензия GPL-3.0 на сайте GNU" };
        license.Click += (_, _) => OpenUrl(GplUrl);

        root.AddView(title);
        root.AddView(version);
        root.AddView(body);
        root.AddView(gpl);
        root.AddView(notices);
        root.AddView(source);
        root.AddView(license);

        SetContentView(root);
    }

    private string ReadVersion()
    {
        try
        {
            var info = PackageManager!.GetPackageInfo(PackageName!, 0);
            return info?.VersionName ?? "1.0.5";
        }
        catch (Exception)
        {
            return "1.0.5";
        }
    }

    private void OpenLicense(string asset, string title)
    {
        var intent = new Intent(this, typeof(LicenseActivity))
            .PutExtra(LicenseActivity.ExtraAsset, asset)
            .PutExtra(LicenseActivity.ExtraTitle, title);
        StartActivity(intent);
    }

    private void OpenUrl(string url)
    {
        try
        {
            var intent = new Intent(Intent.ActionView, Android.Net.Uri.Parse(url)!)
                .AddFlags(ActivityFlags.NewTask);
            StartActivity(intent);
        }
        catch (Exception exception)
        {
            Toast.MakeText(this, "Не удалось открыть ссылку: " + exception.Message, ToastLength.Short)?.Show();
        }
    }
}
