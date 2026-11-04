using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using TaskFlow.Worker.Configuration;

namespace TaskFlow.Worker.Services;

public interface ITenantSecretDecryptor
{
    string Decrypt(string encryptedValue);
}

/// <summary>
/// Configurable, versioned password envelope. V1 format is Base64(nonce[12] + tag[16] + ciphertext).
/// The algorithm, envelope format and key are read from appsettings/environment configuration.
/// </summary>
public sealed class TenantSecretDecryptor(IOptions<TenantPasswordEncryptionOptions> options)
    : ITenantSecretDecryptor
{
    public string Decrypt(string encryptedValue)
    {
        var config = options.Value;
        if (!string.Equals(config.Algorithm, "Aes256Gcm", StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(config.Format, "V1NonceTagCiphertextBase64", StringComparison.OrdinalIgnoreCase))
            throw new CryptographicException("Unsupported TenantPasswordEncryption algorithm or format.");

        if (string.IsNullOrWhiteSpace(config.KeyBase64))
            throw new InvalidOperationException("TenantPasswordEncryption:KeyBase64 is required.");

        byte[] key;
        try { key = Convert.FromBase64String(config.KeyBase64); }
        catch (FormatException ex) { throw new InvalidOperationException("TenantPasswordEncryption:KeyBase64 is invalid.", ex); }
        if (key.Length != 32)
            throw new InvalidOperationException("TenantPasswordEncryption:KeyBase64 must decode to 32 bytes.");

        if (!encryptedValue.StartsWith("v1:", StringComparison.Ordinal))
            throw new CryptographicException("Encrypted tenant password must use the v1: prefix.");

        byte[] envelope;
        try { envelope = Convert.FromBase64String(encryptedValue[3..]); }
        catch (FormatException ex) { throw new CryptographicException("Encrypted tenant password is not valid Base64.", ex); }

        const int nonceLength = 12, tagLength = 16;
        if (envelope.Length <= nonceLength + tagLength)
            throw new CryptographicException("Encrypted tenant password envelope is too short.");

        var plaintext = new byte[envelope.Length - nonceLength - tagLength];
        try
        {
            using var aes = new AesGcm(key, tagLength);
            aes.Decrypt(envelope.AsSpan(0, nonceLength),
                envelope.AsSpan(nonceLength + tagLength),
                envelope.AsSpan(nonceLength, tagLength), plaintext);
            return new UTF8Encoding(false, true).GetString(plaintext);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
            CryptographicOperations.ZeroMemory(plaintext);
        }
    }
}
