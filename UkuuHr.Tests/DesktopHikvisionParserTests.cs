using UkuuHr.Sync;
using Xunit;

namespace UkuuHr.Tests;

/// <summary>
/// Exhaustive tests (1000+ cases) for the Hikvision ISAPI parsing logic that
/// ships in the DESKTOP app (UkuuHrSync — the Windows .exe / macOS .dmg).
/// Every payload the bridge can receive from a real terminal is pinned here:
///   • AcsEvent JSON   (InfoList / EventList — all shapes, casings, minor codes)
///   • AuditLog XML    (plain + namespaced, entity/unicode/whitespace variants)
///   • ExtractXmlValue (device-info probing)
///   • minor → check_in / check_out classification
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

    // The desktop model is ImportedPunch (named to avoid clashing with the Web
    // app's global-namespace ImportedEvent).
    private static (int ins, int outs) Split(List<ImportedPunch> events)
        => (events.Count(e => e.EventType == "check_in"), events.Count(e => e.EventType == "check_out"));

    // ═══════════════════════ 1. AcsEvent JSON — valid payloads ═══════════════════════

    public static IEnumerable<object[]> AcsEventMatrix()
    {
        var minors = new[] { 75, 76, 0, 1, 2, 5, 100, 999, -1, 3, 6, 7 };
        var empKeys = new[] { "employeeNo", "EmployeeNo" };
        var timeKeys = new[] { "time", "eventTime" };
        var cases = new List<object[]>();

        // Single-event matrix: every container × field casing × minor code.
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

    // ═══════════════════ 2. AcsEvent JSON — bad items never kill the batch ═══════════════════

    public static IEnumerable<object[]> AcsEventSkips()
    {
        string[] badItems =
        {
            "{\"time\":\"2026-08-06T08:00:00Z\"}",                 // missing employeeNo
            "{\"employeeNo\":\"E\"}",                               // missing time
            "{\"employeeNo\":123,\"time\":\"2026-08-06T08:00:00Z\"}", // numeric employeeNo
            "{\"employeeNo\":\"E\",\"time\":123}",                  // numeric time
            "null",                                                 // null item
            "\"hello\"",                                            // scalar item
            "42",                                                   // number item
            "{\"employeeNo\":\"\",\"time\":\"2026-08-06T08:00:00Z\"}", // empty employeeNo
            "{\"employeeNo\":\"E\",\"time\":\"\"}",                 // empty time
            "{\"employeeNo\":{\"a\":1},\"time\":\"2026-08-06T08:00:00Z\"}" // object employeeNo
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

    // ═══════════════════ 3. AcsEvent JSON — empty / malformed payloads ═══════════════════

    public static IEnumerable<object[]> AcsEventEmpty()
    {
        var cases = new List<object[]>();

        // Every container wrapping empty / non-object shapes → zero events.
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

        // Wrong casing on the array key → not found → zero events.
        cases.AddRange(new[] { "{\"infolist\":[]}", "{\"INFOLIST\":[]}", "{\"AcsEvent\":{\"InfoList\":[],\"extra\":1}}" }
            .Select(s => new object[] { s }));

        // Completely invalid JSON.
        cases.AddRange(new[]
        {
            "", "not json", "{", "[", "null", "123", "true", "[]",
            "\uFEFF{\"InfoList\":[]}", "  {\"InfoList\":[]}  ", "{\"InfoList\":[]} garbage",
            "{\"InfoList\":[}]", "{\"AcsEvent\":", "[1,2,", "{\"InfoList\":[\"a\""
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

    // ═══════════════════ 4. AuditLog XML — valid payloads ═══════════════════

    public static IEnumerable<object[]> AuditLogCases()
    {
        var cases = new List<object[]>();
        var rng = new Random(2026);

        // Deterministic patterns, plain + namespaced.
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

        // String / missing / malformed minor values on a valid item.
        foreach (var ns in new[] { false, true })
        {
            // Literal quotes around the value are not a number — defaults to 75 (check_in).
            cases.Add(new object[] { AuditXml(new[] { AuditItem("E", "t", "\"76\"") }, ns), 1, 1, 0 });
            cases.Add(new object[] { AuditXml(new[] { AuditItem("E", "t", "abc") }, ns), 1, 1, 0 });
            cases.Add(new object[] { AuditXml(new[] { AuditItem("E", "t", "") }, ns), 1, 1, 0 });
            cases.Add(new object[] { AuditXml(new[] { AuditItem("E", "t", " 76 ") }, ns), 1, 0, 1 });
            cases.Add(new object[] { AuditXml(new[] { "<LogItem><employeeNo>E</employeeNo><time>t</time></LogItem>" }, ns), 1, 1, 0 });
        }

        // Items missing identity fields are skipped.
        foreach (var ns in new[] { false, true })
        {
            cases.Add(new object[] { AuditXml(new[] { "<LogItem><time>t</time><minor>76</minor></LogItem>" }, ns), 0, 0, 0 });
            cases.Add(new object[] { AuditXml(new[] { "<LogItem><employeeNo>E</employeeNo><minor>76</minor></LogItem>" }, ns), 0, 0, 0 });
            cases.Add(new object[] { AuditXml(new[] { "<LogItem><employeeNo></employeeNo><time>t</time></LogItem>" }, ns), 0, 0, 0 });
            cases.Add(new object[] { AuditXml(new[] { "<LogItem><employeeNo>E</employeeNo><time></time></LogItem>" }, ns), 0, 0, 0 });
            // Nested element text IS read (XElement.Value concatenates descendants) — counted.
            cases.Add(new object[] { AuditXml(new[] { "<LogItem><employeeNo><EmployeeNo>X</EmployeeNo></employeeNo><time>t</time></LogItem>" }, ns), 1, 1, 0 });
        }

        // Random multi-item batches, plain + namespaced.
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

        // Alternative roots / wrappers.
        foreach (var ns in new[] { false, true })
            foreach (var root in new[] { "AuditLog", "AuditLogSearch", "Response", "SearchResult", "isapiResponse" })
                cases.Add(new object[] { AuditXml(new[] { AuditItem("E", "t"), AuditItem("E2", "t2", "76") }, ns, root), 2, 1, 1 });

        // Noise: comments, CDATA, attributes, whitespace, extra wrappers.
        // Each of these yields exactly ONE counted LogItem (minor 76 → check_out).
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
            // Nested A/B wrapper with TWO LogItems (76 + 75).
            var ab = "<A>" + AuditItem("E", "t", "76") + "<B>" + AuditItem("E2", "t2", "75") + "</B></A>";
            cases.Add(new object[] { AuditXml(new[] { ab }, ns), 2, 1, 1 });
        }

        // Unicode + XML entities in employee numbers.
        foreach (var ns in new[] { false, true })
            foreach (var emp in new[] { "Mùng", "中", "a&amp;b", "A&amp;B", "&quot;Q&quot;", "x&apos;y" })
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

    // ═══════════════════ 5. AuditLog XML — malformed payloads ═══════════════════

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

    // ═══════════════════ 6. ExtractXmlValue (device-info probing) ═══════════════════

    public static IEnumerable<object[]> ExtractXmlCases()
    {
        var tags = new[] { "deviceName", "model", "serialNo", "name", "status", "UID" };
        var values = new[] { "DS-K1T671", "x", "", " a b ", "ünïcode 中文", "a & b", "multi\nline" };

        foreach (var tag in tags)
            foreach (var v in values)
            {
                var trimmed = v.Trim();
                yield return new object[] { "<" + tag + ">" + v + "</" + tag + ">", tag, trimmed };
                yield return new object[] { "<" + tag.ToUpperInvariant() + ">" + v + "</" + tag.ToUpperInvariant() + ">", tag, trimmed };
                // Attributes are NOT matched — the naive matcher looks for "<tag>".
                yield return new object[] { "<" + tag + " id=\"1\">" + v + "</" + tag + ">", tag, null! };
                yield return new object[] { "<root><" + tag + ">" + v + "</" + tag + "></root>", tag, trimmed };
                yield return new object[] { "xx<" + tag + ">" + v + "</" + tag + ">yy", tag, trimmed };
                yield return new object[] { "<" + tag + ">first</" + tag + "><" + tag + ">second</" + tag + ">", tag, "first" };
            }

        // Special cases.
        yield return new object[] { "<deviceName>v", "deviceName", null! };
        yield return new object[] { "<model>v", "model", null! };
        yield return new object[] { "<deviceName/>", "deviceName", null! };
        yield return new object[] { "<model />", "model", null! };
        yield return new object[] { "<deviceNameExtra>v</deviceNameExtra>", "deviceName", null! };
        yield return new object[] { "<serialNoX>v</serialNoX>", "serialNo", null! };
        yield return new object[] { "<deviceName attr>v</deviceName>", "deviceName", null! };
        yield return new object[] { "<DeViCeNaMe>v</DeViCeNaMe>", "deviceName", "v" };
        yield return new object[] { "<?xml version=\"1.0\"?><deviceName>v</deviceName>", "deviceName", "v" };
        // CDATA is returned raw (no XML decoding in the naive matcher).
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

    // ═══════════════════ 7. minor → event type classification ═══════════════════

    public static IEnumerable<object[]> ClassifyCases()
        => Enumerable.Range(-100, 300).Select(m => new object[] { m, m == 76 ? "check_out" : "check_in" });

    [Theory]
    [MemberData(nameof(ClassifyCases))]
    public void ClassifyEventType_maps_minor_codes(int minor, string expected)
        => Assert.Equal(expected, HikvisionParser.ClassifyEventType(minor));

    // ═══════════════════ 8. The "at all costs" guarantee ═══════════════════

    [Fact]
    public void Suite_covers_at_least_1000_cases()
    {
        var total = AcsEventMatrix().Count()
                  + AcsEventStringMinors().Count()
                  + AcsEventSkips().Count()
                  + AcsEventEmpty().Count()
                  + AuditLogCases().Count()
                  + AuditLogMalformed().Count()
                  + ExtractXmlCases().Count()
                  + ClassifyCases().Count();
        Assert.True(total >= 1000, $"Suite has only {total} cases — the desktop Hikvision parsers require >= 1000.");
    }
}
