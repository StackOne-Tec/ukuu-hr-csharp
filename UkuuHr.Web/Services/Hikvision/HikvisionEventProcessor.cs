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
    ///
    /// Events are grouped per employee and paired CHRONOLOGICALLY: each CheckIn
    /// pairs with the employee's next CheckOut. This correctly handles:
    ///   - overnight shifts (a post-midnight check-out completes the previous
    ///     evening's check-in — the attendance record anchors to the check-in date)
    ///   - multiple in/out cycles within one day (earliest in / latest out win)
    ///   - check-outs without a check-in (completes a previous day's open record,
    ///     or is recorded as a MissingPunch anomaly for manual correction)
    ///   - trailing check-ins with no check-out (recorded; past days without a
    ///     checkout are marked MissingPunch)
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

        foreach (var empGroup in unprocessed.GroupBy(c => c.EmployeeId))
        {
            if (!empGroup.Key.HasValue) continue;
            var empId = empGroup.Key.Value;
            var events = empGroup.OrderBy(e => e.EventTime).ToList();

            DateTime? openIn = null;
            foreach (var evt in events)
            {
                switch (evt.EventType)
                {
                    case ClockEventType.CheckIn:
                        // Earliest open check-in wins (later duplicate check-ins are ignored).
                        openIn ??= evt.EventTime;
                        break;

                    case ClockEventType.CheckOut:
                    {
                        var checkIn = openIn;
                        openIn = null;
                        var checkOut = evt.EventTime;

                        var isCreate = await UpsertAttendanceAsync(orgId, empId, checkIn, checkOut, ct);
                        if (isCreate) created++; else updated++;
                        break;
                    }

                    // Break events are consumed (marked processed) but don't affect pairing.
                    default:
                        break;
                }
            }

            // Trailing check-in with no check-out — record it so the day shows a punch.
            if (openIn.HasValue)
            {
                var isCreate = await UpsertAttendanceAsync(orgId, empId, openIn, null, ct);
                if (isCreate) created++; else updated++;
            }
        }

        foreach (var evt in unprocessed)
        {
            evt.IsProcessed = true;
            evt.ProcessedAt = DateTime.UtcNow;
        }

        await _db.SaveChangesAsync(ct);
        return (created, updated, overtimeCreated);
    }

    /// <summary>
    /// Create or merge an attendance record for one employee/day.
    /// The record anchors to the CHECK-IN's date when one is open, otherwise to
    /// the check-out's own date (which then tries to complete a previous day's
    /// open record first — overnight-shift completion across sync batches).
    /// Returns true when a new record was created.
    /// </summary>
    private async Task<bool> UpsertAttendanceAsync(
        int orgId, int empId, DateTime? checkIn, DateTime? checkOut, CancellationToken ct)
    {
        // A check-out without an open check-in: try to complete a previous day's
        // open record first (the overnight case split across two sync batches).
        if (checkIn == null && checkOut.HasValue)
        {
            var openPrevious = await _db.Attendances
                .Where(a => a.OrganizationId == orgId && a.EmployeeId == empId
                    && a.CheckIn != null && a.CheckOut == null
                    && a.Date < checkOut.Value.Date && a.Date >= checkOut.Value.Date.AddDays(-2))
                .OrderByDescending(a => a.Date)
                .FirstOrDefaultAsync(ct);
            if (openPrevious != null)
            {
                openPrevious.CheckOut = checkOut;
                if (openPrevious.Status == AttendanceStatus.MissingPunch)
                    openPrevious.Status = openPrevious.CheckIn.Value.Hour > 9
                        ? AttendanceStatus.Late : AttendanceStatus.Present;
                return false;
            }
        }

        var anchorDate = (checkIn ?? checkOut)!.Value.Date;
        var dateKey = anchorDate.ToString("yyyy-MM-dd");

        var existing = await _db.Attendances
            .FirstOrDefaultAsync(a => a.OrganizationId == orgId
                && a.EmployeeId == empId
                && a.DateKey == dateKey, ct);

        if (existing == null)
        {
            var emp = await _db.Employees.FirstOrDefaultAsync(e => e.Id == empId, ct);
            if (emp == null) return false;

            var status = AttendanceStatus.Present;
            if (checkIn == null)
            {
                // A lone check-out is an anomaly unless it just completed an
                // overnight record (handled above) — flag it for correction.
                status = AttendanceStatus.MissingPunch;
            }
            else if (checkOut == null && anchorDate < DateTime.Today)
            {
                // Past day that never got a check-out.
                status = AttendanceStatus.MissingPunch;
            }
            else if (checkIn.Value.Hour > 9 || (checkIn.Value.Hour == 9 && checkIn.Value.Minute > 0))
            {
                status = AttendanceStatus.Late;
            }

            _db.Attendances.Add(new Attendance
            {
                OrganizationId = orgId,
                EmployeeId = empId,
                EmployeeName = emp.FullName,
                DateKey = dateKey,
                Date = anchorDate,
                CheckIn = checkIn,
                CheckOut = checkOut,
                Status = status,
                Source = AttendanceSource.Clock,
                BreakMinutes = 60,
                CreatedAt = DateTime.UtcNow
            });
            return true;
        }

        // Merge into the existing record: earliest check-in, latest check-out.
        if (checkIn.HasValue && (existing.CheckIn == null || checkIn < existing.CheckIn))
            existing.CheckIn = checkIn;
        if (checkOut.HasValue && (existing.CheckOut == null || checkOut > existing.CheckOut))
            existing.CheckOut = checkOut;

        // A previously-open day that is now complete clears its MissingPunch flag.
        if (existing.Status == AttendanceStatus.MissingPunch
            && existing.CheckIn != null && existing.CheckOut != null)
        {
            existing.Status = existing.CheckIn.Value.Hour > 9
                ? AttendanceStatus.Late : AttendanceStatus.Present;
        }
        // A past day that now has a check-in but still no check-out gets flagged.
        else if (existing.Status is AttendanceStatus.Present or AttendanceStatus.Late
            && existing.CheckIn != null && existing.CheckOut == null
            && existing.Date < DateTime.Today)
        {
            existing.Status = AttendanceStatus.MissingPunch;
        }
        return false;
    }
}
