using System.ComponentModel.DataAnnotations;

namespace UkuuHr.Models;

/// <summary>
/// Notification record for the Notifications module (FR-013).
/// Supports in-app notifications, email digests, and push notifications.
/// </summary>
public class NotificationRecord
{
    public int Id { get; set; }

    public int OrganizationId { get; set; }
    public Organization Organization { get; set; } = null!;

    public int? EmployeeId { get; set; }
    public Employee? Employee { get; set; }

    /// <summary>The recipient user (nullable — broadcast if null).</summary>
    [MaxLength(450)]
    public string? RecipientUserId { get; set; }

    [MaxLength(256)]
    public string? RecipientEmail { get; set; }

    [Required, MaxLength(100)]
    public string Type { get; set; } = string.Empty; // "info", "warning", "success", "error"

    [Required, MaxLength(200)]
    public string Title { get; set; } = string.Empty;

    [MaxLength(1000)]
    public string? Body { get; set; }

    /// <summary>Optional deep-link URL the notification points to.</summary>
    [MaxLength(500)]
    public string? ActionUrl { get; set; }

    [MaxLength(200)]
    public string? ActionLabel { get; set; }

    /// <summary>Module that triggered this notification (e.g. "attendance", "leave", "payroll").</summary>
    [MaxLength(100)]
    public string? SourceModule { get; set; }

    [MaxLength(100)]
    public string? SourceEntityId { get; set; }

    public NotificationDeliveryStatus DeliveryStatus { get; set; } = NotificationDeliveryStatus.Pending;

    public NotificationChannel Channel { get; set; } = NotificationChannel.InApp;

    public bool IsRead { get; set; }
    public DateTime? ReadAt { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>When the notification was actually delivered (sent via email/push).</summary>
    public DateTime? DeliveredAt { get; set; }

    /// <summary>Error message if delivery failed.</summary>
    [MaxLength(500)]
    public string? DeliveryError { get; set; }
}

public enum NotificationDeliveryStatus
{
    Pending,
    Queued,
    Sent,
    Failed
}

public enum NotificationChannel
{
    InApp,
    Email,
    Push,
    All
}
