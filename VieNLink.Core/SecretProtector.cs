namespace VieNLink;

/// <summary>
/// Шифрование секретов средствами платформы: DPAPI в Windows, Android Keystore
/// в Android-версии. Ядру нельзя зависеть от платформы, поэтому реализацию
/// подставляет приложение при запуске (см. <see cref="SecretProtector"/>).
/// </summary>
internal interface ISecretProtector
{
    /// <summary>Шифрует текст. Исключение — если платформа не смогла зашифровать.</summary>
    byte[] Protect(string text);

    /// <summary>Расшифровывает; null — ключ не подошёл или данные повреждены.</summary>
    string? Unprotect(byte[] data);
}

/// <summary>Точка подстановки платформенного шифрования секретов.</summary>
internal static class SecretProtector
{
    /// <summary>
    /// Текущая реализация. null означает «шифрование недоступно»: файлы с секретами
    /// в этом случае не пишутся открытым текстом — операция завершается ошибкой.
    /// </summary>
    public static ISecretProtector? Current { get; set; }

    public static bool IsAvailable => Current is not null;
}
