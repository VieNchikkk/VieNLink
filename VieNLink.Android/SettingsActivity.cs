using Android.App;
using Android.Content;
using Android.Graphics;
using Android.OS;
using Android.Views;
using Android.Widget;

namespace VieNLink.Mobile;

/// <summary>
/// Настройки Android-версии: оформление (тема), памятка про плитку и служебные ссылки.
/// Поведение приложения всегда автоматическое — значения по умолчанию задаёт SettingsStore.
/// </summary>
#if DEBUG
[Activity(Label = "Настройки", Theme = "@style/Theme.VieNLink", Exported = true)]
#else
[Activity(Label = "Настройки", Theme = "@style/Theme.VieNLink")]
#endif
public sealed class SettingsActivity : BaseActivity
{
    private TextView? _status;

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        BuildPage();
    }

    private void BuildPage()
    {
        var page = ColumnPage();

        var top = Ui.Row(this);
        top.AddView(Ui.Pill(this, P, "Назад", UiButtonKind.Ghost, Finish));
        var title = Ui.Title(this, "Настройки", P);
        title.SetPadding(Ui.Dp(this, 10), 0, 0, 0);
        top.AddView(title, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f));
        page.AddView(top);

        _status = Ui.Hint(this, "Изменения сохраняются сразу", P);
        _status.SetPadding(0, Ui.Dp(this, 6), 0, 0);
        page.AddView(_status);

        // Настройки поведения убраны: приложение работает автоматически со значениями
        // по умолчанию (см. SettingsStore.Load). Здесь остаются только оформление,
        // памятка про плитку и служебные ссылки.

        // ------------------------------------------------------------------ тема
        page.AddView(Ui.Section(this, "Оформление", P));
        var theme = new CheckBox(this)
        {
            Text = "Светлая тема",
            Checked = ThemeManager.IsLight,
        };
        theme.SetTextColor(P.TextSoft());
        theme.CheckedChange += (_, args) =>
        {
            if (SettingsStore.Current.Theme.Equals(args.IsChecked ? "Light" : "Dark", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            SettingsStore.Current.Theme = args.IsChecked ? "Light" : "Dark";
            SettingsStore.Save();
            Recreate();
        };
        page.AddView(theme);

        // -------------------------------------------------------------- уведомление
        page.AddView(Ui.Section(this, "Плитка и уведомление", P));
        var tile = Ui.Card(P, this,
            Ui.Text(this, "Плитка VieNLink", 14.5f, P.TextStrong, bold: true),
            Ui.Hint(this,
                "Плитка добавляется вручную: откройте редактор плиток Android и перетащите VieNLink. " +
                "Уведомление туннеля содержит действия «Лучший», «Узлы» и «Отключить».", P));
        page.AddView(tile);

        // ------------------------------------------------------------------ прочее
        page.AddView(Ui.Section(this, "Прочее", P));
        var about = Ui.Pill(this, P, "О программе и лицензии", UiButtonKind.Secondary,
            () => StartActivity(new Intent(this, typeof(AboutActivity))), Resource.Drawable.ic_info);
        page.AddView(about);
        var log = Ui.Pill(this, P, "Открыть журнал", UiButtonKind.Secondary,
            () => StartActivity(new Intent(this, typeof(LogActivity))), Resource.Drawable.ic_tab_log);
        Ui.Add(page, log, 8);

        SetPage(page);
    }

    private void SetStatus(string text) => RunOnUiThread(() =>
    {
        if (_status is not null)
        {
            _status.Text = text;
        }
    });
}
