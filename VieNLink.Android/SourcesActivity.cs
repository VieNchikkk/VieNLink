using Android.App;
using Android.Content;
using Android.Graphics;
using Android.OS;
using Android.Views;
using Android.Widget;

namespace VieNLink.Mobile;

/// <summary>
/// Источники: подписки, открытые списки и каналы Telegram. Включение, обновление,
/// добавление одиночной ссылки, личные подписки и автопоиск — как в Windows-версии.
/// </summary>
#if DEBUG
[Activity(Label = "Источники", Theme = "@style/Theme.VieNLink", Exported = true)]
#else
[Activity(Label = "Источники", Theme = "@style/Theme.VieNLink")]
#endif
public sealed class SourcesActivity : BaseActivity
{
    private LinearLayout? _list;
    private TextView? _summary;
    private TextView? _progress;
    private bool _busy;
    private bool _loaded;

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        BuildUi();
        _ = LoadAsync();
    }

    private void BuildUi()
    {
        var page = ColumnPage();

        var top = Ui.Row(this);
        top.AddView(Ui.Pill(this, P, "Назад", UiButtonKind.Ghost, Finish));
        var title = Ui.Title(this, "Источники", P);
        title.SetPadding(Ui.Dp(this, 10), 0, 0, 0);
        top.AddView(title, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f));
        page.AddView(top);

        _summary = Ui.Text(this, "Читаю…", 16f, P.TextStrong, bold: true);
        _summary.SetPadding(0, Ui.Dp(this, 12), 0, 0);
        page.AddView(_summary);
        _progress = Ui.Hint(this, "Готово", P);
        _progress.SetPadding(0, Ui.Dp(this, 4), 0, 0);
        page.AddView(_progress);

        var actions = Ui.Row(this);
        var add = Ui.Pill(this, P, "Добавить", UiButtonKind.Primary, ShowAddDialog, Resource.Drawable.ic_add);
        var refresh = Ui.Pill(this, P, "Обновить все", UiButtonKind.Accent,
            () => _ = RunRefreshAsync(), Resource.Drawable.ic_refresh);
        actions.AddView(add, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f));
        actions.AddView(new Space(this), new LinearLayout.LayoutParams(Ui.Dp(this, 8), 1));
        actions.AddView(refresh, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f));
        Ui.Add(page, actions, 12);

        var discover = Ui.Pill(this, P, "Найти новые источники", UiButtonKind.Secondary,
            () => _ = RunDiscoveryAsync(), Resource.Drawable.ic_globe);
        Ui.Add(page, discover, 8);

        var collectProxies = Ui.Pill(this, P, "Собрать Telegram-прокси", UiButtonKind.Secondary,
            () => _ = RunMtProtoCollectAsync(), Resource.Drawable.ic_refresh);
        Ui.Add(page, collectProxies, 8);

        var hint = Ui.Banner(P, this, P.Accent, "Два разных списка",
            "Источники серверов — подписки (base64, Clash/YAML, JSON) и открытые списки ссылок. " +
            "Каналы Telegram — отдельный блок: из них собираются MTProto-прокси для Telegram, " +
            "а не серверы. Ссылки подписок содержат токены и хранятся в sources.dat, " +
            "зашифрованном ключом Keystore.");
        Ui.Add(page, hint, 12);

        _list = Ui.Column(this);
        page.AddView(_list);

        SetPage(page);
    }

    private async Task LoadAsync()
    {
        try
        {
            await Task.Run(() => NodeStore.EnsureLoaded());
        }
        catch (Exception exception)
        {
            AppLog.Write("sources", "Не удалось прочитать источники", exception);
            SetProgress("Ошибка чтения: " + exception.Message);
        }

        Reload();
    }

    private void Reload()
    {
        if (_list is null || _summary is null)
        {
            return;
        }

        _list.RemoveAllViews();
        var sources = NodeStore.SnapshotSources();
        var workingBySource = NodeStore.WorkingBySource();
        var nodeSources = sources
            .Where(source => !SourceKinds.IsProxy(source))
            .OrderByDescending(source => source.IsMine)
            .ThenBy(source => source.Name)
            .ToList();
        var telegramSources = sources
            .Where(SourceKinds.IsProxy)
            .OrderBy(source => source.Name)
            .ToList();

        _summary.Text = $"Серверы: {nodeSources.Count} · Telegram-прокси: {telegramSources.Count}";

        _list.AddView(Ui.Section(this, "Источники серверов", P));
        if (nodeSources.Count == 0)
        {
            _list.AddView(Ui.Card(P, this,
                Ui.Text(this, "Источников серверов нет", 15f, P.MutedStrong, bold: true),
                Ui.Hint(this, "Нажмите «Добавить» и вставьте ссылку на подписку или открытый список.", P)));
        }
        else
        {
            foreach (var source in nodeSources)
            {
                AddSourceCard(source, workingBySource);
            }
        }

        _list.AddView(Ui.Section(this, "Telegram-прокси (каналы и сайты)", P));
        if (telegramSources.Count == 0)
        {
            _list.AddView(Ui.Card(P, this,
                Ui.Text(this, "Каналов нет", 15f, P.MutedStrong, bold: true),
                Ui.Hint(this, "Добавьте канал вида https://t.me/s/канал — из него собираются MTProto-прокси.", P)));
        }
        else
        {
            foreach (var source in telegramSources)
            {
                AddSourceCard(source, workingBySource);
            }
        }

        _loaded = true;
    }

    protected override void OnResume()
    {
        base.OnResume();
        // Вернулись на экран после проверки серверов — числа рабочих могли измениться.
        if (_loaded)
        {
            Reload();
        }
    }

    /// <summary>Карточка одного источника: общая для серверов и каналов Telegram.</summary>
    private void AddSourceCard(SourceDefinition source, IReadOnlyDictionary<string, int> workingBySource)
    {
        if (_list is null)
        {
            return;
        }

        var isProxySource = SourceKinds.IsProxy(source);
        var card = Ui.Column(this);
        card.SetPadding(Ui.Dp(this, 14), Ui.Dp(this, 12), Ui.Dp(this, 14), Ui.Dp(this, 12));
        card.Background = Ui.Rounded(source.Enabled ? P.Card : P.Inner, 14, this,
            source.Enabled ? P.Border : P.BorderSoft);

        var row = Ui.Row(this);
        var name = Ui.Text(this, source.Name, 14.5f,
            source.Enabled ? P.TextStrong : P.Muted, bold: true);
        name.Ellipsize = Android.Text.TextUtils.TruncateAt.End;
        name.SetSingleLine(true);
        row.AddView(name, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f));
        if (source.IsMine)
        {
            row.AddView(Ui.Badge(this, P, "★ МОЯ", P.Accent));
        }

        row.AddView(Ui.Badge(this, P, source.Enabled ? "ВКЛ" : "ВЫКЛ",
            source.Enabled ? P.Positive : P.Muted));
        card.AddView(row);

        var url = Ui.Text(this, source.Url, 11f, P.Muted);
        url.SetSingleLine(true);
        url.Ellipsize = Android.Text.TextUtils.TruncateAt.Middle;
        url.SetPadding(0, Ui.Dp(this, 4), 0, 0);
        card.AddView(url);

        var unit = isProxySource ? "прокси" : "серверов";
        string count;
        if (isProxySource)
        {
            var total = source.TotalNodeCount > 0 ? source.TotalNodeCount : source.LastNodeCount;
            count = total > 0 ? total + " " + unit : "прокси пока нет";
        }
        else if (source.TotalNodeCount > 0 || source.LastNodeCount > 0)
        {
            var working = workingBySource.TryGetValue(source.Name, out var found) ? found : 0;
            var total = source.TotalNodeCount > 0 ? source.TotalNodeCount : source.LastNodeCount;
            count = "всего: " + total + " · сейчас рабочих: " + working +
                    "\nрабочих всего: " + source.TotalWorkingNodeCount;
        }
        else
        {
            count = "серверов пока нет";
        }
        var status = source.LastError is not null
            ? "ошибка: " + source.LastError
            : source.LastCheckedUtc is null
                ? "не проверялся"
                : "обновлён " + source.LastCheckedUtc.Value.ToLocalTime().ToString("dd.MM HH:mm");
        var stats = Ui.Hint(this, count + " · " + status, P,
            source.LastError is null ? P.Muted : P.Danger);
        stats.SetPadding(0, Ui.Dp(this, 2), 0, 0);
        card.AddView(stats);

        if (!string.IsNullOrWhiteSpace(source.SubscriptionInfo))
        {
            var info = Ui.Hint(this, "Подписка: " + source.SubscriptionInfo, P, P.Accent);
            info.SetPadding(0, Ui.Dp(this, 2), 0, 0);
            card.AddView(info);
        }

        var buttons = Ui.Row(this);
        var toggle = Ui.Pill(this, P, source.Enabled ? "Выключить" : "Включить",
            UiButtonKind.Ghost, () =>
            {
                NodeStore.SetSourceEnabled(source, !source.Enabled);
                Reload();
                SetProgress((source.Enabled ? "Включён: " : "Выключен: ") + source.Name);
            });
        var update = Ui.Pill(this, P, "Обновить", UiButtonKind.Ghost,
            () => _ = UpdateOneAsync(source), Resource.Drawable.ic_refresh);
        var delete = Ui.Pill(this, P, "Удалить", UiButtonKind.Ghost,
            () => ConfirmRemove(source), Resource.Drawable.ic_delete);
        delete.SetTextColor(P.Danger);
        buttons.AddView(toggle, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f));
        buttons.AddView(update, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f));
        buttons.AddView(delete, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f));
        var buttonsLayout = new LinearLayout.LayoutParams(
            ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent);
        buttonsLayout.TopMargin = Ui.Dp(this, 8);
        card.AddView(buttons, buttonsLayout);

        var outer = new LinearLayout.LayoutParams(
            ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent);
        outer.BottomMargin = Ui.Dp(this, 8);
        _list.AddView(card, outer);
    }

    private void ShowAddDialog()
    {
        var layout = new LinearLayout(this) { Orientation = Android.Widget.Orientation.Vertical };
        layout.SetPadding(Ui.Dp(this, 20), Ui.Dp(this, 10), Ui.Dp(this, 20), Ui.Dp(this, 10));

        var url = Ui.Input(this, P, "https://…/sub, vless://… или https://t.me/канал");
        var name = Ui.Input(this, P, "Название (необязательно)");
        var mine = new CheckBox(this) { Text = "Моя подписка" };
        mine.SetTextColor(P.TextSoft());

        var paste = Ui.Pill(this, P, "Вставить из буфера", UiButtonKind.Ghost, () =>
        {
            try
            {
                var clipboard = (ClipboardManager?)GetSystemService(ClipboardService);
                var text = clipboard?.PrimaryClip?.GetItemAt(0)?.Text;
                if (!string.IsNullOrWhiteSpace(text))
                {
                    url.Text = text;
                }
            }
            catch (Exception exception)
            {
                SetProgress("Не удалось прочитать буфер: " + exception.Message);
            }
        }, Resource.Drawable.ic_copy);

        layout.AddView(url);
        layout.AddView(name);
        layout.AddView(mine);
        layout.AddView(paste);

        using var dialog = new AlertDialog.Builder(this)
            .SetTitle("Добавить источник")
            .SetView(layout)
            .SetPositiveButton("Добавить", (_, _) => _ = AddSourceAsync(url.Text, name.Text, mine.Checked))
            .SetNegativeButton("Отмена", (_, _) => { })
            .Create();

        dialog.Show();
    }

    private async Task AddSourceAsync(string? input, string? name, bool mine)
    {
        if (_busy)
        {
            return;
        }

        _busy = true;
        try
        {
            var before = NodeStore.SnapshotSources();
            var message = await Task.Run(() => NodeStore.AddSource(input ?? string.Empty, name, mine));
            SetProgress(message);
            Reload();

            var added = NodeStore.SnapshotSources().FirstOrDefault(source => !before.Contains(source));
            if (added is null)
            {
                return;
            }

            SetProgress("Загружаю: " + added.Name);
            var progress = new Progress<string>(SetProgress);
            var result = await NodeStore.ImportSourceAsync(added, progress, CancellationToken.None);
            SetProgress(result);
        }
        catch (Exception exception)
        {
            AppLog.Write("sources", "Не удалось добавить источник", exception);
            SetProgress("Ошибка добавления: " + exception.Message);
        }
        finally
        {
            _busy = false;
            Reload();
        }
    }

    private void ConfirmRemove(SourceDefinition source)
    {
        using var dialog = new AlertDialog.Builder(this)
            .SetMessage("Удалить источник «" + source.Name + "»? Серверы из него останутся в кэше до чистки.")
            .SetPositiveButton("Удалить", (_, _) =>
            {
                NodeStore.RemoveSource(source);
                Reload();
                SetProgress("Источник удалён: " + source.Name);
            })
            .SetNegativeButton("Отмена", (_, _) => { })
            .Create();
        dialog.Show();
    }

    private async Task UpdateOneAsync(SourceDefinition source)
    {
        if (_busy)
        {
            SetProgress("Дождитесь окончания загрузки");
            return;
        }

        _busy = true;
        SetProgress("Обновляю: " + source.Name);
        try
        {
            var progress = new Progress<string>(SetProgress);
            if (SourceKinds.IsProxy(source))
            {
                // Канал Telegram — источник MTProto-прокси, а не серверов.
                SetProgress("Собираю прокси из канала: " + source.Name);
                var proxies = await MtProtoService
                    .FetchAsync(source, CancellationToken.None, forceRss: true)
                    .ConfigureAwait(false);
                var added = MtProtoStore.AddDiscovered(proxies);
                source.LastCheckedUtc = DateTime.UtcNow;
                source.LastError = null;
                source.LastNodeCount = proxies.Count;
                SetProgress($"{source.Name}: прокси {proxies.Count}, новых {added}");
            }
            else
            {
                var result = await NodeStore.ImportSourceAsync(source, progress, CancellationToken.None);
                SetProgress(result);
            }
        }
        catch (Exception exception)
        {
            SetProgress("Ошибка: " + exception.Message);
        }
        finally
        {
            _busy = false;
            Reload();
        }
    }

    private async Task RunMtProtoCollectAsync()
    {
        if (_busy)
        {
            SetProgress("Дождитесь окончания загрузки");
            return;
        }

        _busy = true;
        SetProgress("Собираю MTProto-прокси из каналов…");
        try
        {
            var progress = new Progress<string>(SetProgress);
            var summary = await MtProtoStore.CollectAsync(progress, CancellationToken.None).ConfigureAwait(false);
            SetProgress(summary);
        }
        catch (Exception exception)
        {
            SetProgress("Ошибка сбора прокси: " + exception.Message);
        }
        finally
        {
            _busy = false;
            Reload();
        }
    }

    private async Task RunRefreshAsync()
    {
        if (_busy)
        {
            SetProgress("Дождитесь окончания загрузки");
            return;
        }

        _busy = true;
        SetProgress("Обновляю источники…");
        try
        {
            var progress = new Progress<string>(SetProgress);
            var summary = await NodeStore.RefreshAsync(progress, CancellationToken.None);
            SetProgress(summary);
            AndroidSettingsStore.Current.LastSourceRefreshUtc = DateTime.UtcNow;
            AndroidSettingsStore.Save();

            if (NodeStore.Settings.TestAfterRefresh)
            {
                SetProgress("Проверяю новые серверы в :probe…");
                var tested = await ProbeClient.RunBatchAsync(NodeTester.MaxBatch, CancellationToken.None);
                SetProgress(tested.Error ?? $"Источники обновлены; проверено {tested.Tested}, " +
                                            $"рабочих {tested.Working}");
            }
        }
        catch (Exception exception)
        {
            AppLog.Write("sources", "Ошибка обновления источников", exception);
            SetProgress("Ошибка обновления: " + exception.Message);
        }
        finally
        {
            _busy = false;
            Reload();
        }
    }

    private async Task RunDiscoveryAsync()
    {
        if (_busy)
        {
            return;
        }

        _busy = true;
        SetProgress("Ищу новые источники…");
        try
        {
            var progress = new Progress<string>(SetProgress);
            var result = await SourceDiscoveryService.DiscoverAsync(
                NodeStore.SnapshotSources(), progress, CancellationToken.None);
            var added = await Task.Run(() => NodeStore.AddDiscovered(result));
            var mtproto = MtProtoStore.AddDiscovered(result.MtProto);
            SetProgress($"Новых источников {added}, серверов {result.Nodes.Count}, MTProto {mtproto}." +
                        (result.Error is null ? string.Empty : " Ошибка: " + result.Error));
            Reload();
        }
        catch (Exception exception)
        {
            AppLog.Write("discover", "Автопоиск не удался", exception);
            SetProgress("Автопоиск не удался: " + exception.Message);
        }
        finally
        {
            _busy = false;
        }
    }

    private void SetProgress(string text) => RunOnUiThread(() =>
    {
        if (_progress is not null)
        {
            _progress.Text = text;
        }
    });
}
