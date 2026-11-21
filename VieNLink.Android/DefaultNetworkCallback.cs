using Android.Net;

namespace VieNLink.Mobile;

/// <summary>
/// Отслеживает смену сети по умолчанию (Wi-Fi ↔ мобильная) и сообщает о ней ядру.
/// Без этого sing-box пишет «missing default interface» и падает на первом соединении.
/// </summary>
internal sealed class DefaultNetworkCallback : ConnectivityManager.NetworkCallback
{
    private readonly TunnelService _service;

    public DefaultNetworkCallback(TunnelService service) => _service = service;

    public override void OnAvailable(Network network) => _service.RefreshDefaultInterface();

    public override void OnLost(Network network) => _service.RefreshDefaultInterface();
}
