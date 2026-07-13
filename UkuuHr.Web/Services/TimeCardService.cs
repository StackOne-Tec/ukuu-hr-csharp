using Microsoft.EntityFrameworkCore;
using UkuuHr.Data;
using UkuuHr.Models;

namespace UkuuHr.Services;

/// <summary>
/// TimeCardService — computes daily/weekly/monthly attendance summaries
/// with full interlinking: leave, holidays, overtime, and shifts.
///
/// This is the engine behind the Attendance Logs and Time Cards pages.
/// It pulls data from:
///   - Attendances (clock in/out)
///   - LeaveRequests (approved leave → "LEAVE" marker)
///   - LeaveHolidays (public holidays → "HOLIDAY" marker)
///   - OvertimeRecords (approved overtime → OT hours)
///   - EmployeeShiftAssignments (shift context)
/// </summary>
public class TimeCardService
{
    private readonly UkuuHrDbContext _db;
    public TimeCardService(UkuuHrDbContext db) => _db = db;

    // ───────────── Daily Attendance ─────────────

    /// <summary>
    /// Daily attendance for a specific date.
    /// Columns: First Name, Last Name, Employee ID, Clock in, Clock out,
    ///          Worked Hrs, Late Hrs, Overtime Hrs
    /// </summary>
    public async Task<List<DailyAttendanceRow>> GetDailyAsync(int orgId, DateTime date)
    {
        var dateOnly = date.Date;
        var dateKey = dateOnly.ToString("yyyy-MM-dd");

        // Load all active employees
        var employees = await _db.Employees
            .Where(e => e.OrganizationId == orgId && e.Status != EmploymentStatus.Inactive)
            .OrderBy(e => e.FirstName).ThenBy(e => e.Surname)
            .ToListAsync();
        var empIds = employees.Select(e => e.Id).ToList();

        // Load attendance for this date
        var attendances = await _db.Attendances
            .Where(a => a.OrganizationId == orgId && a.DateKey == dateKey)
            .ToDictionaryAsync(a => a.EmployeeId);

        // Load approved leave that covers this date
        var leaves = await _db.LeaveRequests
            .Where(l => l.OrganizationId == orgId
                && l.Status == LeaveRequestStatus.Approved
                && l.StartDate <= dateOnly && l.EndDate >= dateOnly)
            .ToDictionaryAsync(l => l.EmployeeId);

        // Check if this date is a public holiday
        var isHoliday = await _db.LeaveHolidays
            .AnyAsync(h => h.OrganizationId == orgId && h.Date.Date == dateOnly);

        // Load approved overtime for this date
        var overtimes = await _db.OvertimeRecords
            .Where(o => o.OrganizationId == orgId
                && o.Date.Date == dateOnly
                && (o.Status == OvertimeStatus.Approved || o.Status == OvertimeStatus.AutoApproved))
            .ToDictionaryAsync(o => o.EmployeeId);

        // Load shift assignments
        var shiftAssignments = await _db.EmployeeShiftAssignments
            .Where(a => a.OrganizationId == orgId && a.IsActive && empIds.Contains(a.EmployeeId))
            .Include(a => a.Shift)
            .ToDictionaryAsync(a => a.EmployeeId);

        var rows = new List<DailyAttendanceRow>();
        foreach (var emp in employees)
        {
            var att = attendances.TryGetValue(emp.Id, out var a) ? a : null;
            var leave = leaves.TryGetValue(emp.Id, out var l) ? l : null;
            var ot = overtimes.TryGetValue(emp.Id, out var o) ? o : null;
            var shift = shiftAssignments.TryGetValue(emp.Id, out var s) ? s.Shift : null;

            // Determine late hours (if check-in is after shift start)
            double lateHrs = 0;
            if (att?.CheckIn is DateTime checkIn && shift != null)
            {
                var shiftStart = dateOnly.AddMinutes(shift.StartMinutes);
                if (checkIn > shiftStart)
                    lateHrs = (checkIn - shiftStart).TotalHours;
            }

            rows.Add(new DailyAttendanceRow
            {
                EmployeeId = emp.Id,
                FirstName = emp.FirstName,
                LastName = emp.Surname,
                EmployeeCode = emp.EmployeeCode ?? "—",
                CheckIn = att?.CheckIn,
                CheckOut = att?.CheckOut,
                WorkedHours = att?.WorkedHours ?? 0,
                LateHours = lateHrs,
                OvertimeHours = ot?.Hours ?? 0,
                Status = leave != null ? "LEAVE" : isHoliday ? "HOLIDAY" : att?.Status.ToString() ?? "ABSENT",
                ShiftName = shift?.Name ?? "—"
            });
        }
        return rows;
    }

    // ───────────── Weekly Attendance ─────────────

    /// <summary>
    /// Weekly attendance for the week containing the given date.
    /// Columns: First Name, Last Name, Employee ID, Department,
    ///          Mon-Sun worked hours, Late Hrs, Total worked Hrs,
    ///          Overtime workday/weekend/holiday
    /// </summary>
    public async Task<List<WeeklyAttendanceRow>> GetWeeklyAsync(int orgId, DateTime weekDate)
    {
        // Calculate Monday of the week
        var monday = weekDate.Date.AddDays(-(int)weekDate.DayOfWeek + (int)DayOfWeek.Monday);
        if (weekDate.DayOfWeek == DayOfWeek.Sunday) monday = monday.AddDays(-7);
        var sunday = monday.AddDays(6);

        return await GetRangeAsync(orgId, monday, sunday, "weekly");
    }

    // ───────────── Monthly Attendance ─────────────

    /// <summary>
    /// Monthly attendance for the month containing the given date.
    /// Columns: First Name, Last Name, Employee ID, daily columns (01-31),
    ///          Total worked Hrs, Workday/Weekend/Holiday Overtime, Late Hrs
    /// </summary>
    public async Task<List<MonthlyAttendanceRow>> GetMonthlyAsync(int orgId, DateTime monthDate)
    {
        var firstDay = new DateTime(monthDate.Year, monthDate.Month, 1);
        var lastDay = firstDay.AddMonths(1).AddDays(-1);
        var daysInMonth = lastDay.Day;

        var employees = await _db.Employees
            .Where(e => e.OrganizationId == orgId && e.Status != EmploymentStatus.Inactive)
            .OrderBy(e => e.FirstName).ThenBy(e => e.Surname)
            .ToListAsync();
        var empIds = employees.Select(e => e.Id).ToList();

        // Load all attendance for the month
        var attendances = await _db.Attendances
            .Where(a => a.OrganizationId == orgId && a.Date >= firstDay && a.Date <= lastDay)
            .ToListAsync();

        // Load approved leave for the month
        var leaves = await _db.LeaveRequests
            .Where(l => l.OrganizationId == orgId
                && l.Status == LeaveRequestStatus.Approved
                && l.StartDate <= lastDay && l.EndDate >= firstDay)
            .ToListAsync();

        // Load holidays for the month
        var holidays = await _db.LeaveHolidays
            .Where(h => h.OrganizationId == orgId && h.Date >= firstDay && h.Date <= lastDay)
            .Select(h => h.Date.Date)
            .ToListAsync();
        var holidaySet = new HashSet<DateTime>(holidays);

        // Load approved overtime for the month
        var overtimes = await _db.OvertimeRecords
            .Where(o => o.OrganizationId == orgId
                && o.Date >= firstDay && o.Date <= lastDay
                && (o.Status == OvertimeStatus.Approved || o.Status == OvertimeStatus.AutoApproved))
            .ToListAsync();

        // Load shift assignments
        var shiftAssignments = await _db.EmployeeShiftAssignments
            .Where(a => a.OrganizationId == orgId && a.IsActive && empIds.Contains(a.EmployeeId))
            .Include(a => a.Shift)
            .ToDictionaryAsync(a => a.EmployeeId);

        var rows = new List<MonthlyAttendanceRow>();
        foreach (var emp in employees)
        {
            var row = new MonthlyAttendanceRow
            {
                EmployeeId = emp.Id,
                FirstName = emp.FirstName,
                LastName = emp.Surname,
                EmployeeCode = emp.EmployeeCode ?? "—",
                Department = emp.Department ?? "—",
                DailyHours = new double?[daysInMonth],
                DailyStatus = new string[daysInMonth]
            };

            var empAttendances = attendances.Where(a => a.EmployeeId == emp.Id).ToDictionary(a => a.Date.Day);
            var empLeaves = leaves.Where(l => l.EmployeeId == emp.Id).ToList();
            var empOvertimes = overtimes.Where(o => o.EmployeeId == emp.Id).ToList();
            var shift = shiftAssignments.TryGetValue(emp.Id, out var sa) ? sa.Shift : null;

            double totalWorked = 0;
            double totalLate = 0;
            double otWorkday = 0, otWeekend = 0, otHoliday = 0;

            for (int day = 1; day <= daysInMonth; day++)
            {
                var date = new DateTime(monthDate.Year, monthDate.Month, day);
                var idx = day - 1;

                if (empAttendances.TryGetValue(day, out var att))
                {
                    row.DailyHours[idx] = att.WorkedHours;
                    totalWorked += att.WorkedHours;

                    // Late calculation
                    if (att.CheckIn is DateTime checkIn && shift != null)
                    {
                        var shiftStart = date.AddMinutes(shift.StartMinutes);
                        if (checkIn > shiftStart)
                            totalLate += (checkIn - shiftStart).TotalHours;
                    }

                    row.DailyStatus[idx] = att.Status == AttendanceStatus.OnLeave ? "L" : "P";
                }
                else if (empLeaves.Any(l => l.StartDate <= date && l.EndDate >= date))
                {
                    row.DailyHours[idx] = null;
                    row.DailyStatus[idx] = "L"; // Leave
                }
                else if (holidaySet.Contains(date))
                {
                    row.DailyHours[idx] = null;
                    row.DailyStatus[idx] = "H"; // Holiday
                }
                else if (date.DayOfWeek == DayOfWeek.Saturday || date.DayOfWeek == DayOfWeek.Sunday)
                {
                    row.DailyHours[idx] = null;
                    row.DailyStatus[idx] = "—"; // Weekend (no data)
                }
                else
                {
                    row.DailyHours[idx] = null;
                    row.DailyStatus[idx] = "A"; // Absent
                }

                // Overtime breakdown
                var dayOt = empOvertimes.Where(o => o.Date.Day == day).ToList();
                foreach (var ot in dayOt)
                {
                    if (ot.RateType == OvertimeRateType.PublicHoliday) otHoliday += ot.Hours;
                    else if (ot.RateType == OvertimeRateType.RestDay) otWeekend += ot.Hours;
                    else otWorkday += ot.Hours;
                }
            }

            row.TotalWorkedHours = totalWorked;
            row.LateHours = totalLate;
            row.OvertimeWorkday = otWorkday;
            row.OvertimeWeekend = otWeekend;
            row.OvertimeHoliday = otHoliday;

            rows.Add(row);
        }
        return rows;
    }

    /// <summary>
    /// Internal: get a date range as weekly-style rows.
    /// </summary>
    private async Task<List<WeeklyAttendanceRow>> GetRangeAsync(int orgId, DateTime start, DateTime end, string mode)
    {
        var employees = await _db.Employees
            .Where(e => e.OrganizationId == orgId && e.Status != EmploymentStatus.Inactive)
            .OrderBy(e => e.FirstName).ThenBy(e => e.Surname)
            .ToListAsync();
        var empIds = employees.Select(e => e.Id).ToList();

        var attendances = await _db.Attendances
            .Where(a => a.OrganizationId == orgId && a.Date >= start && a.Date <= end)
            .ToListAsync();

        var leaves = await _db.LeaveRequests
            .Where(l => l.OrganizationId == orgId
                && l.Status == LeaveRequestStatus.Approved
                && l.StartDate <= end && l.EndDate >= start)
            .ToListAsync();

        var holidays = await _db.LeaveHolidays
            .Where(h => h.OrganizationId == orgId && h.Date >= start && h.Date <= end)
            .Select(h => h.Date.Date)
            .ToListAsync();
        var holidaySet = new HashSet<DateTime>(holidays);

        var overtimes = await _db.OvertimeRecords
            .Where(o => o.OrganizationId == orgId
                && o.Date >= start && o.Date <= end
                && (o.Status == OvertimeStatus.Approved || o.Status == OvertimeStatus.AutoApproved))
            .ToListAsync();

        var shiftAssignments = await _db.EmployeeShiftAssignments
            .Where(a => a.OrganizationId == orgId && a.IsActive && empIds.Contains(a.EmployeeId))
            .Include(a => a.Shift)
            .ToDictionaryAsync(a => a.EmployeeId);

        var rows = new List<WeeklyAttendanceRow>();
        var days = new[] { "Mon", "Tue", "Wed", "Thu", "Fri", "Sat", "Sun" };

        foreach (var emp in employees)
        {
            var row = new WeeklyAttendanceRow
            {
                EmployeeId = emp.Id,
                FirstName = emp.FirstName,
                LastName = emp.Surname,
                EmployeeCode = emp.EmployeeCode ?? "—",
                Department = emp.Department ?? "—",
                DailyHours = new double?[7],
                DailyStatus = new string[7]
            };

            var empAttendances = attendances.Where(a => a.EmployeeId == emp.Id).ToDictionary(a => a.Date.Date);
            var empLeaves = leaves.Where(l => l.EmployeeId == emp.Id).ToList();
            var empOvertimes = overtimes.Where(o => o.EmployeeId == emp.Id).ToList();
            var shift = shiftAssignments.TryGetValue(emp.Id, out var sa) ? sa.Shift : null;

            double totalWorked = 0;
            double totalLate = 0;
            double otWorkday = 0, otWeekend = 0, otHoliday = 0;

            for (int i = 0; i < 7; i++)
            {
                var date = start.AddDays(i).Date;
                var idx = i;

                if (empAttendances.TryGetValue(date, out var att))
                {
                    row.DailyHours[idx] = att.WorkedHours;
                    totalWorked += att.WorkedHours;

                    if (att.CheckIn is DateTime checkIn && shift != null)
                    {
                        var shiftStart = date.AddMinutes(shift.StartMinutes);
                        if (checkIn > shiftStart)
                            totalLate += (checkIn - shiftStart).TotalHours;
                    }
                    row.DailyStatus[idx] = att.Status == AttendanceStatus.OnLeave ? "L" : "P";
                }
                else if (empLeaves.Any(l => l.StartDate <= date && l.EndDate >= date))
                {
                    row.DailyHours[idx] = null;
                    row.DailyStatus[idx] = "L";
                }
                else if (holidaySet.Contains(date))
                {
                    row.DailyHours[idx] = null;
                    row.DailyStatus[idx] = "H";
                }
                else
                {
                    row.DailyHours[idx] = null;
                    row.DailyStatus[idx] = date.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday ? "—" : "A";
                }

                // Overtime
                var dayOt = empOvertimes.Where(o => o.Date.Date == date).ToList();
                foreach (var ot in dayOt)
                {
                    if (ot.RateType == OvertimeRateType.PublicHoliday) otHoliday += ot.Hours;
                    else if (ot.RateType == OvertimeRateType.RestDay) otWeekend += ot.Hours;
                    else otWorkday += ot.Hours;
                }
            }

            row.TotalWorkedHours = totalWorked;
            row.LateHours = totalLate;
            row.OvertimeWorkday = otWorkday;
            row.OvertimeWeekend = otWeekend;
            row.OvertimeHoliday = otHoliday;

            rows.Add(row);
        }
        return rows;
    }
}

// ───────────── DTOs ─────────────

public class DailyAttendanceRow
{
    public int EmployeeId { get; set; }
    public string FirstName { get; set; } = "";
    public string LastName { get; set; } = "";
    public string EmployeeCode { get; set; } = "";
    public DateTime? CheckIn { get; set; }
    public DateTime? CheckOut { get; set; }
    public double WorkedHours { get; set; }
    public double LateHours { get; set; }
    public double OvertimeHours { get; set; }
    public string Status { get; set; } = "";
    public string ShiftName { get; set; } = "";

    public string CheckInLabel => CheckIn?.ToString("HH:mm") ?? "—";
    public string CheckOutLabel => CheckOut?.ToString("HH:mm") ?? "—";
    public string WorkedHoursLabel => WorkedHours > 0 ? $"{WorkedHours:F1}h" : "—";
    public string LateHoursLabel => LateHours > 0 ? $"{LateHours:F2}h" : "—";
    public string OvertimeHoursLabel => OvertimeHours > 0 ? $"{OvertimeHours:F1}h" : "—";
}

public class WeeklyAttendanceRow
{
    public int EmployeeId { get; set; }
    public string FirstName { get; set; } = "";
    public string LastName { get; set; } = "";
    public string EmployeeCode { get; set; } = "";
    public string Department { get; set; } = "";
    public double?[] DailyHours { get; set; } = new double?[7];
    public string[] DailyStatus { get; set; } = new string[7];
    public double TotalWorkedHours { get; set; }
    public double LateHours { get; set; }
    public double OvertimeWorkday { get; set; }
    public double OvertimeWeekend { get; set; }
    public double OvertimeHoliday { get; set; }

    public string DayLabel(int i) => DailyHours[i] > 0 ? $"{DailyHours[i]:F1}h" : DailyStatus[i] ?? "—";
    public string TotalWorkedLabel => $"{TotalWorkedHours:F1}h";
    public string LateLabel => LateHours > 0 ? $"{LateHours:F2}h" : "—";
    public string OtWorkdayLabel => OvertimeWorkday > 0 ? $"{OvertimeWorkday:F1}h" : "—";
    public string OtWeekendLabel => OvertimeWeekend > 0 ? $"{OvertimeWeekend:F1}h" : "—";
    public string OtHolidayLabel => OvertimeHoliday > 0 ? $"{OvertimeHoliday:F1}h" : "—";
}

public class MonthlyAttendanceRow
{
    public int EmployeeId { get; set; }
    public string FirstName { get; set; } = "";
    public string LastName { get; set; } = "";
    public string EmployeeCode { get; set; } = "";
    public string Department { get; set; } = "";
    public double?[] DailyHours { get; set; } = Array.Empty<double?>();
    public string[] DailyStatus { get; set; } = Array.Empty<string>();
    public double TotalWorkedHours { get; set; }
    public double LateHours { get; set; }
    public double OvertimeWorkday { get; set; }
    public double OvertimeWeekend { get; set; }
    public double OvertimeHoliday { get; set; }

    public string TotalWorkedLabel => $"{TotalWorkedHours:F1}h";
    public string LateLabel => LateHours > 0 ? $"{LateHours:F2}h" : "—";
    public string OtWorkdayLabel => OvertimeWorkday > 0 ? $"{OvertimeWorkday:F1}h" : "—";
    public string OtWeekendLabel => OvertimeWeekend > 0 ? $"{OvertimeWeekend:F1}h" : "—";
    public string OtHolidayLabel => OvertimeHoliday > 0 ? $"{OvertimeHoliday:F1}h" : "—";
}
