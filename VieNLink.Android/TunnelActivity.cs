using Android.App;
using Android.Content;
using Android.OS;
using Android.Views;
using Android.Widget;

namespace VieNLink.Mobile;

/// <summary>
/// Экран «Туннелирование»: только маршрутизация по доменам — как вкладка
/// «Туннелирование» в Windows-версии. Состояние, подключение, замер, экспорт
/// и системные настройки живут на своих экранах, чтобы не дублироваться здесь.
/// </summary>
#if DEBUG
[Activity(Label = "Туннелирование", Theme = "@style/Theme.VieNLink", Exported = true)]
#else
[Activity(Label = "Туннелирование", Theme = "@style/Theme.VieNLink")]
#endif
public sealed class TunnelActivity : BaseActivity
{
    private TextView? _status;
    private TextView? _appsSummary;
    private EditText? _domains;

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
        var title = Ui.Title(this, "Туннелирование", P);
        title.SetPadding(Ui.Dp(this, 10), 0, 0, 0);
        top.AddView(title, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f));
        top.AddView(Ui.Badge(this, P, "TUN", P.Accent));
        page.AddView(top);

        _status = Ui.Hint(this, "Готово", P);
        Ui.Add(page, _status, 10);

        // ------------------------------------------------------------ маршрутизация
        page.AddView(Ui.Section(this, "Маршрутизация по доменам", P));
        var settings = SettingsStore.Current;
        var routeCard = Ui.Card(P, this);

        var modes = new[]
        {
            (SiteRouteMode.All, "Весь трафик через сервер"),
            (SiteRouteMode.OnlyListed, "Только сайты из списка"),
            (SiteRouteMode.AllExceptListed, "Всё, кроме сайтов из списка"),
        };

        var group = new RadioGroup(this);
        foreach (var (mode, label) in modes)
        {
            var radio = new RadioButton(this) { Text = label };
            radio.SetTextColor(P.TextSoft());
            group.AddView(radio);
            // Checked задаём через группу уже после AddView: иначе RadioGroup не запоминает
            // выбранный элемент и после первого тапа остаются отмеченными два.
            if (settings.SiteRouteMode == mode)
            {
                group.Check(radio.Id);
            }

            radio.CheckedChange += (_, args) =>
            {
                if (!args.IsChecked)
                {
                    return;
                }

                SettingsStore.Current.SiteRouteMode = mode;
                SettingsStore.Save();
                _domains!.Enabled = mode != SiteRouteMode.All;
                SetStatus("Режим маршрутизации сохранён. Переподключитесь, чтобы применить.");
            };
        }

        routeCard.AddView(group);

        _domains = new EditText(this)
        {
            Hint = "youtube.com\nt.me\ndiscord.com",
            Text = settings.SiteList,
            Enabled = settings.SiteRouteMode != SiteRouteMode.All,
            Gravity = GravityFlags.Top,
        };
        _domains.SetTextColor(P.Text);
        _domains.SetHintTextColor(P.Muted);
        _domains.TextSize = 13f;
        _domains.SetSingleLine(false);
        _domains.SetMinLines(4);
        _domains.SetPadding(Ui.Dp(this, 12), Ui.Dp(this, 10), Ui.Dp(this, 12), Ui.Dp(this, 10));
        _domains.Background = Ui.Rounded(P.Inner, 12, this, P.Border);
        var domainsLayout = new LinearLayout.LayoutParams(
            ViewGroup.LayoutParams.MatchParent, Ui.Dp(this, 110));
        domainsLayout.TopMargin = Ui.Dp(this, 8);
        routeCard.AddView(_domains, domainsLayout);
        var save = Ui.Pill(this, P, "Сохранить список", UiButtonKind.Accent, () =>
        {
            SettingsStore.Current.SiteList = _domains.Text ?? string.Empty;
            SettingsStore.Save();
            SetStatus("Список доменов сохранён. Переподключитесь, чтобы применить.");
        }, Resource.Drawable.ic_check);
        Ui.Add(routeCard, save, 8);
        routeCard.AddView(Ui.Hint(this,
            "Домены пишутся по одному в строке, без https://. Правило работает внутри TUN: " +
            "остальной трафик идёт напрямую.", P));
        page.AddView(routeCard);

        var modeHint = Ui.Hint(this,
            "Режим TUN на Android включён всегда: системного прокси здесь нет. Выше — какие сайты " +
            "пускать через сервер; остальные идут напрямую.", P);
        Ui.Add(page, modeHint, 10);

        // -------------------------------------------------------- приложения
        page.AddView(Ui.Section(this, "Маршрутизация по приложениям", P));
        var appsCard = Ui.Card(P, this);
        var android = AndroidSettingsStore.Current;

        var appModes = new[]
        {
            (AppRouteMode.All, "Все приложения через сервер"),
            (AppRouteMode.OnlyListed, "Только выбранные приложения"),
            (AppRouteMode.AllExceptListed, "Все, кроме выбранных"),
        };

        var appGroup = new RadioGroup(this);
        foreach (var (mode, label) in appModes)
        {
            var radio = new RadioButton(this) { Text = label };
            radio.SetTextColor(P.TextSoft());
            appGroup.AddView(radio);
            // Как и выше: выбор задаём через группу, иначе остаются два отмеченных.
            if (android.AppRouteMode == mode)
            {
                appGroup.Check(radio.Id);
            }

            radio.CheckedChange += (_, args) =>
            {
                if (!args.IsChecked)
                {
                    return;
                }

                var current = AndroidSettingsStore.Current;
                current.AppRouteMode = mode;
                current.SplitTunnel = mode != AppRouteMode.All;
                AndroidSettingsStore.Save();
                UpdateAppsSummary();
                SetStatus("Маршрутизация приложений сохранена. Переподключитесь, чтобы применить.");
            };
        }

        appsCard.AddView(appGroup);

        _appsSummary = Ui.Hint(this, string.Empty, P);
        _appsSummary.SetPadding(0, Ui.Dp(this, 6), 0, 0);
        appsCard.AddView(_appsSummary);

        var pick = Ui.Pill(this, P, "Выбрать приложения", UiButtonKind.Accent,
            () => StartActivity(new Intent(this, typeof(AppPickerActivity))), Resource.Drawable.ic_settings);
        Ui.Add(appsCard, pick, 8);
        appsCard.AddView(Ui.Hint(this,
            "Короткий тап в списке отмечает приложение. Сам VieNLink всегда остаётся вне туннеля: " +
            "иначе ядро ушло бы в собственный VPN.", P));
        page.AddView(appsCard);

        UpdateAppsSummary();
        SetPage(page);
    }

    protected override void OnResume()
    {
        base.OnResume();
        UpdateAppsSummary();
    }

    private void UpdateAppsSummary()
    {
        if (_appsSummary is null)
        {
            return;
        }

        var settings = AndroidSettingsStore.Current;
        var count = settings.AllowedPackages.Count;
        _appsSummary.Text = settings.AppRouteMode switch
        {
            AppRouteMode.OnlyListed => count == 0
                ? "Список пуст — через сервер пойдут все приложения. Отметьте нужные в списке."
                : $"Через сервер пойдут только отмеченные приложения: {count}.",
            AppRouteMode.AllExceptListed => count == 0
                ? "Список пуст — через сервер пойдут все приложения. Отметьте те, что должны идти мимо."
                : $"Через сервер пойдут все приложения, кроме отмеченных: {count}.",
            _ => "Через сервер идут все приложения, кроме самого VieNLink.",
        };
    }

    private void SetStatus(string text) => RunOnUiThread(() =>
    {
        if (_status is not null)
        {
            _status.Text = text;
        }
    });
}
