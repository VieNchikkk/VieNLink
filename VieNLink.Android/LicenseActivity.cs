using Android.App;
using Android.Content;
using Android.OS;
using Android.Views;
using Android.Widget;

namespace VieNLink.Mobile;

/// <summary>Показывает текст лицензии или список сторонних компонентов из assets APK.</summary>
#if DEBUG
[Activity(Label = "Лицензия", Theme = "@style/Theme.VieNLink", Exported = true)]
#else
[Activity(Label = "Лицензия", Theme = "@style/Theme.VieNLink")]
#endif
public sealed class LicenseActivity : BaseActivity
{
    public const string ExtraAsset = "asset";
    public const string ExtraTitle = "assetTitle";

    private TextView? _view;

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);

        var page = ColumnPage();
        var top = Ui.Row(this);
        top.AddView(Ui.Pill(this, P, "Назад", UiButtonKind.Ghost, Finish));
        var title = Ui.Title(this, Intent?.GetStringExtra(ExtraTitle) ?? "Лицензия", P);
        title.SetPadding(Ui.Dp(this, 10), 0, 0, 0);
        top.AddView(title, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f));
        page.AddView(top);

        _view = new TextView(this)
        {
            TextSize = 11.5f,
            Typeface = Android.Graphics.Typeface.Monospace,
        };
        _view.SetTextColor(P.MutedStrong);
        _view.SetTextIsSelectable(true);
        _view.SetBackgroundColor(P.Inner);
        _view.SetPadding(Ui.Dp(this, 12), Ui.Dp(this, 12), Ui.Dp(this, 12), Ui.Dp(this, 12));
        Ui.Add(page, _view, 12);

        SetPage(page);
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
