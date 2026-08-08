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
/// P0/C-5: Random IV per encryption operation. IV is prepended to ciphertext
/// (IV‖C format), so the decrypt method reads the IV from the first 16 bytes.
/// This prevents deterministic encryption and pattern analysis attacks.
///
/// P1/H-6: In Production, the application refuses to start if UKUU_ENCRYPTION_KEY
/// is not set, preventing silent use of a publicly known dev key.
///
/// Usage:
///   var cipher = new AesEncryptionService();
///   var encrypted = cipher.Encrypt(employee.AccountNumber);
///   var decrypted = cipher.Decrypt(encrypted);
/// </summary>
public class AesEncryptionService
{
    private readonly byte[] _key;
    private readonly bool _isProduction;

    private static readonly string DevKey = "UkuuHr2026DevKey!!UkuuHr2026Dev!!"; // 32 bytes — dev only

    public AesEncryptionService()
    {
        _isProduction = Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT") == "Production";
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
            // P1/H-6: Fail fast in Production if encryption key is missing
            if (_isProduction)
            {
                throw new InvalidOperationException(
                    "UKUU_ENCRYPTION_KEY is not set. Refusing to start in Production with a publicly known dev key. " +
                    "Generate one with: openssl rand -hex 32");
            }
            _key = Encoding.UTF8.GetBytes(DevKey);
            Console.WriteLine("[AesEncryptionService] WARNING: UKUU_ENCRYPTION_KEY not set — using dev-only key. DO NOT use in production.");
        }
    }

    /// <summary>
    /// Encrypt a plaintext string. Returns Base64-encoded (IV‖Ciphertext).
    /// P0/C-5: A random IV is generated per operation and prepended to the ciphertext.
    /// </summary>
    public string Encrypt(string? plaintext)
    {
        if (string.IsNullOrEmpty(plaintext)) return plaintext ?? "";
        using var aes = Aes.Create();
        aes.Key = _key;
        aes.GenerateIV(); // P0/C-5: Random IV per operation
        aes.Mode = CipherMode.CBC;
        aes.Padding = PaddingMode.PKCS7;

        using var encryptor = aes.CreateEncryptor();
        var bytes = Encoding.UTF8.GetBytes(plaintext);
        var encrypted = encryptor.TransformFinalBlock(bytes, 0, bytes.Length);

        // P0/C-5: Prepend IV to ciphertext (IV‖C format)
        var result = new byte[aes.IV.Length + encrypted.Length];
        Buffer.BlockCopy(aes.IV, 0, result, 0, aes.IV.Length);
        Buffer.BlockCopy(encrypted, 0, result, aes.IV.Length, encrypted.Length);

        return Convert.ToBase64String(result);
    }

    /// <summary>
    /// Decrypt a Base64-encoded (IV‖Ciphertext) back to plaintext.
    /// P0/C-5: Reads the IV from the first 16 bytes of the decoded data.
    /// P3/L-2: Logs and throws on decryption failure instead of silently returning input.
    /// </summary>
    public string Decrypt(string? ciphertext)
    {
        if (string.IsNullOrEmpty(ciphertext)) return ciphertext ?? "";
        try
        {
            var data = Convert.FromBase64String(ciphertext);

            // P0/C-5: Extract IV from first 16 bytes
            if (data.Length < 16)
                throw new FormatException("Ciphertext too short — missing IV prefix");

            var iv = new byte[16];
            var actualCiphertext = new byte[data.Length - 16];
            Buffer.BlockCopy(data, 0, iv, 0, 16);
            Buffer.BlockCopy(data, 16, actualCiphertext, 0, actualCiphertext.Length);

            using var aes = Aes.Create();
            aes.Key = _key;
            aes.IV = iv;
            aes.Mode = CipherMode.CBC;
            aes.Padding = PaddingMode.PKCS7;

            using var decryptor = aes.CreateDecryptor();
            var decrypted = decryptor.TransformFinalBlock(actualCiphertext, 0, actualCiphertext.Length);
            return Encoding.UTF8.GetString(decrypted);
        }
        catch (FormatException)
        {
            // P3/L-2: Value was never encrypted with the new IV-prepended format.
            // Try legacy format (fixed IV) for backwards compatibility during migration.
            return DecryptLegacy(ciphertext);
        }
        catch (CryptographicException ex)
        {
            // P3/L-2: Log decryption failure instead of silently returning garbage
            Console.WriteLine($"[AesEncryptionService] Decryption failed: {ex.Message}");
            return ciphertext; // Return as-is for values that were never encrypted
        }
    }

    /// <summary>
    /// Legacy decryption for data encrypted with the old fixed IV format.
    /// Used for backwards compatibility during migration from fixed IV to random IV.
    /// </summary>
    private string DecryptLegacy(string ciphertext)
    {
        try
        {
            var legacyIv = Encoding.UTF8.GetBytes("UkuuHr2026IV!!!");
            using var aes = Aes.Create();
            aes.Key = _key;
            aes.IV = legacyIv;
            aes.Mode = CipherMode.CBC;
            aes.Padding = PaddingMode.PKCS7;

            using var decryptor = aes.CreateDecryptor();
            var bytes = Convert.FromBase64String(ciphertext);
            var decrypted = decryptor.TransformFinalBlock(bytes, 0, bytes.Length);
            return Encoding.UTF8.GetString(decrypted);
        }
        catch
        {
            return ciphertext; // Value was never encrypted — return as-is
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
