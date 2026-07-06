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

    // ───────────── Queries ─────────────

    public Task<List<LeaveRequest>> AllAsync(int orgId, LeaveRequestStatus? status = null)
    {
        var q = _db.LeaveRequests.Where(l => l.OrganizationId == orgId);
        if (status.HasValue) q = q.Where(l => l.Status == status);
        return q.OrderByDescending(l => l.CreatedAt).ToListAsync();
    }

    /// <summary>Get leave requests for a specific employee (self-service).</summary>
    public Task<List<LeaveRequest>> ForEmployeeAsync(int orgId, int employeeId)
    {
        return _db.LeaveRequests
            .Where(l => l.OrganizationId == orgId && l.EmployeeId == employeeId)
            .OrderByDescending(l => l.CreatedAt)
            .ToListAsync();
    }

    /// <summary>Get a single leave request.</summary>
    public Task<LeaveRequest?> GetAsync(int orgId, int id) =>
        _db.LeaveRequests.FirstOrDefaultAsync(l => l.OrganizationId == orgId && l.Id == id);

    public Task<List<LeaveType>> GetLeaveTypesAsync(int orgId) =>
        _db.LeaveTypes.Where(l => l.OrganizationId == orgId).OrderBy(l => l.Name).ToListAsync();

    // ───────────── Leave Balances ─────────────

    /// <summary>Get or initialize leave balances for an employee for the current year.</summary>
    public async Task<List<LeaveBalance>> GetOrCreateBalancesAsync(int orgId, int employeeId, int? year = null)
    {
        var yr = year ?? DateTime.UtcNow.Year;
        var leaveTypes = await _db.LeaveTypes.Where(lt => lt.OrganizationId == orgId).ToListAsync();
        var existingBalances = await _db.LeaveBalances
            .Include(lb => lb.LeaveType)
            .Where(lb => lb.OrganizationId == orgId && lb.EmployeeId == employeeId && lb.Year == yr)
            .ToListAsync();

        // Track whether we added any new balances
        var addedNew = false;

        // Create any missing balances
        foreach (var lt in leaveTypes)
        {
            if (!existingBalances.Any(b => b.LeaveTypeId == lt.Id))
            {
                var balance = new LeaveBalance
                {
                    OrganizationId = orgId,
                    EmployeeId = employeeId,
                    LeaveTypeId = lt.Id,
                    Year = yr,
                    EntitlementDays = lt.DefaultDays,
                    UsedDays = 0,
                    CarriedForwardDays = 0,
                    AdjustedDays = 0,
                    CreatedAt = DateTime.UtcNow
                };
                _db.LeaveBalances.Add(balance);
                existingBalances.Add(balance);
                addedNew = true;
            }
        }

        if (addedNew)
            await _db.SaveChangesAsync();

        return existingBalances.OrderBy(b => b.LeaveTypeId).ToList();
    }

    // ───────────── Mutations ─────────────

    /// <summary>Create a new leave request (employee self-service).</summary>
    public async Task<LeaveRequest> CreateAsync(LeaveRequest req)
    {
        req.CreatedAt = DateTime.UtcNow;
        req.Status = LeaveRequestStatus.Pending;
        _db.LeaveRequests.Add(req);
        await _db.SaveChangesAsync();
        return req;
    }

    /// <summary>
    /// Review (approve/reject) a leave request. On approval:
    /// 1. Updates the request status
    /// 2. Deducts from the employee's leave balance
    /// 3. Creates Attendance records with OnLeave status for the leave period
    /// </summary>
    public async Task<bool> ReviewAsync(int orgId, int id, bool approve, string reviewerEmail, string? notes = null)
    {
        var lr = await _db.LeaveRequests.FirstOrDefaultAsync(l => l.OrganizationId == orgId && l.Id == id);
        if (lr == null) return false;

        lr.Status = approve ? LeaveRequestStatus.Approved : LeaveRequestStatus.Rejected;
        lr.ReviewedAt = DateTime.UtcNow;
        lr.ReviewedByEmail = reviewerEmail;
        if (approve) lr.ApproverNotes = notes; else lr.RejectionReason = notes;

        if (approve)
        {
            // 1. Validate sufficient balance before approving
            await ValidateSufficientBalanceAsync(orgId, lr);

            // 2. Deduct from leave balance
            await DeductBalanceAsync(orgId, lr);

            // 3. Create Attendance records with OnLeave status for the leave period
            await CreateLeaveAttendanceRecordsAsync(orgId, lr);
        }

        await _db.SaveChangesAsync();
        return true;
    }

    /// <summary>Cancel a leave request (employee self-service, only if still pending).</summary>
    public async Task<bool> CancelAsync(int orgId, int id, string? userId = null)
    {
        var lr = await _db.LeaveRequests.FirstOrDefaultAsync(l => l.OrganizationId == orgId && l.Id == id);
        if (lr == null || lr.Status != LeaveRequestStatus.Pending) return false;

        lr.Status = LeaveRequestStatus.Cancelled;
        lr.ReviewedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        return true;
    }

    // ───────────── Internal Helpers ─────────────

    /// <summary>Get holiday dates in the leave period for holiday-aware calculation.</summary>
    private async Task<HashSet<DateTime>> GetLeaveHolidayDatesAsync(int orgId, LeaveRequest lr)
    {
        return (await _db.LeaveHolidays
            .Where(h => h.OrganizationId == orgId && h.Date >= lr.StartDate && h.Date <= lr.EndDate)
            .Select(h => h.Date.Date)
            .ToListAsync()).ToHashSet();
    }

    /// <summary>Validate that the employee has sufficient balance for the requested leave days.
    /// FR-008: Holidays within the leave period are excluded from the day count.</summary>
    private async Task ValidateSufficientBalanceAsync(int orgId, LeaveRequest lr)
    {
        var year = lr.StartDate.Year;
        var holidayDates = await GetLeaveHolidayDatesAsync(orgId, lr);
        var netDays = LeaveRequest.CalculateBusinessDays(lr.StartDate, lr.EndDate, holidayDates);

        var balance = await _db.LeaveBalances
            .FirstOrDefaultAsync(b => b.OrganizationId == orgId
                                   && b.EmployeeId == lr.EmployeeId
                                   && b.LeaveTypeId == lr.LeaveTypeId
                                   && b.Year == year);

        if (balance != null && balance.RemainingDays < netDays)
        {
            throw new InvalidOperationException(
                $"Insufficient leave balance for '{lr.LeaveTypeName}'. " +
                $"Requested {netDays} day(s) (after excluding {holidayDates.Count} holidays) " +
                $"but only {balance.RemainingDays:0.#} remaining.");
        }
    }

    /// <summary>Deduct the leave balance when a request is approved.
    /// FR-008: Holidays within the leave period are excluded from the deduction.</summary>
    private async Task DeductBalanceAsync(int orgId, LeaveRequest lr)
    {
        var year = lr.StartDate.Year;
        var holidayDates = await GetLeaveHolidayDatesAsync(orgId, lr);
        var netDays = LeaveRequest.CalculateBusinessDays(lr.StartDate, lr.EndDate, holidayDates);

        var balance = await _db.LeaveBalances
            .FirstOrDefaultAsync(b => b.OrganizationId == orgId
                                   && b.EmployeeId == lr.EmployeeId
                                   && b.LeaveTypeId == lr.LeaveTypeId
                                   && b.Year == year);

        if (balance == null)
        {
            // Auto-create balance if it doesn't exist
            var leaveType = await _db.LeaveTypes.FindAsync(lr.LeaveTypeId);
            balance = new LeaveBalance
            {
                OrganizationId = orgId,
                EmployeeId = lr.EmployeeId,
                LeaveTypeId = lr.LeaveTypeId,
                Year = year,
                EntitlementDays = leaveType?.DefaultDays ?? 0,
                UsedDays = 0,
                CarriedForwardDays = 0,
                AdjustedDays = 0,
                CreatedAt = DateTime.UtcNow
            };
            _db.LeaveBalances.Add(balance);
        }

        // FR-008: Deduct only net business days (excluding holidays)
        balance.UsedDays += netDays;

        // Store the holiday count for reference
        lr.HolidayDays = holidayDates.Count(h => h >= lr.StartDate.Date && h <= lr.EndDate.Date);
    }

    /// <summary>Create attendance records with OnLeave status for the approved leave period.
    /// Skips weekends, holidays (FR-008), and existing attendance records.</summary>
    private async Task CreateLeaveAttendanceRecordsAsync(int orgId, LeaveRequest lr)
    {
        var employee = await _db.Employees.FindAsync(lr.EmployeeId);
        if (employee == null) return;

        // FR-008: Fetch holidays so they are excluded from leave attendance
        var holidayDates = (await _db.LeaveHolidays
            .Where(h => h.OrganizationId == orgId && h.Date >= lr.StartDate && h.Date <= lr.EndDate)
            .Select(h => h.Date.Date)
            .ToListAsync()).ToHashSet();

        var existingDates = await _db.Attendances
            .Where(a => a.OrganizationId == orgId && a.EmployeeId == lr.EmployeeId
                     && a.Date >= lr.StartDate && a.Date <= lr.EndDate)
            .Select(a => a.DateKey)
            .ToHashSetAsync();

        var recordsToAdd = new List<Attendance>();
        for (var d = lr.StartDate.Date; d <= lr.EndDate.Date; d = d.AddDays(1))
        {
            // Skip weekends
            if (d.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday)
                continue;

            // FR-008: Skip public holidays
            if (holidayDates.Contains(d))
                continue;

            var dateKey = d.ToString("yyyy-MM-dd");

            // Skip if attendance record already exists (e.g., they clocked in before leave was approved)
            if (existingDates.Contains(dateKey))
                continue;

            recordsToAdd.Add(new Attendance
            {
                OrganizationId = orgId,
                EmployeeId = lr.EmployeeId,
                EmployeeName = employee.FullName,
                DateKey = dateKey,
                Date = d,
                Status = AttendanceStatus.OnLeave,
                Source = AttendanceSource.System,
                Notes = $"Approved leave: {lr.LeaveTypeName}",
                BreakMinutes = 0,
                CreatedAt = DateTime.UtcNow
            });
        }

        if (recordsToAdd.Count > 0)
        {
            _db.Attendances.AddRange(recordsToAdd);
        }
    }
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

    /// <summary>Bulk-approve all pending payroll runs for an org (single DB round-trip via ExecuteUpdate).</summary>
    public async Task<int> BulkApproveAllAsync(int orgId, string approverEmail, string? notes = null)
    {
        var now = DateTime.UtcNow;
        return await _db.PayrollRuns
            .Where(p => p.OrganizationId == orgId && p.ApprovalStatus == PayrollApprovalStatus.Pending)
            .ExecuteUpdateAsync(s => s
                .SetProperty(p => p.ApprovalStatus, PayrollApprovalStatus.Approved)
                .SetProperty(p => p.Status, PayrollStatus.Approved)
                .SetProperty(p => p.ApprovedByEmail, approverEmail)
                .SetProperty(p => p.ApprovedAt, now)
                .SetProperty(p => p.ApproverNotes, notes ?? "Batch approved."));
    }

    /// <summary>Bulk-approve all pending payroll runs in a specific batch (single DB round-trip).</summary>
    public async Task<int> BulkApproveBatchAsync(int orgId, string batchId, string approverEmail, string? notes = null)
    {
        var now = DateTime.UtcNow;
        return await _db.PayrollRuns
            .Where(p => p.OrganizationId == orgId && p.BatchId == batchId && p.ApprovalStatus == PayrollApprovalStatus.Pending)
            .ExecuteUpdateAsync(s => s
                .SetProperty(p => p.ApprovalStatus, PayrollApprovalStatus.Approved)
                .SetProperty(p => p.Status, PayrollStatus.Approved)
                .SetProperty(p => p.ApprovedByEmail, approverEmail)
                .SetProperty(p => p.ApprovedAt, now)
                .SetProperty(p => p.ApproverNotes, notes ?? $"Batch {batchId} approved."));
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
