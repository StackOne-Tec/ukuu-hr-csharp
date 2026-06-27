using Microsoft.EntityFrameworkCore;
using UkuuHr.Data;
using UkuuHr.Models;

namespace UkuuHr.Services;

public class EmployeeService
{
    private readonly UkuuHrDbContext _db;
    public EmployeeService(UkuuHrDbContext db) => _db = db;

    public Task<List<Employee>> GetAllAsync(int orgId, string? search = null, string? department = null, EmploymentStatus? status = null)
    {
        var q = _db.Employees.Where(e => e.OrganizationId == orgId);
        if (!string.IsNullOrWhiteSpace(search))
        {
            var s = search.Trim().ToLower();
            q = q.Where(e =>
                (e.FirstName + " " + e.Surname).ToLower().Contains(s) ||
                (e.Email ?? "").ToLower().Contains(s) ||
                (e.EmployeeCode ?? "").ToLower().Contains(s) ||
                (e.JobTitle ?? "").ToLower().Contains(s));
        }
        if (!string.IsNullOrWhiteSpace(department))
            q = q.Where(e => e.Department == department);
        if (status.HasValue) q = q.Where(e => e.Status == status);
        return q.OrderByDescending(e => e.CreatedAt).ToListAsync();
    }

    public Task<List<string>> GetDepartmentsAsync(int orgId) =>
        _db.Employees.Where(e => e.OrganizationId == orgId && e.Department != null)
            .Select(e => e.Department!).Distinct().OrderBy(d => d).ToListAsync();

    public Task<Employee?> GetAsync(int orgId, int id) =>
        _db.Employees.FirstOrDefaultAsync(e => e.OrganizationId == orgId && e.Id == id);

    public async Task<Employee> CreateAsync(Employee emp)
    {
        emp.CreatedAt = DateTime.UtcNow;
        _db.Employees.Add(emp);
        await _db.SaveChangesAsync();
        return emp;
    }

    public async Task<Employee> UpdateAsync(Employee emp)
    {
        emp.UpdatedAt = DateTime.UtcNow;
        _db.Employees.Update(emp);
        await _db.SaveChangesAsync();
        return emp;
    }

    public async Task<bool> DeleteAsync(int orgId, int id)
    {
        var emp = await _db.Employees.FirstOrDefaultAsync(e => e.OrganizationId == orgId && e.Id == id);
        if (emp == null) return false;
        _db.Employees.Remove(emp);
        await _db.SaveChangesAsync();
        return true;
    }

    public Task<int> CountAsync(int orgId) =>
        _db.Employees.CountAsync(e => e.OrganizationId == orgId);

    public Task<int> CountByStatusAsync(int orgId, EmploymentStatus status) =>
        _db.Employees.CountAsync(e => e.OrganizationId == orgId && e.Status == status);

    public async Task<double> TotalPayrollAsync(int orgId)
    {
        var employees = await _db.Employees
            .Where(e => e.OrganizationId == orgId && e.Status != EmploymentStatus.Inactive)
            .ToListAsync();
        return employees.Sum(e => e.GrossSalary);
    }

    public async Task<Dictionary<string, int>> ByDepartmentAsync(int orgId) =>
        await _db.Employees.Where(e => e.OrganizationId == orgId && e.Department != null)
            .GroupBy(e => e.Department!)
            .Select(g => new { Dept = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.Dept, x => x.Count);
}

public class AttendanceService
{
    private readonly UkuuHrDbContext _db;
    public AttendanceService(UkuuHrDbContext db) => _db = db;

    public Task<List<Attendance>> ForDateAsync(int orgId, DateTime date) =>
        _db.Attendances.Where(a => a.OrganizationId == orgId && a.DateKey == date.ToString("yyyy-MM-dd"))
            .OrderBy(a => a.EmployeeName).ToListAsync();

    public Task<List<Attendance>> ForRangeAsync(int orgId, DateTime from, DateTime to) =>
        _db.Attendances.Where(a => a.OrganizationId == orgId && a.Date >= from && a.Date <= to)
            .OrderByDescending(a => a.Date).ThenBy(a => a.EmployeeName).ToListAsync();

    public async Task<Attendance?> ClockAsync(int orgId, int employeeId, bool clockIn)
    {
        var today = DateTime.UtcNow.Date;
        var key = today.ToString("yyyy-MM-dd");
        var att = await _db.Attendances.FirstOrDefaultAsync(a => a.OrganizationId == orgId && a.EmployeeId == employeeId && a.DateKey == key);
        var emp = await _db.Employees.FirstOrDefaultAsync(e => e.Id == employeeId);
        if (emp == null) return null;

        if (att == null)
        {
            att = new Attendance
            {
                OrganizationId = orgId, EmployeeId = employeeId, EmployeeName = emp.FullName,
                DateKey = key, Date = today, Status = AttendanceStatus.Present, Source = AttendanceSource.Clock,
                BreakMinutes = 60, CreatedAt = DateTime.UtcNow,
                CheckIn = clockIn ? DateTime.UtcNow : null
            };
            _db.Attendances.Add(att);
        }
        else if (clockIn) att.CheckIn = DateTime.UtcNow;
        else att.CheckOut = DateTime.UtcNow;

        await _db.SaveChangesAsync();
        return att;
    }

    public Task<double> AttendanceRateAsync(int orgId, DateTime date)
    {
        var key = date.ToString("yyyy-MM-dd");
        return _db.Attendances.Where(a => a.OrganizationId == orgId && a.DateKey == key && a.Status != AttendanceStatus.Absent)
            .CountAsync().ContinueWith(t => (double)t.Result);
    }

    public async Task<Dictionary<AttendanceStatus, int>> BreakdownAsync(int orgId, DateTime date)
    {
        var key = date.ToString("yyyy-MM-dd");
        return await _db.Attendances.Where(a => a.OrganizationId == orgId && a.DateKey == key)
            .GroupBy(a => a.Status)
            .Select(g => new { Status = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.Status, x => x.Count);
    }
}

public class LeaveService
{
    private readonly UkuuHrDbContext _db;
    public LeaveService(UkuuHrDbContext db) => _db = db;

    public Task<List<LeaveRequest>> AllAsync(int orgId, LeaveRequestStatus? status = null)
    {
        var q = _db.LeaveRequests.Where(l => l.OrganizationId == orgId);
        if (status.HasValue) q = q.Where(l => l.Status == status);
        return q.OrderByDescending(l => l.CreatedAt).ToListAsync();
    }

    public async Task<LeaveRequest> CreateAsync(LeaveRequest req)
    {
        req.CreatedAt = DateTime.UtcNow;
        req.Status = LeaveRequestStatus.Pending;
        _db.LeaveRequests.Add(req);
        await _db.SaveChangesAsync();
        return req;
    }

    public async Task<bool> ReviewAsync(int orgId, int id, bool approve, string reviewerEmail, string? notes = null)
    {
        var lr = await _db.LeaveRequests.FirstOrDefaultAsync(l => l.OrganizationId == orgId && l.Id == id);
        if (lr == null) return false;
        lr.Status = approve ? LeaveRequestStatus.Approved : LeaveRequestStatus.Rejected;
        lr.ReviewedAt = DateTime.UtcNow;
        lr.ReviewedByEmail = reviewerEmail;
        if (approve) lr.ApproverNotes = notes; else lr.RejectionReason = notes;
        await _db.SaveChangesAsync();
        return true;
    }

    public Task<List<LeaveType>> GetLeaveTypesAsync(int orgId) =>
        _db.LeaveTypes.Where(l => l.OrganizationId == orgId).OrderBy(l => l.Name).ToListAsync();
}

public class PayrollService
{
    private readonly UkuuHrDbContext _db;
    public PayrollService(UkuuHrDbContext db) => _db = db;

    public Task<List<PayrollRun>> ForPeriodAsync(int orgId, int month, int year) =>
        _db.PayrollRuns.Where(p => p.OrganizationId == orgId && p.Month == month && p.Year == year)
            .OrderBy(p => p.EmployeeName).ToListAsync();

    public Task<List<PayrollRun>> PendingApprovalsAsync(int orgId) =>
        _db.PayrollRuns.Where(p => p.OrganizationId == orgId && p.ApprovalStatus == PayrollApprovalStatus.Pending)
            .OrderByDescending(p => p.CreatedAt).ToListAsync();

    public async Task<PayrollRun> CreateAsync(PayrollRun run)
    {
        run.CreatedAt = DateTime.UtcNow;
        _db.PayrollRuns.Add(run);
        await _db.SaveChangesAsync();
        return run;
    }

    public async Task<bool> ApproveAsync(int orgId, int id, string approverEmail, string? notes = null)
    {
        var p = await _db.PayrollRuns.FirstOrDefaultAsync(x => x.OrganizationId == orgId && x.Id == id);
        if (p == null) return false;
        p.ApprovalStatus = PayrollApprovalStatus.Approved;
        p.Status = PayrollStatus.Approved;
        p.ApprovedByEmail = approverEmail;
        p.ApprovedAt = DateTime.UtcNow;
        p.ApproverNotes = notes;
        await _db.SaveChangesAsync();
        return true;
    }

    public async Task<bool> RejectAsync(int orgId, int id, string rejectorEmail, string reason)
    {
        var p = await _db.PayrollRuns.FirstOrDefaultAsync(x => x.OrganizationId == orgId && x.Id == id);
        if (p == null) return false;
        p.ApprovalStatus = PayrollApprovalStatus.Rejected;
        p.Status = PayrollStatus.Rejected;
        p.RejectedByEmail = rejectorEmail;
        p.RejectedAt = DateTime.UtcNow;
        p.RejectionReason = reason;
        await _db.SaveChangesAsync();
        return true;
    }

    public async Task<List<PayrollRun>> GenerateBatchForPeriodAsync(int orgId, int month, int year, string generatorEmail)
    {
        var cfg = PayrollCountryConfig.Zambia();
        var payStart = new DateTime(year, month, 1);
        var payEnd = payStart.AddMonths(1).AddDays(-1);
        var batchId = $"BATCH-{payStart:yyyyMM}";

        // Check if batch already exists
        var existing = await _db.PayrollRuns.AnyAsync(p => p.OrganizationId == orgId && p.BatchId == batchId);
        if (existing) return await ForPeriodAsync(orgId, month, year);

        var employees = await _db.Employees.Where(e => e.OrganizationId == orgId && e.Status != EmploymentStatus.Inactive).ToListAsync();
        var runs = new List<PayrollRun>();

        foreach (var emp in employees)
        {
            var allowances = emp.Allowances.Select(a => new AllowanceInput
            {
                Name = a.Name, Amount = a.Amount,
                Type = a.Type == AllowanceType.Percentage ? AllowanceTypeInput.Percentage : AllowanceTypeInput.Fixed,
                Taxable = a.Taxable
            }).ToList();

            var calc = PayrollCalculator.Calculate(emp.BasicSalary, allowances, countryConfig: cfg);

            var run = new PayrollRun
            {
                OrganizationId = orgId,
                EmployeeId = emp.Id,
                EmployeeName = emp.FullName,
                BatchId = batchId,
                Month = month, Year = year,
                PayPeriodStart = payStart, PayPeriodEnd = payEnd,
                Status = PayrollStatus.PendingApproval,
                ApprovalStatus = PayrollApprovalStatus.Pending,
                Base = calc.Basic,
                Allowances = calc.TaxableAllowances + calc.NonTaxableAllowances,
                NonTaxableAllowances = calc.NonTaxableAllowances,
                Paye = Math.Round(calc.Paye, 2),
                Napsa = Math.Round(calc.Napsa, 2),
                Nhima = Math.Round(calc.Nhima, 2),
                PayePercent = calc.EffectivePayePercent,
                NapsaPercent = cfg.NapsaPercent,
                NhimaPercent = cfg.NhimaPercent,
                Currency = emp.DisplayCurrency,
                SubmittedByUserId = "system", SubmittedByEmail = generatorEmail,
                SubmittedAt = DateTime.UtcNow,
                CreatedByUserId = "system",
                CreatedAt = DateTime.UtcNow
            };
            runs.Add(run);
            _db.PayrollRuns.Add(run);
        }

        await _db.SaveChangesAsync();
        return runs;
    }

    public async Task<PayrollMonthlyStats> GetMonthlyStatsAsync(int orgId, int month, int year)
    {
        var runs = await _db.PayrollRuns.Where(p => p.OrganizationId == orgId && p.Month == month && p.Year == year).ToListAsync();
        return new PayrollMonthlyStats
        {
            TotalGross = runs.Sum(p => p.Gross),
            TotalNet = runs.Sum(p => p.Net),
            TotalPaye = runs.Sum(p => p.Paye),
            TotalNapsa = runs.Sum(p => p.Napsa),
            TotalNhima = runs.Sum(p => p.Nhima),
            Count = runs.Count,
            Pending = runs.Count(p => p.ApprovalStatus == PayrollApprovalStatus.Pending)
        };
    }
}

public class PayrollMonthlyStats
{
    public double TotalGross { get; set; }
    public double TotalNet { get; set; }
    public double TotalPaye { get; set; }
    public double TotalNapsa { get; set; }
    public double TotalNhima { get; set; }
    public int Count { get; set; }
    public int Pending { get; set; }
}

public class AuditService
{
    private readonly UkuuHrDbContext _db;
    public AuditService(UkuuHrDbContext db) => _db = db;

    public Task<List<AuditLog>> RecentAsync(int orgId, int take = 50) =>
        _db.AuditLogs.Where(a => a.OrganizationId == orgId)
            .OrderByDescending(a => a.Timestamp).Take(take).ToListAsync();

    public async Task LogAsync(int orgId, AuditAction action, string? performedByEmail, string? details = null,
        string? targetUserEmail = null, string? previousValue = null, string? newValue = null)
    {
        _db.AuditLogs.Add(new AuditLog
        {
            OrganizationId = orgId,
            Action = action,
            PerformedByEmail = performedByEmail,
            TargetUserEmail = targetUserEmail,
            Details = details,
            PreviousValue = previousValue,
            NewValue = newValue,
            Timestamp = DateTime.UtcNow
        });
        await _db.SaveChangesAsync();
    }
}
