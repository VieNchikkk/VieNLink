using System.Text;

namespace VieNLink;

/// <summary>Декодирование base64/base64url: так приходят vmess-ссылки и подписки.</summary>
internal static class Base64Util
{
    public static bool TryDecode(string text, out string result)
    {
        result = string.Empty;
        var input = text.Trim();
        if (input.Length < 4)
        {
            return false;
        }

        var normalized = input.Replace('-', '+').Replace('_', '/').Replace("\r", string.Empty).Replace("\n", string.Empty).Replace(" ", string.Empty);
        switch (normalized.Length % 4)
        {
            case 2:
                normalized += "==";
                break;
            case 3:
                normalized += "=";
                break;
        }

        try
        {
            result = Encoding.UTF8.GetString(Convert.FromBase64String(normalized));
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    public static bool LooksLikeBase64(string text)
    {
        var trimmed = text.Trim();
        if (trimmed.Length < 16 || trimmed.Any(char.IsWhiteSpace) && trimmed.Contains("://", StringComparison.Ordinal))
        {
            return false;
        }

        return trimmed.All(c => char.IsLetterOrDigit(c) || c is '+' or '/' or '=' or '-' or '_');
    }
}
