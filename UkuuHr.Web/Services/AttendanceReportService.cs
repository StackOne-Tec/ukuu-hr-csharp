using System.Globalization;
using System.Text;
using ClosedXML.Excel;
using CsvHelper;
using Microsoft.EntityFrameworkCore;
using UkuuHr.Data;
using UkuuHr.Models;

namespace UkuuHr.Services;

// ─────────────────────────────────────────────────────────────────────────────
// FR-009 Attendance Search
// Multi-filter search across attendance records: employee, department,
// branch, shift, status, date, custom range. Returns paged results.
// ─────────────────────────────────────────────────────────────────────────────

public class AttendanceSearchService
{
    private readonly UkuuHrDbContext _db;
    public AttendanceSearchService(UkuuHrDbContext db) => _db = db;

    /// <summary>
    /// Build the base IQueryable with all non-pagination filters applied.
    /// Shared by SearchAsync, CountAsync, and ReportExportService for filtered exports.
    /// </summary>
    public IQueryable<Attendance> BuildFilteredQuery(int orgId, AttendanceSearchFilter filter)
    {
        var q = _db.Attendances.Where(a => a.OrganizationId == orgId);

        if (filter.EmployeeId.HasValue)
            q = q.Where(a => a.EmployeeId == filter.EmployeeId.Value);

        if (!string.IsNullOrWhiteSpace(filter.Department))
        {
            q = from a in q
                join e in _db.Employees on a.EmployeeId equals e.Id
                where e.Department == filter.Department
                select a;
        }

        if (!string.IsNullOrWhiteSpace(filter.Branch))
        {
            q = from a in q
                join e in _db.Employees on a.EmployeeId equals e.Id
                where e.City == filter.Branch
                select a;
        }

        if (filter.ShiftId.HasValue)
        {
            var empIds = _db.EmployeeShiftAssignments
                .Where(s => s.ShiftId == filter.ShiftId.Value && s.IsActive)
                .Select(s => s.EmployeeId)
                .Distinct();
            q = q.Where(a => empIds.Contains(a.EmployeeId));
        }

        if (filter.Status.HasValue)
            q = q.Where(a => a.Status == filter.Status.Value);

        if (filter.FromDate.HasValue)
            q = q.Where(a => a.Date >= filter.FromDate.Value.Date);

        if (filter.ToDate.HasValue)
            q = q.Where(a => a.Date <= filter.ToDate.Value.Date.AddDays(1).AddTicks(-1));

        if (!string.IsNullOrWhiteSpace(filter.Search))
        {
            var s = filter.Search.Trim().ToLower();
            q = q.Where(a => a.EmployeeName.ToLower().Contains(s));
        }

        return q;
    }

    /// <summary>Search attendance records with multiple filters (paged).</summary>
    public async Task<List<Attendance>> SearchAsync(int orgId, AttendanceSearchFilter filter)
    {
        var q = BuildFilteredQuery(orgId, filter);
        return await q
            .OrderByDescending(a => a.Date)
            .ThenBy(a => a.EmployeeName)
            .Skip((filter.Page - 1) * filter.PageSize)
            .Take(filter.PageSize)
            .ToListAsync();
    }

    /// <summary>Count of total matching records (for pagination).</summary>
    public async Task<int> CountAsync(int orgId, AttendanceSearchFilter filter)
    {
        var q = BuildFilteredQuery(orgId, filter);
        return await q.CountAsync();
    }

    /// <summary>Get all unique departments for the filter dropdown.</summary>
    public Task<List<string>> GetDepartmentsAsync(int orgId) =>
        _db.Employees.Where(e => e.OrganizationId == orgId && e.Department != null)
            .Select(e => e.Department!).Distinct().OrderBy(d => d).ToListAsync();

    /// <summary>Get all unique branches (cities) for the filter dropdown.</summary>
    public Task<List<string>> GetBranchesAsync(int orgId) =>
        _db.Employees.Where(e => e.OrganizationId == orgId && e.City != null)
            .Select(e => e.City!).Distinct().OrderBy(c => c).ToListAsync();

    /// <summary>Get all shifts for the filter dropdown.</summary>
    public Task<List<Shift>> GetShiftsAsync(int orgId) =>
        _db.Shifts.Where(s => s.OrganizationId == orgId && s.IsActive).OrderBy(s => s.Name).ToListAsync();
}

/// <summary>Filter parameters for attendance search.</summary>
public class AttendanceSearchFilter
{
    public int? EmployeeId { get; set; }
    public string? Department { get; set; }
    public string? Branch { get; set; }
    public int? ShiftId { get; set; }
    public AttendanceStatus? Status { get; set; }
    public DateTime? FromDate { get; set; }
    public DateTime? ToDate { get; set; }
    public string? Search { get; set; }
    public int Page { get; set; } = 1;
    public int PageSize { get; set; } = 50;
}

// ─────────────────────────────────────────────────────────────────────────────
// FR-010 Reporting
// Generates daily / weekly / monthly / custom-range reports with CSV and
// XLSX export. Reports include: check-in, check-out, worked hours,
// overtime, attendance status, leave status.
// ─────────────────────────────────────────────────────────────────────────────

public class ReportExportService
{
    private readonly UkuuHrDbContext _db;
    public ReportExportService(UkuuHrDbContext db) => _db = db;

    /// <summary>Generate a report for the given period, including overtime data.</summary>
    public async Task<AttendanceReport> GenerateAsync(int orgId, ReportPeriod period, DateTime? fromDate = null, DateTime? toDate = null)
    {
        var (from, to) = ResolveRange(period, fromDate, toDate);
        var records = await _db.Attendances
            .Where(a => a.OrganizationId == orgId && a.Date >= from && a.Date <= to)
            .OrderBy(a => a.Date)
            .ThenBy(a => a.EmployeeName)
            .ToListAsync();

        return await BuildReportAsync(orgId, records, period, from, to);
    }

    /// <summary>Generate a report from an AttendanceSearchFilter (respects all search filters for export).</summary>
    public async Task<AttendanceReport> GenerateFromFilterAsync(int orgId, AttendanceSearchFilter filter)
    {
        var searchSvc = new AttendanceSearchService(_db);
        var q = searchSvc.BuildFilteredQuery(orgId, filter);

        var records = await q
            .OrderBy(a => a.Date)
            .ThenBy(a => a.EmployeeName)
            .ToListAsync();

        var from = filter.FromDate ?? records.MinBy(r => r.Date)?.Date ?? DateTime.Today;
        var to = filter.ToDate ?? records.MaxBy(r => r.Date)?.Date ?? DateTime.Today;

        return await BuildReportAsync(orgId, records, ReportPeriod.Custom, from, to.AddDays(1).AddTicks(-1));
    }

    /// <summary>Shared implementation that builds the report rows + summary from raw attendance records.</summary>
    private async Task<AttendanceReport> BuildReportAsync(int orgId, List<Attendance> records, ReportPeriod period, DateTime from, DateTime to)
    {
        var employeeIds = records.Select(r => r.EmployeeId).Distinct().ToList();

        var employees = await _db.Employees
            .Where(e => employeeIds.Contains(e.Id))
            .ToDictionaryAsync(e => e.Id);

        // Fetch overtime records for the same period and index by (EmployeeId, Date)
        var overtimeRecords = await _db.OvertimeRecords
            .Where(o => o.OrganizationId == orgId && o.Date >= from && o.Date <= to
                     && o.Status != OvertimeStatus.Rejected)
            .ToListAsync();
        var overtimeByKey = overtimeRecords
            .GroupBy(o => (o.EmployeeId, o.Date.Date))
            .ToDictionary(g => g.Key, g => g.Sum(o => o.Hours));
        var overtimePayByKey = overtimeRecords
            .GroupBy(o => (o.EmployeeId, o.Date.Date))
            .ToDictionary(g => g.Key, g => g.Sum(o => o.Pay));

        var rows = records.Select(r =>
        {
            employees.TryGetValue(r.EmployeeId, out var emp);
            var key = (r.EmployeeId, r.Date.Date);
            overtimeByKey.TryGetValue(key, out var otHours);
            overtimePayByKey.TryGetValue(key, out var otPay);
            return new AttendanceReportRow
            {
                Date = r.Date,
                EmployeeCode = emp?.EmployeeCode ?? "",
                EmployeeName = r.EmployeeName,
                Department = emp?.Department ?? "",
                Branch = emp?.City ?? "",
                CheckIn = r.CheckIn,
                CheckOut = r.CheckOut,
                WorkedHours = r.WorkedHours,
                OvertimeHours = Math.Round(otHours, 2),
                OvertimePay = Math.Round(otPay, 2),
                Status = r.Status.ToString(),
                Notes = r.Notes
            };
        }).ToList();

        return new AttendanceReport
        {
            Period = period,
            FromDate = from,
            ToDate = to,
            Rows = rows,
            Summary = new AttendanceReportSummary
            {
                TotalRecords = rows.Count,
                DistinctEmployees = rows.Select(r => r.EmployeeCode).Distinct().Count(),
                PresentCount = rows.Count(r => r.Status == nameof(AttendanceStatus.Present)),
                LateCount = rows.Count(r => r.Status == nameof(AttendanceStatus.Late)),
                AbsentCount = rows.Count(r => r.Status == nameof(AttendanceStatus.Absent)),
                OnLeaveCount = rows.Count(r => r.Status == nameof(AttendanceStatus.OnLeave)),
                RemoteCount = rows.Count(r => r.Status == nameof(AttendanceStatus.Remote)),
                HalfDayCount = rows.Count(r => r.Status == nameof(AttendanceStatus.HalfDay)),
                TotalWorkedHours = rows.Sum(r => r.WorkedHours),
                TotalOvertimeHours = Math.Round(rows.Sum(r => r.OvertimeHours), 2),
                TotalOvertimePay = Math.Round(rows.Sum(r => r.OvertimePay), 2)
            }
        };
    }

    /// <summary>Resolve the date range based on the report period.</summary>
    private static (DateTime from, DateTime to) ResolveRange(ReportPeriod period, DateTime? fromDate, DateTime? toDate)
    {
        if (fromDate.HasValue && toDate.HasValue)
            return (fromDate.Value.Date, toDate.Value.Date.AddDays(1).AddTicks(-1));

        var today = DateTime.Today;
        return period switch
        {
            ReportPeriod.Daily => (today, today.AddDays(1).AddTicks(-1)),
            ReportPeriod.Weekly => (today.AddDays(-7), today.AddDays(1).AddTicks(-1)),
            ReportPeriod.Monthly => (new DateTime(today.Year, today.Month, 1),
                                       new DateTime(today.Year, today.Month, 1).AddMonths(1).AddTicks(-1)),
            ReportPeriod.Custom => (today.AddDays(-30), today.AddDays(1).AddTicks(-1)),
            _ => (today.AddDays(-30), today.AddDays(1).AddTicks(-1))
        };
    }

    /// <summary>Export report to CSV (FR-010 — exportable to CSV).</summary>
    public byte[] ExportCsv(AttendanceReport report)
    {
        using var ms = new MemoryStream();
        using var writer = new StreamWriter(ms, Encoding.UTF8);
        using var csv = new CsvWriter(writer, CultureInfo.InvariantCulture);

        // Header
        csv.WriteField("Date");
        csv.WriteField("EmployeeCode");
        csv.WriteField("EmployeeName");
        csv.WriteField("Department");
        csv.WriteField("Branch");
        csv.WriteField("CheckIn");
        csv.WriteField("CheckOut");
        csv.WriteField("WorkedHours");
        csv.WriteField("OvertimeHours");
        csv.WriteField("OvertimePay");
        csv.WriteField("Status");
        csv.WriteField("Notes");
        csv.NextRecord();

        // Rows
        foreach (var r in report.Rows)
        {
            csv.WriteField(r.Date.ToString("yyyy-MM-dd"));
            csv.WriteField(r.EmployeeCode);
            csv.WriteField(r.EmployeeName);
            csv.WriteField(r.Department);
            csv.WriteField(r.Branch);
            csv.WriteField(r.CheckIn?.ToString("HH:mm") ?? "");
            csv.WriteField(r.CheckOut?.ToString("HH:mm") ?? "");
            csv.WriteField(r.WorkedHours.ToString("0.00", CultureInfo.InvariantCulture));
            csv.WriteField(r.OvertimeHours.ToString("0.00", CultureInfo.InvariantCulture));
            csv.WriteField(r.OvertimePay.ToString("0.00", CultureInfo.InvariantCulture));
            csv.WriteField(r.Status);
            csv.WriteField(r.Notes ?? "");
            csv.NextRecord();
        }

        writer.Flush();
        return ms.ToArray();
    }

    /// <summary>Export report to XLSX (FR-010 — exportable to Microsoft Excel).</summary>
    public byte[] ExportXlsx(AttendanceReport report)
    {
        using var wb = new XLWorkbook();

        // ─── Sheet 1: Summary ───
        var summaryWs = wb.AddWorksheet("Summary");
        summaryWs.Cell(1, 1).Value = "Ukuu HR — Attendance Report";
        summaryWs.Cell(1, 1).Style.Font.Bold = true;
        summaryWs.Cell(1, 1).Style.Font.FontSize = 16;
        summaryWs.Cell(2, 1).Value = $"Period: {report.Period} ({report.FromDate:yyyy-MM-dd} to {report.ToDate:yyyy-MM-dd})";

        summaryWs.Cell(4, 1).Value = "Metric";
        summaryWs.Cell(4, 2).Value = "Value";
        summaryWs.Cell(4, 1).Style.Font.Bold = true;
        summaryWs.Cell(4, 2).Style.Font.Bold = true;
        summaryWs.Cell(4, 1).Style.Fill.BackgroundColor = XLColor.FromHtml("#25163F");
        summaryWs.Cell(4, 2).Style.Fill.BackgroundColor = XLColor.FromHtml("#25163F");
        summaryWs.Cell(4, 1).Style.Font.FontColor = XLColor.White;
        summaryWs.Cell(4, 2).Style.Font.FontColor = XLColor.White;

        var row = 5;
        summaryWs.Cell(row, 1).Value = "Total Records"; summaryWs.Cell(row++, 2).Value = report.Summary.TotalRecords;
        summaryWs.Cell(row, 1).Value = "Distinct Employees"; summaryWs.Cell(row++, 2).Value = report.Summary.DistinctEmployees;
        summaryWs.Cell(row, 1).Value = "Present"; summaryWs.Cell(row, 2).Value = report.Summary.PresentCount;
        summaryWs.Cell(row, 1).Value = "Late"; summaryWs.Cell(row, 2).Value = report.Summary.LateCount;
        summaryWs.Cell(row, 1).Value = "Absent"; summaryWs.Cell(row, 2).Value = report.Summary.AbsentCount;
        summaryWs.Cell(row, 1).Value = "On Leave"; summaryWs.Cell(row, 2).Value = report.Summary.OnLeaveCount;
        summaryWs.Cell(row, 1).Value = "Remote"; summaryWs.Cell(row, 2).Value = report.Summary.RemoteCount;
        summaryWs.Cell(row, 1).Value = "Half Day"; summaryWs.Cell(row, 2).Value = report.Summary.HalfDayCount;
        summaryWs.Cell(row, 1).Value = "Total Worked Hours"; summaryWs.Cell(row, 2).Value = Math.Round(report.Summary.TotalWorkedHours, 2);
        summaryWs.Cell(row, 1).Value = "Total Overtime Hours"; summaryWs.Cell(row++, 2).Value = Math.Round(report.Summary.TotalOvertimeHours, 2);
        summaryWs.Cell(row, 1).Value = "Total Overtime Pay"; summaryWs.Cell(row, 2).Value = Math.Round(report.Summary.TotalOvertimePay, 2);

        summaryWs.Columns().AdjustToContents();

        // ─── Sheet 2: Detail ───
        var detailWs = wb.AddWorksheet("Detail");
        var headers = new[] { "Date", "EmployeeCode", "EmployeeName", "Department", "Branch", "CheckIn", "CheckOut", "WorkedHours", "OvertimeHours", "OvertimePay", "Status", "Notes" };
        for (int i = 0; i < headers.Length; i++)
        {
            detailWs.Cell(1, i + 1).Value = headers[i];
            detailWs.Cell(1, i + 1).Style.Font.Bold = true;
            detailWs.Cell(1, i + 1).Style.Fill.BackgroundColor = XLColor.FromHtml("#25163F");
            detailWs.Cell(1, i + 1).Style.Font.FontColor = XLColor.White;
        }

        for (int i = 0; i < report.Rows.Count; i++)
        {
            var r = report.Rows[i];
            var x = i + 2;
            detailWs.Cell(x, 1).Value = r.Date.ToString("yyyy-MM-dd");
            detailWs.Cell(x, 2).Value = r.EmployeeCode;
            detailWs.Cell(x, 3).Value = r.EmployeeName;
            detailWs.Cell(x, 4).Value = r.Department;
            detailWs.Cell(x, 5).Value = r.Branch;
            detailWs.Cell(x, 6).Value = r.CheckIn?.ToString("HH:mm") ?? "";
            detailWs.Cell(x, 7).Value = r.CheckOut?.ToString("HH:mm") ?? "";
            detailWs.Cell(x, 8).Value = Math.Round(r.WorkedHours, 2);
            detailWs.Cell(x, 9).Value = Math.Round(r.OvertimeHours, 2);
            detailWs.Cell(x, 10).Value = Math.Round(r.OvertimePay, 2);
            detailWs.Cell(x, 11).Value = r.Status;
            detailWs.Cell(x, 12).Value = r.Notes ?? "";

            // Color-code the Status cell.
            var statusCell = detailWs.Cell(x, 9);
            statusCell.Style.Fill.BackgroundColor = r.Status switch
            {
                "Present" => XLColor.FromHtml("#DCFCE7"),
                "Late" => XLColor.FromHtml("#FEF3C7"),
                "Absent" => XLColor.FromHtml("#FEE2E2"),
                "OnLeave" => XLColor.FromHtml("#DBEAFE"),
                "Remote" => XLColor.FromHtml("#E0E7FF"),
                "HalfDay" => XLColor.FromHtml("#FEF3C7"),
                _ => XLColor.White
            };
        }

        detailWs.Columns().AdjustToContents();
        detailWs.SheetView.FreezeRows(1);

        using var ms = new MemoryStream();
        wb.SaveAs(ms);
        return ms.ToArray();
    }
}

/// <summary>Report period types per FRS FR-010.</summary>
public enum ReportPeriod
{
    Daily,
    Weekly,
    Monthly,
    Custom
}

/// <summary>Generated attendance report.</summary>
public class AttendanceReport
{
    public ReportPeriod Period { get; set; }
    public DateTime FromDate { get; set; }
    public DateTime ToDate { get; set; }
    public List<AttendanceReportRow> Rows { get; set; } = new();
    public AttendanceReportSummary Summary { get; set; } = new();
}

public class AttendanceReportRow
{
    public DateTime Date { get; set; }
    public string EmployeeCode { get; set; } = "";
    public string EmployeeName { get; set; } = "";
    public string Department { get; set; } = "";
    public string Branch { get; set; } = "";
    public DateTime? CheckIn { get; set; }
    public DateTime? CheckOut { get; set; }
    public double WorkedHours { get; set; }
    /// <summary>Overtime hours for this attendance date.</summary>
    public double OvertimeHours { get; set; }
    /// <summary>Overtime pay amount for this attendance date.</summary>
    public double OvertimePay { get; set; }
    public string Status { get; set; } = "";
    public string? Notes { get; set; }
}

public class AttendanceReportSummary
{
    public int TotalRecords { get; set; }
    public int DistinctEmployees { get; set; }
    public int PresentCount { get; set; }
    public int LateCount { get; set; }
    public int AbsentCount { get; set; }
    public int OnLeaveCount { get; set; }
    public int RemoteCount { get; set; }
    public int HalfDayCount { get; set; }
    public double TotalWorkedHours { get; set; }
    public double TotalOvertimeHours { get; set; }
    public double TotalOvertimePay { get; set; }
}
