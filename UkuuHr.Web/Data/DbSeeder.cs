using Microsoft.EntityFrameworkCore;
using UkuuHr.Models;
using UkuuHr.Services;

namespace UkuuHr.Data;

/// <summary>
/// Seeds the database with default demo data on first run.
/// Creates a demo organization with sample employees, leave types, attendance, payroll.
/// </summary>
public static class DbSeeder
{
    public static async Task SeedAsync(UkuuHrDbContext db)
    {
        await db.Database.EnsureCreatedAsync();

        if (await db.Organizations.AnyAsync()) 
        {
            // Main seed already ran — make sure Phase 1 + Phase 2 + Phase 3 additions are present too.
            await Phase1Seeder.SeedAsync(db);
            await Phase2Seeder.SeedAsync(db);
            await Phase3Seeder.SeedAsync(db);
            return;
        }

        // ───── Organization ─────
        var org = new Organization
        {
            Name = "UkuuHR Demo Ltd",
            Country = "Zambia",
            Currency = "ZMW",
            Industry = "Technology",
            PayrollConfigJson = "{}",
            CreatedAt = DateTime.UtcNow
        };
        db.Organizations.Add(org);
        await db.SaveChangesAsync();

        // ───── Leave Types ─────
        var annualLeave = new LeaveType { OrganizationId = org.Id, Name = "Annual Leave", DefaultDays = 21, Color = "#25163F", IsPaid = true };
        var sickLeave = new LeaveType { OrganizationId = org.Id, Name = "Sick Leave", DefaultDays = 10, Color = "#DC2626", IsPaid = true };
        var compassionate = new LeaveType { OrganizationId = org.Id, Name = "Compassionate Leave", DefaultDays = 3, Color = "#F59E0B", IsPaid = true };
        var maternity = new LeaveType { OrganizationId = org.Id, Name = "Maternity Leave", DefaultDays = 90, Color = "#EC4899", IsPaid = true };
        var unpaid = new LeaveType { OrganizationId = org.Id, Name = "Unpaid Leave", DefaultDays = 0, Color = "#6B7280", IsPaid = false };
        db.LeaveTypes.AddRange(annualLeave, sickLeave, compassionate, maternity, unpaid);
        await db.SaveChangesAsync();

        // ───── Employees ─────
        var employees = new List<Employee>
        {
            new()
            {
                OrganizationId = org.Id,
                Title = "Mr.", FirstName = "Chungu", Surname = "Chama",
                Nationality = "Zambian", Country = "Zambia", City = "Lusaka",
                Gender = "Male", MaritalStatus = "Married",
                Email = "chungu.chama@ukuuhr.demo", Phone = "+260 97 123 4567",
                JobTitle = "Chief Executive Officer", Department = "Executive",
                Status = EmploymentStatus.Active, EmploymentType = "Full-time", ContractType = "Permanent",
                JoiningDate = new DateTime(2021, 1, 15), WorkHoursPerWeek = 40,
                EmployeeCode = "UKU-001",
                BasicSalary = 35000, HourlyRate = 35000.0 / 26 / 8,
                Currency = "ZMW", BankName = "Zanaco", AccountNumber = "0123456789012",
                BeneficiaryName = "Chungu Chama", SwiftCode = "ZABKZMLX",
                AllowancesJson = System.Text.Json.JsonSerializer.Serialize(new List<Allowance>
                {
                    new() { Name = "Housing", Amount = 5000, Type = AllowanceType.Fixed, Taxable = true },
                    new() { Name = "Transport", Amount = 2000, Type = AllowanceType.Fixed, Taxable = true }
                }),
                Tpin = "1234567890", NapsaNumber = "123456789/012", HealthInsuranceNumber = "NHIMA-001234",
                CreatedAt = DateTime.UtcNow
            },
            new()
            {
                OrganizationId = org.Id,
                Title = "Ms.", FirstName = "Thandiwe", Surname = "Banda",
                Nationality = "Zambian", Country = "Zambia", City = "Lusaka",
                Gender = "Female", MaritalStatus = "Single",
                Email = "thandiwe.banda@ukuuhr.demo", Phone = "+260 96 555 8899",
                JobTitle = "HR Manager", Department = "Human Resources",
                Status = EmploymentStatus.Active, EmploymentType = "Full-time", ContractType = "Permanent",
                JoiningDate = new DateTime(2021, 4, 1), WorkHoursPerWeek = 40,
                EmployeeCode = "UKU-002",
                BasicSalary = 18000, HourlyRate = 18000.0 / 26 / 8,
                Currency = "ZMW", BankName = "Standard Chartered", AccountNumber = "9876543210987",
                BeneficiaryName = "Thandiwe Banda", SwiftCode = "SCBLZMLX",
                AllowancesJson = System.Text.Json.JsonSerializer.Serialize(new List<Allowance>
                {
                    new() { Name = "Housing", Amount = 3000, Type = AllowanceType.Fixed, Taxable = true },
                    new() { Name = "Transport", Amount = 1500, Type = AllowanceType.Fixed, Taxable = true }
                }),
                Tpin = "2345678901", NapsaNumber = "234567890/012", HealthInsuranceNumber = "NHIMA-002345",
                CreatedAt = DateTime.UtcNow
            },
            new()
            {
                OrganizationId = org.Id,
                Title = "Mr.", FirstName = "Joseph", MiddleNames = "Mwila", Surname = "Phiri",
                Nationality = "Zambian", Country = "Zambia", City = "Ndola",
                Gender = "Male", MaritalStatus = "Married",
                Email = "joseph.phiri@ukuuhr.demo", Phone = "+260 97 777 1212",
                JobTitle = "Senior Software Engineer", Department = "Engineering",
                Status = EmploymentStatus.Active, EmploymentType = "Full-time", ContractType = "Permanent",
                JoiningDate = new DateTime(2022, 2, 10), WorkHoursPerWeek = 40,
                EmployeeCode = "UKU-003",
                BasicSalary = 22000, HourlyRate = 22000.0 / 26 / 8,
                Currency = "ZMW", BankName = "FNB Zambia", AccountNumber = "5512345678901",
                BeneficiaryName = "Joseph Phiri", SwiftCode = "FNBLZMLX",
                AllowancesJson = System.Text.Json.JsonSerializer.Serialize(new List<Allowance>
                {
                    new() { Name = "Housing", Amount = 3500, Type = AllowanceType.Fixed, Taxable = true },
                    new() { Name = "Transport", Amount = 1800, Type = AllowanceType.Fixed, Taxable = true },
                    new() { Name = "Internet", Amount = 5, Type = AllowanceType.Percentage, Taxable = false }
                }),
                Tpin = "3456789012", NapsaNumber = "345678901/012", HealthInsuranceNumber = "NHIMA-003456",
                CreatedAt = DateTime.UtcNow
            },
            new()
            {
                OrganizationId = org.Id,
                Title = "Ms.", FirstName = "Grace", Surname = "Mwape",
                Nationality = "Zambian", Country = "Zambia", City = "Kitwe",
                Gender = "Female", MaritalStatus = "Married",
                Email = "grace.mwape@ukuuhr.demo", Phone = "+260 95 333 4545",
                JobTitle = "Finance & Payroll Officer", Department = "Finance",
                Status = EmploymentStatus.Active, EmploymentType = "Full-time", ContractType = "Permanent",
                JoiningDate = new DateTime(2022, 6, 1), WorkHoursPerWeek = 40,
                EmployeeCode = "UKU-004",
                BasicSalary = 14500, HourlyRate = 14500.0 / 26 / 8,
                Currency = "ZMW", BankName = "Atlas Mara", AccountNumber = "1209876543210",
                BeneficiaryName = "Grace Mwape", SwiftCode = "AUBKZMLX",
                AllowancesJson = System.Text.Json.JsonSerializer.Serialize(new List<Allowance>
                {
                    new() { Name = "Housing", Amount = 2500, Type = AllowanceType.Fixed, Taxable = true },
                    new() { Name = "Transport", Amount = 1200, Type = AllowanceType.Fixed, Taxable = true }
                }),
                Tpin = "4567890123", NapsaNumber = "456789012/012", HealthInsuranceNumber = "NHIMA-004567",
                CreatedAt = DateTime.UtcNow
            },
            new()
            {
                OrganizationId = org.Id,
                Title = "Mr.", FirstName = "Brian", Surname = "Tembo",
                Nationality = "Zambian", Country = "Zambia", City = "Lusaka",
                Gender = "Male", MaritalStatus = "Single",
                Email = "brian.tembo@ukuuhr.demo", Phone = "+260 96 111 2233",
                JobTitle = "Software Engineer", Department = "Engineering",
                Status = EmploymentStatus.Probation, EmploymentType = "Full-time", ContractType = "Permanent",
                JoiningDate = new DateTime(2024, 8, 12), WorkHoursPerWeek = 40,
                EmployeeCode = "UKU-005",
                BasicSalary = 12000, HourlyRate = 12000.0 / 26 / 8,
                Currency = "ZMW", BankName = "ZICB", AccountNumber = "8800123456789",
                BeneficiaryName = "Brian Tembo", SwiftCode = "ZICBZMLX",
                AllowancesJson = System.Text.Json.JsonSerializer.Serialize(new List<Allowance>
                {
                    new() { Name = "Transport", Amount = 1500, Type = AllowanceType.Fixed, Taxable = true }
                }),
                Tpin = "5678901234", NapsaNumber = "567890123/012", HealthInsuranceNumber = "NHIMA-005678",
                CreatedAt = DateTime.UtcNow
            },
            new()
            {
                OrganizationId = org.Id,
                Title = "Mrs.", FirstName = "Beatrice", Surname = "Lungu",
                Nationality = "Zambian", Country = "Zambia", City = "Lusaka",
                Gender = "Female", MaritalStatus = "Married",
                Email = "beatrice.lungu@ukuuhr.demo", Phone = "+260 97 444 5566",
                JobTitle = "Marketing Specialist", Department = "Marketing",
                Status = EmploymentStatus.Active, EmploymentType = "Full-time", ContractType = "Permanent",
                JoiningDate = new DateTime(2023, 3, 1), WorkHoursPerWeek = 40,
                EmployeeCode = "UKU-006",
                BasicSalary = 11000, HourlyRate = 11000.0 / 26 / 8,
                Currency = "ZMW", BankName = "ABSA Zambia", AccountNumber = "0102020304050",
                BeneficiaryName = "Beatrice Lungu", SwiftCode = "ABSCZMLX",
                AllowancesJson = System.Text.Json.JsonSerializer.Serialize(new List<Allowance>
                {
                    new() { Name = "Transport", Amount = 1200, Type = AllowanceType.Fixed, Taxable = true }
                }),
                Tpin = "6789012345", NapsaNumber = "678901234/012", HealthInsuranceNumber = "NHIMA-006789",
                CreatedAt = DateTime.UtcNow
            },
            new()
            {
                OrganizationId = org.Id,
                Title = "Mr.", FirstName = "David", Surname = "Mumba",
                Nationality = "Zambian", Country = "Zambia", City = "Lusaka",
                Gender = "Male", MaritalStatus = "Single",
                Email = "david.mumba@ukuuhr.demo", Phone = "+260 95 222 7788",
                JobTitle = "Sales Executive", Department = "Sales",
                Status = EmploymentStatus.Active, EmploymentType = "Full-time", ContractType = "Fixed Term",
                ContractEndDate = new DateTime(2026, 1, 31), JoiningDate = new DateTime(2024, 1, 5), WorkHoursPerWeek = 40,
                EmployeeCode = "UKU-007",
                BasicSalary = 8500, HourlyRate = 8500.0 / 26 / 8,
                Currency = "ZMW", BankName = "Investrust", AccountNumber = "3300445566778",
                BeneficiaryName = "David Mumba", SwiftCode = "INVZZMLX",
                AllowancesJson = System.Text.Json.JsonSerializer.Serialize(new List<Allowance>
                {
                    new() { Name = "Commission", Amount = 10, Type = AllowanceType.Percentage, Taxable = true },
                    new() { Name = "Transport", Amount = 1000, Type = AllowanceType.Fixed, Taxable = true }
                }),
                Tpin = "7890123456", NapsaNumber = "789012345/012", HealthInsuranceNumber = "NHIMA-007890",
                CreatedAt = DateTime.UtcNow
            },
            new()
            {
                OrganizationId = org.Id,
                Title = "Ms.", FirstName = "Lillian", Surname = "Sichone",
                Nationality = "Zambian", Country = "Zambia", City = "Kabwe",
                Gender = "Female", MaritalStatus = "Single",
                Email = "lillian.sichone@ukuuhr.demo", Phone = "+260 96 888 9900",
                JobTitle = "Customer Support Lead", Department = "Support",
                Status = EmploymentStatus.Active, EmploymentType = "Full-time", ContractType = "Permanent",
                JoiningDate = new DateTime(2023, 9, 15), WorkHoursPerWeek = 40,
                EmployeeCode = "UKU-008",
                BasicSalary = 9500, HourlyRate = 9500.0 / 26 / 8,
                Currency = "ZMW", BankName = "Cavmont", AccountNumber = "6600778899001",
                BeneficiaryName = "Lillian Sichone", SwiftCode = "CAVKZMLX",
                AllowancesJson = System.Text.Json.JsonSerializer.Serialize(new List<Allowance>
                {
                    new() { Name = "Transport", Amount = 1200, Type = AllowanceType.Fixed, Taxable = true }
                }),
                Tpin = "8901234567", NapsaNumber = "890123456/012", HealthInsuranceNumber = "NHIMA-008901",
                CreatedAt = DateTime.UtcNow
            }
        };
        db.Employees.AddRange(employees);
        await db.SaveChangesAsync();

        // ───── Attendance for the last 30 days (Present/Late/Absent) ─────
        var rnd = new Random(42);
        var today = DateTime.Today;
        for (int i = 0; i < 30; i++)
        {
            var date = today.AddDays(-i);
            if (date.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday) continue;

            foreach (var emp in employees.Where(e => e.Status != EmploymentStatus.Inactive))
            {
                var roll = rnd.NextDouble();
                AttendanceStatus status = AttendanceStatus.Present;
                DateTime? checkIn = null, checkOut = null;
                if (roll < 0.85)
                {
                    var late = roll < 0.15;
                    status = late ? AttendanceStatus.Late : AttendanceStatus.Present;
                    var inHour = late ? 9 : 8;
                    checkIn = new DateTime(date.Year, date.Month, date.Day, inHour, rnd.Next(0, 30), 0);
                    checkOut = new DateTime(date.Year, date.Month, date.Day, 17, rnd.Next(0, 30), 0);
                }
                else if (roll < 0.95) status = AttendanceStatus.OnLeave;
                else status = AttendanceStatus.Absent;

                db.Attendances.Add(new Attendance
                {
                    OrganizationId = org.Id,
                    EmployeeId = emp.Id,
                    EmployeeName = emp.FullName,
                    DateKey = date.ToString("yyyy-MM-dd"),
                    Date = date,
                    CheckIn = checkIn,
                    CheckOut = checkOut,
                    Status = status,
                    Source = AttendanceSource.System,
                    BreakMinutes = 60,
                    CreatedAt = DateTime.UtcNow
                });
            }
        }
        await db.SaveChangesAsync();

        // ───── Leave requests ─────
        db.LeaveRequests.AddRange(
            new LeaveRequest
            {
                OrganizationId = org.Id, EmployeeId = employees[1].Id, EmployeeName = employees[1].FullName,
                LeaveTypeId = annualLeave.Id, LeaveTypeName = "Annual Leave",
                StartDate = today.AddDays(-7), EndDate = today.AddDays(-5),
                Reason = "Family trip to Livingstone.",
                Status = LeaveRequestStatus.Approved,
                ReviewedAt = today.AddDays(-12), ReviewedByEmail = "chungu.chama@ukuuhr.demo"
            },
            new LeaveRequest
            {
                OrganizationId = org.Id, EmployeeId = employees[2].Id, EmployeeName = employees[2].FullName,
                LeaveTypeId = sickLeave.Id, LeaveTypeName = "Sick Leave",
                StartDate = today.AddDays(2), EndDate = today.AddDays(3),
                Reason = "Medical appointment and recovery.",
                Status = LeaveRequestStatus.Pending
            },
            new LeaveRequest
            {
                OrganizationId = org.Id, EmployeeId = employees[4].Id, EmployeeName = employees[4].FullName,
                LeaveTypeId = annualLeave.Id, LeaveTypeName = "Annual Leave",
                StartDate = today.AddDays(10), EndDate = today.AddDays(20),
                Reason = "Pre-scheduled vacation to South Africa.",
                Status = LeaveRequestStatus.Pending
            },
            new LeaveRequest
            {
                OrganizationId = org.Id, EmployeeId = employees[5].Id, EmployeeName = employees[5].FullName,
                LeaveTypeId = compassionate.Id, LeaveTypeName = "Compassionate Leave",
                StartDate = today.AddDays(-20), EndDate = today.AddDays(-19),
                Reason = "Family bereavement.",
                Status = LeaveRequestStatus.Approved,
                ReviewedAt = today.AddDays(-22), ReviewedByEmail = "chungu.chama@ukuuhr.demo"
            },
            new LeaveRequest
            {
                OrganizationId = org.Id, EmployeeId = employees[6].Id, EmployeeName = employees[6].FullName,
                LeaveTypeId = unpaid.Id, LeaveTypeName = "Unpaid Leave",
                StartDate = today.AddDays(5), EndDate = today.AddDays(7),
                Reason = "Personal matters requiring extended absence.",
                Status = LeaveRequestStatus.Rejected,
                ReviewedAt = today.AddDays(-1), ReviewedByEmail = "thandiwe.banda@ukuuhr.demo",
                RejectionReason = "Critical sales period — please reschedule after quarter close."
            }
        );
        await db.SaveChangesAsync();

        // ───── Payroll runs for previous month ─────
        var prevMonth = today.AddMonths(-1);
        var payrollDate = new DateTime(prevMonth.Year, prevMonth.Month, 1);
        var payStart = payrollDate;
        var payEnd = payStart.AddMonths(1).AddDays(-1);
        var zambiaCfg = PayrollCountryConfig.Zambia();
        var batchId = $"BATCH-{payrollDate:yyyyMM}";

        foreach (var emp in employees.Where(e => e.Status != EmploymentStatus.Inactive))
        {
            var allowancesList = emp.Allowances.Select(a => new AllowanceInput
            {
                Name = a.Name, Amount = a.Amount,
                Type = a.Type == AllowanceType.Percentage ? AllowanceTypeInput.Percentage : AllowanceTypeInput.Fixed,
                Taxable = a.Taxable
            }).ToList();

            var calc = PayrollCalculator.Calculate(emp.BasicSalary, allowancesList, countryConfig: zambiaCfg);

            db.PayrollRuns.Add(new PayrollRun
            {
                OrganizationId = org.Id,
                EmployeeId = emp.Id,
                EmployeeName = emp.FullName,
                BatchId = batchId,
                Month = payStart.Month, Year = payStart.Year,
                PayPeriodStart = payStart, PayPeriodEnd = payEnd,
                Status = PayrollStatus.Approved,
                ApprovalStatus = PayrollApprovalStatus.Approved,
                Base = calc.Basic,
                Allowances = calc.TaxableAllowances + calc.NonTaxableAllowances,
                NonTaxableAllowances = calc.NonTaxableAllowances,
                OvertimePay = 0, Bonuses = 0,
                Paye = Math.Round(calc.Paye, 2),
                Napsa = Math.Round(calc.Napsa, 2),
                Nhima = Math.Round(calc.Nhima, 2),
                OtherDeductions = 0,
                PayePercent = calc.EffectivePayePercent,
                NapsaPercent = zambiaCfg.NapsaPercent,
                NhimaPercent = zambiaCfg.NhimaPercent,
                OvertimeHours = 0, OvertimeRate = 0, BonusAmount = 0,
                Currency = emp.DisplayCurrency,
                SubmittedByUserId = "system", SubmittedByEmail = "system@ukuuhr.demo",
                SubmittedAt = DateTime.UtcNow.AddDays(-10),
                ApprovedByUserId = "system", ApprovedByEmail = "chungu.chama@ukuuhr.demo",
                ApprovedAt = DateTime.UtcNow.AddDays(-5),
                ApproverNotes = "Monthly payroll batch approved.",
                PayslipDelivery = PayslipDeliveryStatus.Sent,
                SentToEmail = emp.Email, SentAt = DateTime.UtcNow.AddDays(-3),
                CreatedByUserId = "system",
                CreatedAt = DateTime.UtcNow.AddDays(-10)
            });
        }
        await db.SaveChangesAsync();

        // ───── Pending payroll approvals for current month ─────
        var thisMonthPayStart = new DateTime(today.Year, today.Month, 1);
        var thisMonthPayEnd = thisMonthPayStart.AddMonths(1).AddDays(-1);
        var thisBatchId = $"BATCH-{thisMonthPayStart:yyyyMM}";
        foreach (var emp in employees.Where(e => e.Status != EmploymentStatus.Inactive).Take(4))
        {
            var allowancesList = emp.Allowances.Select(a => new AllowanceInput
            {
                Name = a.Name, Amount = a.Amount,
                Type = a.Type == AllowanceType.Percentage ? AllowanceTypeInput.Percentage : AllowanceTypeInput.Fixed,
                Taxable = a.Taxable
            }).ToList();

            var calc = PayrollCalculator.Calculate(emp.BasicSalary, allowancesList, countryConfig: zambiaCfg);
            db.PayrollRuns.Add(new PayrollRun
            {
                OrganizationId = org.Id,
                EmployeeId = emp.Id,
                EmployeeName = emp.FullName,
                BatchId = thisBatchId,
                Month = thisMonthPayStart.Month, Year = thisMonthPayStart.Year,
                PayPeriodStart = thisMonthPayStart, PayPeriodEnd = thisMonthPayEnd,
                Status = PayrollStatus.PendingApproval,
                ApprovalStatus = PayrollApprovalStatus.Pending,
                Base = calc.Basic,
                Allowances = calc.TaxableAllowances + calc.NonTaxableAllowances,
                NonTaxableAllowances = calc.NonTaxableAllowances,
                Paye = Math.Round(calc.Paye, 2),
                Napsa = Math.Round(calc.Napsa, 2),
                Nhima = Math.Round(calc.Nhima, 2),
                PayePercent = calc.EffectivePayePercent,
                NapsaPercent = zambiaCfg.NapsaPercent,
                NhimaPercent = zambiaCfg.NhimaPercent,
                Currency = emp.DisplayCurrency,
                SubmittedByUserId = "system", SubmittedByEmail = "grace.mwape@ukuuhr.demo",
                SubmittedAt = DateTime.UtcNow.AddDays(-1),
                CreatedByUserId = "system",
                CreatedAt = DateTime.UtcNow.AddDays(-1)
            });
        }
        await db.SaveChangesAsync();

        // ───── Department shifts ─────
        db.DepartmentShifts.AddRange(
            new DepartmentShiftAssignment { OrganizationId = org.Id, Department = "Engineering", Shift = ShiftType.Morning, Schedule = ShiftSchedule.Weekdays, StartMinutes = 8 * 60, EndMinutes = 17 * 60, DaysOfWeekMask = 0b0011111 },
            new DepartmentShiftAssignment { OrganizationId = org.Id, Department = "Engineering", Shift = ShiftType.Night, Schedule = ShiftSchedule.Rotating, StartMinutes = 22 * 60, EndMinutes = 6 * 60, DaysOfWeekMask = 0b1111111 },
            new DepartmentShiftAssignment { OrganizationId = org.Id, Department = "Support", Shift = ShiftType.Mid, Schedule = ShiftSchedule.Rotating, StartMinutes = 14 * 60, EndMinutes = 22 * 60, DaysOfWeekMask = 0b1111111 },
            new DepartmentShiftAssignment { OrganizationId = org.Id, Department = "Sales", Shift = ShiftType.Morning, Schedule = ShiftSchedule.Weekdays, StartMinutes = 8 * 60, EndMinutes = 17 * 60, DaysOfWeekMask = 0b0011111 },
            new DepartmentShiftAssignment { OrganizationId = org.Id, Department = "Finance", Shift = ShiftType.Morning, Schedule = ShiftSchedule.Weekdays, StartMinutes = 8 * 60, EndMinutes = 17 * 60, DaysOfWeekMask = 0b0011111 },
            new DepartmentShiftAssignment { OrganizationId = org.Id, Department = "Human Resources", Shift = ShiftType.Morning, Schedule = ShiftSchedule.Weekdays, StartMinutes = 8 * 60, EndMinutes = 17 * 60, DaysOfWeekMask = 0b0011111 },
            new DepartmentShiftAssignment { OrganizationId = org.Id, Department = "Marketing", Shift = ShiftType.Morning, Schedule = ShiftSchedule.Weekdays, StartMinutes = 8 * 60, EndMinutes = 17 * 60, DaysOfWeekMask = 0b0011111 }
        );
        await db.SaveChangesAsync();

        // ───── Public holidays ─────
        var yr = today.Year;
        db.LeaveHolidays.AddRange(
            new LeaveHoliday { OrganizationId = org.Id, Name = "New Year's Day", Date = new DateTime(yr, 1, 1), Country = "Zambia" },
            new LeaveHoliday { OrganizationId = org.Id, Name = "Zambia Independence Day", Date = new DateTime(yr, 10, 24), Country = "Zambia" },
            new LeaveHoliday { OrganizationId = org.Id, Name = "Labour Day", Date = new DateTime(yr, 5, 1), Country = "Zambia" },
            new LeaveHoliday { OrganizationId = org.Id, Name = "Africa Freedom Day", Date = new DateTime(yr, 5, 25), Country = "Zambia" },
            new LeaveHoliday { OrganizationId = org.Id, Name = "Heroes' Day", Date = new DateTime(yr, 7, 1), Country = "Zambia" },
            new LeaveHoliday { OrganizationId = org.Id, Name = "Unity Day", Date = new DateTime(yr, 7, 2), Country = "Zambia" },
            new LeaveHoliday { OrganizationId = org.Id, Name = "Christmas Day", Date = new DateTime(yr, 12, 25), Country = "Zambia" },
            new LeaveHoliday { OrganizationId = org.Id, Name = "Boxing Day", Date = new DateTime(yr, 12, 26), Country = "Zambia" }
        );
        await db.SaveChangesAsync();

        // ───── Employee documents ─────
        db.EmployeeDocuments.AddRange(
            new EmployeeDocument { OrganizationId = org.Id, EmployeeId = employees[0].Id, Name = "Employment Contract - Chungu Chama.pdf", Type = DocumentType.Pdf, Category = DocumentCategory.Contract, Folder = DocumentFolder.HrShared, SizeBytes = 245000, UploadedBy = "HR", UploadedByName = "Thandiwe Banda", UploadedAt = DateTime.UtcNow.AddDays(-365) },
            new EmployeeDocument { OrganizationId = org.Id, EmployeeId = employees[0].Id, Name = "Payslip - November 2025.pdf", Type = DocumentType.Pdf, Category = DocumentCategory.Payslip, Folder = DocumentFolder.HrShared, SizeBytes = 89000, UploadedBy = "System", UploadedByName = "Payroll System", UploadedAt = DateTime.UtcNow.AddDays(-5) },
            new EmployeeDocument { OrganizationId = org.Id, EmployeeId = employees[2].Id, Name = "Employment Contract - Joseph Phiri.pdf", Type = DocumentType.Pdf, Category = DocumentCategory.Contract, Folder = DocumentFolder.HrShared, SizeBytes = 221000, UploadedBy = "HR", UploadedByName = "Thandiwe Banda", UploadedAt = DateTime.UtcNow.AddDays(-700) },
            new EmployeeDocument { OrganizationId = org.Id, EmployeeId = employees[3].Id, Name = "NRC Scan.pdf", Type = DocumentType.Pdf, Category = DocumentCategory.Identity, Folder = DocumentFolder.Compliance, SizeBytes = 145000, UploadedBy = "HR", UploadedByName = "Thandiwe Banda", UploadedAt = DateTime.UtcNow.AddDays(-180) },
            new EmployeeDocument { OrganizationId = org.Id, EmployeeId = employees[4].Id, Name = "NHIMA Registration.pdf", Type = DocumentType.Pdf, Category = DocumentCategory.Compliance, Folder = DocumentFolder.Compliance, SizeBytes = 65000, UploadedBy = "HR", UploadedByName = "Thandiwe Banda", UploadedAt = DateTime.UtcNow.AddDays(-120) }
        );
        await db.SaveChangesAsync();

        // ───── HR policies ─────
        db.HrPolicies.AddRange(
            new HrPolicy { OrganizationId = org.Id, Title = "Employee Handbook 2025", Description = "Comprehensive guide covering conduct, benefits, and procedures.", PublishedAt = DateTime.UtcNow.AddDays(-100), UpdatedAt = DateTime.UtcNow.AddDays(-10) },
            new HrPolicy { OrganizationId = org.Id, Title = "Leave & Time-Off Policy", Description = "Annual, sick, compassionate, and parental leave entitlements.", PublishedAt = DateTime.UtcNow.AddDays(-100) },
            new HrPolicy { OrganizationId = org.Id, Title = "Remote Work Policy", Description = "Guidelines for hybrid and remote working arrangements.", PublishedAt = DateTime.UtcNow.AddDays(-50) },
            new HrPolicy { OrganizationId = org.Id, Title = "Code of Conduct", Description = "Standards of professional behavior expected of all employees.", PublishedAt = DateTime.UtcNow.AddDays(-100) }
        );
        await db.SaveChangesAsync();

        // ───── Audit logs ─────
        db.AuditLogs.AddRange(
            new AuditLog { OrganizationId = org.Id, Action = AuditAction.UserCreated, TargetUserEmail = "thandiwe.banda@ukuuhr.demo", PerformedByEmail = "chungu.chama@ukuuhr.demo", Timestamp = DateTime.UtcNow.AddDays(-30), Details = "User account created with HR Admin role." },
            new AuditLog { OrganizationId = org.Id, Action = AuditAction.LoginSuccess, TargetUserEmail = "chungu.chama@ukuuhr.demo", PerformedByEmail = "chungu.chama@ukuuhr.demo", Timestamp = DateTime.UtcNow.AddHours(-3), Details = "Successful sign-in." },
            new AuditLog { OrganizationId = org.Id, Action = AuditAction.ProfileUpdated, TargetUserEmail = "joseph.phiri@ukuuhr.demo", PerformedByEmail = "thandiwe.banda@ukuuhr.demo", Timestamp = DateTime.UtcNow.AddDays(-2), Details = "Updated banking details." },
            new AuditLog { OrganizationId = org.Id, Action = AuditAction.BulkExport, PerformedByEmail = "grace.mwape@ukuuhr.demo", Timestamp = DateTime.UtcNow.AddDays(-1), Details = "Exported 8 employee records to Excel." }
        );
        await db.SaveChangesAsync();

        // ───── User accounts (for PostgreSQL-backed authentication) ─────
        // The admin account (admin@ukuuhr.demo / Admin@2025) is what the login form checks against.
        // AuthUid="demo-admin" marks these as demo accounts with the known Admin@2025 password.
        db.UserAccounts.AddRange(
            new UserAccount
            {
                OrganizationId = org.Id,
                AuthUid = "demo-admin",
                Email = "admin@ukuuhr.demo",
                FirstName = "Chungu",
                LastName = "Chama",
                Role = UserRole.SuperAdmin,
                UserType = "owner",
                Status = AccountStatus.Active,
                IsFirstLogin = false,
                EmployeeId = employees[0].Id,
                CreatedAt = DateTime.UtcNow.AddDays(-365),
                LastActivatedAt = DateTime.UtcNow.AddDays(-365)
            },
            new UserAccount
            {
                OrganizationId = org.Id,
                AuthUid = "demo-admin",
                Email = "thandiwe.banda@ukuuhr.demo",
                FirstName = "Thandiwe",
                LastName = "Banda",
                Role = UserRole.HrAdmin,
                UserType = "admin",
                Status = AccountStatus.Active,
                IsFirstLogin = false,
                EmployeeId = employees[1].Id,
                CreatedAt = DateTime.UtcNow.AddDays(-330)
            },
            new UserAccount
            {
                OrganizationId = org.Id,
                AuthUid = "demo-admin",
                Email = "grace.mwape@ukuuhr.demo",
                FirstName = "Grace",
                LastName = "Mwape",
                Role = UserRole.FinancePayrollAdmin,
                UserType = "admin",
                Status = AccountStatus.Active,
                IsFirstLogin = false,
                EmployeeId = employees[3].Id,
                CreatedAt = DateTime.UtcNow.AddDays(-300)
            }
        );
        await db.SaveChangesAsync();

        // ───── License code ─────
        db.LicenseCodes.Add(new LicenseCode
        {
            Code = "UKUU-DEMO-2025-PRO1",
            PlanType = LicensePlanType.Annual,
            Status = LicenseStatus.Used,
            ExpiresAt = DateTime.UtcNow.AddDays(300),
            ActivatedAt = DateTime.UtcNow.AddDays(-65),
            ActivatedByOrganizationId = org.Id,
            ActivatedByEmail = "chungu.chama@ukuuhr.demo",
            Notes = "Demo annual subscription."
        });
        await db.SaveChangesAsync();

        // ───── Hikvision devices ─────
        var device1 = new HikvisionDevice
        {
            OrganizationId = org.Id,
            Name = "Main Entrance — HQ",
            IpAddress = "192.168.1.100",
            Port = 80,
            Username = "admin",
            Password = "hikvision123",
            DeviceSerial = "DS-K1T80420231215AA001",
            Location = "Ground Floor — Main Entrance",
            IsActive = true,
            LastSyncAt = DateTime.UtcNow.AddHours(-2),
            LastSuccessfulSyncAt = DateTime.UtcNow.AddHours(-2),
            TotalEventsSynced = 1240,
            CreatedAt = DateTime.UtcNow.AddDays(-90)
        };
        var device2 = new HikvisionDevice
        {
            OrganizationId = org.Id,
            Name = "Engineering — Side Gate",
            IpAddress = "192.168.1.101",
            Port = 80,
            Username = "admin",
            Password = "hikvision456",
            DeviceSerial = "DS-K1T80420231215AA002",
            Location = "Engineering Block — Side Gate",
            IsActive = true,
            LastSyncAt = DateTime.UtcNow.AddHours(-1),
            LastSuccessfulSyncAt = DateTime.UtcNow.AddHours(-1),
            TotalEventsSynced = 680,
            CreatedAt = DateTime.UtcNow.AddDays(-60)
        };
        db.HikvisionDevices.AddRange(device1, device2);
        await db.SaveChangesAsync();

        // ───── Hikvision clock events (last 7 days) ─────
        var hikRnd = new Random(99);
        var startDate = DateTime.UtcNow.AddDays(-7);
        for (int dayOffset = 0; dayOffset < 7; dayOffset++)
        {
            var date = startDate.AddDays(dayOffset);
            if (date.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday) continue;

            foreach (var emp in employees.Where(e => e.Status != EmploymentStatus.Inactive))
            {
                if (hikRnd.NextDouble() > 0.85) continue;

                var checkInHour = 8;
                var checkInMin = hikRnd.Next(0, 60);
                if (hikRnd.NextDouble() < 0.15) { checkInHour = 9; checkInMin = hikRnd.Next(0, 15); }

                var checkOutHour = 17;
                var checkOutMin = hikRnd.Next(0, 30);
                if (hikRnd.NextDouble() < 0.25) { checkOutHour = 18; checkOutMin = hikRnd.Next(0, 45); }

                var deviceId = hikRnd.NextDouble() > 0.5 ? device1.Id : device2.Id;

                db.HikvisionClockEvents.Add(new HikvisionClockEvent
                {
                    OrganizationId = org.Id,
                    DeviceId = deviceId,
                    EmployeeCode = emp.EmployeeCode ?? emp.Id.ToString(),
                    EmployeeId = emp.Id,
                    EventTime = new DateTime(date.Year, date.Month, date.Day, checkInHour, checkInMin, 0),
                    EventType = ClockEventType.CheckIn,
                    VerifyMode = "Card",
                    InOutMode = "Entrance",
                    SyncedAt = DateTime.UtcNow.AddHours(-2),
                    IsProcessed = true,
                    ProcessedAt = DateTime.UtcNow.AddHours(-2)
                });

                db.HikvisionClockEvents.Add(new HikvisionClockEvent
                {
                    OrganizationId = org.Id,
                    DeviceId = deviceId,
                    EmployeeCode = emp.EmployeeCode ?? emp.Id.ToString(),
                    EmployeeId = emp.Id,
                    EventTime = new DateTime(date.Year, date.Month, date.Day, checkOutHour, checkOutMin, 0),
                    EventType = ClockEventType.CheckOut,
                    VerifyMode = "Card",
                    InOutMode = "Exit",
                    SyncedAt = DateTime.UtcNow.AddHours(-2),
                    IsProcessed = true,
                    ProcessedAt = DateTime.UtcNow.AddHours(-2)
                });
            }
        }
        await db.SaveChangesAsync();

        // ───── Overtime records (auto-calculated from attendance) ─────
        // Generate overtime for employees who worked late in the past 30 days
        foreach (var emp in employees.Where(e => e.Status != EmploymentStatus.Inactive).Take(5))
        {
            for (int dayOffset = 0; dayOffset < 30; dayOffset += 3)
            {
                var date = DateTime.Today.AddDays(-dayOffset);
                if (date.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday) continue;

                // 40% chance of overtime on a given day
                if (hikRnd.NextDouble() > 0.4) continue;

                var otHours = Math.Round(hikRnd.NextDouble() * 2.5 + 0.5, 2); // 0.5 - 3.0 hours
                var checkOutHour = 17 + (int)otHours;
                var startTime = new DateTime(date.Year, date.Month, date.Day, 17, 0, 0);
                var endTime = startTime.AddHours(otHours);

                var isHoliday = db.LeaveHolidays.Any(h => h.Date.Date == date.Date);
                var isWeekend = date.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday;

                OvertimeRateType rateType = OvertimeRateType.Standard;
                double multiplier = 1.5;
                if (isHoliday) { rateType = OvertimeRateType.PublicHoliday; multiplier = 2.5; }
                else if (isWeekend) { rateType = OvertimeRateType.RestDay; multiplier = 2.0; }
                else if (otHours > 2) { rateType = OvertimeRateType.DoubleTime; multiplier = 2.0; }

                var status = dayOffset < 7 ? OvertimeStatus.Pending : (hikRnd.NextDouble() > 0.3 ? OvertimeStatus.Approved : OvertimeStatus.Pending);

                db.OvertimeRecords.Add(new OvertimeRecord
                {
                    OrganizationId = org.Id,
                    EmployeeId = emp.Id,
                    EmployeeName = emp.FullName,
                    Date = date,
                    StartTime = startTime,
                    EndTime = endTime,
                    Hours = otHours,
                    RateType = rateType,
                    RateMultiplier = multiplier,
                    HourlyRate = emp.EffectiveHourlyRate,
                    Source = dayOffset % 6 == 0 ? OvertimeSource.Hikvision : OvertimeSource.AutoCalculated,
                    Status = status,
                    Reason = $"Auto-calculated from attendance ({otHours:F1}h overtime)",
                    ApprovedByEmail = status == OvertimeStatus.Approved ? "admin@ukuuhr.demo" : null,
                    ApprovedAt = status == OvertimeStatus.Approved ? DateTime.UtcNow.AddDays(-dayOffset / 2) : null,
                    ApproverNotes = status == OvertimeStatus.Approved ? "Approved by admin." : null,
                    CreatedAt = DateTime.UtcNow.AddDays(-dayOffset)
                });
            }
        }
        await db.SaveChangesAsync();

        // ───── Phase 1: FR-003 / FR-004 / FR-005 — Tolerance + Shifts + Assignments ─────
        await Phase1Seeder.SeedAsync(db);

        // ───── Phase 2: FR-006 / FR-007 / FR-008 — Holiday calendar ─────
        await Phase2Seeder.SeedAsync(db);

        // ───── Phase 3: FR-001 — Multi-vendor device integration ─────
        await Phase3Seeder.SeedAsync(db);
    }
}
