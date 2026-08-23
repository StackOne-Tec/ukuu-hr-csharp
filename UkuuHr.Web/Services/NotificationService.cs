using Microsoft.EntityFrameworkCore;
using UkuuHr.Data;
using UkuuHr.Models;

namespace UkuuHr.Services;

/// <summary>
/// Notification service for FR-013 Notifications module.
/// Supports in-app notifications with the ability to extend to email/push delivery.
/// Error/warning notifications are additionally emailed to the org's admin
/// accounts when email (Resend) is configured.
/// </summary>
public class NotificationService
{
    private readonly UkuuHrDbContext _db;
    private readonly EmailService? _email;
    private readonly ILogger<NotificationService>? _logger;

    public NotificationService(UkuuHrDbContext db, EmailService? email = null, ILogger<NotificationService>? logger = null)
    {
        _db = db;
        _email = email;
        _logger = logger;
    }

    // ───────────── Queries ─────────────

    /// <summary>Get the most recent notifications for a user/organization.</summary>
    public Task<List<NotificationRecord>> RecentAsync(int orgId, string? recipientUserId = null, int take = 20)
    {
        var q = _db.Set<NotificationRecord>()
            .Where(n => n.OrganizationId == orgId);

        if (!string.IsNullOrWhiteSpace(recipientUserId))
            q = q.Where(n => n.RecipientUserId == null || n.RecipientUserId == recipientUserId);
        else
            q = q.Where(n => n.RecipientUserId == null);

        return q.OrderByDescending(n => n.CreatedAt).Take(take).ToListAsync();
    }

    /// <summary>Count unread notifications for a user.</summary>
    public Task<int> UnreadCountAsync(int orgId, string? recipientUserId = null)
    {
        var q = _db.Set<NotificationRecord>()
            .Where(n => n.OrganizationId == orgId && !n.IsRead);

        if (!string.IsNullOrWhiteSpace(recipientUserId))
            q = q.Where(n => n.RecipientUserId == null || n.RecipientUserId == recipientUserId);
        else
            q = q.Where(n => n.RecipientUserId == null);

        return q.CountAsync();
    }

    /// <summary>Get a single notification by ID.</summary>
    public Task<NotificationRecord?> GetAsync(int orgId, int id) =>
        _db.Set<NotificationRecord>().FirstOrDefaultAsync(n => n.OrganizationId == orgId && n.Id == id);

    // ───────────── Mutations ─────────────

    /// <summary>Create a new notification.</summary>
    public async Task<NotificationRecord> CreateAsync(NotificationRecord notification)
    {
        // Ensure all required fields have defaults
        if (string.IsNullOrEmpty(notification.Type))
            notification.Type = "info";
        notification.CreatedAt = DateTime.UtcNow;

        _db.Set<NotificationRecord>().Add(notification);
        await _db.SaveChangesAsync();

        // Best-effort admin email for error/warning notifications (device failures,
        // sync problems, urgent operational issues).
        if (notification.Type is "error" or "warning")
            _ = EmailAdminsBestEffortAsync(notification);

        return notification;
    }

    /// <summary>Email error/warning notifications to the org's admin accounts (never throws).</summary>
    private async Task EmailAdminsBestEffortAsync(NotificationRecord notification)
    {
        if (_email == null || !_email.Enabled) return;
        try
        {
            var adminEmails = await _db.UserAccounts
                .Where(u => u.OrganizationId == notification.OrganizationId
                         && u.Status == AccountStatus.Active
                         && (u.Role == UserRole.SuperAdmin || u.Role == UserRole.HrAdmin || u.Role == UserRole.FinancePayrollAdmin))
                .Select(u => u.Email)
                .ToListAsync();
            if (adminEmails.Count == 0) return;

            var html = EmailService.WrapHtml(notification.Title,
                $@"<p>{notification.Body}</p>
<p style=""color:#6b6580;font-size:12px;"">Raised by module: <b>{notification.SourceModule ?? "system"}</b></p>");
            await _email.SendToManyAsync(adminEmails, $"[Ukuu HR] {notification.Title}", html);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Admin email for notification '{Title}' failed", notification.Title);
        }
    }

    /// <summary>Mark a notification as read.</summary>
    public async Task<bool> MarkReadAsync(int orgId, int id)
    {
        var n = await _db.Set<NotificationRecord>()
            .FirstOrDefaultAsync(x => x.OrganizationId == orgId && x.Id == id);
        if (n == null) return false;

        n.IsRead = true;
        n.ReadAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        return true;
    }

    /// <summary>Mark all notifications as read for a user.</summary>
    public async Task<int> MarkAllReadAsync(int orgId, string? recipientUserId = null)
    {
        var q = _db.Set<NotificationRecord>()
            .Where(n => n.OrganizationId == orgId && !n.IsRead);

        if (!string.IsNullOrWhiteSpace(recipientUserId))
            q = q.Where(n => n.RecipientUserId == null || n.RecipientUserId == recipientUserId);
        else
            q = q.Where(n => n.RecipientUserId == null);

        var now = DateTime.UtcNow;
        var count = await q.CountAsync();

        await q.ExecuteUpdateAsync(s => s
            .SetProperty(n => n.IsRead, true)
            .SetProperty(n => n.ReadAt, now));

        return count;
    }

    /// <summary>Delete a notification.</summary>
    public async Task<bool> DeleteAsync(int orgId, int id)
    {
        var n = await _db.Set<NotificationRecord>()
            .FirstOrDefaultAsync(x => x.OrganizationId == orgId && x.Id == id);
        if (n == null) return false;

        _db.Set<NotificationRecord>().Remove(n);
        await _db.SaveChangesAsync();
        return true;
    }

    // ───────────── Convenience helpers ─────────────

    /// <summary>Quickly create an info notification.</summary>
    public async Task<NotificationRecord> NotifyInfoAsync(int orgId, string title, string? body = null,
        string? recipientUserId = null, string? sourceModule = null, string? actionUrl = null)
    {
        return await CreateAsync(new NotificationRecord
        {
            OrganizationId = orgId,
            RecipientUserId = recipientUserId,
            Type = "info",
            Title = title,
            Body = body,
            SourceModule = sourceModule,
            ActionUrl = actionUrl,
            Channel = NotificationChannel.InApp,
            DeliveryStatus = NotificationDeliveryStatus.Queued
        });
    }

    /// <summary>Quickly create a warning notification.</summary>
    public async Task<NotificationRecord> NotifyWarningAsync(int orgId, string title, string? body = null,
        string? recipientUserId = null, string? sourceModule = null, string? actionUrl = null)
    {
        return await CreateAsync(new NotificationRecord
        {
            OrganizationId = orgId,
            RecipientUserId = recipientUserId,
            Type = "warning",
            Title = title,
            Body = body,
            SourceModule = sourceModule,
            ActionUrl = actionUrl,
            Channel = NotificationChannel.InApp,
            DeliveryStatus = NotificationDeliveryStatus.Queued
        });
    }

    /// <summary>Quickly create a success notification.</summary>
    public async Task<NotificationRecord> NotifySuccessAsync(int orgId, string title, string? body = null,
        string? recipientUserId = null, string? sourceModule = null, string? actionUrl = null)
    {
        return await CreateAsync(new NotificationRecord
        {
            OrganizationId = orgId,
            RecipientUserId = recipientUserId,
            Type = "success",
            Title = title,
            Body = body,
            SourceModule = sourceModule,
            ActionUrl = actionUrl,
            Channel = NotificationChannel.InApp,
            DeliveryStatus = NotificationDeliveryStatus.Queued
        });
    }

    /// <summary>Quickly create an error notification.</summary>
    public async Task<NotificationRecord> NotifyErrorAsync(int orgId, string title, string? body = null,
        string? recipientUserId = null, string? sourceModule = null, string? actionUrl = null)
    {
        return await CreateAsync(new NotificationRecord
        {
            OrganizationId = orgId,
            RecipientUserId = recipientUserId,
            Type = "error",
            Title = title,
            Body = body,
            SourceModule = sourceModule,
            ActionUrl = actionUrl,
            Channel = NotificationChannel.InApp,
            DeliveryStatus = NotificationDeliveryStatus.Queued
        });
    }
}
