using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace UkuuHr.Tests;

/// <summary>
/// Integration tests for the UkuuHR REST API endpoints.
/// Uses WebApplicationFactory to spin up the app in-memory with SQLite.
/// </summary>
public class ApiIntegrationTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly HttpClient _client;

    public ApiIntegrationTests(WebApplicationFactory<Program> factory)
    {
        _client = factory.CreateClient();
    }

    // ═════════════════════════════════════════════════════════════════════
    // Health & Availability
    // ═════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task Health_Endpoint_Returns_Ok()
    {
        var response = await _client.GetAsync("/health");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task Liveness_Endpoint_Returns_Ok()
    {
        var response = await _client.GetAsync("/liveness");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<LivenessResponse>();
        Assert.NotNull(body);
        Assert.Equal("alive", body!.Status);
    }

    [Fact]
    public async Task Readiness_Endpoint_Returns_Ok()
    {
        var response = await _client.GetAsync("/readiness");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<ReadinessResponse>();
        Assert.NotNull(body);
        Assert.Equal("ready", body!.Status);
        Assert.Equal("connected", body.Db);
    }

    // ═════════════════════════════════════════════════════════════════════
    // System / Modules
    // ═════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task System_Metrics_Returns_Modules()
    {
        var response = await _client.GetAsync("/api/system/metrics");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<SystemMetricsResponse>();
        Assert.NotNull(body);
        Assert.Equal("ok", body!.Status);
        Assert.NotEmpty(body.ModulesActive);
    }

    [Fact]
    public async Task Modules_Endpoint_Lists_All_Modules()
    {
        var response = await _client.GetAsync("/api/modules");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<ModulesResponse>();
        Assert.NotNull(body);
        Assert.NotEmpty(body!.Modules);
        Assert.Contains(body.Modules, m => m.Key == "employees");
        Assert.Contains(body.Modules, m => m.Key == "attendance");
        Assert.Contains(body.Modules, m => m.Key == "shifts");
        Assert.Contains(body.Modules, m => m.Key == "leave");
        Assert.Contains(body.Modules, m => m.Key == "payroll");
    }

    // ═════════════════════════════════════════════════════════════════════
    // Employee Management
    // ═════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task Employees_List_Returns_Employees()
    {
        var response = await _client.GetAsync("/api/employees");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<EmployeeListResponse>();
        Assert.NotNull(body);
        Assert.True(body!.Total > 0);
        Assert.NotEmpty(body.Employees);
    }

    [Fact]
    public async Task Employees_List_Filters_By_Department()
    {
        var response = await _client.GetAsync("/api/employees?department=Engineering");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<EmployeeListResponse>();
        Assert.NotNull(body);
        Assert.All(body!.Employees, e => Assert.Equal("Engineering", e.Department));
    }

    [Fact]
    public async Task Employees_Get_ById_Returns_Employee()
    {
        // First get the list to find an ID
        var listResp = await _client.GetFromJsonAsync<EmployeeListResponse>("/api/employees");
        Assert.NotNull(listResp);
        var firstId = listResp!.Employees[0].Id;

        var response = await _client.GetAsync($"/api/employees/{firstId}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<EmployeeDetailResponse>();
        Assert.NotNull(body);
        Assert.Equal(firstId, body!.Id);
        Assert.NotEmpty(body.FullName);
    }

    [Fact]
    public async Task Employees_Get_NonExistent_Returns_NotFound()
    {
        var response = await _client.GetAsync("/api/employees/99999");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Employees_Stats_Returns_Statistics()
    {
        var response = await _client.GetAsync("/api/employees/stats");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<EmployeeStatsResponse>();
        Assert.NotNull(body);
        Assert.True(body!.Total > 0);
        Assert.True(body.Active > 0);
        Assert.True(body.TotalPayroll > 0);
    }

    // ═════════════════════════════════════════════════════════════════════
    // Attendance Management
    // ═════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task Attendance_Today_Returns_Breakdown()
    {
        var response = await _client.GetAsync("/api/attendance/today");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<AttendanceTodayResponse>();
        Assert.NotNull(body);
        Assert.NotNull(body!.Breakdown);
        Assert.True(body.Total >= 0);
    }

    [Fact]
    public async Task Attendance_List_Accepts_Date_Range()
    {
        var from = DateTime.Today.AddDays(-30).ToString("yyyy-MM-dd");
        var to = DateTime.Today.ToString("yyyy-MM-dd");

        var response = await _client.GetAsync($"/api/attendance?from={from}&to={to}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<AttendanceListResponse>();
        Assert.NotNull(body);
        Assert.Equal(from, body!.From);
        Assert.Equal(to, body.To);
    }

    // ═════════════════════════════════════════════════════════════════════
    // Shift Management
    // ═════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task Shifts_List_Returns_Shifts()
    {
        var response = await _client.GetAsync("/api/shifts");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<ShiftsListResponse>();
        Assert.NotNull(body);
        Assert.True(body!.Total > 0);
        Assert.NotEmpty(body.Shifts);
    }

    [Fact]
    public async Task Shifts_Get_ById_Returns_Shift()
    {
        var listResp = await _client.GetFromJsonAsync<ShiftsListResponse>("/api/shifts");
        Assert.NotNull(listResp);
        var firstId = listResp!.Shifts[0].Id;

        var response = await _client.GetAsync($"/api/shifts/{firstId}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<ShiftDetailResponse>();
        Assert.NotNull(body);
        Assert.Equal(firstId, body!.Id);
        Assert.NotEmpty(body.Name);
    }

    // ═════════════════════════════════════════════════════════════════════
    // Leave Management
    // ═════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task Leave_Types_Returns_Types()
    {
        var response = await _client.GetAsync("/api/leave/types");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<LeaveTypesResponse>();
        Assert.NotNull(body);
        Assert.True(body!.Total > 0);
        Assert.Contains(body.Types, t => t.Name == "Annual Leave");
    }

    [Fact]
    public async Task Leave_List_Returns_Requests()
    {
        var response = await _client.GetAsync("/api/leave");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<LeaveListResponse>();
        Assert.NotNull(body);
        Assert.True(body!.Total >= 0);
    }

    [Fact]
    public async Task Leave_List_Filters_By_Status()
    {
        var response = await _client.GetAsync("/api/leave?status=Pending");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<LeaveListResponse>();
        Assert.NotNull(body);
        Assert.All(body!.Requests, r => Assert.Equal("Pending", r.Status));
    }

    // ═════════════════════════════════════════════════════════════════════
    // Payroll
    // ═════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task Payroll_AttendanceSummary_Returns_Data()
    {
        var today = DateTime.Today;
        var response = await _client.GetAsync($"/api/payroll/attendance-summary?year={today.Year}&month={today.Month}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<PayrollSummaryResponse>();
        Assert.NotNull(body);
        Assert.NotEmpty(body!.Rows);
    }

    // ═════════════════════════════════════════════════════════════════════
    // Devices
    // ═════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task Devices_List_Returns_Devices()
    {
        var response = await _client.GetAsync("/api/devices");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<DevicesListResponse>();
        Assert.NotNull(body);
        Assert.True(body!.Total >= 0);
    }

    // ═════════════════════════════════════════════════════════════════════
    // Notifications
    // ═════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task Notifications_List_Returns_Notifications()
    {
        var response = await _client.GetAsync("/api/notifications");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<NotificationsListResponse>();
        Assert.NotNull(body);
        Assert.True(body!.Total >= 0);
    }

    // ═════════════════════════════════════════════════════════════════════
    // Unauthenticated API — key not set, endpoints open in dev
    // ═════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task Unauthenticated_Request_To_Api_Returns_Ok_When_No_ApiKey()
    {
        // When UKUU_API_KEY is not set (default in tests), API endpoints should
        // still work (development mode — open access).
        var response = await _client.GetAsync("/api/employees");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    // ═════════════════════════════════════════════════════════════════════
    // Response DTOs (for JSON deserialization)
    // ═════════════════════════════════════════════════════════════════════

    private record LivenessResponse(string Status, DateTime Timestamp);
    private record ReadinessResponse(string Status, DateTime Timestamp, string Db);
    private record SystemMetricsResponse(string Status, double UptimeSeconds, DateTime Timestamp, string[] ModulesActive);

    private record ModulesResponse(string? Organization, ModuleInfoRecord[] Modules);
    private record ModuleInfoRecord(string Key, string Name, bool Implemented, string? Endpoint);

    // Employees
    private record EmployeeListResponse(int Total, int OrganizationId, EmployeeRecord[] Employees);
    private record EmployeeRecord(int Id, string? EmployeeCode, string FirstName, string Surname, string FullName, string? Department, string? Email, string? Status, string? JobTitle);
    private record EmployeeDetailResponse(int Id, string? EmployeeCode, string FullName, string? Department, string? Email, string? JobTitle, string? Status);
    private record EmployeeStatsResponse(int Total, int Active, int Probation, int Inactive, int Terminated, double TotalPayroll, object? ByDepartment);

    // Attendance
    private record AttendanceTodayResponse(string Date, int Total, object Breakdown, object[] Records);
    private record AttendanceListResponse(int Total, string From, string To, object[] Records);

    // Shifts
    private record ShiftsListResponse(int Total, ShiftRecord[] Shifts);
    private record ShiftRecord(int Id, string Name, string? Description, string Kind, string? Color, string StartTime, double PlannedHours, bool IsActive);
    private record ShiftDetailResponse(int Id, string Name, string? Description, string Kind, string? Color, string TimeWindow, double PlannedHours, bool IsActive);

    // Leave
    private record LeaveTypesResponse(int Total, LeaveTypeRecord[] Types);
    private record LeaveTypeRecord(int Id, string Name, string? Color, int DefaultDays, bool IsPaid);
    private record LeaveListResponse(int Total, LeaveRequestRecord[] Requests);
    private record LeaveRequestRecord(int Id, int EmployeeId, string? EmployeeName, string LeaveType, string Status, string StartDate, string EndDate);

    // Payroll
    private record PayrollSummaryResponse(string Period, string? Organization, DateTime GeneratedAt, int TotalEmployees, object[] Rows);

    // Devices
    private record DevicesListResponse(int Total, object[] Devices);

    // Notifications
    private record NotificationsListResponse(int Total, int Unread, object[] Notifications);
}
