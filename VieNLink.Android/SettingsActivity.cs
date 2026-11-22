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
    private TextView? _updateStatus;

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

        // -------------------------------------------------------------- обновления
        page.AddView(Ui.Section(this, "Обновления", P));
        if (StoreBuildInfo.IsStoreBuild)
        {
            page.AddView(Ui.Hint(this,
                "Обновления устанавливаются через магазин приложений (Google Play или RuStore).", P));
        }
        else
        {
            _updateStatus = Ui.Hint(this,
                "Проверка идёт автоматически при запуске. Кнопка проверяет вручную, скачивает новую версию и предлагает установку.", P);
            var update = Ui.Pill(this, P, "Проверить обновление", UiButtonKind.Accent,
                () => _ = CheckUpdateAsync(), Resource.Drawable.ic_download);
            page.AddView(update);
            page.AddView(_updateStatus);
        }

        // -------------------------------------------------------------- уведомление
        page.AddView(Ui.Section(this, "Плитка, виджет и уведомление", P));
        var tile = Ui.Card(P, this,
            Ui.Text(this, "Плитка и виджет VieNLink", 14.5f, P.TextStrong, bold: true),
            Ui.Hint(this,
                "Плитка добавляется вручную: откройте редактор плиток Android и перетащите VieNLink. " +
                "Виджет добавляется долгим нажатием на рабочем столе → «Виджеты» → VieNLink; " +
                "он показывает состояние туннеля и кнопки «Лучший» и «Отключить». " +
                "Уведомление туннеля содержит действия «Лучший», «Серверы» и «Отключить».", P));
        page.AddView(tile);

        // -------------------------------------------------------------- автоматика
        page.AddView(Ui.Section(this, "Автоматика", P));
        var paused = SettingsStore.Current.AutomationPaused;
        var automation = Ui.Pill(this, P, paused ? "Запустить все процессы" : "Остановить все процессы",
            paused ? UiButtonKind.Primary : UiButtonKind.Danger,
            ToggleAutomation, Resource.Drawable.ic_power);
        page.AddView(automation);
        page.AddView(Ui.Hint(this,
            "Останавливает все фоновые процессы приложения: проверку серверов, сбор и проверку MTProto, " +
            "обслуживание по будильнику и автообновления. Подключённый туннель и ручные кнопки продолжают работать.",
            P));

        // -------------------------------------------------------------- данные и VPN
        page.AddView(Ui.Section(this, "Данные и VPN", P));
        var clear = Ui.Pill(this, P, "Очистить кэш серверов", UiButtonKind.Danger, ConfirmClearNodes,
            Resource.Drawable.ic_delete);
        page.AddView(clear);
        var vpnSettings = Ui.Pill(this, P, "Настройки VPN Android", UiButtonKind.Secondary,
            OpenVpnSettings, Resource.Drawable.ic_settings);
        Ui.Add(page, vpnSettings, 8);
        page.AddView(Ui.Hint(this,
            "Очистка удаляет серверы и результаты проверок, источники остаются. " +
            "Настройки VPN Android открывают системный экран VPN (постоянная VPN, исключения).", P));

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

    private void ToggleAutomation()
    {
        var settings = SettingsStore.Current;
        settings.AutomationPaused = !settings.AutomationPaused;
        SettingsStore.Save();
        Automation.Apply(this);
        Recreate();
    }

    private async Task CheckUpdateAsync()
    {
        if (_updateStatus is null)
        {
            return;
        }

        _updateStatus.Text = "Проверяю GitHub…";
        await UpdateCenter.CheckInteractiveAsync(this, text => _updateStatus.Text = text);
    }

    private void ConfirmClearNodes()
    {
        using var dialog = new AlertDialog.Builder(this)
            .SetTitle("Очистить кэш серверов?")
            .SetMessage("Источники останутся, но все серверы и результаты проверок будут удалены. " +
                        "После этого нужно обновить источники и проверить серверы.")
            .SetPositiveButton("Очистить", (_, _) =>
            {
                var removed = NodeStore.ClearCache();
                SetStatus($"Удалено серверов: {removed}");
                Ui.Toast(this, $"Удалено серверов: {removed}");
            })
            .SetNegativeButton("Отмена", (_, _) => { })
            .Create();
        dialog.Show();
    }

    private void OpenVpnSettings()
    {
        try
        {
            StartActivity(new Intent(Android.Provider.Settings.ActionVpnSettings));
        }
        catch (Exception exception)
        {
            Ui.Toast(this, "Не удалось открыть настройки: " + exception.Message);
        }
    }

    private void SetStatus(string text) => RunOnUiThread(() =>
    {
        if (_status is not null)
        {
            _status.Text = text;
        }
    });
}
