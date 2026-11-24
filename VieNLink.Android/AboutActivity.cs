using Android.App;
using Android.Content;
using Android.OS;
using Android.Views;
using Android.Widget;

namespace VieNLink.Mobile;

/// <summary>
/// «О программе»: версия, бета-статус, лицензия GPL-3.0, исходники sing-box,
/// сторонние компоненты и проверка обновлений на GitHub.
/// </summary>
#if DEBUG
[Activity(Label = "О программе", Theme = "@style/Theme.VieNLink", Exported = true)]
#else
[Activity(Label = "О программе", Theme = "@style/Theme.VieNLink")]
#endif
public sealed class AboutActivity : BaseActivity
{
    private const string SingBoxSource = "https://github.com/SagerNet/sing-box";
    private const string GplUrl = "https://www.gnu.org/licenses/gpl-3.0.html";
    private const string Repository = "https://github.com/VieNchikkk/VieNLink";
    private const string PrivacyUrl = "https://vienchikkk.github.io/VieNLink/privacy.html";

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);

        var page = ColumnPage();
        var top = Ui.Row(this);
        top.AddView(Ui.Pill(this, P, "Назад", UiButtonKind.Ghost, Finish));
        var title = Ui.Title(this, "О программе", P);
        title.SetPadding(Ui.Dp(this, 10), 0, 0, 0);
        top.AddView(title, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f));
        page.AddView(top);

        var hero = Ui.HeaderCard(P, this);
        var logo = new ImageView(this);
        logo.SetScaleType(ImageView.ScaleType.CenterCrop);
        logo.SetImageResource(Resource.Mipmap.ic_launcher);
        logo.Background = Ui.Rounded(P.Inner, 20, this, P.Border);
        logo.ClipToOutline = true;
        var logoLayout = new LinearLayout.LayoutParams(
            Ui.Dp(this, Ui.IsCompactLayout ? 72 : 84), Ui.Dp(this, Ui.IsCompactLayout ? 72 : 84));
        logoLayout.Gravity = GravityFlags.CenterHorizontal;
        hero.AddView(logo, logoLayout);

        var name = Ui.Text(this, "VieNLink", 24f, P.TextStrong, bold: true);
        name.Gravity = GravityFlags.Center;
        name.SetPadding(0, Ui.Dp(this, 12), 0, 0);
        hero.AddView(name);
        var version = Ui.Text(this, "Android · версия " + ReadVersion(), 13f, P.Muted);
        version.Gravity = GravityFlags.Center;
        hero.AddView(version);
        var beta = Ui.Badge(this, P, "БЕТА", P.Warning);
        var betaLayout = new LinearLayout.LayoutParams(
            ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent);
        betaLayout.Gravity = GravityFlags.CenterHorizontal;
        betaLayout.TopMargin = Ui.Dp(this, 8);
        hero.AddView(beta, betaLayout);
        Ui.Add(page, hero, 12);

        var body = Ui.Card(P, this,
            Ui.Text(this, "Назначение", 14.5f, P.TextStrong, bold: true),
            Ui.Hint(this,
                "Клиент для сбора, проверки и подключения прокси-серверов из открытых списков, " +
                "подписок и каналов Telegram. Ядро — sing-box через libbox (JNI).", P),
            Ui.Text(this, "Лицензия", 14.5f, P.TextStrong, bold: true),
            Ui.Hint(this,
                "Проект распространяется по GNU GPL-3.0: libbox (sing-box) тоже под GPL-3.0, " +
                "поэтому исходный код открыт. Тексты лицензий лежат в APK.", P),
            Ui.Text(this, "Безопасность", 14.5f, P.TextStrong, bold: true),
            Ui.Hint(this,
                "Бесплатные серверы никем не проверяются: владелец сервера видит ваш трафик. " +
                "Не входите через них в банки и почту, если не доверяете серверу.", P));
        page.AddView(body);
        page.AddView(Ui.Hint(this,
            "Проверка и установка обновлений — в «Настройках» → «Обновления».", P));

        page.AddView(Ui.Section(this, "Документы и исходники", P));
        page.AddView(Ui.Pill(this, P, "Политика конфиденциальности", UiButtonKind.Ghost,
            () => OpenUrl(PrivacyUrl), Resource.Drawable.ic_open));
        page.AddView(Ui.Pill(this, P, "Текст GPL-3.0", UiButtonKind.Secondary,
            () => OpenLicense("LICENSE.txt", "GPL-3.0"), Resource.Drawable.ic_info));
        page.AddView(Ui.Pill(this, P, "Сторонние компоненты", UiButtonKind.Secondary,
            () => OpenLicense("THIRD-PARTY-NOTICES.md", "Сторонние компоненты"), Resource.Drawable.ic_info));
        page.AddView(Ui.Pill(this, P, "Исходники sing-box", UiButtonKind.Ghost,
            () => OpenUrl(SingBoxSource), Resource.Drawable.ic_open));
        page.AddView(Ui.Pill(this, P, "Репозиторий VieNLink", UiButtonKind.Ghost,
            () => OpenUrl(Repository), Resource.Drawable.ic_open));
        page.AddView(Ui.Pill(this, P, "Лицензия GPL-3.0 на сайте GNU", UiButtonKind.Ghost,
            () => OpenUrl(GplUrl), Resource.Drawable.ic_open));

        var paths = Ui.Card(P, this,
            Ui.Text(this, "Служебные папки", 14f, P.TextStrong, bold: true),
            Ui.Hint(this, "Данные: " + AppPaths.DataDirectory + "\nЖурналы: " + AppPaths.LogsDirectory, P));
        Ui.Add(page, paths, 14);

        SetPage(page);
    }

    private string ReadVersion() => UpdateCenter.CurrentVersion(this);

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
            Ui.Toast(this, "Не удалось открыть ссылку: " + exception.Message);
        }
    }
}
