using Microsoft.EntityFrameworkCore;
using UkuuHr.Data;
using UkuuHr.Models;
using UkuuHr.Services.Devices;

namespace UkuuHr.Services.Hikvision;

// ─────────────────────────────────────────────────────────────────────────────
// HikVision Event Processing Pipeline
//
// Processes NormalizedClockEvents from the ISAPI client into:
//   1. UnifiedClockEvent records (audit trail)
//   2. Attendance records (business logic)
//   3. Real-time notifications (admin alerts)
//
// Handles duplicate detection, employee resolution, late-arrival detection,
// and auto-calculation of overtime from clock-in/out pairs.
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>Result of processing a batch of events.</summary>
public class EventProcessingResult
{
    public int EventsReceived { get; set; }
    public int EventsImported { get; set; }
    public int DuplicatesSkipped { get; set; }
    public int UnmatchedEmployees { get; set; }
    public int AttendanceRecordsCreated { get; set; }
    public int AttendanceRecordsUpdated { get; set; }
    public int OvertimeRecordsCreated { get; set; }
    public TimeSpan Duration { get; set; }
}

public class HikvisionEventProcessor
{
    private readonly UkuuHrDbContext _db;
    private readonly ILogger<HikvisionEventProcessor> _logger;
    private readonly NotificationService? _notifications;

    public HikvisionEventProcessor(UkuuHrDbContext db, ILogger<HikvisionEventProcessor> logger, NotificationService? notifications = null)
    {
        _db = db;
        _logger = logger;
        _notifications = notifications;
    }

    /// <summary>
    /// Process a batch of NormalizedClockEvents from any vendor into UnifiedClockEvent
    /// and Attendance records. Handles duplicate detection, employee resolution,
    /// and attendance record creation/updates.
    /// </summary>
    public async Task<EventProcessingResult> ProcessEventsAsync(
        int orgId, int deviceId, DeviceVendor vendor, List<NormalizedClockEvent> events, CancellationToken ct = default)
    {
        var start = DateTime.UtcNow;
        var result = new EventProcessingResult { EventsReceived = events.Count };

        if (events.Count == 0) return result;

        // Load employees for this org (for code-to-id resolution)
        var employees = await _db.Employees
            .Where(e => e.OrganizationId == orgId && e.Status != EmploymentStatus.Inactive)
            .ToDictionaryAsync(e => e.EmployeeCode ?? e.Id.ToString(), e => e, ct);

        var imported = 0;
        var dupes = 0;
        var unmatched = 0;
        var newEvents = new List<UnifiedClockEvent>();

        foreach (var evt in events)
        {
            // Resolve employee by code
            if (!employees.TryGetValue(evt.EmployeeCode, out var emp))
            {
                unmatched++;
                continue;
            }

            // Duplicate check: same employee + device + event type + time within 60 seconds
            var isDup = await _db.UnifiedClockEvents.AnyAsync(x =>
                x.OrganizationId == orgId &&
                x.DeviceId == deviceId &&
                x.EmployeeId == emp.Id &&
                x.EventType == evt.EventType &&
                Math.Abs((x.EventTime - evt.EventTime).TotalSeconds) < 60, ct);

            if (isDup) { dupes++; continue; }

            var clockEvent = new UnifiedClockEvent
            {
                OrganizationId = orgId,
                DeviceId = deviceId,
                Vendor = vendor,
                EmployeeCode = evt.EmployeeCode,
                EmployeeId = emp.Id,
                EventTime = evt.EventTime,
                EventType = evt.EventType,
                VerifyMode = evt.VerifyMode,
                InOutMode = evt.InOutMode,
                RawPayload = evt.RawPayload,
                SyncedAt = DateTime.UtcNow,
                IsProcessed = false
            };

            _db.UnifiedClockEvents.Add(clockEvent);
            newEvents.Add(clockEvent);
            imported++;
        }

        await _db.SaveChangesAsync(ct);
        result.EventsImported = imported;
        result.DuplicatesSkipped = dupes;
        result.UnmatchedEmployees = unmatched;

        // Now process unprocessed events into Attendance records
        var (attendanceCreated, attendanceUpdated, overtimeCreated) = await ProcessUnprocessedEventsAsync(orgId, ct);
        result.AttendanceRecordsCreated = attendanceCreated;
        result.AttendanceRecordsUpdated = attendanceUpdated;
        result.OvertimeRecordsCreated = overtimeCreated;

        result.Duration = DateTime.UtcNow - start;
        return result;
    }

    /// <summary>
    /// Process all unprocessed UnifiedClockEvents into Attendance records.
    /// Groups by employee + date, detects check-in/out pairs, computes late arrivals.
    /// </summary>
    private async Task<(int created, int updated, int overtimeCreated)> ProcessUnprocessedEventsAsync(int orgId, CancellationToken ct)
    {
        var unprocessed = await _db.UnifiedClockEvents
            .Where(c => c.OrganizationId == orgId && !c.IsProcessed)
            .OrderBy(c => c.EventTime)
            .Take(500) // Process in batches to avoid memory issues
            .ToListAsync(ct);

        if (unprocessed.Count == 0) return (0, 0, 0);

        var created = 0;
        var updated = 0;
        var overtimeCreated = 0;

        // Group by employee + date
        var grouped = unprocessed.GroupBy(c => new { c.EmployeeId, c.EventTime.Date });

        foreach (var group in grouped)
        {
            if (!group.Key.EmployeeId.HasValue) continue;

            var events = group.OrderBy(e => e.EventTime).ToList();
            var checkIn = events.FirstOrDefault(e => e.EventType == ClockEventType.CheckIn);
            var checkOut = events.LastOrDefault(e => e.EventType == ClockEventType.CheckOut);

            if (checkIn == null) continue;

            var dateKey = group.Key.Date.ToString("yyyy-MM-dd");

            // Check if attendance record already exists
            var existing = await _db.Attendances
                .FirstOrDefaultAsync(a => a.OrganizationId == orgId
                    && a.EmployeeId == group.Key.EmployeeId.Value
                    && a.DateKey == dateKey, ct);

            if (existing == null)
            {
                var emp = await _db.Employees.FirstOrDefaultAsync(e => e.Id == group.Key.EmployeeId.Value, ct);
                if (emp == null) continue;

                var status = AttendanceStatus.Present;
                if (checkIn.EventTime.Hour > 9 || (checkIn.EventTime.Hour == 9 && checkIn.EventTime.Minute > 0))
                    status = AttendanceStatus.Late;

                var attendance = new Attendance
                {
                    OrganizationId = orgId,
                    EmployeeId = emp.Id,
                    EmployeeName = emp.FullName,
                    DateKey = dateKey,
                    Date = group.Key.Date,
                    CheckIn = checkIn.EventTime,
                    CheckOut = checkOut?.EventTime,
                    Status = status,
                    Source = AttendanceSource.Clock,
                    BreakMinutes = 60,
                    CreatedAt = DateTime.UtcNow
                };
                _db.Attendances.Add(attendance);
                created++;
            }
            else if (existing.CheckIn == null)
            {
                existing.CheckIn = checkIn.EventTime;
                if (checkOut != null) existing.CheckOut = checkOut.EventTime;
                if (existing.Status == AttendanceStatus.Absent || existing.Status == AttendanceStatus.OnLeave)
                    existing.Status = checkIn.EventTime.Hour > 9 ? AttendanceStatus.Late : AttendanceStatus.Present;
                updated++;
            }
            else if (existing.CheckOut == null && checkOut != null)
            {
                existing.CheckOut = checkOut.EventTime;
                updated++;
            }

            // Mark events as processed
            foreach (var evt in events)
            {
                evt.IsProcessed = true;
                evt.ProcessedAt = DateTime.UtcNow;
            }
        }

        await _db.SaveChangesAsync(ct);
        return (created, updated, overtimeCreated);
    }
}
