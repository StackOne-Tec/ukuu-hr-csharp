using UkuuHr.Models;
using UkuuHr.Services.Devices;
using Xunit;

namespace UkuuHr.Tests;

/// <summary>
/// Unit tests for vendor connector parsers (FR-001).
/// Each test feeds a sample vendor payload and verifies the parser
/// extracts the correct NormalizedClockEvent records.
/// </summary>
public class VendorConnectorTests
{
    private static AttendanceDevice Device(DeviceVendor v) => new()
    {
        Id = 1,
        Name = "Test",
        Vendor = v,
        Mode = DeviceIntegrationMode.RestApi,
        IpAddress = "10.0.0.1",
        Port = 80
    };

    // ───────────── Hikvision ISAPI XML ─────────────

    [Fact]
    public void Hikvision_Parses_AuditLog_Xml()
    {
        var xml = @"<?xml version=""1.0"" encoding=""UTF-8""?>
<CASearchResult>
  <LogItem>
    <employeeNo>UKU-001</employeeNo>
    <time>2025-07-07 08:05:30</time>
    <major>1</major>
    <minor>75</minor>
    <VerifyMode>Card</VerifyMode>
    <inAndOutMode>1</inAndOutMode>
  </LogItem>
  <LogItem>
    <employeeNo>UKU-001</employeeNo>
    <time>2025-07-07 17:15:00</time>
    <major>1</major>
    <minor>76</minor>
    <VerifyMode>Fingerprint</VerifyMode>
  </LogItem>
</CASearchResult>";

        var events = HikvisionRestConnector.ParseHikvisionXml(xml, Device(DeviceVendor.Hikvision));

        Assert.Equal(2, events.Count);
        Assert.Equal("UKU-001", events[0].EmployeeCode);
        Assert.Equal(ClockEventType.CheckIn, events[0].EventType);
        Assert.Equal(ClockEventType.CheckOut, events[1].EventType);
        Assert.Equal(new DateTime(2025, 7, 7, 8, 5, 30), events[0].EventTime);
    }

    [Fact]
    public void Hikvision_Returns_Empty_On_Malformed_Xml()
    {
        var events = HikvisionRestConnector.ParseHikvisionXml("not xml", Device(DeviceVendor.Hikvision));
        Assert.Empty(events);
    }

    // ───────────── ZKTeco JSON ─────────────

    [Fact]
    public void ZKTeco_Parses_Json_AttLog()
    {
        var json = """
        {
          "data": [
            { "pin": "1001", "time": "2025-07-07 08:00:00", "type": 0, "verifyMode": "Fingerprint" },
            { "pin": "1001", "time": "2025-07-07 17:30:00", "type": 1 },
            { "pin": "1002", "time": "2025-07-07 09:00:00", "type": 0 }
          ]
        }
        """;

        var events = ZKTecoRestConnector.ParseZKTecoJson(json);

        Assert.Equal(3, events.Count);
        Assert.Equal("1001", events[0].EmployeeCode);
        Assert.Equal(ClockEventType.CheckIn, events[0].EventType);
        Assert.Equal(ClockEventType.CheckOut, events[1].EventType);
        Assert.Equal(ClockEventType.CheckIn, events[2].EventType);
        Assert.Equal("Fingerprint", events[0].VerifyMode);
    }

    [Fact]
    public void ZKTeco_Handles_Empty_Data_Array()
    {
        var events = ZKTecoRestConnector.ParseZKTecoJson("""{"data":[]}""");
        Assert.Empty(events);
    }

    [Fact]
    public void ZKTeco_Handles_Missing_Data_Property()
    {
        var events = ZKTecoRestConnector.ParseZKTecoJson("""{"status":"ok"}""");
        Assert.Empty(events);
    }

    // ───────────── Suprema BioStar 2 JSON ─────────────

    [Fact]
    public void Suprema_Parses_EventCollection()
    {
        var json = """
        {
          "EventCollection": [
            { "user_id": "EMP001", "datetime": "2025-07-07T08:00:00.000Z", "event_type_id": 4352 },
            { "user_id": "EMP001", "datetime": "2025-07-07T17:00:00.000Z", "event_type_id": 16384 }
          ]
        }
        """;

        var events = SupremaRestConnector.ParseSupremaJson(json);

        Assert.Equal(2, events.Count);
        Assert.Equal("EMP001", events[0].EmployeeCode);
        Assert.Equal(ClockEventType.CheckIn, events[0].EventType);
        Assert.Equal(ClockEventType.CheckOut, events[1].EventType); // 0x4000 = checkout
    }

    // ───────────── Dahua key=value response ─────────────

    [Fact]
    public void Dahua_Parses_KeyValue_Blocks()
    {
        var body = """
        EmployeeNo=UKU-001
        Time=2025-07-07 08:00:00
        Method=Verify
        CardType=RFID

        EmployeeNo=UKU-001
        Time=2025-07-07 17:30:00
        Method=Logout
        CardType=RFID

        """;

        var events = DahuaRestConnector.ParseDahuaResponse(body);

        Assert.Equal(2, events.Count);
        Assert.Equal("UKU-001", events[0].EmployeeCode);
        Assert.Equal(ClockEventType.CheckIn, events[0].EventType);
        Assert.Equal(ClockEventType.CheckOut, events[1].EventType); // "Logout"
    }

    // ───────────── Anviz cloud JSON ─────────────

    [Fact]
    public void Anviz_Parses_Checking_Records()
    {
        var json = """
        {
          "data": [
            { "pin": "ANV001", "checkinTime": "2025-07-07 08:00:00", "checkinType": "0" },
            { "pin": "ANV001", "checkinTime": "2025-07-07 17:00:00", "checkinType": "1" }
          ]
        }
        """;

        var events = AnvizRestConnector.ParseAnvizJson(json);

        Assert.Equal(2, events.Count);
        Assert.Equal("ANV001", events[0].EmployeeCode);
        Assert.Equal(ClockEventType.CheckIn, events[0].EventType);
        Assert.Equal(ClockEventType.CheckOut, events[1].EventType);
    }

    // ───────────── Matrix COSEC JSON ─────────────

    [Fact]
    public void Matrix_Parses_Events_Array()
    {
        var json = """
        {
          "events": [
            { "user_id": "MX001", "time": "2025-07-07 08:00:00", "type": "CheckIn", "verify_mode": "Face" },
            { "user_id": "MX001", "time": "2025-07-07 17:00:00", "type": "CheckOut" }
          ]
        }
        """;

        var events = MatrixRestConnector.ParseMatrixJson(json);

        Assert.Equal(2, events.Count);
        Assert.Equal("MX001", events[0].EmployeeCode);
        Assert.Equal(ClockEventType.CheckIn, events[0].EventType);
        Assert.Equal(ClockEventType.CheckOut, events[1].EventType);
        Assert.Equal("Face", events[0].VerifyMode);
    }

    // ───────────── eSSL CSV-like response ─────────────

    [Fact]
    public void Essl_Parses_Csv_Like_Rows()
    {
        var body = """
        1001,2025-07-07 08:00:00,0,15,0
        1001,2025-07-07 17:30:00,1,15,0
        1002,2025-07-07 09:00:00,0,1,0

        """;

        var events = EsslRestConnector.ParseEsslResponse(body);

        Assert.Equal(3, events.Count);
        Assert.Equal("1001", events[0].EmployeeCode);
        Assert.Equal(ClockEventType.CheckIn, events[0].EventType);
        Assert.Equal(ClockEventType.CheckOut, events[1].EventType);
        Assert.Equal("15", events[0].VerifyMode);
    }

    // ───────────── CSV connector ─────────────

    [Fact]
    public void CsvConnector_Parses_Standard_Format()
    {
        var tempPath = Path.Combine(Path.GetTempPath(), $"ukuu-test-{Guid.NewGuid():N}.csv");
        File.WriteAllText(tempPath, """
            EmployeeCode,EventTime,EventType,VerifyMode
            UKU-001,2025-07-07 08:00:00,CheckIn,Card
            UKU-001,2025-07-07 17:30:00,CheckOut,Fingerprint
            UKU-002,2025-07-07 09:00:00,CheckIn,Face
            """);

        try
        {
            var events = CsvConnector.ParseCsv(tempPath, null);
            Assert.Equal(3, events.Count);
            Assert.Equal("UKU-001", events[0].EmployeeCode);
            Assert.Equal(ClockEventType.CheckIn, events[0].EventType);
            Assert.Equal(ClockEventType.CheckOut, events[1].EventType);
            Assert.Equal("UKU-002", events[2].EmployeeCode);
        }
        finally { File.Delete(tempPath); }
    }

    [Fact]
    public void CsvConnector_Filters_By_Since_Date()
    {
        var tempPath = Path.Combine(Path.GetTempPath(), $"ukuu-test-{Guid.NewGuid():N}.csv");
        File.WriteAllText(tempPath, """
            EmployeeCode,EventTime,EventType,VerifyMode
            UKU-001,2025-07-01 08:00:00,CheckIn,Card
            UKU-001,2025-07-10 08:00:00,CheckIn,Card
            UKU-001,2025-07-15 08:00:00,CheckIn,Card
            """);

        try
        {
            var events = CsvConnector.ParseCsv(tempPath, new DateTime(2025, 7, 5));
            Assert.Equal(2, events.Count); // Skipped the July 1 event.
            Assert.True(events.All(e => e.EventTime >= new DateTime(2025, 7, 5)));
        }
        finally { File.Delete(tempPath); }
    }

    [Fact]
    public void CsvConnector_Handles_Alternate_EventType_Names()
    {
        var tempPath = Path.Combine(Path.GetTempPath(), $"ukuu-test-{Guid.NewGuid():N}.csv");
        File.WriteAllText(tempPath, """
            EmployeeCode,EventTime,EventType
            UKU-001,2025-07-07 08:00:00,in
            UKU-001,2025-07-07 12:00:00,break_out
            UKU-001,2025-07-07 13:00:00,break_in
            UKU-001,2025-07-07 17:00:00,out
            """);

        try
        {
            var events = CsvConnector.ParseCsv(tempPath, null);
            Assert.Equal(4, events.Count);
            Assert.Equal(ClockEventType.CheckIn, events[0].EventType);
            Assert.Equal(ClockEventType.BreakOut, events[1].EventType);
            Assert.Equal(ClockEventType.BreakIn, events[2].EventType);
            Assert.Equal(ClockEventType.CheckOut, events[3].EventType);
        }
        finally { File.Delete(tempPath); }
    }

    // ───────────── Registry ─────────────

    [Fact]
    public void Registry_Resolves_Rest_Connectors_For_Each_Vendor()
    {
        var connectors = new IDeviceConnector[]
        {
            new HikvisionRestConnector(),
            new ZKTecoRestConnector(),
            new SupremaRestConnector(),
            new DahuaRestConnector(),
            new AnvizRestConnector(),
            new MatrixRestConnector(),
            new EsslRestConnector()
        };
        var registry = new DeviceConnectorRegistry(connectors);

        foreach (var vendor in Enum.GetValues<DeviceVendor>())
        {
            var resolved = registry.Resolve(vendor, DeviceIntegrationMode.RestApi);
            Assert.NotNull(resolved);
            Assert.Equal(vendor, resolved!.Vendor);
        }
    }

    [Fact]
    public void Registry_Returns_Null_For_Unregistered_Combination()
    {
        var registry = new DeviceConnectorRegistry(Array.Empty<IDeviceConnector>());
        var resolved = registry.Resolve(DeviceVendor.Hikvision, DeviceIntegrationMode.RestApi);
        Assert.Null(resolved);
    }

    [Fact]
    public void SdkConnector_Returns_Clear_NotRegistered_Error()
    {
        var connector = new ZKTecoSdkConnector();
        var device = Device(DeviceVendor.ZKTeco);
        device.Mode = DeviceIntegrationMode.Sdk;

        var (reachable, error) = connector.PingAsync(device).GetAwaiter().GetResult();

        Assert.False(reachable);
        Assert.Contains("SDK connector not registered", error);
        Assert.Contains("ZKTeco", error);
    }

    [Fact]
    public void TcpConnector_Returns_Clear_NotRegistered_Error()
    {
        var connector = new SupremaTcpConnector();
        var device = Device(DeviceVendor.Suprema);
        device.Mode = DeviceIntegrationMode.TcpIp;
        device.Port = 5005;

        var result = connector.SyncAsync(device, null).GetAwaiter().GetResult();

        Assert.False(result.Success);
        Assert.Contains("TCP/IP connector not registered", result.ErrorMessage);
        Assert.Contains("5005", result.ErrorMessage);
    }
}
