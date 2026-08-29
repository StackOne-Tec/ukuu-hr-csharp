using Microsoft.EntityFrameworkCore;
using UkuuHr.Data;
using UkuuHr.Models;

namespace UkuuHr.Services;

public class EmployeeService
{
    private readonly UkuuHrDbContext _db;
    private readonly AesEncryptionService _cipher;
    public EmployeeService(UkuuHrDbContext db, AesEncryptionService cipher)
    {
        _db = db;
        _cipher = cipher;
    }

    /// <summary>Encrypt sensitive fields before persisting to the database.</summary>
    private void EncryptSensitiveFields(Employee emp)
    {
        emp.AccountNumber = _cipher.Encrypt(emp.AccountNumber);
        emp.Tpin = _cipher.Encrypt(emp.Tpin);
        emp.NapsaNumber = _cipher.Encrypt(emp.NapsaNumber);
        emp.HealthInsuranceNumber = _cipher.Encrypt(emp.HealthInsuranceNumber);
        emp.MobileMoney = _cipher.Encrypt(emp.MobileMoney);
        emp.PassportNumber = _cipher.Encrypt(emp.PassportNumber);
        emp.NationalIdentityNumber = _cipher.Encrypt(emp.NationalIdentityNumber);
        emp.BankName = _cipher.Encrypt(emp.BankName);
        emp.BeneficiaryName = _cipher.Encrypt(emp.BeneficiaryName);
        emp.RoutingNumbers = _cipher.Encrypt(emp.RoutingNumbers);
        emp.SwiftCode = _cipher.Encrypt(emp.SwiftCode);
        emp.IbanNumber = _cipher.Encrypt(emp.IbanNumber);
    }

    /// <summary>Decrypt sensitive fields after reading from the database.</summary>
    private void DecryptSensitiveFields(Employee emp)
    {
        emp.AccountNumber = _cipher.Decrypt(emp.AccountNumber);
        emp.Tpin = _cipher.Decrypt(emp.Tpin);
        emp.NapsaNumber = _cipher.Decrypt(emp.NapsaNumber);
        emp.HealthInsuranceNumber = _cipher.Decrypt(emp.HealthInsuranceNumber);
        emp.MobileMoney = _cipher.Decrypt(emp.MobileMoney);
        emp.PassportNumber = _cipher.Decrypt(emp.PassportNumber);
        emp.NationalIdentityNumber = _cipher.Decrypt(emp.NationalIdentityNumber);
        emp.BankName = _cipher.Decrypt(emp.BankName);
        emp.BeneficiaryName = _cipher.Decrypt(emp.BeneficiaryName);
        emp.RoutingNumbers = _cipher.Decrypt(emp.RoutingNumbers);
        emp.SwiftCode = _cipher.Decrypt(emp.SwiftCode);
        emp.IbanNumber = _cipher.Decrypt(emp.IbanNumber);
    }

    public async Task<List<Employee>> GetAllAsync(int orgId, string? search = null, string? department = null, EmploymentStatus? status = null)
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
        var employees = await q.OrderByDescending(e => e.CreatedAt).ToListAsync();
        foreach (var emp in employees) DecryptSensitiveFields(emp);
        return employees;
    }

    public Task<List<string>> GetDepartmentsAsync(int orgId) =>
        _db.Employees.Where(e => e.OrganizationId == orgId && e.Department != null)
            .Select(e => e.Department!).Distinct().OrderBy(d => d).ToListAsync();

    public async Task<Employee?> GetAsync(int orgId, int id)
    {
        var emp = await _db.Employees.FirstOrDefaultAsync(e => e.OrganizationId == orgId && e.Id == id);
        if (emp != null) DecryptSensitiveFields(emp);
        return emp;
    }

    public async Task<Employee> CreateAsync(Employee emp)
    {
        EncryptSensitiveFields(emp);
        emp.CreatedAt = DateTime.UtcNow;
        _db.Employees.Add(emp);
        await _db.SaveChangesAsync();
        return emp;
    }

    public async Task<Employee> UpdateAsync(Employee emp)
    {
        EncryptSensitiveFields(emp);
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

    /// <summary>
    /// Import employees from CSV. Expected columns: FirstName, Surname, Email, EmployeeCode,
    /// Department, JobTitle, Phone, BasicSalary, Gender, JoiningDate.
    /// All other fields are optional. Returns (imported, skipped, errors).
    /// </summary>
    public async Task<(int imported, int skipped, List<string> errors)> ImportCsvAsync(int orgId, Stream csvStream)
    {
        var imported = 0;
        var skipped = 0;
        var errors = new List<string>();

        using var reader = new StreamReader(csvStream, System.Text.Encoding.UTF8);
        using var csv = new CsvHelper.CsvReader(reader, System.Globalization.CultureInfo.InvariantCulture);

        await csv.ReadAsync();
        csv.ReadHeader();
        var headers = csv.HeaderRecord?.Select(h => h.Trim().ToLower()).ToList() ?? new();

        // Validate required columns
        var required = new[] { "firstname", "surname" };
        foreach (var col in required)
        {
            if (!headers.Contains(col))
            {
                errors.Add($"Missing required column: {col}");
                return (0, 0, errors);
            }
        }

        // Get existing employee codes to prevent duplicates
        var existingCodes = await _db.Employees
            .Where(e => e.OrganizationId == orgId && e.EmployeeCode != null)
            .Select(e => e.EmployeeCode!)
            .ToListAsync();
        var codeSet = existingCodes.ToHashSet(StringComparer.OrdinalIgnoreCase);

        var row = 1;
        while (await csv.ReadAsync())
        {
            row++;
            try
            {
                var firstName = csv.GetField<string>("FirstName")?.Trim();
                var surname = csv.GetField<string>("Surname")?.Trim();

                if (string.IsNullOrWhiteSpace(firstName) || string.IsNullOrWhiteSpace(surname))
                {
                    errors.Add($"Row {row}: FirstName and Surname are required.");
                    skipped++;
                    continue;
                }

                var email = csv.GetField<string>("Email")?.Trim();
                var employeeCode = csv.GetField<string>("EmployeeCode")?.Trim();
                var department = csv.GetField<string>("Department")?.Trim();
                var jobTitle = csv.GetField<string>("JobTitle")?.Trim();
                var phone = csv.GetField<string>("Phone")?.Trim();
                var gender = csv.GetField<string>("Gender")?.Trim();
                var basicSalaryStr = csv.GetField<string>("BasicSalary")?.Trim();
                var joiningDateStr = csv.GetField<string>("JoiningDate")?.Trim();

                // Generate employee code if not provided
                if (string.IsNullOrWhiteSpace(employeeCode))
                    employeeCode = $"EMP-{orgId}-{(codeSet.Count + imported + skipped + 1):D4}";

                // Skip if code already exists
                if (codeSet.Contains(employeeCode))
                {
                    skipped++;
                    continue;
                }

                var emp = new Employee
                {
                    OrganizationId = orgId,
                    FirstName = firstName,
                    Surname = surname,
                    Email = email,
                    EmployeeCode = employeeCode,
                    Department = department,
                    JobTitle = jobTitle,
                    Phone = phone,
                    Gender = gender,
                    Status = EmploymentStatus.Active,
                    CreatedAt = DateTime.UtcNow
                };

                if (double.TryParse(basicSalaryStr, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var salary))
                    emp.BasicSalary = salary;

                if (DateTime.TryParse(joiningDateStr, out var joinDate))
                    emp.JoiningDate = joinDate;

                EncryptSensitiveFields(emp);
                _db.Employees.Add(emp);
                codeSet.Add(employeeCode);
                imported++;
            }
            catch (Exception ex)
            {
                errors.Add($"Row {row}: {ex.Message}");
                skipped++;
            }
        }

        if (imported > 0)
            await _db.SaveChangesAsync();

        return (imported, skipped, errors);
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

    // ───────────── FR: Employee CSV/XLSX import & export ─────────────

    /// <summary>Column headers shared by the CSV and XLSX exports (round-trips with ImportCsvAsync).</summary>
    private static readonly string[] EmployeeExportColumns =
    {
        "EmployeeCode", "FirstName", "MiddleNames", "Surname", "Email", "Phone", "Gender",
        "MaritalStatus", "Country", "City", "Department", "JobTitle", "EmploymentType",
        "ContractType", "BasicSalary", "Currency", "WorkHoursPerWeek", "JoiningDate", "Status"
    };

    /// <summary>
    /// Export all employees for the org as CSV. Sensitive fields (bank details,
    /// NRC, TPIN, NAPSA number…) are deliberately excluded — this export targets
    /// directory/bulk-edit workflows, not payroll data movement.
    /// </summary>
    public async Task<byte[]> ExportCsvAsync(int orgId)
    {
        var employees = await _db.Employees
            .Where(e => e.OrganizationId == orgId)
            .OrderBy(e => e.Surname).ThenBy(e => e.FirstName)
            .ToListAsync();

        var writer = new StringWriter();
        var csv = new CsvHelper.CsvWriter(writer, System.Globalization.CultureInfo.InvariantCulture);
        foreach (var col in EmployeeExportColumns) csv.WriteField(col);
        await csv.NextRecordAsync();

        foreach (var e in employees)
        {
            csv.WriteField(e.EmployeeCode);
            csv.WriteField(e.FirstName);
            csv.WriteField(e.MiddleNames);
            csv.WriteField(e.Surname);
            csv.WriteField(e.Email);
            csv.WriteField(e.Phone);
            csv.WriteField(e.Gender);
            csv.WriteField(e.MaritalStatus);
            csv.WriteField(e.Country);
            csv.WriteField(e.City);
            csv.WriteField(e.Department);
            csv.WriteField(e.JobTitle);
            csv.WriteField(e.EmploymentType);
            csv.WriteField(e.ContractType);
            csv.WriteField(e.BasicSalary);
            csv.WriteField(e.Currency);
            csv.WriteField(e.WorkHoursPerWeek);
            csv.WriteField(e.JoiningDate?.ToString("yyyy-MM-dd"));
            csv.WriteField(e.Status.ToString());
            await csv.NextRecordAsync();
        }
        await csv.FlushAsync();
        return System.Text.Encoding.UTF8.GetBytes(writer.ToString());
    }

    /// <summary>Export all employees as a styled .xlsx workbook (ClosedXML).</summary>
    public async Task<byte[]> ExportXlsxAsync(int orgId)
    {
        var employees = await _db.Employees
            .Where(e => e.OrganizationId == orgId)
            .OrderBy(e => e.Surname).ThenBy(e => e.FirstName)
            .ToListAsync();

        using var wb = new ClosedXML.Excel.XLWorkbook();
        var ws = wb.Worksheets.Add("Employees");

        for (var i = 0; i < EmployeeExportColumns.Length; i++)
        {
            var cell = ws.Cell(1, i + 1);
            cell.Value = EmployeeExportColumns[i];
            cell.Style.Font.Bold = true;
            cell.Style.Fill.BackgroundColor = ClosedXML.Excel.XLColor.FromHtml("#25163F");
            cell.Style.Font.FontColor = ClosedXML.Excel.XLColor.White;
        }
        ws.Row(1).Style.Alignment.Horizontal = ClosedXML.Excel.XLAlignmentHorizontalValues.Center;
        ws.SheetView.FreezeRows(1);

        var row = 2;
        foreach (var e in employees)
        {
            ws.Cell(row, 1).Value = e.EmployeeCode;
            ws.Cell(row, 2).Value = e.FirstName;
            ws.Cell(row, 3).Value = e.MiddleNames;
            ws.Cell(row, 4).Value = e.Surname;
            ws.Cell(row, 5).Value = e.Email;
            ws.Cell(row, 6).Value = e.Phone;
            ws.Cell(row, 7).Value = e.Gender;
            ws.Cell(row, 8).Value = e.MaritalStatus;
            ws.Cell(row, 9).Value = e.Country;
            ws.Cell(row, 10).Value = e.City;
            ws.Cell(row, 11).Value = e.Department;
            ws.Cell(row, 12).Value = e.JobTitle;
            ws.Cell(row, 13).Value = e.EmploymentType;
            ws.Cell(row, 14).Value = e.ContractType;
            ws.Cell(row, 15).Value = e.BasicSalary;
            ws.Cell(row, 16).Value = e.Currency;
            ws.Cell(row, 17).Value = e.WorkHoursPerWeek;
            ws.Cell(row, 18).Value = e.JoiningDate?.ToString("yyyy-MM-dd") ?? "";
            ws.Cell(row, 19).Value = e.Status.ToString();
            row++;
        }
        ws.Columns().AdjustToContents();

        using var ms = new MemoryStream();
        wb.SaveAs(ms);
        return ms.ToArray();
    }

    /// <summary>Outcome of a CSV import.</summary>
    public sealed class EmployeeCsvImportResult
    {
        public int Imported { get; set; }
        public int Skipped { get; set; }
        public List<string> Errors { get; set; } = new();
    }

    /// <summary>
    /// Import employees from a CSV stream. Columns are matched case-insensitively
    /// against the export headers (FirstName/Surname or a FullName fallback).
    /// Rows with a duplicate EmployeeCode or a missing name are skipped and
    /// reported in <see cref="EmployeeCsvImportResult.Errors"/> — one bad row
    /// never aborts the batch.
    /// </summary>
    public async Task<EmployeeCsvImportResult> ImportCsvAsync(int orgId, Stream csvStream)
    {
        var result = new EmployeeCsvImportResult();

        var existingCodes = await _db.Employees
            .Where(e => e.OrganizationId == orgId && e.EmployeeCode != null)
            .Select(e => e.EmployeeCode!)
            .ToListAsync();
        var codeSet = new HashSet<string>(existingCodes, StringComparer.OrdinalIgnoreCase);

        using var reader = new StreamReader(csvStream);
        using var csv = new CsvHelper.CsvReader(reader, System.Globalization.CultureInfo.InvariantCulture);

        await csv.ReadAsync();
        csv.ReadHeader();
        var header = csv.HeaderRecord ?? Array.Empty<string>();

        string? Col(params string[] names)
        {
            foreach (var n in names)
            {
                var idx = Array.FindIndex(header, h => string.Equals(h?.Trim(), n, StringComparison.OrdinalIgnoreCase));
                if (idx >= 0)
                {
                    var v = csv.GetField(idx);
                    return string.IsNullOrWhiteSpace(v) ? null : v.Trim();
                }
            }
            return null;
        }

        while (await csv.ReadAsync())
        {
            var rowNum = csv.Context.Parser?.Row ?? (result.Imported + result.Skipped + 2);
            try
            {
                var firstName = Col("FirstName", "First Name");
                var surname = Col("Surname", "LastName", "Last Name");
                var fullName = Col("FullName", "Full Name", "Name");

                // FullName fallback: "First [Middle] Last".
                if (string.IsNullOrWhiteSpace(firstName) && !string.IsNullOrWhiteSpace(fullName))
                {
                    var parts = fullName.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length > 0) firstName = parts[0];
                    if (string.IsNullOrWhiteSpace(surname) && parts.Length > 1)
                        surname = string.Join(" ", parts.Skip(1));
                }

                if (string.IsNullOrWhiteSpace(firstName) || string.IsNullOrWhiteSpace(surname))
                {
                    result.Skipped++;
                    result.Errors.Add($"Row {rowNum}: skipped — missing FirstName/Surname (provide columns or a FullName).");
                    continue;
                }

                var code = Col("EmployeeCode", "Employee Code", "Code");
                if (!string.IsNullOrWhiteSpace(code) && codeSet.Contains(code))
                {
                    result.Skipped++;
                    result.Errors.Add($"Row {rowNum}: skipped — EmployeeCode '{code}' already exists.");
                    continue;
                }

                var emp = new Employee
                {
                    OrganizationId = orgId,
                    FirstName = firstName,
                    MiddleNames = Col("MiddleNames", "Middle Names") ?? "",
                    Surname = surname,
                    Email = Col("Email"),
                    Phone = Col("Phone", "Mobile"),
                    Gender = Col("Gender"),
                    MaritalStatus = Col("MaritalStatus", "Marital Status"),
                    Country = Col("Country"),
                    City = Col("City"),
                    Department = Col("Department"),
                    JobTitle = Col("JobTitle", "Job Title", "Title"),
                    EmploymentType = Col("EmploymentType", "Employment Type"),
                    ContractType = Col("ContractType", "Contract Type"),
                    BasicSalary = double.TryParse(Col("BasicSalary", "Basic Salary"), out var bs) ? bs : 0,
                    Currency = Col("Currency") ?? "ZMW",
                    WorkHoursPerWeek = double.TryParse(Col("WorkHoursPerWeek", "Work Hours Per Week"), out var wh) ? wh : 40,
                    EmployeeCode = string.IsNullOrWhiteSpace(code) ? null : code,
                    CreatedAt = DateTime.UtcNow
                };

                if (DateTime.TryParse(Col("JoiningDate", "Joining Date"), out var jd))
                    emp.JoiningDate = jd;
                if (Enum.TryParse<EmploymentStatus>(Col("Status"), out var st))
                    emp.Status = st;

                await CreateAsync(emp); // persists via the normal path
                if (!string.IsNullOrWhiteSpace(emp.EmployeeCode))
                    codeSet.Add(emp.EmployeeCode);
                result.Imported++;
            }
            catch (Exception ex)
            {
                result.Skipped++;
                result.Errors.Add($"Row {rowNum}: {ex.Message}");
            }
        }
        return result;
    }
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
    private readonly NotificationService? _notifications;
    private readonly EmailService? _email;
    public LeaveService(UkuuHrDbContext db, NotificationService? notifications = null, EmailService? email = null)
    {
        _db = db;
        _notifications = notifications;
        _email = email;
    }

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

        // FR-013: notify managers/admins that a request is awaiting review.
        await NotifySafeAsync(req.OrganizationId,
            type: "info",
            title: "Leave request pending approval",
            body: $"{req.EmployeeName} requested leave from {req.StartDate:yyyy-MM-dd} to {req.EndDate:yyyy-MM-dd} ({req.LeaveTypeName}).",
            sourceModule: "leave",
            actionUrl: "/leave");

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

        // FR-013: notify the requester of the decision.
        await NotifySafeAsync(orgId,
            type: approve ? "success" : "warning",
            title: approve ? "Leave request approved" : "Leave request rejected",
            body: $"{lr.EmployeeName}'s {lr.LeaveTypeName} request ({lr.StartDate:yyyy-MM-dd} → {lr.EndDate:yyyy-MM-dd}) was {(approve ? "approved" : "rejected")} by {reviewerEmail}.",
            sourceModule: "leave",
            actionUrl: "/leave");

        // Best-effort email to the employee (module 10 — notifications).
        if (_email is { Enabled: true })
        {
            try
            {
                var employeeEmail = await _db.Employees
                    .Where(e => e.OrganizationId == orgId && e.Id == lr.EmployeeId)
                    .Select(e => e.Email)
                    .FirstOrDefaultAsync();
                if (!string.IsNullOrWhiteSpace(employeeEmail))
                {
                    var html = EmailService.WrapHtml(
                        approve ? "Leave request approved" : "Leave request rejected",
                        $@"<p>Hi {lr.EmployeeName},</p>
<p>Your <b>{lr.LeaveTypeName}</b> request for <b>{lr.StartDate:dd MMM yyyy} – {lr.EndDate:dd MMM yyyy}</b> has been <b>{(approve ? "approved" : "rejected")}</b>.</p>
{(string.IsNullOrWhiteSpace(notes) ? "" : $"<p style=\"color:#6b6580;\">Reviewer note: {notes}</p>")}");
                    await _email.SendAsync(employeeEmail, $"[Ukuu HR] Leave request {(approve ? "approved" : "rejected")}", html);
                }
            }
            catch { /* email is best-effort */ }
        }

        return true;
    }

    /// <summary>Best-effort notification helper — never fails the leave workflow.</summary>
    private async Task NotifySafeAsync(int orgId, string type, string title, string body, string sourceModule, string actionUrl)
    {
        if (_notifications == null) return;
        try
        {
            switch (type)
            {
                case "success":
                    await _notifications.NotifySuccessAsync(orgId, title, body, sourceModule: sourceModule, actionUrl: actionUrl);
                    break;
                case "warning":
                    await _notifications.NotifyWarningAsync(orgId, title, body, sourceModule: sourceModule, actionUrl: actionUrl);
                    break;
                default:
                    await _notifications.NotifyInfoAsync(orgId, title, body, sourceModule: sourceModule, actionUrl: actionUrl);
                    break;
            }
        }
        catch { /* notifications are best-effort */ }
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

        // FR-006/FR-012: pull APPROVED overtime for the period so payroll reflects it.
        var approvedOvertime = await _db.OvertimeRecords
            .Where(o => o.OrganizationId == orgId
                     && o.Date >= payStart && o.Date <= payEnd
                     && (o.Status == OvertimeStatus.Approved || o.Status == OvertimeStatus.AutoApproved))
            .ToListAsync();

        var runs = new List<PayrollRun>();

        foreach (var emp in employees)
        {
            var allowances = emp.Allowances.Select(a => new AllowanceInput
            {
                Name = a.Name, Amount = a.Amount,
                Type = a.Type == AllowanceType.Percentage ? AllowanceTypeInput.Percentage : AllowanceTypeInput.Fixed,
                Taxable = a.Taxable
            }).ToList();

            // Approved overtime for this employee in the pay period.
            var empOt = approvedOvertime.Where(o => o.EmployeeId == emp.Id).ToList();
            var otHours = Math.Round(empOt.Sum(o => o.Hours), 2);
            var otPay = Math.Round(empOt.Sum(o => o.Pay), 2);
            // Effective blended hourly OT rate (pay / hours) keeps PAYE math consistent.
            var otRate = otHours > 0 ? otPay / otHours : 0;

            var calc = PayrollCalculator.Calculate(emp.BasicSalary, allowances,
                overtimeHours: otHours, overtimeRate: otRate, countryConfig: cfg);

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
                OvertimePay = Math.Round(calc.OvertimePay, 2),
                OvertimeHours = otHours,
                OvertimeRate = Math.Round(otRate, 4),
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
