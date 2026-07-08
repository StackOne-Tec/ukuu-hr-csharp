using System.Security.Cryptography;
using System.Text;

namespace UkuuHr.Services;

/// <summary>
/// Phase 13.5: AES-256 encryption service for sensitive data at rest.
///
/// Encrypts fields like bank account numbers, NRCs, TPINs, and API keys
/// before they're stored in the database. The master key is read from the
/// UKUU_ENCRYPTION_KEY env var (32-byte hex string). If not set, a
/// development-only key is used (with a console warning).
///
/// Usage:
///   var cipher = new AesEncryptionService();
///   var encrypted = cipher.Encrypt(employee.AccountNumber);
///   var decrypted = cipher.Decrypt(encrypted);
/// </summary>
public class AesEncryptionService
{
    private readonly byte[] _key;
    private static readonly byte[] IV =
        Encoding.UTF8.GetBytes("UkuuHr2026IV!!!"); // 16 bytes — fixed IV for deterministic search

    private static readonly string DevKey = "UkuuHr2026DevKey!!UkuuHr2026Dev!!"; // 32 bytes — dev only

    public AesEncryptionService()
    {
        var envKey = Environment.GetEnvironmentVariable("UKUU_ENCRYPTION_KEY");
        if (!string.IsNullOrEmpty(envKey))
        {
            // Env var should be a 64-char hex string (32 bytes)
            if (envKey.Length == 64 && IsHex(envKey))
            {
                _key = Convert.FromHexString(envKey);
            }
            else
            {
                // Treat as raw string (pad/truncate to 32 bytes)
                _key = Encoding.UTF8.GetBytes(envKey.PadRight(32, '0')[..32]);
            }
        }
        else
        {
            _key = Encoding.UTF8.GetBytes(DevKey);
            Console.WriteLine("[AesEncryptionService] WARNING: UKUU_ENCRYPTION_KEY not set — using dev-only key. DO NOT use in production.");
        }
    }

    /// <summary>Encrypt a plaintext string. Returns Base64-encoded ciphertext.</summary>
    public string Encrypt(string? plaintext)
    {
        if (string.IsNullOrEmpty(plaintext)) return plaintext ?? "";
        using var aes = Aes.Create();
        aes.Key = _key;
        aes.IV = IV;
        aes.Mode = CipherMode.CBC;
        aes.Padding = PaddingMode.PKCS7;

        using var encryptor = aes.CreateEncryptor();
        var bytes = Encoding.UTF8.GetBytes(plaintext);
        var encrypted = encryptor.TransformFinalBlock(bytes, 0, bytes.Length);
        return Convert.ToBase64String(encrypted);
    }

    /// <summary>Decrypt a Base64-encoded ciphertext back to plaintext.</summary>
    public string Decrypt(string? ciphertext)
    {
        if (string.IsNullOrEmpty(ciphertext)) return ciphertext ?? "";
        try
        {
            using var aes = Aes.Create();
            aes.Key = _key;
            aes.IV = IV;
            aes.Mode = CipherMode.CBC;
            aes.Padding = PaddingMode.PKCS7;

            using var decryptor = aes.CreateDecryptor();
            var bytes = Convert.FromBase64String(ciphertext);
            var decrypted = decryptor.TransformFinalBlock(bytes, 0, bytes.Length);
            return Encoding.UTF8.GetString(decrypted);
        }
        catch
        {
            // If decryption fails (e.g., the value was never encrypted), return as-is
            return ciphertext;
        }
    }

    /// <summary>Check if a string looks like it's already encrypted (Base64 and not plaintext).</summary>
    public bool IsEncrypted(string? value)
    {
        if (string.IsNullOrEmpty(value)) return false;
        try
        {
            Convert.FromBase64String(value);
            return value.Length > 0 && value != Decrypt(value);
        }
        catch { return false; }
    }

    private static bool IsHex(string s)
    {
        foreach (var c in s)
            if (!((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F')))
                return false;
        return true;
    }
}
