namespace VieNLink.Mobile;

/// <summary>
/// Признак сборки для магазина (Google Play / RuStore): в ней нет самообновления APK.
/// Задаётся свойством <c>StoreBuild=true</c> при сборке и определяет <c>STORE_BUILD</c>.
/// </summary>
internal static class StoreBuildInfo
{
#if STORE_BUILD
    /// <summary>Сборка для магазина: обновления ставит магазин, APK сам себя не обновляет.</summary>
    public const bool IsStoreBuild = true;
#else
    public const bool IsStoreBuild = false;
#endif
}
