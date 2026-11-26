using Android.Net;

namespace VieNLink.Mobile;

/// <summary>
/// Отслеживает смену сети по умолчанию (Wi-Fi ↔ мобильная) и сообщает о ней ядру.
/// Без этого sing-box пишет «missing default interface» и падает на первом соединении.
/// Используется и VPN-службой, и процессом :probe (где VPN-службы нет) — поэтому
/// принимает делегат, а не конкретную службу.
/// </summary>
internal sealed class DefaultNetworkCallback : ConnectivityManager.NetworkCallback
{
    private readonly Action _onChanged;

    public DefaultNetworkCallback(Action onChanged) => _onChanged = onChanged;

    public override void OnAvailable(Network network)
    {
        NetworkPresets.SyncProfile();
        _onChanged();
    }

    public override void OnLost(Network network)
    {
        NetworkPresets.SyncProfile();
        _onChanged();
    }
}
