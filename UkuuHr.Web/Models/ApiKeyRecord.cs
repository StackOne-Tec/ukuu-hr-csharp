using System.ComponentModel.DataAnnotations;

namespace UkuuHr.Models;

// ─────────────────────────────────────────────────────────────────────────────
// API Key Management — World-Class Implementation
//
// Supports per-organization, per-user API keys with:
//   - Scoped permissions (granular access control)
//   - Rate limiting (requests per minute per key)
//   - Expiration dates (automatic deactivation)
//   - Last-used tracking (dormant key detection)
//   - Audit trail (created/revoked by whom, when)
//   - Key prefix for identification (non-sensitive part of the key)
//   - Constant-time comparison for security
//   - SHA-256 hashing at rest (key never stored in plaintext)
//
// Design decisions:
//   - The raw key is shown ONLY once at creation time
//   - Only a SHA-256 hash is stored (irreversible)
//   - The first 8 chars are stored as Prefix for UI display
//   - Scopes are stored as comma-separated string for SQLite compat
//   - Rate limiting is enforced in middleware, not DB constraints
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>
/// Scopes (permissions) that can be granted to an API key.
/// Each scope corresponds to a subset of API endpoints.
/// </summary>
public enum ApiKeyScope
{
    /// <summary>Read employee, department, and org data.</summary>
    ReadEmployees,
    /// <summary>Write employee records (create, update, deactivate).</summary>
    WriteEmployees,
    /// <summary>Read attendance and clock events.</summary>
    ReadAttendance,
    /// <summary>Write attendance records (import from devices, manual entry).</summary>
    WriteAttendance,
    /// <summary>Read and write leave requests, balances, holidays.</summary>
    LeaveManagement,
    /// <summary>Read payroll runs and payslips.</summary>
    ReadPayroll,
    /// <summary>Run payroll, approve/reject payroll batches.</summary>
    WritePayroll,
    /// <summary>Manage attendance devices (add, sync, delete).</summary>
    DeviceManagement,
    /// <summary>Full access to all endpoints (super-user key).</summary>
    FullAccess
}

/// <summary>
/// An API key record. The raw key is NEVER stored — only its SHA-256 hash.
/// The key is shown to the user exactly once at creation time.
/// </summary>
public class ApiKeyRecord
{
    public int Id { get; set; }

    /// <summary>Organization this key belongs to.</summary>
    public int OrganizationId { get; set; }

    /// <summary>User who created this key (audit trail).</summary>
    [Required, MaxLength(200)]
    public string CreatedByUserId { get; set; } = string.Empty;

    /// <summary>Human-readable name for this key (e.g. "UkuuBridge Sync", "Payroll Export").</summary>
    [Required, MaxLength(100)]
    public string Name { get; set; } = string.Empty;

    /// <summary>SHA-256 hash of the raw key. Used for constant-time lookup + validation.</summary>
    [Required, MaxLength(64)]
    public string KeyHash { get; set; } = string.Empty;

    /// <summary>
    /// Non-sensitive prefix (first 8 chars) of the raw key.
    /// Shown in the UI so users can identify keys without exposing them.
    /// </summary>
    [Required, MaxLength(12)]
    public string KeyPrefix { get; set; } = string.Empty;

    /// <summary>Comma-separated list of granted scopes (stored as string for SQLite compat).</summary>
    [Required]
    public string Scopes { get; set; } = "FullAccess";

    /// <summary>Maximum requests per minute allowed for this key. 0 = unlimited.</summary>
    public int RateLimitPerMinute { get; set; } = 60;

    /// <summary>When this key was created.</summary>
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>When this key expires. Null = never expires.</summary>
    public DateTime? ExpiresAt { get; set; }

    /// <summary>When this key was last used to authenticate a request.</summary>
    public DateTime? LastUsedAt { get; set; }

    /// <summary>IP address from which the key was last used.</summary>
    [MaxLength(45)]
    public string? LastUsedIp { get; set; }

    /// <summary>Total number of requests authenticated with this key.</summary>
    public long TotalRequestCount { get; set; }

    /// <summary>When this key was revoked. Null = active.</summary>
    public DateTime? RevokedAt { get; set; }

    /// <summary>User who revoked this key (audit trail).</summary>
    [MaxLength(200)]
    public string? RevokedByUserId { get; set; }

    /// <summary>Reason for revocation (audit trail).</summary>
    [MaxLength(500)]
    public string? RevocationReason { get; set; }

    // ─── Computed helpers (not mapped to DB) ───

    /// <summary>Whether this key is currently active (not revoked, not expired).</summary>
    public bool IsActive =>
        RevokedAt == null &&
        (ExpiresAt == null || ExpiresAt > DateTime.UtcNow);

    /// <summary>Human-readable status.</summary>
    public string StatusDisplay =>
        RevokedAt.HasValue ? "Revoked" :
        ExpiresAt.HasValue && ExpiresAt <= DateTime.UtcNow ? "Expired" :
        "Active";

    /// <summary>Parsed scope list from comma-separated string.</summary>
    public List<ApiKeyScope> ParsedScopes =>
        Scopes.Split(',', StringSplitOptions.RemoveEmptyEntries)
              .Select(s => Enum.TryParse<ApiKeyScope>(s, out var scope) ? scope : (ApiKeyScope?)null)
              .Where(s => s.HasValue)
              .Select(s => s!.Value)
              .ToList();

    /// <summary>Whether this key has the given scope. (Method — not a property EF would try to map.)</summary>
    public bool HasScope(ApiKeyScope scope) =>
        ParsedScopes.Contains(ApiKeyScope.FullAccess) || ParsedScopes.Contains(scope);

    /// <summary>EF-safe stub property for HasScope — always false at DB level; computed at runtime.</summary>
    public bool _HasScopeComputed => false;

    /// <summary>Display-friendly scope list.</summary>
    public string ScopesDisplay =>
        ParsedScopes.Contains(ApiKeyScope.FullAccess) ? "Full Access" :
        string.Join(", ", ParsedScopes.Select(s => ScopeDisplayName(s)));

    /// <summary>Days until expiry. Null = never expires. Negative = already expired.</summary>
    public int? DaysUntilExpiry =>
        ExpiresAt.HasValue ? (int)(ExpiresAt.Value - DateTime.UtcNow).TotalDays : null;

    private static string ScopeDisplayName(ApiKeyScope s) => s switch
    {
        ApiKeyScope.ReadEmployees => "Read Employees",
        ApiKeyScope.WriteEmployees => "Write Employees",
        ApiKeyScope.ReadAttendance => "Read Attendance",
        ApiKeyScope.WriteAttendance => "Write Attendance",
        ApiKeyScope.LeaveManagement => "Leave Mgmt",
        ApiKeyScope.ReadPayroll => "Read Payroll",
        ApiKeyScope.WritePayroll => "Write Payroll",
        ApiKeyScope.DeviceManagement => "Devices",
        ApiKeyScope.FullAccess => "Full Access",
        _ => s.ToString()
    };
}

/// <summary>
/// In-memory rate limit tracker. Tracks request counts per API key per minute.
/// Used by the middleware to enforce per-key rate limits.
/// </summary>
public class ApiKeyRateLimitTracker
{
    private readonly Dictionary<int, List<DateTime>> _requests = new();
    private readonly object _lock = new();

    /// <summary>
    /// Records a request and returns true if the rate limit was exceeded.
    /// </summary>
    public bool IsRateLimited(int keyId, int limitPerMinute)
    {
        if (limitPerMinute <= 0) return false; // 0 = unlimited

        lock (_lock)
        {
            var now = DateTime.UtcNow;
            var cutoff = now.AddMinutes(-1);

            if (!_requests.TryGetValue(keyId, out var timestamps))
            {
                timestamps = new List<DateTime>();
                _requests[keyId] = timestamps;
            }

            // Remove timestamps older than 1 minute
            timestamps.RemoveAll(t => t < cutoff);

            if (timestamps.Count >= limitPerMinute)
                return true; // Rate limited

            timestamps.Add(now);
            return false;
        }
    }

    /// <summary>
    /// Periodic cleanup of stale entries to prevent memory leaks.
    /// </summary>
    public void Cleanup()
    {
        lock (_lock)
        {
            var cutoff = DateTime.UtcNow.AddMinutes(-5);
            foreach (var kvp in _requests)
                kvp.Value.RemoveAll(t => t < cutoff);
            // Remove keys with no recent requests
            var emptyKeys = _requests.Where(kvp => kvp.Value.Count == 0).Select(kvp => kvp.Key).ToList();
            foreach (var key in emptyKeys)
                _requests.Remove(key);
        }
    }
}
