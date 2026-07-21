using Microsoft.EntityFrameworkCore;
using UkuuHr.Data;
using UkuuHr.Models;

namespace UkuuHr.Services;

/// <summary>
/// Service for syncing with Hikvision time & attendance devices.
/// Supports device management, employee sync, clock event retrieval, and
/// mapping Hikvision events to Ukuu HR Attendance records.
///
/// In production, this would use the Hikvision ISAPI (HTTP/REST) or SDK
/// to communicate with the device. For this demo, we simulate the sync
/// with realistic mock data.
/// </summary>
public class HikvisionSyncService
{
    private readonly UkuuHrDbContext _db;
    private readonly ILogger<HikvisionSyncService> _logger;

    public HikvisionSyncService(UkuuHrDbContext db, ILogger<HikvisionSyncService> logger)
    {
        _db = db;
        _logger = logger;
    }

    public async Task<List<HikvisionDevice>> GetDevicesAsync(int orgId)
    {
        return await _db.HikvisionDevices
            .Where(d => d.OrganizationId == orgId)
            .OrderByDescending(d => d.IsActive)
            .ThenBy(d => d.Name)
            .ToListAsync();
    }

    public async Task<HikvisionDevice?> GetDeviceAsync(int orgId, int deviceId)
    {
        return await _db.HikvisionDevices
            .FirstOrDefaultAsync(d => d.OrganizationId == orgId && d.Id == deviceId);
    }

    public async Task<HikvisionDevice> AddDeviceAsync(HikvisionDevice device)
    {
        device.CreatedAt = DateTime.UtcNow;
        _db.HikvisionDevices.Add(device);
        await _db.SaveChangesAsync();
        return device;
    }

    public async Task<bool> DeleteDeviceAsync(int orgId, int deviceId)
    {
        var device = await GetDeviceAsync(orgId, deviceId);
        if (device == null) return false;
        _db.HikvisionDevices.Remove(device);
        await _db.SaveChangesAsync();
        return true;
    }

    /// <summary>
    /// Sync clock events from a Hikvision device.
    /// In production, this would call the device's ISAPI endpoint
    /// (e.g. /ISAPI/AccessControl/AcsEvent?searchID=...) to fetch
    /// new card/fingerprint events since the last sync.
    /// For this demo, we simulate by generating realistic clock events
    /// for the past 7 days.
    /// </summary>
    public async Task<HikvisionSyncResult> SyncDeviceAsync(int orgId, int deviceId)
    {
        var device = await GetDeviceAsync(orgId, deviceId);
        if (device == null || !device.IsActive)
        {
            return new HikvisionSyncResult { Success = false, ErrorMessage = "Device not found or inactive" };
        }

        _logger.LogInformation("Starting Hikvision sync for device {DeviceName} ({Ip})", device.Name, device.IpAddress);
        device.LastSyncAt = DateTime.UtcNow;

        try
        {
            // Fetch employees for this org (to map clock events to)
            var employees = await _db.Employees
                .Where(e => e.OrganizationId == orgId && e.Status != EmploymentStatus.Inactive)
                .ToListAsync();

            if (!employees.Any())
            {
                return new HikvisionSyncResult { Success = false, ErrorMessage = "No employees to sync" };
            }

            var rnd = new Random(deviceId + DateTime.UtcNow.DayOfYear);
            var eventsAdded = 0;
            var startDate = DateTime.UtcNow.AddDays(-7);

            // Generate clock events for the past 7 days (skip weekends for most employees)
            for (int dayOffset = 0; dayOffset < 7; dayOffset++)
            {
                var date = startDate.AddDays(dayOffset);
                if (date.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday) continue;

                foreach (var emp in employees)
                {
                    // 85% attendance rate
                    if (rnd.NextDouble() > 0.85) continue;

                    // Generate check-in event (8:00-9:15 AM)
                    var checkInHour = 8;
                    var checkInMinute = rnd.Next(0, 60);
                    if (rnd.NextDouble() < 0.15) { checkInHour = 9; checkInMinute = rnd.Next(0, 15); }
                    var checkInTime = new DateTime(date.Year, date.Month, date.Day, checkInHour, checkInMinute, 0);

                    // Generate check-out event (17:00-18:30 PM)
                    var checkOutHour = 17;
                    var checkOutMinute = rnd.Next(0, 30);
                    if (rnd.NextDouble() < 0.20) { checkOutHour = 18; checkOutMinute = rnd.Next(0, 30); }
                    var checkOutTime = new DateTime(date.Year, date.Month, date.Day, checkOutHour, checkOutMinute, 0);

                    // Check if events already exist (avoid duplicates)
                    var dateKey = date.ToString("yyyy-MM-dd");
                    var existing = await _db.HikvisionClockEvents
                        .AnyAsync(c => c.DeviceId == deviceId && c.EmployeeCode == emp.EmployeeCode
                                    && c.EventTime.Date == date.Date);
                    if (existing) continue;

                    // Add check-in event
                    _db.HikvisionClockEvents.Add(new HikvisionClockEvent
                    {
                        OrganizationId = orgId,
                        DeviceId = deviceId,
                        EmployeeCode = emp.EmployeeCode ?? emp.Id.ToString(),
                        EmployeeId = emp.Id,
                        EventTime = checkInTime,
                        EventType = ClockEventType.CheckIn,
                        VerifyMode = "Card",
                        InOutMode = "Entrance",
                        SyncedAt = DateTime.UtcNow
                    });

                    // Add check-out event
                    _db.HikvisionClockEvents.Add(new HikvisionClockEvent
                    {
                        OrganizationId = orgId,
                        DeviceId = deviceId,
                        EmployeeCode = emp.EmployeeCode ?? emp.Id.ToString(),
                        EmployeeId = emp.Id,
                        EventTime = checkOutTime,
                        EventType = ClockEventType.CheckOut,
                        VerifyMode = "Card",
                        InOutMode = "Exit",
                        SyncedAt = DateTime.UtcNow
                    });

                    eventsAdded += 2;
                }
            }

            device.LastSuccessfulSyncAt = DateTime.UtcNow;
            device.TotalEventsSynced += eventsAdded;
            await _db.SaveChangesAsync();

            // Process the synced events → create Attendance records
            var processed = await ProcessUnprocessedEventsAsync(orgId, deviceId);

            _logger.LogInformation("Hikvision sync complete: {EventsAdded} events added, {Processed} attendance records created",
                eventsAdded, processed);

            return new HikvisionSyncResult
            {
                Success = true,
                EventsAdded = eventsAdded,
                AttendanceRecordsCreated = processed,
                DeviceName = device.Name
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Hikvision sync failed for device {DeviceName}", device.Name);
            return new HikvisionSyncResult { Success = false, ErrorMessage = ex.Message };
        }
    }

    /// <summary>
    /// Process unprocessed Hikvision clock events and create/update Attendance records.
    /// Maps check-in/check-out event pairs to Attendance records.
    /// </summary>
    public async Task<int> ProcessUnprocessedEventsAsync(int orgId, int? deviceId = null)
    {
        var query = _db.HikvisionClockEvents
            .Where(c => c.OrganizationId == orgId && !c.IsProcessed);

        if (deviceId.HasValue)
            query = query.Where(c => c.DeviceId == deviceId.Value);

        var unprocessed = await query.OrderBy(c => c.EventTime).ToListAsync();
        var processedCount = 0;

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

            // Check if attendance record already exists for this employee + date
            var existing = await _db.Attendances
                .FirstOrDefaultAsync(a => a.OrganizationId == orgId
                                       && a.EmployeeId == group.Key.EmployeeId.Value
                                       && a.DateKey == dateKey);

            if (existing == null)
            {
                var emp = await _db.Employees.FirstOrDefaultAsync(e => e.Id == group.Key.EmployeeId.Value);
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
                    Source = AttendanceSource.System,
                    BreakMinutes = 60,
                    CreatedAt = DateTime.UtcNow
                };
                _db.Attendances.Add(attendance);
                processedCount++;
            }
            else if (existing.CheckIn == null)
            {
                existing.CheckIn = checkIn.EventTime;
                if (checkOut != null) existing.CheckOut = checkOut.EventTime;
                if (existing.Status == AttendanceStatus.Absent || existing.Status == AttendanceStatus.OnLeave)
                    existing.Status = checkIn.EventTime.Hour > 9 ? AttendanceStatus.Late : AttendanceStatus.Present;
                processedCount++;
            }
            else if (existing.CheckOut == null && checkOut != null)
            {
                existing.CheckOut = checkOut.EventTime;
                processedCount++;
            }

            // Mark events as processed
            foreach (var evt in events)
            {
                evt.IsProcessed = true;
                evt.ProcessedAt = DateTime.UtcNow;
            }
        }

        await _db.SaveChangesAsync();
        return processedCount;
    }

    /// <summary>
    /// Get recent clock events for display.
    /// </summary>
    public async Task<List<HikvisionClockEvent>> GetRecentEventsAsync(int orgId, int take = 50)
    {
        return await _db.HikvisionClockEvents
            .Where(c => c.OrganizationId == orgId)
            .Include(c => c.Device)
            .OrderByDescending(c => c.EventTime)
            .Take(take)
            .ToListAsync();
    }

    /// <summary>
    /// Get sync statistics for dashboard.
    /// </summary>
    public async Task<HikvisionStats> GetStatsAsync(int orgId)
    {
        var devices = await GetDevicesAsync(orgId);
        var totalEvents = await _db.HikvisionClockEvents.CountAsync(c => c.OrganizationId == orgId);
        var unprocessed = await _db.HikvisionClockEvents.CountAsync(c => c.OrganizationId == orgId && !c.IsProcessed);

        return new HikvisionStats
        {
            TotalDevices = devices.Count,
            ActiveDevices = devices.Count(d => d.IsActive),
            TotalEvents = totalEvents,
            UnprocessedEvents = unprocessed,
            LastSync = devices.Max(d => d.LastSuccessfulSyncAt)
        };
    }
}

public class HikvisionSyncResult
{
    public bool Success { get; set; }
    public int EventsAdded { get; set; }
    public int AttendanceRecordsCreated { get; set; }
    public string? DeviceName { get; set; }
    public string? ErrorMessage { get; set; }
}

public class HikvisionStats
{
    public int TotalDevices { get; set; }
    public int ActiveDevices { get; set; }
    public int TotalEvents { get; set; }
    public int UnprocessedEvents { get; set; }
    public DateTime? LastSync { get; set; }
}

/// <summary>
/// Overtime calculation and management service.
/// Computes overtime from attendance records, supports manual entry,
/// and handles approval workflow.
/// </summary>
public class OvertimeService
{
    private readonly UkuuHrDbContext _db;
    private readonly ILogger<OvertimeService> _logger;
    private readonly NotificationService _notifications;

    public OvertimeService(UkuuHrDbContext db, ILogger<OvertimeService> logger, NotificationService notifications)
    {
        _db = db;
        _logger = logger;
        _notifications = notifications;
    }

    /// <summary>
    /// Calculate overtime for an employee on a given date based on their attendance.
    /// Standard work day = 8 hours. Anything beyond is overtime.
    /// Rate type is determined by day of week and holiday calendar.
    /// </summary>
    public async Task<List<OvertimeRecord>> CalculateOvertimeForEmployeeAsync(int orgId, int employeeId, DateTime date)
    {
        var results = new List<OvertimeRecord>();
        var dateKey = date.ToString("yyyy-MM-dd");

        var attendance = await _db.Attendances
            .FirstOrDefaultAsync(a => a.OrganizationId == orgId && a.EmployeeId == employeeId && a.DateKey == dateKey);

        if (attendance?.CheckIn == null || attendance?.CheckOut == null) return results;

        var emp = await _db.Employees.FirstOrDefaultAsync(e => e.Id == employeeId);
        if (emp == null) return results;

        // Standard work hours = 8 hours (plus 1 hour break = 9 hours from check-in to check-out)
        var totalHours = (attendance.CheckOut.Value - attendance.CheckIn.Value).TotalHours - (attendance.BreakMinutes / 60.0);
        var standardHours = 8.0;
        var overtimeHours = totalHours - standardHours;

        if (overtimeHours <= 0.5) return results; // No overtime if less than 30 min over

        // Determine rate type
        var isHoliday = await _db.LeaveHolidays
            .AnyAsync(h => h.OrganizationId == orgId && h.Date.Date == date.Date);
        var isWeekend = date.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday;

        var rateType = OvertimeRateType.Standard;
        var multiplier = 1.5;
        if (isHoliday) { rateType = OvertimeRateType.PublicHoliday; multiplier = 2.5; }
        else if (isWeekend) { rateType = OvertimeRateType.RestDay; multiplier = 2.0; }
        else if (overtimeHours > 2) { rateType = OvertimeRateType.DoubleTime; multiplier = 2.0; }

        // Calculate overtime period (starts after standard hours + break)
        var overtimeStart = attendance.CheckIn.Value.AddHours(standardHours + attendance.BreakMinutes / 60.0);
        var overtimeEnd = attendance.CheckOut.Value;

        // Check if overtime record already exists
        var existing = await _db.OvertimeRecords
            .AnyAsync(o => o.OrganizationId == orgId && o.EmployeeId == employeeId && o.Date == date.Date);

        if (existing) return results;

        var record = new OvertimeRecord
        {
            OrganizationId = orgId,
            EmployeeId = employeeId,
            EmployeeName = emp.FullName,
            Date = date.Date,
            StartTime = overtimeStart,
            EndTime = overtimeEnd,
            Hours = Math.Round(overtimeHours, 2),
            RateType = rateType,
            RateMultiplier = multiplier,
            HourlyRate = emp.EffectiveHourlyRate,
            Source = OvertimeSource.AutoCalculated,
            Status = OvertimeStatus.Pending,
            Reason = $"Auto-calculated from attendance ({totalHours:F1}h total, {overtimeHours:F1}h overtime)",
            CreatedAt = DateTime.UtcNow
        };

        _db.OvertimeRecords.Add(record);
        results.Add(record);
        await _db.SaveChangesAsync();
        return results;
    }

    /// <summary>
    /// Auto-calculate overtime for all employees for a given date range.
    /// FR-006: Notifies admins of batch results.
    /// </summary>
    public async Task<int> AutoCalculateForDateRangeAsync(int orgId, DateTime from, DateTime to)
    {
        var employees = await _db.Employees
            .Where(e => e.OrganizationId == orgId && e.Status != EmploymentStatus.Inactive)
            .ToListAsync();

        var totalCreated = 0;
        for (var date = from.Date; date <= to.Date; date = date.AddDays(1))
        {
            foreach (var emp in employees)
            {
                var records = await CalculateOvertimeForEmployeeAsync(orgId, emp.Id, date);
                totalCreated += records.Count;
            }
        }

        // FR-006: Single batch notification (avoids noise of per-record notifications)
        if (totalCreated > 0)
        {
            await NotifySafeAsync(orgId,
                type: "info",
                title: "Overtime auto-calculated",
                body: $"Generated {totalCreated} overtime records for {from:yyyy-MM-dd} to {to:yyyy-MM-dd}",
                sourceModule: "overtime",
                actionUrl: "/overtime");
        }

        return totalCreated;
    }

    public async Task<List<OvertimeRecord>> GetAllAsync(int orgId, OvertimeStatus? status = null)
    {
        var query = _db.OvertimeRecords.Where(o => o.OrganizationId == orgId);
        if (status.HasValue) query = query.Where(o => o.Status == status);
        return await query.OrderByDescending(o => o.Date).ThenByDescending(o => o.CreatedAt).ToListAsync();
    }

    public async Task<List<OvertimeRecord>> GetPendingAsync(int orgId)
    {
        return await _db.OvertimeRecords
            .Where(o => o.OrganizationId == orgId && o.Status == OvertimeStatus.Pending)
            .OrderByDescending(o => o.Date)
            .ToListAsync();
    }

    public async Task<OvertimeRecord?> GetAsync(int orgId, int id)
    {
        return await _db.OvertimeRecords.FirstOrDefaultAsync(o => o.OrganizationId == orgId && o.Id == id);
    }

    public async Task<OvertimeRecord> CreateManualAsync(OvertimeRecord record)
    {
        record.CreatedAt = DateTime.UtcNow;
        record.Source = OvertimeSource.Manual;
        record.Status = OvertimeStatus.Pending;

        // Calculate hours from start/end time
        record.Hours = Math.Round((record.EndTime - record.StartTime).TotalHours, 2);

        // Set multiplier based on rate type
        record.RateMultiplier = record.RateType switch
        {
            OvertimeRateType.Standard => 1.5,
            OvertimeRateType.RestDay => 2.0,
            OvertimeRateType.PublicHoliday => 2.5,
            OvertimeRateType.DoubleTime => 2.0,
            _ => 1.5
        };

        _db.OvertimeRecords.Add(record);
        await _db.SaveChangesAsync();

        // FR-006: Notify admins of manual overtime entry
        await NotifySafeAsync(record.OrganizationId,
            type: "info",
            title: $"Manual overtime: {record.EmployeeName}",
            body: $"{record.Hours:F1}h {record.RateTypeDisplay} on {record.Date:yyyy-MM-dd} — pending approval",
            sourceModule: "overtime",
            actionUrl: "/overtime");

        return record;
    }

    public async Task<OvertimeRecord?> UpdateAsync(int orgId, int id, double hours, DateTime startTime, DateTime endTime, OvertimeRateType rateType, string? reason)
    {
        var ot = await GetAsync(orgId, id);
        if (ot == null) return null;

        ot.Hours = Math.Round(hours, 2);
        ot.StartTime = startTime;
        ot.EndTime = endTime;
        ot.RateType = rateType;
        ot.RateMultiplier = rateType switch
        {
            OvertimeRateType.Standard => 1.5,
            OvertimeRateType.RestDay => 2.0,
            OvertimeRateType.PublicHoliday => 2.5,
            OvertimeRateType.DoubleTime => 2.0,
            _ => 1.5
        };
        ot.Reason = reason;
        ot.UpdatedAt = DateTime.UtcNow;

        await _db.SaveChangesAsync();
        return ot;
    }

    public async Task<bool> ApproveAsync(int orgId, int id, string approverEmail, string? notes = null)
    {
        var ot = await GetAsync(orgId, id);
        if (ot == null) return false;
        ot.Status = OvertimeStatus.Approved;
        ot.ApprovedByEmail = approverEmail;
        ot.ApprovedAt = DateTime.UtcNow;
        ot.ApproverNotes = notes;
        await _db.SaveChangesAsync();

        // FR-006: Notify employee (broadcast if no specific requester)
        await NotifySafeAsync(orgId,
            type: "success",
            title: "Overtime approved",
            body: $"{ot.Hours:F1}h overtime on {ot.Date:yyyy-MM-dd} ({ot.RateTypeDisplay}) approved.",
            recipientUserId: ot.RequestedByUserId,
            sourceModule: "overtime",
            actionUrl: "/overtime");

        return true;
    }

    public async Task<bool> RejectAsync(int orgId, int id, string rejectorEmail, string reason)
    {
        var ot = await GetAsync(orgId, id);
        if (ot == null) return false;
        ot.Status = OvertimeStatus.Rejected;
        ot.ApprovedByEmail = rejectorEmail;
        ot.ApprovedAt = DateTime.UtcNow;
        ot.RejectionReason = reason;
        await _db.SaveChangesAsync();

        // FR-006: Notify employee (broadcast if no specific requester)
        await NotifySafeAsync(orgId,
            type: "error",
            title: "Overtime rejected",
            body: $"{ot.Hours:F1}h overtime on {ot.Date:yyyy-MM-dd} rejected. Reason: {reason}",
            recipientUserId: ot.RequestedByUserId,
            sourceModule: "overtime",
            actionUrl: "/overtime");

        return true;
    }

    // ───────────── FR-006: Notification helper ─────────────

    /// <summary>Send a notification safely — failures are silently caught (best-effort delivery).</summary>
    private async Task NotifySafeAsync(int orgId, string type, string title, string? body = null,
        string? recipientUserId = null, string? sourceModule = null, string? actionUrl = null)
    {
        try
        {
            switch (type)
            {
                case "success":
                    await _notifications.NotifySuccessAsync(orgId, title, body, recipientUserId, sourceModule, actionUrl);
                    break;
                case "error":
                    await _notifications.NotifyErrorAsync(orgId, title, body, recipientUserId, sourceModule, actionUrl);
                    break;
                case "warning":
                    await _notifications.NotifyWarningAsync(orgId, title, body, recipientUserId, sourceModule, actionUrl);
                    break;
                default:
                    await _notifications.NotifyInfoAsync(orgId, title, body, recipientUserId, sourceModule, actionUrl);
                    break;
            }
        }
        catch
        {
            // Notification failures must never break the operation — best-effort only
        }
    }

    public async Task<OvertimeSummary> GetSummaryAsync(int orgId, int month, int year)
    {
        var records = await _db.OvertimeRecords
            .Where(o => o.OrganizationId == orgId && o.Date.Month == month && o.Date.Year == year)
            .ToListAsync();

        return new OvertimeSummary
        {
            TotalHours = records.Where(o => o.Status != OvertimeStatus.Rejected).Sum(o => o.Hours),
            TotalPay = records.Where(o => o.Status == OvertimeStatus.Approved || o.Status == OvertimeStatus.AutoApproved).Sum(o => o.Pay),
            PendingCount = records.Count(o => o.Status == OvertimeStatus.Pending),
            ApprovedCount = records.Count(o => o.Status == OvertimeStatus.Approved || o.Status == OvertimeStatus.AutoApproved),
            EmployeeCount = records.Where(o => o.Status != OvertimeStatus.Rejected).Select(o => o.EmployeeId).Distinct().Count()
        };
    }
}

public class OvertimeSummary
{
    public double TotalHours { get; set; }
    public double TotalPay { get; set; }
    public int PendingCount { get; set; }
    public int ApprovedCount { get; set; }
    public int EmployeeCount { get; set; }
}
