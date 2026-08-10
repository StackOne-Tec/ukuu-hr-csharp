using UkuuHr.Sync;
using Xunit;

namespace UkuuHr.Tests;

/// <summary>
/// Exhaustive tests (1000+ cases) for the Hikvision ISAPI parsing logic that
/// ships in the DESKTOP app (UkuuHrSync - the Windows .exe / macOS .dmg).
/// Every payload the bridge can receive from a real terminal is pinned here:
///   - AcsEvent JSON   (InfoList / EventList - all shapes, casings, minor codes)
///   - AcsEvent XML    (Info elements - attributes + child elements, all casings)
///   - AuditLog XML    (plain + namespaced, entity/unicode/whitespace variants)
///   - ExtractXmlValue (device-info probing)
///   - minor -> check_in / check_out classification
///   - SyncSettings    (defaults, validation, round-trip)
///   - ImportedPunch   (model defaults)
/// Plus the robustness contract: malformed payloads return empty (never crash)
/// and one bad item never discards the rest of a valid batch.
/// </summary>
public class DesktopHikvisionParserTests
{
    // ═══════════════════════════════ Helpers ═══════════════════════════════

    private static readonly string[] Containers =
        { "AcsEvent.InfoList", "AcsEvent.EventList", "Top.InfoList", "Top.EventList" };

    private static string AcsItem(string empKey, string empNo, string timeKey, string time, string minorField)
        => "{\"" + empKey + "\":\"" + empNo + "\",\"" + timeKey + "\":\"" + time + "\",\"minor\":" + minorField + "}";

    private static string AcsContainer(string inner, string container) => container switch
    {
        "AcsEvent.InfoList" => "{\"AcsEvent\":{\"InfoList\":[" + inner + "]}}",
        "AcsEvent.EventList" => "{\"AcsEvent\":{\"EventList\":[" + inner + "]}}",
        "Top.InfoList" => "{\"InfoList\":[" + inner + "]}",
        "Top.EventList" => "{\"EventList\":[" + inner + "]}",
        _ => throw new ArgumentException("Unknown container: " + container)
    };

    private static string AuditXml(IEnumerable<string> items, bool namespaced, string root = "AuditLog")
    {
        var ns = namespaced ? " xmlns=\"http://www.hikvision.com/ver20/XMLSchema\"" : "";
        return "<?xml version=\"1.0\" encoding=\"UTF-8\"?><" + root + ns + ">" + string.Concat(items) + "</" + root + ">";
    }

    private static string AuditItem(string empNo, string time, string minor = "75")
        => "<LogItem><employeeNo>" + empNo + "</employeeNo><time>" + time + "</time><minor>" + minor + "</minor></LogItem>";

    // AcsEvent XML helpers
    private static string AcsXmlInfoAttr(string empNo, string time, string minor = "75")
        => "<Info employeeNo=\"" + empNo + "\" time=\"" + time + "\" minor=\"" + minor + "\"/>";

    private static string AcsXmlInfoElems(string empNo, string time, string minor = "75")
        => "<Info><employeeNo>" + empNo + "</employeeNo><time>" + time + "</time><minor>" + minor + "</minor></Info>";

    private static string AcsXmlWrap(string inner, string root = "AcsEvent", bool namespaced = false)
    {
        var ns = namespaced ? " xmlns=\"http://www.hikvision.com/ver20/XMLSchema\"" : "";
        return "<?xml version=\"1.0\" encoding=\"UTF-8\"?><" + root + ns + "><InfoList>" + inner + "</InfoList></" + root + ">";
    }

    // The desktop model is ImportedPunch (named to avoid clashing with the Web
    // app's global-namespace ImportedEvent).
    private static (int ins, int outs) Split(List<ImportedPunch> events)
        => (events.Count(e => e.EventType == "check_in"), events.Count(e => e.EventType == "check_out"));

    // ═══════════════════════ 1. AcsEvent JSON - valid payloads ═══════════════════════

    public static IEnumerable<object[]> AcsEventMatrix()
    {
        var minors = new[] { 75, 76, 0, 1, 2, 5, 100, 999, -1, 3, 6, 7 };
        var empKeys = new[] { "employeeNo", "EmployeeNo" };
        var timeKeys = new[] { "time", "eventTime" };
        var cases = new List<object[]>();

        // Single-event matrix: every container x field casing x minor code.
        foreach (var c in Containers)
            foreach (var e in empKeys)
                foreach (var t in timeKeys)
                    foreach (var m in minors)
                    {
                        var json = AcsContainer(AcsItem(e, "EMP-" + m, t, "2026-08-06T08:00:00Z", m.ToString()), c);
                        cases.Add(new object[] { json, 1, m == 76 ? 0 : 1, m == 76 ? 1 : 0 });
                    }

        // Multi-event batches with mixed minor codes.
        var rng = new Random(42);
        for (var n = 2; n <= 8; n++)
        {
            for (var trial = 0; trial < 6; trial++)
            {
                var items = new List<string>();
                var outs = 0;
                for (var i = 0; i < n; i++)
                {
                    var minor = minors[rng.Next(minors.Length)];
                    items.Add(AcsItem("employeeNo", "E" + i, "time", "2026-08-06T0" + i + ":00:00Z", minor.ToString()));
                    if (minor == 76) outs++;
                }
                var json = AcsContainer(string.Join(",", items), Containers[rng.Next(Containers.Length)]);
                cases.Add(new object[] { json, n, n - outs, outs });
            }
        }
        return cases;
    }

    [Theory]
    [MemberData(nameof(AcsEventMatrix))]
    public void AcsEvent_parses_valid_payloads(string json, int expectedTotal, int expectedIns, int expectedOuts)
    {
        var events = HikvisionParser.ParseAcsEventJson(json);
        var (ins, outs) = Split(events);
        Assert.Equal(expectedTotal, events.Count);
        Assert.Equal(expectedIns, ins);
        Assert.Equal(expectedOuts, outs);
    }

    /// <summary>String-encoded minor values (some firmware emits them) must parse.</summary>
    public static IEnumerable<object[]> AcsEventStringMinors()
    {
        var minors = new[] { ("\"76\"", 0, 1), ("\"75\"", 1, 0), ("\"abc\"", 1, 0), ("\"\"", 1, 0), ("\" 76 \"", 0, 1) };
        foreach (var c in Containers)
            foreach (var (field, ins, outs) in minors)
                yield return new object[]
                {
                    AcsContainer(AcsItem("employeeNo", "E", "time", "2026-08-06T08:00:00Z", field), c),
                    1, ins, outs
                };
    }

    [Theory]
    [MemberData(nameof(AcsEventStringMinors))]
    public void AcsEvent_parses_string_minor_values(string json, int expectedTotal, int expectedIns, int expectedOuts)
    {
        var events = HikvisionParser.ParseAcsEventJson(json);
        var (ins, outs) = Split(events);
        Assert.Equal(expectedTotal, events.Count);
        Assert.Equal(expectedIns, ins);
        Assert.Equal(expectedOuts, outs);
    }

    // ═══════════════════ 2. AcsEvent JSON - bad items never kill the batch ═══════════════════

    public static IEnumerable<object[]> AcsEventSkips()
    {
        string[] badItems =
        {
            "{\"time\":\"2026-08-06T08:00:00Z\"}",
            "{\"employeeNo\":\"E\"}",
            "{\"employeeNo\":123,\"time\":\"2026-08-06T08:00:00Z\"}",
            "{\"employeeNo\":\"E\",\"time\":123}",
            "null",
            "\"hello\"",
            "42",
            "{\"employeeNo\":\"\",\"time\":\"2026-08-06T08:00:00Z\"}",
            "{\"employeeNo\":\"E\",\"time\":\"\"}",
            "{\"employeeNo\":{\"a\":1},\"time\":\"2026-08-06T08:00:00Z\"}"
        };

        foreach (var c in Containers)
            foreach (var bad in badItems)
                for (var good = 0; good <= 3; good++)
                {
                    var parts = new List<string>();
                    for (var i = 0; i < good; i++)
                        parts.Add(AcsItem("employeeNo", "G" + i, "time", "2026-08-06T08:00:00Z", "75"));
                    parts.Add(bad);
                    yield return new object[] { AcsContainer(string.Join(",", parts), c), good };
                }
    }

    [Theory]
    [MemberData(nameof(AcsEventSkips))]
    public void AcsEvent_skips_bad_items_without_losing_good_ones(string json, int expectedTotal)
    {
        var events = HikvisionParser.ParseAcsEventJson(json);
        Assert.Equal(expectedTotal, events.Count);
        Assert.All(events, e => Assert.Equal("check_in", e.EventType));
    }

    // ═══════════════════ 3. AcsEvent JSON - empty / malformed payloads ═══════════════════

    public static IEnumerable<object[]> AcsEventEmpty()
    {
        var cases = new List<object[]>();

        foreach (var c in Containers)
            foreach (var empty in new[] { "[]", "{}", "null", "\"x\"", "42", "true" })
                cases.Add(new object[] { AcsContainer(empty, c) });

        cases.AddRange(new object[]
        {
            "{}", "{\"AcsEvent\":{}}", "{\"InfoList\":{}}", "{\"EventList\":null}",
            "{\"InfoList\":\"x\"}", "{\"InfoList\":42}", "{\"responseStatusStrg\":\"OK\"}",
            "{\"InfoList\":[]}", "{\"EventList\":[]}", "{\"AcsEvent\":{\"InfoList\":[]}}",
            "{\"AcsEvent\":{\"EventList\":[]}}", "{\"AcsEvent\":null}",
            "{\"InfoList\":[[]]}", "{\"InfoList\":[{\"employeeNo\":\"E\"}]}"
        }.Select(s => new object[] { s! }));

        cases.AddRange(new[] { "{\"infolist\":[]}", "{\"INFOLIST\":[]}", "{\"AcsEvent\":{\"InfoList\":[],\"extra\":1}}" }
            .Select(s => new object[] { s }));

        cases.AddRange(new[]
        {
            "", "not json", "{", "[", "null", "123", "true", "[]",
            "\uFEFF{\"InfoList\":[]}", "  {\"InfoList\":[]}  ", "{\"InfoList\":[]} garbage",
            "{\"InfoList\":[}]}", "{\"AcsEvent\":", "[1,2,", "{\"InfoList\":[\"a\""
        }.Select(s => new object[] { s }));

        return cases;
    }

    [Theory]
    [MemberData(nameof(AcsEventEmpty))]
    public void AcsEvent_returns_empty_without_throwing(string json)
    {
        var events = HikvisionParser.ParseAcsEventJson(json);
        Assert.NotNull(events);
        Assert.Empty(events);
    }

    // ═══════════════════════ 4. AcsEvent XML - valid payloads ═══════════════════════

    public static IEnumerable<object[]> AcsEventXmlCases()
    {
        var cases = new List<object[]>();
        var minors = new[] { 75, 76, 0, 1, 5, 100 };

        // Attribute-based <Info employeeNo="..." time="..." minor="75"/>
        foreach (var ns in new[] { false, true })
            foreach (var m in minors)
            {
                var xml = AcsXmlWrap(AcsXmlInfoAttr("E1", "2026-08-06T08:00:00Z", m.ToString()), namespaced: ns);
                cases.Add(new object[] { xml, 1, m == 76 ? 0 : 1, m == 76 ? 1 : 0 });
            }

        // Element-based <Info><employeeNo>...</employeeNo><time>...</time><minor>...</minor></Info>
        foreach (var ns in new[] { false, true })
            foreach (var m in minors)
            {
                var xml = AcsXmlWrap(AcsXmlInfoElems("E1", "2026-08-06T08:00:00Z", m.ToString()), namespaced: ns);
                cases.Add(new object[] { xml, 1, m == 76 ? 0 : 1, m == 76 ? 1 : 0 });
            }

        // Mixed attribute + element batches
        foreach (var ns in new[] { false, true })
        {
            var inner = AcsXmlInfoAttr("E1", "t1", "75") + AcsXmlInfoElems("E2", "t2", "76");
            var xml = AcsXmlWrap(inner, namespaced: ns);
            cases.Add(new object[] { xml, 2, 1, 1 });
        }

        // Multi-item batches (attribute-style)
        var rng = new Random(2027);
        for (var trial = 0; trial < 60; trial++)
        {
            var n = 1 + trial % 10;
            var ns = trial % 2 == 0;
            var items = new List<string>();
            var outs = 0;
            for (var i = 0; i < n; i++)
            {
                var m = minors[rng.Next(minors.Length)];
                items.Add(AcsXmlInfoAttr("E" + i, "2026-08-06T08:00:00Z", m.ToString()));
                if (m == 76) outs++;
            }
            cases.Add(new object[] { AcsXmlWrap(string.Concat(items), namespaced: ns), n, n - outs, outs });
        }

        // Multi-item batches (element-style)
        for (var trial = 0; trial < 60; trial++)
        {
            var n = 1 + trial % 10;
            var ns = trial % 2 == 0;
            var items = new List<string>();
            var outs = 0;
            for (var i = 0; i < n; i++)
            {
                var m = minors[rng.Next(minors.Length)];
                items.Add(AcsXmlInfoElems("E" + i, "2026-08-06T08:00:00Z", m.ToString()));
                if (m == 76) outs++;
            }
            cases.Add(new object[] { AcsXmlWrap(string.Concat(items), namespaced: ns), n, n - outs, outs });
        }

        // Alternative casing: EmployeeNo / eventTime
        var xmlCasing1 = "<?xml version=\"1.0\"?><AcsEvent><InfoList><Info EmployeeNo=\"E1\" time=\"t1\" minor=\"75\"/></InfoList></AcsEvent>";
        cases.Add(new object[] { xmlCasing1, 1, 1, 0 });

        var xmlCasing2 = "<?xml version=\"1.0\"?><AcsEvent><InfoList><Info employeeNo=\"E1\" eventTime=\"t1\" minor=\"76\"/></InfoList></AcsEvent>";
        cases.Add(new object[] { xmlCasing2, 1, 0, 1 });

        // Alternative root elements
        foreach (var root in new[] { "AcsEvent", "AcsEventSearchResult", "Response", "isapiResponse" })
        {
            var xml = AcsXmlWrap(AcsXmlInfoAttr("E1", "t1", "75"), root);
            cases.Add(new object[] { xml, 1, 1, 0 });
        }

        return cases;
    }

    [Theory]
    [MemberData(nameof(AcsEventXmlCases))]
    public void AcsEventXml_parses_valid_payloads(string xml, int expectedTotal, int expectedIns, int expectedOuts)
    {
        var events = HikvisionParser.ParseAcsEventXml(xml);
        var (ins, outs) = Split(events);
        Assert.Equal(expectedTotal, events.Count);
        Assert.Equal(expectedIns, ins);
        Assert.Equal(expectedOuts, outs);
    }

    // ═══════════════════ 5. AcsEvent XML - bad items never kill the batch ═══════════════════

    public static IEnumerable<object[]> AcsEventXmlSkips()
    {
        var cases = new List<object[]>();

        string[] badInfos =
        {
            "<Info time=\"t\" minor=\"75\"/>",
            "<Info employeeNo=\"E\" minor=\"75\"/>",
            "<Info employeeNo=\"\" time=\"t\" minor=\"75\"/>",
            "<Info employeeNo=\"E\" time=\"\" minor=\"75\"/>",
            "<Info/>",
            "<Info><employeeNo>E</employeeNo></Info>",
            "<Info><time>t</time></Info>",
        };

        foreach (var bad in badInfos)
        {
            cases.Add(new object[] { AcsXmlWrap(bad), 0 });
            var good = AcsXmlInfoAttr("G1", "t1", "75");
            cases.Add(new object[] { AcsXmlWrap(good + bad), 1 });
            var good2 = AcsXmlInfoAttr("G1", "t1", "75") + AcsXmlInfoAttr("G2", "t2", "75");
            cases.Add(new object[] { AcsXmlWrap(good2 + bad), 2 });
        }

        return cases;
    }

    [Theory]
    [MemberData(nameof(AcsEventXmlSkips))]
    public void AcsEventXml_skips_bad_items_without_losing_good_ones(string xml, int expectedTotal)
    {
        var events = HikvisionParser.ParseAcsEventXml(xml);
        Assert.Equal(expectedTotal, events.Count);
    }

    // ═══════════════════ 6. AcsEvent XML - empty / malformed payloads ═══════════════════

    public static IEnumerable<object[]> AcsEventXmlMalformed()
    {
        var fixedBad = new[]
        {
            "", "not xml", "<", "<?xml", "<Info>", "<root><unclosed>", "null", "[]", "{'a':1}",
            "<AcsEvent><InfoList><Info employeeNo=\"E\"</InfoList></AcsEvent>",
            "<!DOCTYPE foo>", "<?xml version=\"1.0\"?>", "\uFEFF<AcsEvent/>",
            "<AcsEvent xmlns=\"http://x\">",
            "<?xml version=\"1.0\"?><AcsEvent><InfoList/></AcsEvent>",
            "<?xml version=\"1.0\"?><AcsEvent></AcsEvent>",
            "<?xml version=\"1.0\"?><AcsEvent><InfoList></InfoList></AcsEvent>",
        };

        var generated = Enumerable.Range(1, 20)
            .Select(i => "<AcsEvent><InfoList>" + new string('a', i) + "<")
            .ToArray();

        return fixedBad.Concat(generated).Select(s => new object[] { s });
    }

    [Theory]
    [MemberData(nameof(AcsEventXmlMalformed))]
    public void AcsEventXml_returns_empty_without_throwing(string xml)
    {
        var events = HikvisionParser.ParseAcsEventXml(xml);
        Assert.NotNull(events);
        Assert.Empty(events);
    }

    // ═══════════════════ 7. AcsEvent XML - namespace handling ═══════════════════

    [Fact]
    public void AcsEventXml_parses_namespaced_Info_elements()
    {
        var xml = "<?xml version=\"1.0\"?>" +
                  "<AcsEvent xmlns=\"http://www.hikvision.com/ver20/XMLSchema\">" +
                  "<InfoList>" +
                  "<Info employeeNo=\"EMP-001\" time=\"2026-08-06T08:00:00Z\" minor=\"75\"/>" +
                  "<Info employeeNo=\"EMP-002\" time=\"2026-08-06T17:00:00Z\" minor=\"76\"/>" +
                  "</InfoList></AcsEvent>";

        var events = HikvisionParser.ParseAcsEventXml(xml);
        Assert.Equal(2, events.Count);
        Assert.Equal("EMP-001", events[0].EmployeeNo);
        Assert.Equal("check_in", events[0].EventType);
        Assert.Equal("EMP-002", events[1].EmployeeNo);
        Assert.Equal("check_out", events[1].EventType);
    }

    [Fact]
    public void AcsEventXml_parses_mixed_attribute_and_element_Info()
    {
        var xml = "<?xml version=\"1.0\"?><AcsEvent><InfoList>" +
                  "<Info employeeNo=\"A1\" time=\"t1\" minor=\"75\"/>" +
                  "<Info><employeeNo>A2</employeeNo><time>t2</time><minor>76</minor></Info>" +
                  "<Info employeeNo=\"A3\" eventTime=\"t3\" minor=\"1\"/>" +
                  "</InfoList></AcsEvent>";

        var events = HikvisionParser.ParseAcsEventXml(xml);
        Assert.Equal(3, events.Count);
        Assert.Equal("A1", events[0].EmployeeNo);
        Assert.Equal("A2", events[1].EmployeeNo);
        Assert.Equal("A3", events[2].EmployeeNo);
        Assert.Equal("check_in", events[0].EventType);
        Assert.Equal("check_out", events[1].EventType);
        Assert.Equal("check_in", events[2].EventType);
    }

    // ═══════════════════ 8. AuditLog XML - valid payloads ═══════════════════

    public static IEnumerable<object[]> AuditLogCases()
    {
        var cases = new List<object[]>();
        var rng = new Random(2026);

        int[][] patterns =
        {
            new[] { 75 }, new[] { 76 },
            new[] { 75, 76 }, new[] { 75, 75, 75 }, new[] { 76, 76, 76 },
            new[] { 75, 76, 75, 76, 75 }, new[] { 0, 1, 5 }, new[] { 76, 0, 76, 1 },
            Enumerable.Repeat(75, 10).ToArray(),
            Enumerable.Range(0, 10).Select(i => i % 2 == 0 ? 76 : 75).ToArray()
        };
        foreach (var ns in new[] { false, true })
            foreach (var pattern in patterns)
            {
                var outs = pattern.Count(m => m == 76);
                var items = pattern.Select((m, i) => AuditItem("E" + i, "2026-08-06T08:00:00Z", m.ToString()));
                cases.Add(new object[] { AuditXml(items, ns), pattern.Length, pattern.Length - outs, outs });
            }

        foreach (var ns in new[] { false, true })
        {
            cases.Add(new object[] { AuditXml(new[] { AuditItem("E", "t", "\"76\"") }, ns), 1, 1, 0 });
            cases.Add(new object[] { AuditXml(new[] { AuditItem("E", "t", "abc") }, ns), 1, 1, 0 });
            cases.Add(new object[] { AuditXml(new[] { AuditItem("E", "t", "") }, ns), 1, 1, 0 });
            cases.Add(new object[] { AuditXml(new[] { AuditItem("E", "t", " 76 ") }, ns), 1, 0, 1 });
            cases.Add(new object[] { AuditXml(new[] { "<LogItem><employeeNo>E</employeeNo><time>t</time></LogItem>" }, ns), 1, 1, 0 });
        }

        foreach (var ns in new[] { false, true })
        {
            cases.Add(new object[] { AuditXml(new[] { "<LogItem><time>t</time><minor>76</minor></LogItem>" }, ns), 0, 0, 0 });
            cases.Add(new object[] { AuditXml(new[] { "<LogItem><employeeNo>E</employeeNo><minor>76</minor></LogItem>" }, ns), 0, 0, 0 });
            cases.Add(new object[] { AuditXml(new[] { "<LogItem><employeeNo></employeeNo><time>t</time></LogItem>" }, ns), 0, 0, 0 });
            cases.Add(new object[] { AuditXml(new[] { "<LogItem><employeeNo>E</employeeNo><time></time></LogItem>" }, ns), 0, 0, 0 });
            cases.Add(new object[] { AuditXml(new[] { "<LogItem><employeeNo><EmployeeNo>X</EmployeeNo></employeeNo><time>t</time></LogItem>" }, ns), 1, 1, 0 });
        }

        var minorPool = new[] { 75, 76, 0, 1, 5, 100 };
        for (var trial = 0; trial < 140; trial++)
        {
            var n = 1 + (trial * 7) % 8;
            var ns = trial % 2 == 0;
            var items = new List<string>();
            var outs = 0;
            for (var i = 0; i < n; i++)
            {
                var m = minorPool[rng.Next(minorPool.Length)];
                items.Add(AuditItem("E" + i, "2026-08-06T08:00:00Z", m.ToString()));
                if (m == 76) outs++;
            }
            cases.Add(new object[] { AuditXml(items, ns), n, n - outs, outs });
        }

        foreach (var ns in new[] { false, true })
            foreach (var root in new[] { "AuditLog", "AuditLogSearch", "Response", "SearchResult", "isapiResponse" })
                cases.Add(new object[] { AuditXml(new[] { AuditItem("E", "t"), AuditItem("E2", "t2", "76") }, ns, root), 2, 1, 1 });

        var noisy = new[]
        {
            "<!-- device response -->" + AuditItem("E", "t", "76"),
            "<LogItem><employeeNo>E</employeeNo><time><![CDATA[2026-08-06T08:00:00Z]]></time><minor>76</minor></LogItem>",
            "<LogItem id=\"9\">" + AuditItem("E", "t", "76") + "</LogItem>",
            "  <LogItem>  <employeeNo>  E  </employeeNo> <time>t</time> <minor>76</minor> </LogItem>  ",
            "<SearchResult>" + AuditItem("E", "t", "76") + "</SearchResult>"
        };
        foreach (var ns in new[] { false, true })
        {
            foreach (var n in noisy)
                cases.Add(new object[] { AuditXml(new[] { n }, ns), 1, 0, 1 });
            var ab = "<A>" + AuditItem("E", "t", "76") + "<B>" + AuditItem("E2", "t2", "75") + "</B></A>";
            cases.Add(new object[] { AuditXml(new[] { ab }, ns), 2, 1, 1 });
        }

        foreach (var ns in new[] { false, true })
            foreach (var emp in new[] { "Mung", "Zhong", "a&amp;b", "A&amp;B", "&quot;Q&quot;", "x&apos;y" })
                cases.Add(new object[] { AuditXml(new[] { AuditItem(emp, "t") }, ns), 1, 1, 0 });

        return cases;
    }

    [Theory]
    [MemberData(nameof(AuditLogCases))]
    public void AuditLog_parses_items_and_classifies(string xml, int expectedTotal, int expectedIns, int expectedOuts)
    {
        var events = HikvisionParser.ParseAuditLogXml(xml);
        var (ins, outs) = Split(events);
        Assert.Equal(expectedTotal, events.Count);
        Assert.Equal(expectedIns, ins);
        Assert.Equal(expectedOuts, outs);
    }

    // ═══════════════════ 9. AuditLog XML - malformed payloads ═══════════════════

    public static IEnumerable<object[]> AuditLogMalformed()
    {
        var fixedBad = new[]
        {
            "", "not xml", "<", "<?xml", "<LogItem>", "<root><unclosed>", "null", "[]", "{'a':1}",
            "<AuditLog><LogItem><employeeNo>E</employeeNo></LogItem>",
            "<!DOCTYPE foo>", "<?xml version=\"1.0\"?>", "\uFEFF<AuditLog/>", "<AuditLog xmlns=\"http://x\">"
        };
        var generated = Enumerable.Range(1, 26)
            .Select(i => "<AuditLog>" + new string('a', i) + "<")
            .ToArray();
        return fixedBad.Concat(generated).Select(s => new object[] { s });
    }

    [Theory]
    [MemberData(nameof(AuditLogMalformed))]
    public void AuditLog_returns_empty_without_throwing(string xml)
    {
        var events = HikvisionParser.ParseAuditLogXml(xml);
        Assert.NotNull(events);
        Assert.Empty(events);
    }

    // ═══════════════════ 10. ExtractXmlValue (device-info probing) ═══════════════════

    public static IEnumerable<object[]> ExtractXmlCases()
    {
        var tags = new[] { "deviceName", "model", "serialNo", "name", "status", "UID" };
        var values = new[] { "DS-K1T671", "x", "", " a b ", "unicode", "a & b", "multi\nline" };

        foreach (var tag in tags)
            foreach (var v in values)
            {
                var trimmed = v.Trim();
                yield return new object[] { "<" + tag + ">" + v + "</" + tag + ">", tag, trimmed };
                yield return new object[] { "<" + tag.ToUpperInvariant() + ">" + v + "</" + tag.ToUpperInvariant() + ">", tag, trimmed };
                yield return new object[] { "<" + tag + " id=\"1\">" + v + "</" + tag + ">", tag, null! };
                yield return new object[] { "<root><" + tag + ">" + v + "</" + tag + "></root>", tag, trimmed };
                yield return new object[] { "xx<" + tag + ">" + v + "</" + tag + ">yy", tag, trimmed };
                yield return new object[] { "<" + tag + ">first</" + tag + "><" + tag + ">second</" + tag + ">", tag, "first" };
            }

        yield return new object[] { "<deviceName>v", "deviceName", null! };
        yield return new object[] { "<model>v", "model", null! };
        yield return new object[] { "<deviceName/>", "deviceName", null! };
        yield return new object[] { "<model />", "model", null! };
        yield return new object[] { "<deviceNameExtra>v</deviceNameExtra>", "deviceName", null! };
        yield return new object[] { "<serialNoX>v</serialNoX>", "serialNo", null! };
        yield return new object[] { "<deviceName attr>v</deviceName>", "deviceName", null! };
        yield return new object[] { "<DeViCeNaMe>v</DeViCeNaMe>", "deviceName", "v" };
        yield return new object[] { "<?xml version=\"1.0\"?><deviceName>v</deviceName>", "deviceName", "v" };
        yield return new object[] { "<deviceName><![CDATA[v]]></deviceName>", "deviceName", "<![CDATA[v]]>" };
        yield return new object[] { "<deviceName></deviceName><deviceName>v</deviceName>", "deviceName", "" };
        yield return new object[] { "\n<deviceName>v</deviceName>\n", "deviceName", "v" };
        yield return new object[] { "<deviceName>12345</deviceName>", "deviceName", "12345" };
        yield return new object[] { "<serialNo>SN-0001</serialNo>", "model", null! };
    }

    [Theory]
    [MemberData(nameof(ExtractXmlCases))]
    public void ExtractXmlValue_returns_expected(string xml, string tag, string? expected)
    {
        Assert.Equal(expected, HikvisionParser.ExtractXmlValue(xml, tag));
    }

    // ═══════════════════ 11. minor -> event type classification ═══════════════════

    public static IEnumerable<object[]> ClassifyCases()
        => Enumerable.Range(-100, 300).Select(m => new object[] { m, m == 76 ? "check_out" : "check_in" });

    [Theory]
    [MemberData(nameof(ClassifyCases))]
    public void ClassifyEventType_maps_minor_codes(int minor, string expected)
        => Assert.Equal(expected, HikvisionParser.ClassifyEventType(minor));

    // ═══════════════════ 12. SyncSettings - validation ═══════════════════

    [Fact]
    public void SyncSettings_defaults_are_valid()
    {
        var s = new SyncSettings();
        Assert.True(s.IsValid());
        Assert.Equal("192.168.1.137", s.DeviceIp);
        Assert.Equal(80, s.DevicePort);
        Assert.Equal(false, s.UseHttps);
        Assert.Equal("admin", s.DeviceUsername);
        Assert.Equal("https://ukuuhr.com", s.CloudUrl);
        Assert.Null(s.ApiKey);
        Assert.Equal(5, s.SyncIntervalMinutes);
    }

    [Fact]
    public void SyncSettings_is_invalid_when_DeviceIp_is_null_or_empty()
    {
        var s = new SyncSettings { DeviceIp = "" };
        Assert.False(s.IsValid());
        s.DeviceIp = null!;
        Assert.False(s.IsValid());
    }

    [Fact]
    public void SyncSettings_is_invalid_when_DeviceUsername_is_null_or_empty()
    {
        var s = new SyncSettings { DeviceUsername = "" };
        Assert.False(s.IsValid());
        s.DeviceUsername = null!;
        Assert.False(s.IsValid());
    }

    [Fact]
    public void SyncSettings_is_invalid_when_CloudUrl_is_null_or_empty()
    {
        var s = new SyncSettings { CloudUrl = "" };
        Assert.False(s.IsValid());
        s.CloudUrl = null!;
        Assert.False(s.IsValid());
    }

    [Fact]
    public void SyncSettings_is_valid_with_minimal_fields()
    {
        var s = new SyncSettings { DeviceIp = "10.0.0.1", DeviceUsername = "user", CloudUrl = "https://example.com" };
        Assert.True(s.IsValid());
    }

    [Theory]
    [InlineData(1)]
    [InlineData(5)]
    [InlineData(60)]
    [InlineData(1440)]
    public void SyncSettings_accepts_various_sync_intervals(int minutes)
    {
        var s = new SyncSettings { SyncIntervalMinutes = minutes };
        Assert.Equal(minutes, s.SyncIntervalMinutes);
    }

    // ═══════════════════ 13. SyncSettings - JSON round-trip ═══════════════════

    [Fact]
    public void SyncSettings_roundtrips_via_JSON()
    {
        var original = new SyncSettings
        {
            DeviceIp = "192.168.0.100",
            DevicePort = 8080,
            UseHttps = true,
            DeviceUsername = "operator",
            DevicePassword = "s3cret!",
            CloudUrl = "https://my-cloud.example.com",
            ApiKey = "key-123",
            SyncIntervalMinutes = 15
        };

        var json = System.Text.Json.JsonSerializer.Serialize(original, new System.Text.Json.JsonSerializerOptions
        {
            WriteIndented = false,
            PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase
        });

        var deserialized = System.Text.Json.JsonSerializer.Deserialize<SyncSettings>(json, new System.Text.Json.JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        });

        Assert.NotNull(deserialized);
        Assert.Equal(original.DeviceIp, deserialized.DeviceIp);
        Assert.Equal(original.DevicePort, deserialized.DevicePort);
        Assert.Equal(original.UseHttps, deserialized.UseHttps);
        Assert.Equal(original.DeviceUsername, deserialized.DeviceUsername);
        Assert.Equal(original.DevicePassword, deserialized.DevicePassword);
        Assert.Equal(original.CloudUrl, deserialized.CloudUrl);
        Assert.Equal(original.ApiKey, deserialized.ApiKey);
        Assert.Equal(original.SyncIntervalMinutes, deserialized.SyncIntervalMinutes);
    }

    // ═══════════════════ 14. ImportedPunch - model defaults ═══════════════════

    [Fact]
    public void ImportedPunch_has_correct_defaults()
    {
        var p = new ImportedPunch();
        Assert.Equal("", p.EmployeeNo);
        Assert.Equal("", p.Time);
        Assert.Equal("check_in", p.EventType);
        Assert.Equal(1, p.Major);
        Assert.Equal(75, p.Minor);
    }

    [Fact]
    public void ImportedPunch_properties_are_settable()
    {
        var p = new ImportedPunch
        {
            EmployeeNo = "EMP-001",
            Time = "2026-08-06T08:00:00Z",
            EventType = "check_out",
            Major = 1,
            Minor = 76
        };
        Assert.Equal("EMP-001", p.EmployeeNo);
        Assert.Equal("2026-08-06T08:00:00Z", p.Time);
        Assert.Equal("check_out", p.EventType);
        Assert.Equal(1, p.Major);
        Assert.Equal(76, p.Minor);
    }

    // ═══════════════════ 15. Parser equivalence - JSON vs XML ═══════════════════

    [Fact]
    public void AcsEvent_JSON_and_XML_produce_equivalent_results()
    {
        var json = "{\"AcsEvent\":{\"InfoList\":[" +
            "{\"employeeNo\":\"E1\",\"time\":\"2026-08-06T08:00:00Z\",\"minor\":75}," +
            "{\"employeeNo\":\"E2\",\"time\":\"2026-08-06T17:00:00Z\",\"minor\":76}," +
            "{\"employeeNo\":\"E3\",\"time\":\"2026-08-06T09:30:00Z\",\"minor\":1}" +
            "]}}";

        var xml = "<?xml version=\"1.0\"?><AcsEvent><InfoList>" +
            "<Info employeeNo=\"E1\" time=\"2026-08-06T08:00:00Z\" minor=\"75\"/>" +
            "<Info employeeNo=\"E2\" time=\"2026-08-06T17:00:00Z\" minor=\"76\"/>" +
            "<Info employeeNo=\"E3\" time=\"2026-08-06T09:30:00Z\" minor=\"1\"/>" +
            "</InfoList></AcsEvent>";

        var jsonEvents = HikvisionParser.ParseAcsEventJson(json);
        var xmlEvents = HikvisionParser.ParseAcsEventXml(xml);

        Assert.Equal(jsonEvents.Count, xmlEvents.Count);
        for (var i = 0; i < jsonEvents.Count; i++)
        {
            Assert.Equal(jsonEvents[i].EmployeeNo, xmlEvents[i].EmployeeNo);
            Assert.Equal(jsonEvents[i].Time, xmlEvents[i].Time);
            Assert.Equal(jsonEvents[i].EventType, xmlEvents[i].EventType);
            Assert.Equal(jsonEvents[i].Major, xmlEvents[i].Major);
            Assert.Equal(jsonEvents[i].Minor, xmlEvents[i].Minor);
        }
    }

    // ═══════════════════ 16. The "at all costs" guarantee ═══════════════════

    [Fact]
    public void Suite_covers_at_least_1000_cases()
    {
        var total = AcsEventMatrix().Count()
                  + AcsEventStringMinors().Count()
                  + AcsEventSkips().Count()
                  + AcsEventEmpty().Count()
                  + AcsEventXmlCases().Count()
                  + AcsEventXmlSkips().Count()
                  + AcsEventXmlMalformed().Count()
                  + AuditLogCases().Count()
                  + AuditLogMalformed().Count()
                  + ExtractXmlCases().Count()
                  + ClassifyCases().Count();
        Assert.True(total >= 1000, $"Suite has only {total} cases - the desktop Hikvision parsers require >= 1000.");
    }
}
