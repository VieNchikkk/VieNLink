using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace VieNLink;

/// <summary>Строка таблицы серверов. Обёртка нужна, чтобы обновлять задержку без перезагрузки списка.</summary>
internal sealed class NodeRow : INotifyPropertyChanged
{
    public NodeRow(ProxyNode node) => Node = node;

    public ProxyNode Node { get; }

    public string Id => Node.Id;

    public string Name => Node.Name;

    public string Protocol => Node.Protocol;

    /// <summary>Пометка маскировки: REALITY/Vision, WS·TLS, Hysteria2 и т.п.</summary>
    public string MaskingLabel => NodeRanking.Label(Node);

    /// <summary>Под какой домен маскируется рукопожатие: YouTube/Google, Yandex и т.д.</summary>
    public string CamouflageLabel => NodeRanking.CamouflageLabel(Node);

    /// <summary>Маскированный сервер подсвечивается в таблице жёлтым.</summary>
    public bool IsMasked => NodeRanking.IsMasked(Node);

    /// <summary>Цвет строки: рабочий — зелёный, непроверенный — красный.</summary>
    public bool IsWorking => Node.Status == NodeStatus.Working;

    public bool IsUntested => Node.Status == NodeStatus.Unknown;

    public string ServerLabel => Node.Server + ":" + Node.Port;

    public string SourceName => Node.SourceName;

    public bool IsMine => Node.IsMine;

    /// <summary>В таблице серверов личная подписка помечается звездой.</summary>
    public string SourceLabel => Node.IsMine ? "★ " + Node.SourceName : Node.SourceName;

    public int? DelayMs => Node.DelayMs;

    /// <summary>Числовое значение для сортировки по пингу; непроверенные — в конце.</summary>
    public int PingSortValue => Node.DelayMs ?? int.MaxValue;

    /// <summary>Сколько проверок подряд сервер не открыл ни один сервис.</summary>
    public int FailCount => Node.FailCount;

    /// <summary>Колонка «Провалы»: сколько попыток израсходовано до удаления.</summary>
    public string FailLabel => Node.FailCount == 0
        ? "—"
        : $"{Node.FailCount} из {NodeLimits.RemoveAfterFailures}";

    public string DelayLabel => Node.DelayMs is null ? "—" : Node.DelayMs.Value + " мс";

    /// <summary>Скорость последнего замера, Мбит/с. Задержка не показывает пропускную способность.</summary>
    public string SpeedLabel => Node.SpeedMbps is null ? "—" : Node.SpeedMbps.Value.ToString("0.0") + " Мбит/с";

    /// <summary>Числовое значение для сортировки по скорости; не замеренные — в конце.</summary>
    public double SpeedSortValue => Node.SpeedMbps ?? -1;

    /// <summary>Отметки проверки: ✓ — сервис открылся, — — нет, ? — не проверялся.</summary>
    public string YoutubeLabel => Node.YoutubeOk is null ? "?" : Node.YoutubeOk.Value ? "✓" : "—";

    public string TelegramLabel => Node.TelegramOk is null ? "?" : Node.TelegramOk.Value ? "✓" : "—";

    public string CheckedAtLabel => Node.CheckedAtUtc is null ? "—" : Node.CheckedAtUtc.Value.ToLocalTime().ToString("dd.MM HH:mm");

    public event PropertyChangedEventHandler? PropertyChanged;

    public void Refresh() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));
}

/// <summary>Строка списка MTProto-прокси.</summary>
internal sealed class MtProtoRow : INotifyPropertyChanged
{
    public MtProtoRow(MtProtoProxy proxy) => Proxy = proxy;

    public MtProtoProxy Proxy { get; }

    public string Server => Proxy.Server;

    public int Port => Proxy.Port;

    public string DelayLabel => Proxy.DelayMs is null ? "—" : Proxy.DelayMs.Value + " мс";

    /// <summary>Числовое значение для сортировки по пингу; непроверенные — в конце.</summary>
    public int PingSortValue => Proxy.DelayMs ?? int.MaxValue;

    public string SecretShort => Proxy.SecretShort;

    public string SourceName => Proxy.SourceName;

    public string CheckedAtLabel => Proxy.CheckedAtUtc is null ? "—" : Proxy.CheckedAtUtc.Value.ToLocalTime().ToString("dd.MM HH:mm");

    public string DeadAtLabel => Proxy.DeadAtUtc is null ? "—" : Proxy.DeadAtUtc.Value.ToLocalTime().ToString("dd.MM HH:mm");

    /// <summary>Сколько часов осталось лежать в «Нерабочих» (храним 24 часа).</summary>
    public string DeadExpiresLabel
    {
        get
        {
            if (Proxy.DeadAtUtc is null)
            {
                return "—";
            }

            var left = MtProtoRetention.DeadHours - (int)(DateTime.UtcNow - Proxy.DeadAtUtc.Value).TotalHours;
            return left <= 0 ? "удалить" : left + " ч.";
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public void Refresh() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));
}

/// <summary>Строка вкладки «Отсев — Перепроверка»: рабочий сервер, переставший работать.</summary>
internal sealed class RejectedNodeRow
{
    public RejectedNodeRow(RejectedNode entry) => Entry = entry;

    public RejectedNode Entry { get; }
    public ProxyNode Node => Entry.Node;

    public string Name => Node.Name;
    public string ServerPort => Node.Server + ":" + Node.Port;
    public string Protocol => Node.Protocol;
    public string SourceName => Node.SourceName;
    public string RejectedLabel => Entry.RejectedAtUtc.ToLocalTime().ToString("dd.MM HH:mm");
    public string FailLabel => Node.Status == NodeStatus.Working
        ? "—"
        : Node.FailCount + " / " + NodeLimits.RemoveAfterFailures;
    public string DelayLabel => Node.DelayMs is null ? "—" : Node.DelayMs + " мс";

    /// <summary>
    /// Состояние после перепроверки: рабочий сервер остаётся в отсеве до «Вернуть»,
    /// поэтому колонка нужна, чтобы он не выглядел мёртвым.
    /// </summary>
    public string StatusLabel => Node.Status == NodeStatus.Working
        ? Node.DelayMs is null ? "РАБОТАЕТ" : "РАБОТАЕТ · " + Node.DelayMs + " мс"
        : "НЕ РАБОТАЕТ";
    public string YoutubeLabel => Node.YoutubeOk == true ? "✓" : "—";
    public string TelegramLabel => Node.TelegramOk == true ? "✓" : "—";
    public string DiscordLabel => Node.DiscordOk == true ? "✓" : "—";
    public string UniversalLabel => Node.UniversalOk == true ? "✓" : "—";
}

/// <summary>Строка списка источников: включает/выключает источник прямо в таблице.</summary>
internal sealed class SourceRow : INotifyPropertyChanged
{
    public SourceRow(SourceDefinition source) => Source = source;

    public SourceDefinition Source { get; }

    public bool Enabled
    {
        get => Source.Enabled;
        set
        {
            if (Source.Enabled == value)
            {
                return;
            }

            Source.Enabled = value;
            OnPropertyChanged();
        }
    }

    public string Name => Source.IsAutoDiscovered ? "★ " + Source.Name : Source.Name;

    /// <summary>Иконка «доверенный источник»: обновляется не реже раза в 24 часа.</summary>
    public string TrustedIcon => Source.IsTrustedNow ? "✓" : string.Empty;

    public bool IsMine
    {
        get => Source.IsMine;
        set
        {
            if (Source.IsMine == value)
            {
                return;
            }

            Source.IsMine = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(Name));
            OnPropertyChanged(nameof(GroupName));
            OnPropertyChanged(nameof(OriginLabel));
        }
    }

    /// <summary>Заголовок группы в списке источников.</summary>
    public string GroupName => SourceKinds.IsProxy(Source)
        ? "Telegram-прокси (каналы и сайты)"
        : Source.IsMine
            ? "Источники серверов · мои подписки"
            : "Источники серверов · публичные";

    public string OriginLabel => Source.IsMine ? "моя" : "публичный";

    public string Url => Source.Url;

    /// <summary>Для таблицы: несколько ссылок в одной ячейке показываем в одну строку.</summary>
    public string UrlDisplay => Source.Url.ReplaceLineEndings(" ").Trim();

    public string CountLabel => Source.LastNodeCount > 0 ? Source.LastNodeCount.ToString() : "—";

    /// <summary>Всего уникальных серверов, когда-либо собранных из источника.</summary>
    public string TotalLabel => Source.TotalNodeCount > 0
        ? Source.TotalNodeCount.ToString()
        : CountLabel;

    private int _workingCount;
    private bool _workingCountSet;

    /// <summary>
    /// Сколько рабочих серверов сейчас числится за источником. Считается по текущему
    /// списку рабочих серверов, а не по числу найденных ссылок: источник с сотнями
    /// мёртвых ссылок и нулём рабочих — кандидат на удаление.
    /// </summary>
    public int WorkingCount
    {
        get => _workingCount;
        set
        {
            _workingCountSet = true;
            if (_workingCount == value)
            {
                return;
            }

            _workingCount = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(WorkingLabel));
        }
    }

    public string WorkingLabel => SourceKinds.IsProxy(Source)
        ? "—"
        : !_workingCountSet ? "—" : _workingCount.ToString();

    /// <summary>Сколько уникальных серверов источника хотя бы раз были рабочими.</summary>
    public string TotalWorkingLabel => SourceKinds.IsProxy(Source)
        ? "—"
        : Source.TotalWorkingNodeCount.ToString();

    public string TrafficLabel => Source.SubscriptionInfo ?? "—";

    public string StatusLabel => Source.LastError is not null
        ? Source.LastError
        : Source.LastCheckedUtc is null
            ? "не проверялся"
            : "обновлён " + Source.LastCheckedUtc.Value.ToLocalTime().ToString("HH:mm");

    public event PropertyChangedEventHandler? PropertyChanged;

    public void Refresh() => OnPropertyChanged(string.Empty);

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
