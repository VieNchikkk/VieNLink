using System.Net.NetworkInformation;

namespace VieNLink.Mobile;

/// <summary>
/// Список сетевых интерфейсов для ядра. Своё перечисление нужно потому, что на Android
/// ядру запрещён netlink (SELinux), и оно спрашивает интерфейсы у платформы.
/// Возвращать null нельзя — Go падает на разыменовании.
/// </summary>
internal sealed class NetworkInterfaceIterator : Java.Lang.Object, Libbox.INetworkInterfaceIterator
{
    private readonly List<Libbox.NetworkInterface> _items;
    private int _position = -1;

    public NetworkInterfaceIterator(List<Libbox.NetworkInterface> items) => _items = items;

    public bool HasNext => _position + 1 < _items.Count;

    public Libbox.NetworkInterface? Next() => ++_position < _items.Count ? _items[_position] : null;

    /// <summary>Снимок текущих интерфейсов устройства в формате ядра.</summary>
    public static List<Libbox.NetworkInterface> Snapshot()
    {
        var result = new List<Libbox.NetworkInterface>();

        foreach (var source in NetworkInterface.GetAllNetworkInterfaces())
        {
            try
            {
                var properties = source.GetIPProperties();
                var index = 0;
                try
                {
                    index = properties.GetIPv4Properties()?.Index ?? 0;
                }
                catch (NetworkInformationException)
                {
                    // Интерфейс без IPv4 — индекс останется нулевым.
                }

                var entry = new Libbox.NetworkInterface
                {
                    Name = source.Name,
                    Index = index,
                    MTU = 1500,
                    Flags = source.OperationalStatus == OperationalStatus.Up ? 1 : 0,
                    Type = 1,
                    Metered = false,
                };

                result.Add(entry);
            }
            catch (Exception exception)
            {
                Android.Util.Log.Warn("VieNLink", "интерфейс " + source.Name + ": " + exception.Message);
            }
        }

        return result;
    }
}
