
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using TaskFlow.Worker.Configuration;

namespace TaskFlow.Worker.Services;

public interface ITenantSecretDecryptor
{
    string Decrypt(string encryptedValue);
}

public sealed class TenantSecretDecryptor(
    IOptions<TenantPasswordEncryptionOptions> options)
    : ITenantSecretDecryptor
{
    private const int KeyLength = 32;
    private const int IvLength = 12;
    private const int TagLength = 16;
    private const string Version = "v1";

    private static readonly UTF8Encoding StrictUtf8 =
        new(false, true);

    public string Decrypt(string encryptedValue)
    {
        if (string.IsNullOrWhiteSpace(encryptedValue))
            throw new CryptographicException(
                "Encrypted credential is empty.");

        var config = options.Value;

        if (!string.Equals(
            config.Algorithm,
            "Aes256Gcm",
            StringComparison.OrdinalIgnoreCase))
        {
            throw new CryptographicException(
                "Unsupported encryption algorithm.");
        }

        var keyHex = config.KeyHex;

        if (string.IsNullOrEmpty(keyHex) ||
            keyHex.Length != 64 ||
            !keyHex.All(Uri.IsHexDigit))
        {
            throw new InvalidOperationException(
                "Encryption key must contain exactly 64 hexadecimal characters.");
        }

        var key = Convert.FromHexString(keyHex);

        try
        {
            // Format: v1.iv.tag.ciphertext
            var parts = encryptedValue.Split('.');

            if (parts.Length != 4 || parts[0] != Version)
                throw new CryptographicException(
                    "INVALID_STORED_TENANT_CREDENTIAL");

            // Decode Base64URL
            var iv = DecodeBase64Url(parts[1]);
            var tag = DecodeBase64Url(parts[2]);
            var ciphertext = DecodeBase64Url(parts[3]);

            if (iv.Length != IvLength ||
                tag.Length != TagLength ||
                ciphertext.Length == 0)
            {
                throw new CryptographicException(
                    "Invalid tenant connection ciphertext.");
            }

            var plaintext = new byte[ciphertext.Length];

            try
            {
                using var aes = new AesGcm(key, TagLength);

                aes.Decrypt(
                    iv,
                    ciphertext,
                    tag,
                    plaintext);

                return StrictUtf8.GetString(plaintext);
            }
            catch (AuthenticationTagMismatchException ex)
            {
                throw new CryptographicException(
                    "Tenant credential decryption failed. Check encryption key and stored data.",
                    ex);
            }
            catch (DecoderFallbackException ex)
            {
                throw new CryptographicException(
                    "Decrypted tenant credential is not valid UTF-8.",
                    ex);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(plaintext);
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
    }

    private static byte[] DecodeBase64Url(string value)
    {
        if (string.IsNullOrEmpty(value))
            throw new CryptographicException(
                "Empty Base64URL component.");

        foreach (var c in value)
        {
            if (!(c >= 'A' && c <= 'Z') &&
                !(c >= 'a' && c <= 'z') &&
                !(c >= '0' && c <= '9') &&
                c != '-' && c != '_')
            {
                throw new CryptographicException(
                    "Invalid Base64URL component.");
            }
        }

        if (value.Length % 4 == 1)
            throw new CryptographicException(
                "Invalid Base64URL length.");

        var base64 = value
            .Replace('-', '+')
            .Replace('_', '/');

        base64 = base64.PadRight(
            base64.Length + (4 - base64.Length % 4) % 4,
            '=');

        try
        {
            var decoded = Convert.FromBase64String(base64);

            // Ensure canonical Base64URL representation
            if (!string.Equals(
                EncodeBase64Url(decoded),
                value,
                StringComparison.Ordinal))
            {
                throw new CryptographicException(
                    "Invalid Base64URL encoding.");
            }

            return decoded;
        }
        catch (FormatException ex)
        {
            throw new CryptographicException(
                "Invalid Base64URL component.", ex);
        }
    }

    private static string EncodeBase64Url(byte[] data)
    {
        return Convert.ToBase64String(data)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }
}
