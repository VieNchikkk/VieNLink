using Android.App;
using Android.Content;
using Android.Graphics;
using Android.OS;
using Android.Views;
using Android.Widget;

namespace VieNLink.Mobile;

/// <summary>
/// Настройки Android-версии: оформление (тема), обновления, автоматика, данные и VPN,
/// служебные ссылки. Открываются четвёртой вкладкой нижнего меню.
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
        Ui.StyleSystemText(theme, 14.5f);
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
                "он показывает состояние туннеля и кнопки «Следующий» и «Отключить». " +
                "Уведомление туннеля содержит действия «Следующий», «Серверы» и «Отключить».", P));
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

        // ------------------------------------------------- автоматические работы
        page.AddView(Ui.Section(this, "Автоматические работы", P));
        page.AddView(Ui.Hint(this,
            "Каждую работу можно выключить отдельно. Общий выключатель «Остановить все процессы» выше " +
            "гасит их все сразу; туннель и ручные кнопки работают всегда.", P));

        var settings = SettingsStore.Current;
        var android = AndroidSettingsStore.Current;
        AddAutomationToggle(page, "Автопоиск новых источников",
            "Искать новые списки в GitHub. Берём только репозитории, обновлённые за выбранное " +
            "окно свежести; источники без даты публикации не добавляем.",
            settings.AutoDiscoverSources,
            value => settings.AutoDiscoverSources = value);
        AddAutomationToggle(page, "Авто-сбор серверов из источников",
            "Периодически заново собирать серверы из включённых источников. " +
            "GitHub-источники берём только за выбранное окно свежести; без даты публикации удаляем.",
            settings.AutoCollectServers,
            value =>
            {
                settings.AutoCollectServers = value;
                settings.AutoUpdateSubscriptions = value;
            });

        var freshness = Ui.Input(this, P, "Свежесть источников, ч", settings.SourceFreshnessHours.ToString());
        freshness.InputType = Android.Text.InputTypes.ClassNumber;
        freshness.FocusChange += (_, args) =>
        {
            if (args.HasFocus)
            {
                return;
            }

            if (int.TryParse(freshness.Text, out var hours))
            {
                settings.SourceFreshnessHours = Math.Clamp(hours, 1, 24 * 30);
                freshness.Text = settings.SourceFreshnessHours.ToString();
                SettingsStore.Save();
            }
        };
        page.AddView(freshness);
        page.AddView(Ui.Hint(this,
            "Собирать серверы только из источников, опубликованных не позже этого времени. " +
            "По умолчанию 24 часа; источники без даты публикации удаляются.", P));

        AddAutomationToggle(page, "Автопроверка серверов",
            "Проверять накопленные и рабочие серверы в фоне отдельным процессом :probe.",
            settings.IdleTestEnabled,
            value =>
            {
                settings.IdleTestEnabled = value;
                android.AutoProbe = value;
            });
        AddAutomationToggle(page, "Автосбор и проверка MTProto",
            "Читать каналы Telegram через RSS-зеркала и проверять MTProto-прокси.",
            android.AutoMtProto,
            value => android.AutoMtProto = value);
        AddAutomationToggle(page, "Автопереключение при сбое сервера",
            "Переключаться на запасной сервер, если текущий перестал открывать сайт.",
            settings.AutoFailover,
            value => settings.AutoFailover = value);
        AddAutomationToggle(page, "Автопроверка обновлений приложения",
            "Проверять новые версии VieNLink на GitHub. Кнопка «Проверить обновление» работает всегда.",
            settings.AutoCheckAppUpdates,
            value => settings.AutoCheckAppUpdates = value);

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

        SetPage(page);
    }

    /// <summary>Переключатель одной автоматической работы; сохраняет настройки сразу.</summary>
    private void AddAutomationToggle(
        LinearLayout page, string title, string hint, bool value, Action<bool> apply)
    {
        var box = new CheckBox(this) { Text = title, Checked = value };
        box.SetTextColor(P.TextSoft());
        Ui.StyleSystemText(box, 14.5f);
        box.CheckedChange += (_, args) =>
        {
            apply(args.IsChecked);
            SettingsStore.Save();
            AndroidSettingsStore.Save();
            SetStatus((args.IsChecked ? "Включено: " : "Выключено: ") + title);
        };
        page.AddView(box);

        var note = Ui.Hint(this, hint, P);
        note.SetPadding(Ui.Dp(this, 28), 0, 0, Ui.Dp(this, 4));
        page.AddView(note);
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
