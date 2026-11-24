namespace VieNLink.Mobile;

/// <summary>Отличия сборок: тестовая и телевизионная (отдельные package и имена).</summary>
internal static class BuildInfo
{
#if TV_BUILD
    public const string AppLabel = "VieNLink TV";
    public const string UpdateAuthority = "com.vienlink.app.tv.updates";
    /// <summary>Телевизионная сборка: из релиза берётся только APK с «-tv» в имени.</summary>
    public const bool IsTelevisionBuild = true;
#elif TEST_BUILD
    public const string AppLabel = "VieNLink Test";
    public const string UpdateAuthority = "com.vienlink.app.test.updates";
    public const bool IsTelevisionBuild = false;
#else
    public const string AppLabel = "VieNLink";
    public const string UpdateAuthority = "com.vienlink.app.updates";
    public const bool IsTelevisionBuild = false;
#endif
}
