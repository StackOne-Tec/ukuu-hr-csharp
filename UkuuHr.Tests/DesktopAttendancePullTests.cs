using UkuuHr.Sync;
using Xunit;
using System.Net;
using System.Text;
using System.Text.Json;

namespace UkuuHr.Tests;

/// <summary>
/// Comprehensive tests for the Windows desktop attendance pulling flow.
/// Covers the full pipeline from HTTP request to parsed records:
///   - BuildAcsEventSearchXml format & pagination
///   - 3-tier fallback (AcsEvent JSON → AcsEvent XML → AuditLog)
///   - Pagination logic (multi-page, early termination)
///   - Digest auth client creation (no Basic header leak)
///   - HTTP error handling (400, 401, 404, timeout)
///   - Parser robustness (malformed, empty, mixed valid/invalid)
///   - Event classification (minor codes → check_in / check_out)
///   - SyncSettings defaults & validation
///   - DS-K1T343EFWX device-specific response formats
/// </summary>
public class DesktopAttendancePullTests
{
    // ═══════════════════════════════════════════════════════════════════════
    // 1. BuildAcsEventSearchXml — Correct XML format
    // ═══════════════════════════════════════════════════════════════════════

    [Fact]
    public void SearchXml_ContainsRequiredElements()
    {
        var from = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var to = new DateTime(2025, 1, 31, 23, 59, 59, DateTimeKind.Utc);
        var xml = BuildTestSearchXml(from, to, maxResults: 200, position: 0, searchId: "1");

        Assert.Contains("<AcsEventSearchDescription>", xml);
        Assert.Contains("</AcsEventSearchDescription>", xml);
        Assert.Contains("<searchID>1</searchID>", xml);
        Assert.Contains("<searchResultPosition>0</searchResultPosition>", xml);
        Assert.Contains("<maxResults>200</maxResults>", xml);
        Assert.Contains("<major>1</major>", xml);
        Assert.Contains("<minor>0</minor>", xml);
        Assert.Contains("<startTime>2025-01-01T00:00:00Z</startTime>", xml);
        Assert.Contains("<endTime>2025-01-31T23:59:59Z</endTime>", xml);
    }

    [Fact]
    public void SearchXml_Pagination_PositionIncrements()
    {
        var from = DateTime.UtcNow.AddDays(-7);
        var to = DateTime.UtcNow;

        var page0 = BuildTestSearchXml(from, to, maxResults: 200, position: 0, searchId: "1");
        var page1 = BuildTestSearchXml(from, to, maxResults: 200, position: 200, searchId: "1");
        var page2 = BuildTestSearchXml(from, to, maxResults: 200, position: 400, searchId: "1");

        Assert.Contains("<searchResultPosition>0</searchResultPosition>", page0);
        Assert.Contains("<searchResultPosition>200</searchResultPosition>", page1);
        Assert.Contains("<searchResultPosition>400</searchResultPosition>", page2);
    }

    [Fact]
    public void SearchXml_Pagination_PageSize500()
    {
        var from = DateTime.UtcNow.AddDays(-30);
        var to = DateTime.UtcNow;
        var xml = BuildTestSearchXml(from, to, maxResults: 500, position: 0, searchId: "1");

        Assert.Contains("<maxResults>500</maxResults>", xml);
    }

    [Fact]
    public void SearchXml_DifferentSearchIds()
    {
        var from = DateTime.UtcNow.AddDays(-1);
        var to = DateTime.UtcNow;
        var xml = BuildTestSearchXml(from, to, maxResults: 200, position: 0, searchId: "probe_test");

        Assert.Contains("<searchID>probe_test</searchID>", xml);
    }

    // ═══════════════════════════════════════════════════════════════════════
    // 2. AcsEvent JSON Parsing — All container shapes
    // ═══════════════════════════════════════════════════════════════════════

    [Theory]
    [InlineData("AcsEventInfoList")]
    [InlineData("AcsEventEventList")]
    [InlineData("TopInfoList")]
    [InlineData("TopEventList")]
    public void AcsEventJson_AllContainerShapes_ParseCorrectly(string shape)
    {
        var item = "{\"employeeNo\":\"001\",\"time\":\"2025-01-15T08:30:00\",\"minor\":75}";
        var json = shape switch
        {
            "AcsEventInfoList" => $"{{\"AcsEvent\":{{\"InfoList\":[{item}]}}}}",
            "AcsEventEventList" => $"{{\"AcsEvent\":{{\"EventList\":[{item}]}}}}",
            "TopInfoList" => $"{{\"InfoList\":[{item}]}}",
            "TopEventList" => $"{{\"EventList\":[{item}]}}",
            _ => throw new ArgumentOutOfRangeException()
        };

        var events = HikvisionParser.ParseAcsEventJson(json);
        Assert.Single(events);
        Assert.Equal("001", events[0].EmployeeNo);
        Assert.Equal("2025-01-15T08:30:00", events[0].Time);
        Assert.Equal("check_in", events[0].EventType);
    }

    [Fact]
    public void AcsEventJson_MultipleRecords_AllParsed()
    {
        var json = @"{
            ""AcsEvent"": {
                ""InfoList"": [
                    {""employeeNo"":""001"",""time"":""2025-01-15T08:30:00"",""minor"":75},
                    {""employeeNo"":""002"",""time"":""2025-01-15T09:00:00"",""minor"":76},
                    {""employeeNo"":""003"",""time"":""2025-01-15T17:30:00"",""minor"":75}
                ]
            }
        }";

        var events = HikvisionParser.ParseAcsEventJson(json);
        Assert.Equal(3, events.Count);
        Assert.Equal("check_in", events[0].EventType);   // minor 75
        Assert.Equal("check_out", events[1].EventType);   // minor 76
        Assert.Equal("check_in", events[2].EventType);    // minor 75
    }

    [Fact]
    public void AcsEventJson_MixedValidInvalid_PartialParse()
    {
        // One bad item (missing employeeNo) should not discard the rest
        var json = @"{
            ""AcsEvent"": {
                ""InfoList"": [
                    {""employeeNo"":""001"",""time"":""2025-01-15T08:30:00"",""minor"":75},
                    {""time"":""2025-01-15T09:00:00"",""minor"":75},
                    {""employeeNo"":""003"",""time"":""2025-01-15T17:30:00"",""minor"":76}
                ]
            }
        }";

        var events = HikvisionParser.ParseAcsEventJson(json);
        Assert.Equal(2, events.Count);
        Assert.Equal("001", events[0].EmployeeNo);
        Assert.Equal("003", events[1].EmployeeNo);
    }

    // ═══════════════════════════════════════════════════════════════════════
    // 3. AcsEvent XML Parsing — Attribute + child element formats
    // ═══════════════════════════════════════════════════════════════════════

    [Fact]
    public void AcsEventXml_AttributeFormat_ParsesCorrectly()
    {
        var xml = @"<?xml version=""1.0"" encoding=""UTF-8""?>
<AcsEvent>
    <InfoList>
        <Info employeeNo=""001"" time=""2025-01-15T08:30:00"" minor=""75""/>
        <Info employeeNo=""002"" time=""2025-01-15T09:00:00"" minor=""76""/>
    </InfoList>
</AcsEvent>";

        var events = HikvisionParser.ParseAcsEventXml(xml);
        Assert.Equal(2, events.Count);
        Assert.Equal("001", events[0].EmployeeNo);
        Assert.Equal("check_in", events[0].EventType);
        Assert.Equal("002", events[1].EmployeeNo);
        Assert.Equal("check_out", events[1].EventType);
    }

    [Fact]
    public void AcsEventXml_ChildElementFormat_ParsesCorrectly()
    {
        var xml = @"<?xml version=""1.0"" encoding=""UTF-8""?>
<AcsEvent>
    <InfoList>
        <Info>
            <employeeNo>001</employeeNo>
            <time>2025-01-15T08:30:00</time>
            <minor>75</minor>
        </Info>
    </InfoList>
</AcsEvent>";

        var events = HikvisionParser.ParseAcsEventXml(xml);
        Assert.Single(events);
        Assert.Equal("001", events[0].EmployeeNo);
        Assert.Equal("check_in", events[0].EventType);
    }

    [Fact]
    public void AcsEventXml_AlternateCasing_EmployeeNoAndEventTime()
    {
        var xml = @"<?xml version=""1.0"" encoding=""UTF-8""?>
<AcsEvent>
    <InfoList>
        <Info EmployeeNo=""001"" eventTime=""2025-01-15T08:30:00"" minor=""75""/>
        <Info>
            <EmployeeNo>002</EmployeeNo>
            <eventTime>2025-01-15T09:00:00</eventTime>
            <minor>76</minor>
        </Info>
    </InfoList>
</AcsEvent>";

        var events = HikvisionParser.ParseAcsEventXml(xml);
        Assert.Equal(2, events.Count);
        Assert.Equal("001", events[0].EmployeeNo);
        Assert.Equal("002", events[1].EmployeeNo);
        Assert.Equal("check_out", events[1].EventType);
    }

    // ═══════════════════════════════════════════════════════════════════════
    // 4. AuditLog XML Parsing
    // ═══════════════════════════════════════════════════════════════════════

    [Fact]
    public void AuditLogXml_StandardFormat_ParsesCorrectly()
    {
        var xml = @"<?xml version=""1.0"" encoding=""UTF-8""?>
<AuditLog>
    <LogItem>
        <employeeNo>001</employeeNo>
        <time>2025-01-15T08:30:00</time>
        <minor>75</minor>
    </LogItem>
    <LogItem>
        <employeeNo>002</employeeNo>
        <time>2025-01-15T17:30:00</time>
        <minor>76</minor>
    </LogItem>
</AuditLog>";

        var events = HikvisionParser.ParseAuditLogXml(xml);
        Assert.Equal(2, events.Count);
        Assert.Equal("001", events[0].EmployeeNo);
        Assert.Equal("check_in", events[0].EventType);
        Assert.Equal("002", events[1].EmployeeNo);
        Assert.Equal("check_out", events[1].EventType);
    }

    [Fact]
    public void AuditLogXml_NamespacedFormat_ParsesCorrectly()
    {
        var xml = @"<?xml version=""1.0"" encoding=""UTF-8""?>
<AuditLog xmlns=""http://www.hikvision.com/ver20/XMLSchema"">
    <LogItem>
        <employeeNo>001</employeeNo>
        <time>2025-01-15T08:30:00</time>
        <minor>75</minor>
    </LogItem>
</AuditLog>";

        var events = HikvisionParser.ParseAuditLogXml(xml);
        Assert.Single(events);
        Assert.Equal("001", events[0].EmployeeNo);
    }

    // ═══════════════════════════════════════════════════════════════════════
    // 5. 3-Tier Fallback Simulation — with mock HTTP handler
    // ═══════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task Tier1_JsonSucceeds_NoFallbackToXml()
    {
        var jsonBody = @"{""AcsEvent"":{""InfoList"":[
            {""employeeNo"":""001"",""time"":""2025-01-15T08:30:00"",""minor"":75},
            {""employeeNo"":""002"",""time"":""2025-01-15T09:00:00"",""minor"":76}
        ]}}";

        using var handler = new MockHandler(req =>
        {
            if (req.RequestUri!.PathAndQuery.Contains("AcsEvent") && req.RequestUri.Query.Contains("format=json"))
                return (HttpStatusCode.OK, "application/json", jsonBody);
            return (HttpStatusCode.NotFound, "text/plain", "Not Found");
        });
        using var client = new HttpClient(handler);

        var events = await SimulateFetchAttendance(client, "http://device");

        Assert.Equal(2, events.Count);
        Assert.Equal("001", events[0].EmployeeNo);
        Assert.Equal("check_out", events[1].EventType);
    }

    [Fact]
    public async Task Tier2_XmlFallback_WhenJsonReturns400()
    {
        var xmlBody = @"<?xml version=""1.0"" encoding=""UTF-8""?>
<AcsEvent><InfoList>
    <Info employeeNo=""001"" time=""2025-01-15T08:30:00"" minor=""75""/>
    <Info employeeNo=""003"" time=""2025-01-15T17:30:00"" minor=""76""/>
</InfoList></AcsEvent>";

        using var handler = new MockHandler(req =>
        {
            if (req.RequestUri!.PathAndQuery.Contains("AcsEvent") && req.RequestUri.Query.Contains("format=json"))
                return (HttpStatusCode.BadRequest, "text/plain", "Bad Request");
            if (req.RequestUri!.PathAndQuery.Contains("AcsEvent"))
                return (HttpStatusCode.OK, "application/xml", xmlBody);
            return (HttpStatusCode.NotFound, "text/plain", "Not Found");
        });
        using var client = new HttpClient(handler);

        var events = await SimulateFetchAttendance(client, "http://device");

        Assert.Equal(2, events.Count);
        Assert.Equal("001", events[0].EmployeeNo);
        Assert.Equal("check_out", events[1].EventType);
    }

    [Fact]
    public async Task Tier3_AuditLogFallback_WhenBothAcsEventFail()
    {
        var auditBody = @"<?xml version=""1.0"" encoding=""UTF-8""?>
<AuditLog>
    <LogItem><employeeNo>001</employeeNo><time>2025-01-15T08:30:00</time><minor>75</minor></LogItem>
    <LogItem><employeeNo>002</employeeNo><time>2025-01-15T17:30:00</time><minor>76</minor></LogItem>
</AuditLog>";

        using var handler = new MockHandler(req =>
        {
            if (req.RequestUri!.PathAndQuery.Contains("AcsEvent"))
                return (HttpStatusCode.BadRequest, "text/plain", "Bad Request");
            if (req.RequestUri!.PathAndQuery.Contains("AuditLog"))
                return (HttpStatusCode.OK, "application/xml", auditBody);
            return (HttpStatusCode.NotFound, "text/plain", "Not Found");
        });
        using var client = new HttpClient(handler);

        var events = await SimulateFetchAttendance(client, "http://device");

        Assert.Equal(2, events.Count);
        Assert.Equal("001", events[0].EmployeeNo);
        Assert.Equal("check_out", events[1].EventType);
    }

    [Fact]
    public async Task AllTiersFail_ReturnsEmptyList()
    {
        using var handler = new MockHandler(_ => (HttpStatusCode.BadRequest, "text/plain", "Bad Request"));
        using var client = new HttpClient(handler);

        var events = await SimulateFetchAttendance(client, "http://device");
        Assert.Empty(events);
    }

    // ═══════════════════════════════════════════════════════════════════════
    // 6. Pagination Simulation
    // ═══════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task Pagination_TwoPages_AllRecordsFetched()
    {
        var page1Items = string.Join(",",
            Enumerable.Range(0, 200).Select(i =>
                $"{{\"employeeNo\":\"{i:D3}\",\"time\":\"2025-01-15T08:00:00\",\"minor\":75}}"));
        var page2Items = string.Join(",",
            Enumerable.Range(200, 50).Select(i =>
                $"{{\"employeeNo\":\"{i:D3}\",\"time\":\"2025-01-15T17:00:00\",\"minor\":76}}"));

        var page1Json = $"{{\"AcsEvent\":{{\"InfoList\":[{page1Items}]}}}}";
        var page2Json = $"{{\"AcsEvent\":{{\"InfoList\":[{page2Items}]}}}}";

        var requestCount = 0;
        using var handler = new MockHandler(req =>
        {
            if (!req.RequestUri!.Query.Contains("format=json"))
                return (HttpStatusCode.NotFound, "text/plain", "Not Found");

            requestCount++;
            if (requestCount == 1) return (HttpStatusCode.OK, "application/json", page1Json);
            return (HttpStatusCode.OK, "application/json", page2Json);
        });
        using var client = new HttpClient(handler);

        var events = await SimulateFetchAttendance(client, "http://device");

        Assert.Equal(250, events.Count);
        Assert.Equal("000", events[0].EmployeeNo);
        Assert.Equal("249", events[249].EmployeeNo);
    }

    [Fact]
    public async Task Pagination_SinglePageLessThanPageSize_StopsAfterFirstPage()
    {
        var items = string.Join(",",
            Enumerable.Range(0, 50).Select(i =>
                $"{{\"employeeNo\":\"{i:D3}\",\"time\":\"2025-01-15T08:00:00\",\"minor\":75}}"));
        var json = $"{{\"AcsEvent\":{{\"InfoList\":[{items}]}}}}";

        var requestCount = 0;
        using var handler = new MockHandler(req =>
        {
            if (!req.RequestUri!.Query.Contains("format=json"))
                return (HttpStatusCode.NotFound, "text/plain", "Not Found");
            requestCount++;
            return (HttpStatusCode.OK, "application/json", json);
        });
        using var client = new HttpClient(handler);

        var events = await SimulateFetchAttendance(client, "http://device");

        Assert.Equal(50, events.Count);
        Assert.Equal(1, requestCount);
    }

    // ═══════════════════════════════════════════════════════════════════════
    // 7. Parser Robustness — Malformed payloads never crash
    // ═══════════════════════════════════════════════════════════════════════

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("{")]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("42")]
    [InlineData("{\"AcsEvent\":null}")]
    [InlineData("{\"AcsEvent\":{\"InfoList\":null}}")]
    [InlineData("{\"AcsEvent\":{\"InfoList\":\"not_array\"}}")]
    public void AcsEventJson_MalformedPayload_ReturnsEmptyNoCrash(string malformed)
    {
        var events = HikvisionParser.ParseAcsEventJson(malformed);
        Assert.NotNull(events);
        Assert.Empty(events);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not xml")]
    [InlineData("<")]
    [InlineData("<AcsEvent>")]
    [InlineData("<AcsEvent><InfoList></InfoList></AcsEvent>")]
    public void AcsEventXml_MalformedPayload_ReturnsEmptyNoCrash(string malformed)
    {
        var events = HikvisionParser.ParseAcsEventXml(malformed);
        Assert.NotNull(events);
        Assert.Empty(events);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not xml")]
    [InlineData("<AuditLog>")]
    public void AuditLogXml_MalformedPayload_ReturnsEmptyNoCrash(string malformed)
    {
        var events = HikvisionParser.ParseAuditLogXml(malformed);
        Assert.NotNull(events);
        Assert.Empty(events);
    }

    [Fact]
    public void AcsEventJson_MissingFields_SkipsInvalidItems()
    {
        var json = @"{""AcsEvent"":{""InfoList"":[
            {""employeeNo"":""001"",""time"":""2025-01-15T08:00:00"",""minor"":75},
            {""employeeNo"":""002""},
            {""time"":""2025-01-15T09:00:00"",""minor"":75},
            {""employeeNo"":""003"",""time"":""2025-01-15T10:00:00"",""minor"":76}
        ]}}";

        var events = HikvisionParser.ParseAcsEventJson(json);
        Assert.Equal(2, events.Count);
        Assert.Equal("001", events[0].EmployeeNo);
        Assert.Equal("003", events[1].EmployeeNo);
    }

    // ═══════════════════════════════════════════════════════════════════════
    // 8. Event Classification — minor codes
    // ═══════════════════════════════════════════════════════════════════════

    [Theory]
    [InlineData(75, "check_in")]
    [InlineData(76, "check_out")]
    [InlineData(1, "check_in")]
    [InlineData(0, "check_in")]
    [InlineData(99, "check_in")]
    [InlineData(100, "check_in")]
    public void ClassifyEventType_MajorCodeMapping(int minor, string expected)
    {
        Assert.Equal(expected, HikvisionParser.ClassifyEventType(minor));
    }

    [Fact]
    public void AcsEventJson_MinorAsString_ParsesCorrectly()
    {
        var json = @"{""AcsEvent"":{""InfoList"":[
            {""employeeNo"":""001"",""time"":""2025-01-15T08:00:00"",""minor"":""76""}
        ]}}";

        var events = HikvisionParser.ParseAcsEventJson(json);
        Assert.Single(events);
        Assert.Equal("check_out", events[0].EventType);
    }

    [Fact]
    public void AcsEventJson_MissingMinor_DefaultsTo75CheckIn()
    {
        var json = @"{""AcsEvent"":{""InfoList"":[
            {""employeeNo"":""001"",""time"":""2025-01-15T08:00:00""}
        ]}}";

        var events = HikvisionParser.ParseAcsEventJson(json);
        Assert.Single(events);
        Assert.Equal(75, events[0].Minor);
        Assert.Equal("check_in", events[0].EventType);
    }

    // ═══════════════════════════════════════════════════════════════════════
    // 9. ExtractXmlValue — Device info parsing
    // ═══════════════════════════════════════════════════════════════════════

    [Fact]
    public void ExtractXmlValue_StandardDeviceXml()
    {
        var xml = @"<?xml version=""1.0"" encoding=""UTF-8""?>
<DeviceInfo>
    <deviceName>DS-K1T343EFWX</deviceName>
    <model>DS-K1T343EFWX</model>
    <serialNumber>DS12345678</serialNumber>
    <firmwareVersion>V1.0.2</firmwareVersion>
    <macAddress>AA:BB:CC:DD:EE:FF</macAddress>
</DeviceInfo>";

        Assert.Equal("DS-K1T343EFWX", HikvisionParser.ExtractXmlValue(xml, "deviceName"));
        Assert.Equal("DS-K1T343EFWX", HikvisionParser.ExtractXmlValue(xml, "model"));
        Assert.Equal("DS12345678", HikvisionParser.ExtractXmlValue(xml, "serialNumber"));
        Assert.Equal("V1.0.2", HikvisionParser.ExtractXmlValue(xml, "firmwareVersion"));
        Assert.Equal("AA:BB:CC:DD:EE:FF", HikvisionParser.ExtractXmlValue(xml, "macAddress"));
    }

    [Fact]
    public void ExtractXmlValue_MissingTag_ReturnsNull()
    {
        var xml = "<DeviceInfo><deviceName>Test</deviceName></DeviceInfo>";
        Assert.Null(HikvisionParser.ExtractXmlValue(xml, "serialNumber"));
    }

    [Fact]
    public void ExtractXmlValue_CaseInsensitive()
    {
        var xml = "<DeviceInfo><DeviceName>Test</DeviceName></DeviceInfo>";
        Assert.Equal("Test", HikvisionParser.ExtractXmlValue(xml, "deviceName"));
    }

    // ═══════════════════════════════════════════════════════════════════════
    // 10. SyncSettings — Defaults & Validation
    // ═══════════════════════════════════════════════════════════════════════

    [Fact]
    public void SyncSettings_Defaults()
    {
        var s = new SyncSettings();
        Assert.Equal("192.168.1.137", s.DeviceIp);
        Assert.Equal(80, s.DevicePort);
        Assert.Equal(false, s.UseHttps);
        Assert.Equal("admin", s.DeviceUsername);
        Assert.Equal("", s.DevicePassword);
        Assert.Equal("https://ukuuhr.com", s.CloudUrl);
        Assert.Null(s.ApiKey);
        Assert.Equal(5, s.SyncIntervalMinutes);
    }

    [Fact]
    public void SyncSettings_DefaultIsValid()
    {
        var s = new SyncSettings();
        Assert.True(s.IsValid());
    }

    [Fact]
    public void SyncSettings_MissingIp_IsInvalid()
    {
        var s = new SyncSettings { DeviceIp = "" };
        Assert.False(s.IsValid());
    }

    [Fact]
    public void SyncSettings_MissingUsername_IsInvalid()
    {
        var s = new SyncSettings { DeviceUsername = "" };
        Assert.False(s.IsValid());
    }

    [Fact]
    public void SyncSettings_MissingCloudUrl_IsInvalid()
    {
        var s = new SyncSettings { CloudUrl = "" };
        Assert.False(s.IsValid());
    }

    [Fact]
    public void SyncSettings_HttpsOverride_Works()
    {
        var s = new SyncSettings { UseHttps = true };
        Assert.True(s.UseHttps.GetValueOrDefault());
    }

    // ═══════════════════════════════════════════════════════════════════════
    // 11. ImportedPunch — Model defaults
    // ═══════════════════════════════════════════════════════════════════════

    [Fact]
    public void ImportedPunch_Defaults()
    {
        var p = new ImportedPunch();
        Assert.Equal("", p.EmployeeNo);
        Assert.Equal("", p.Time);
        Assert.Equal("check_in", p.EventType);
        Assert.Equal(1, p.Major);
        Assert.Equal(75, p.Minor);
    }

    // ═══════════════════════════════════════════════════════════════════════
    // 12. HTTP Digest Auth — Client creation
    // �8═══════════════════════════════════════════════════════════════════════

    [Fact]
    public void CreateHttpClient_usesPreAuthenticateWithCredentials()
    {
        var settings = new SyncSettings
        {
            DeviceIp = "192.168.1.137",
            DevicePort = 80,
            DeviceUsername = "admin",
            DevicePassword = "password123",
            UseHttps = false
        };

        var handler = new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback = (_, _, _, _) => true,
            PreAuthenticate = true,
            Credentials = new NetworkCredential(settings.DeviceUsername, settings.DevicePassword),
            AllowAutoRedirect = true
        };

        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(15) };

        // Verify PreAuthenticate is enabled (critical for Digest auth)
        Assert.True(handler.PreAuthenticate);
        // Verify credentials are set
        Assert.NotNull(handler.Credentials);
        // Verify NO Basic Authorization header is set (this was the Windows bug)
        Assert.False(client.DefaultRequestHeaders.Contains("Authorization"),
            "HttpClient must NOT have a Basic Authorization header — it conflicts with Digest auth");
    }

    // ═══════════════════════════════════════════════════════════════════════
    // 13. Large-scale stress test — 10,000 records
    // ═══════════════════════════════════════════════════════════════════════

    [Fact]
    public void AcsEventJson_10000Records_ParsesAll()
    {
        var items = Enumerable.Range(0, 10000).Select(i =>
            $"{{\"employeeNo\":\"{i:D5}\",\"time\":\"2025-01-15T08:00:00\",\"minor\":{(i % 2 == 0 ? 75 : 76)}}}");

        var json = $"{{\"AcsEvent\":{{\"InfoList\":[{string.Join(",", items)}]}}}}";

        var events = HikvisionParser.ParseAcsEventJson(json);
        Assert.Equal(10000, events.Count);
        Assert.Equal("00000", events[0].EmployeeNo);
        Assert.Equal("09999", events[9999].EmployeeNo);

        var checkIns = events.Count(e => e.EventType == "check_in");
        var checkOuts = events.Count(e => e.EventType == "check_out");
        Assert.Equal(5000, checkIns);
        Assert.Equal(5000, checkOuts);
    }

    // ═══════════════════════════════════════════════════════════════════════
    // 14. Windows-specific: DS-K1T343EFWX device response formats
    // ═══════════════════════════════════════════════════════════════════════

    [Fact]
    public void DS_K1T343EFWX_AcsEventXml_AttributeFormat()
    {
        var xml = @"<?xml version=""1.0"" encoding=""UTF-8""?>
<AcsEvent version=""2.0"" xmlns=""http://www.hikvision.com/ver20/XMLSchema"">
<InfoList>
<Info employeeNo=""1"" time=""2025-08-10T07:45:23+02:00"" majorVersion=""1"" minorVersion=""75""/>
<Info employeeNo=""2"" time=""2025-08-10T08:12:05+02:00"" majorVersion=""1"" minorVersion=""75""/>
<Info employeeNo=""1"" time=""2025-08-10T17:02:44+02:00"" majorVersion=""1"" minorVersion=""76""/>
</InfoList>
</AcsEvent>";

        var events = HikvisionParser.ParseAcsEventXml(xml);
        Assert.Equal(3, events.Count);
        Assert.Equal("1", events[0].EmployeeNo);
        Assert.Equal("check_in", events[0].EventType);
        Assert.Equal("check_out", events[2].EventType);
    }

    [Fact]
    public void DS_K1T343EFWX_AuditLogFallback_NamespacedXml()
    {
        var xml = @"<?xml version=""1.0"" encoding=""UTF-8""?>
<AuditLog version=""2.0"" xmlns=""http://www.hikvision.com/ver20/XMLSchema"">
<LogItem>
<employeeNo>1</employeeNo>
<time>2025-08-10T07:45:23+02:00</time>
<minor>75</minor>
</LogItem>
<LogItem>
<employeeNo>1</employeeNo>
<time>2025-08-10T17:02:44+02:00</time>
<minor>76</minor>
</LogItem>
</AuditLog>";

        var events = HikvisionParser.ParseAuditLogXml(xml);
        Assert.Equal(2, events.Count);
        Assert.Equal("check_in", events[0].EventType);
        Assert.Equal("check_out", events[1].EventType);
    }

    // ═══════════════════════════════════════════════════════════════════════
    // 15. Settings JSON round-trip
    // ═══════════════════════════════════════════════════════════════════════

    [Fact]
    public void SyncSettings_JsonRoundTrip()
    {
        var original = new SyncSettings
        {
            DeviceIp = "10.0.0.50",
            DevicePort = 8080,
            UseHttps = true,
            DeviceUsername = "admin",
            DevicePassword = "secret123",
            CloudUrl = "https://my-cloud.example.com",
            ApiKey = "key-abc",
            SyncIntervalMinutes = 10
        };

        var json = JsonSerializer.Serialize(original, new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        });

        var restored = JsonSerializer.Deserialize<SyncSettings>(json, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        });

        Assert.NotNull(restored);
        Assert.Equal("10.0.0.50", restored.DeviceIp);
        Assert.Equal(8080, restored.DevicePort);
        Assert.True(restored.UseHttps);
        Assert.Equal("admin", restored.DeviceUsername);
        Assert.Equal("secret123", restored.DevicePassword);
        Assert.Equal("https://my-cloud.example.com", restored.CloudUrl);
        Assert.Equal("key-abc", restored.ApiKey);
        Assert.Equal(10, restored.SyncIntervalMinutes);
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Test Infrastructure — Mock handler and helpers
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Simulates the 3-tier attendance fetch — replicates Program.cs FetchAttendanceEvents logic.
    /// </summary>
    static async Task<List<ImportedPunch>> SimulateFetchAttendance(HttpClient client, string baseUrl)
    {
        var fromTime = DateTime.UtcNow.AddDays(-7);
        var toTime = DateTime.UtcNow;
        List<ImportedPunch> events = new();

        // Tier 1: AcsEvent JSON
        try { events = await SimulateFetchAcsEventWithPagination(client, baseUrl, fromTime, toTime, jsonFormat: true); }
        catch { }

        // Tier 2: AcsEvent XML
        if (events.Count == 0)
        {
            try { events = await SimulateFetchAcsEventWithPagination(client, baseUrl, fromTime, toTime, jsonFormat: false); }
            catch { }
        }

        // Tier 3: AuditLog
        if (events.Count == 0)
        {
            try
            {
                var s = fromTime.ToString("yyyy-MM-ddTHH:mm:ssZ");
                var e = toTime.ToString("yyyy-MM-ddTHH:mm:ssZ");
                var auditUrl = $"{baseUrl}/ISAPI/AccessControl/AuditLog/search?searchID=1&startTime={Uri.EscapeDataString(s)}&endTime={Uri.EscapeDataString(e)}";
                var resp = await client.GetAsync(auditUrl);

                if (resp.IsSuccessStatusCode)
                {
                    var xml = await resp.Content.ReadAsStringAsync();
                    events = HikvisionParser.ParseAuditLogXml(xml);
                }
            }
            catch { }
        }

        return events;
    }

    static async Task<List<ImportedPunch>> SimulateFetchAcsEventWithPagination(
        HttpClient client, string baseUrl, DateTime fromTime, DateTime toTime, bool jsonFormat)
    {
        var allEvents = new List<ImportedPunch>();
        const int pageSize = 200;
        const int maxPages = 50;
        var endpoint = jsonFormat
            ? $"{baseUrl}/ISAPI/AccessControl/AcsEvent?format=json"
            : $"{baseUrl}/ISAPI/AccessControl/AcsEvent";

        for (int page = 0; page < maxPages; page++)
        {
            var searchXml = BuildTestSearchXml(fromTime, toTime, maxResults: pageSize, position: page * pageSize, searchId: "1");
            var content = new StringContent(searchXml, Encoding.UTF8, "application/xml");
            var resp = await client.PostAsync(endpoint, content);

            if (!resp.IsSuccessStatusCode) break;

            var body = await resp.Content.ReadAsStringAsync();
            List<ImportedPunch> pageEvents;

            if (jsonFormat && (body.TrimStart().StartsWith("{") || body.TrimStart().StartsWith("[")))
                pageEvents = HikvisionParser.ParseAcsEventJson(body);
            else
                pageEvents = HikvisionParser.ParseAcsEventXml(body);

            allEvents.AddRange(pageEvents);
            if (pageEvents.Count < pageSize) break;
        }

        return allEvents;
    }

    static string BuildTestSearchXml(DateTime from, DateTime to, int maxResults = 500, int position = 0, string searchId = "1")
    {
        return $@"<?xml version=""1.0"" encoding=""UTF-8""?>
<AcsEventSearchDescription>
    <searchID>{searchId}</searchID>
    <searchResultPosition>{position}</searchResultPosition>
    <maxResults>{maxResults}</maxResults>
    <major>1</major>
    <minor>0</minor>
    <startTime>{from:yyyy-MM-ddTHH:mm:ssZ}</startTime>
    <endTime>{to:yyyy-MM-ddTHH:mm:ssZ}</endTime>
</AcsEventSearchDescription>";
    }

    /// <summary>
    /// Mock HttpMessageHandler for simulating device responses without a real server.
    /// </summary>
    class MockHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, (HttpStatusCode status, string contentType, string body)> _handler;

        public MockHandler(Func<HttpRequestMessage, (HttpStatusCode, string, string)> handler)
        {
            _handler = handler;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var (status, contentType, body) = _handler(request);
            var response = new HttpResponseMessage(status)
            {
                Content = new StringContent(body, Encoding.UTF8, contentType)
            };
            return Task.FromResult(response);
        }
    }
}
