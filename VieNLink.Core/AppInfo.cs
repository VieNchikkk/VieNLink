using System.Reflection;

namespace VieNLink;

/// <summary>
/// Название, версия и статус сборки в одном месте. Пока приложение в бете,
/// это видно в заголовке окна, в шапке, в «Общих» и в установщике.
/// </summary>
internal static class AppInfo
{
    public const string Name = "VieNLink";

    /// <summary>Бета-статус: снять после стабилизации основных режимов.</summary>
    public const bool IsBeta = true;

    /// <summary>«VieNLink (бета)» — для заголовков и подписей.</summary>
    public static string DisplayName => IsBeta ? Name + " (бета)" : Name;

    /// <summary>Версия из csproj (InformationalVersion), например 1.0.4-beta.</summary>
    public static string Version
    {
        get
        {
            // AppInfo лежит в общем ядре, поэтому «своя» сборка — это VieNLink.Core
            // с другой версией. Показываем версию самого приложения.
            var assembly = Assembly.GetEntryAssembly() ?? typeof(AppInfo).Assembly;
            var informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
            var version = !string.IsNullOrWhiteSpace(informational)
                ? informational
                : assembly.GetName().Version?.ToString(3) ?? "1.0.4";

            // SDK дописывает к версии «+хэш коммита»: в интерфейсе он не нужен,
            // это служебная метка сборки, а не часть названия.
            var plus = version.IndexOf('+', StringComparison.Ordinal);
            if (plus > 0)
            {
                version = version[..plus];
            }

            return version.Trim();
        }
    }

    /// <summary>Подпись для «Общих»: версия и, если нужно, статус.</summary>
    public static string VersionLabel =>
        IsBeta && !Version.Contains("beta", StringComparison.OrdinalIgnoreCase)
            ? Version + " · бета"
            : Version;
}
