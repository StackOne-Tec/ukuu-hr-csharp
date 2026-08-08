using System.Collections.Concurrent;
using System.Security.Cryptography;

namespace UkuuHr.Services;

/// <summary>
/// P0/C-1: One-time token service for post-signup auto-login.
/// Replaces credentials-in-URL with a short-lived token that is exchanged
/// for an authenticated session. Tokens expire after 5 minutes and are
/// consumed on first use (TryRemove pattern).
/// </summary>
public class AutoLoginTokenService
{
    private readonly ConcurrentDictionary<string, (string Email, string Password, DateTime Expires)> _tokens = new();

    /// <summary>Token lifetime — 5 minutes.</summary>
    public static readonly TimeSpan TokenLifetime = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Generate a one-time auto-login token for the given credentials.
    /// Returns the token string to be included in a POST form.
    /// </summary>
    public string CreateToken(string email, string password)
    {
        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        _tokens[token] = (email, password, DateTime.UtcNow.Add(TokenLifetime));

        // Periodically clean up expired tokens (every 100 inserts)
        if (_tokens.Count > 100)
        {
            foreach (var kvp in _tokens.Where(k => k.Value.Expires < DateTime.UtcNow).ToList())
                _tokens.TryRemove(kvp.Key, out _);
        }

        return token;
    }

    /// <summary>
    /// Attempt to consume a token and retrieve the associated credentials.
    /// Returns null if the token is invalid, expired, or already used.
    /// </summary>
    public (string Email, string Password)? ConsumeToken(string token)
    {
        if (!_tokens.TryRemove(token, out var credentials))
            return null;

        if (credentials.Expires < DateTime.UtcNow)
            return null;

        return (credentials.Email, credentials.Password);
    }
}
