using System.Text;
using Android.Security.Keystore;
using Java.Security;
using Javax.Crypto;
using Javax.Crypto.Spec;

namespace VieNLink.Mobile;

/// <summary>
/// Шифрование секретов на Android: ключ AES-GCM создаётся и живёт в Android Keystore
/// (не покидает систему и не попадает в резервные копии). Замена DPAPI из Windows.
/// Формат файла: [версия 1 байт][длина IV 1 байт][IV][шифртекст].
/// </summary>
internal sealed class KeystoreSecretStore : ISecretProtector
{
    private const string KeyAlias = "vienlink-secrets";
    private const string Transformation = "AES/GCM/NoPadding";
    private const int GcmTagBits = 128;
    private const byte FormatVersion = 1;

    
    private readonly KeyStore _keyStore;

    public KeystoreSecretStore()
    {
        _keyStore = KeyStore.GetInstance("AndroidKeyStore")!;
        _keyStore.Load(null);
        EnsureKey();
    }

    public byte[] Protect(string text)
    {
        using var cipher = Cipher.GetInstance(Transformation)!;
        using var key = _keyStore.GetKey(KeyAlias, null) ?? throw new InvalidOperationException("Ключ Keystore не найден");
        cipher.Init(CipherMode.EncryptMode, key);
        var iv = cipher.GetIV() ?? Array.Empty<byte>();
        var encrypted = cipher.DoFinal(Encoding.UTF8.GetBytes(text)) ?? Array.Empty<byte>();

        var result = new byte[2 + iv.Length + encrypted.Length];
        result[0] = FormatVersion;
        result[1] = (byte)iv.Length;
        Buffer.BlockCopy(iv, 0, result, 2, iv.Length);
        Buffer.BlockCopy(encrypted, 0, result, 2 + iv.Length, encrypted.Length);
        return result;
    }

    public string? Unprotect(byte[] data)
    {
        if (data.Length < 3 || data[0] != FormatVersion)
        {
            return null;
        }

        var ivLength = data[1];
        if (ivLength == 0 || data.Length < 2 + ivLength)
        {
            return null;
        }

        try
        {
            var iv = new byte[ivLength];
            Buffer.BlockCopy(data, 2, iv, 0, ivLength);
            var encrypted = new byte[data.Length - 2 - ivLength];
            Buffer.BlockCopy(data, 2 + ivLength, encrypted, 0, encrypted.Length);

            using var cipher = Cipher.GetInstance(Transformation)!;
            using var key = _keyStore.GetKey(KeyAlias, null) ?? throw new InvalidOperationException("Ключ Keystore не найден");
            using var spec = new GCMParameterSpec(GcmTagBits, iv);
            cipher.Init(CipherMode.DecryptMode, key, (Java.Security.Spec.IAlgorithmParameterSpec)spec);
            var plain = cipher.DoFinal(encrypted);
            return plain is null ? null : Encoding.UTF8.GetString(plain);
        }
        catch (GeneralSecurityException exception)
        {
            AppLog.Write("secrets", "Не удалось расшифровать секреты (ключ не подошёл?)", exception);
            return null;
        }
        catch (Java.Lang.Exception exception)
        {
            AppLog.Write("secrets", "Ошибка Keystore при расшифровке", exception);
            return null;
        }
    }

    private void EnsureKey()
    {
        if (_keyStore.ContainsAlias(KeyAlias))
        {
            return;
        }

        using var generator = KeyGenerator.GetInstance(KeyProperties.KeyAlgorithmAes, "AndroidKeyStore")!;
        using var builder = new KeyGenParameterSpec.Builder(
            KeyAlias,
            KeyStorePurpose.Encrypt | KeyStorePurpose.Decrypt)
            .SetBlockModes(KeyProperties.BlockModeGcm)!
            .SetEncryptionPaddings(KeyProperties.EncryptionPaddingNone)!;
        generator.Init(builder.Build());
        generator.GenerateKey();
    }
}


