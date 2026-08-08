using System.Text.Json;
using System.Xml.Linq;

namespace UkuuHr.Sync;

/// <summary>
/// Parsers for the Hikvision ISAPI payloads used by the Ukuu HR Sync Bridge
/// (desktop). Extracted from Program.cs into a static class so the exact
/// parsing logic that ships in the desktop build is directly unit-testable.
///
/// The bridge talks to Hikvision access-control terminals over ISAPI:
///   - AcsEvent search results come back as JSON (InfoList / EventList arrays)
///   - AuditLog search results come back as XML (LogItem elements)
///
/// Robustness contract (what the 1000+ test suite pins down):
///   - A valid payload always parses — every structure and casing variant.
///   - A malformed payload NEVER crashes the bridge — it yields zero events.
///   - One malformed item NEVER discards the rest of the batch — bad items are
///     skipped, good items are kept.
///   - AuditLog XML parses whether or not the device emits a default namespace.
/// </summary>
public static class HikvisionParser
{
    /// <summary>Parse an ISAPI AcsEvent JSON response into attendance events.</summary>
    public static List<ImportedPunch> ParseAcsEventJson(string json)
    {
        var events = new List<ImportedPunch>();
        try
        {
            using var doc = JsonDocument.Parse(json);
            JsonElement infoList = default;

            // Hikvision returns the event array in a few shapes — accept them all:
            // {"AcsEvent":{"InfoList":[...]}}  {"AcsEvent":{"EventList":[...]}}
            // {"InfoList":[...]}               {"EventList":[...]}
            if (doc.RootElement.TryGetProperty("AcsEvent", out var acsEvent))
            {
                if (!acsEvent.TryGetProperty("InfoList", out infoList))
                    acsEvent.TryGetProperty("EventList", out infoList);
            }
            if (infoList.ValueKind != JsonValueKind.Array)
            {
                doc.RootElement.TryGetProperty("InfoList", out infoList);
                if (infoList.ValueKind != JsonValueKind.Array)
                    doc.RootElement.TryGetProperty("EventList", out infoList);
            }

            if (infoList.ValueKind != JsonValueKind.Array) return events;

            foreach (var item in infoList.EnumerateArray())
            {
                // A single malformed item must never discard the whole batch.
                try { AddAcsEvent(item, events); }
                catch { /* skip malformed item */ }
            }
        }
        catch
        {
            // The payload isn't valid JSON at all — no events, no crash.
        }
        return events;
    }

    private static void AddAcsEvent(JsonElement item, List<ImportedPunch> events)
    {
        if (item.ValueKind != JsonValueKind.Object) return;

        var empNo = TryGetString(item, "employeeNo", "EmployeeNo");
        var time = TryGetString(item, "time", "eventTime");
        if (string.IsNullOrEmpty(empNo) || string.IsNullOrEmpty(time)) return;

        int minor = 75;
        if (item.TryGetProperty("minor", out var minorElem))
        {
            if (minorElem.ValueKind == JsonValueKind.Number)
                minorElem.TryGetInt32(out minor);
            else if (minorElem.ValueKind == JsonValueKind.String &&
                     int.TryParse(minorElem.GetString(), out var parsed))
                minor = parsed;
        }

        events.Add(new ImportedPunch
        {
            EmployeeNo = empNo,
            Time = time,
            EventType = ClassifyEventType(minor),
            Major = 1,
            Minor = minor
        });
    }

    private static string TryGetString(JsonElement item, params string[] keys)
    {
        foreach (var key in keys)
        {
            if (item.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String)
                return value.GetString() ?? "";
        }
        return "";
    }

    /// <summary>Parse an ISAPI AuditLog XML response into attendance events.</summary>
    public static List<ImportedPunch> ParseAuditLogXml(string xml)
    {
        var events = new List<ImportedPunch>();
        try
        {
            var doc = XDocument.Parse(xml);

            // Match by local name so payloads carrying an XML default namespace
            // (some Hikvision firmware versions) parse exactly like plain ones.
            foreach (var item in doc.Descendants().Where(e => e.Name.LocalName == "LogItem"))
            {
                var empNo = ChildLocalValue(item, "employeeNo") ?? "";
                var time = ChildLocalValue(item, "time") ?? "";
                if (string.IsNullOrEmpty(empNo) || string.IsNullOrEmpty(time)) continue;

                var minorStr = ChildLocalValue(item, "minor") ?? "75";
                var minor = int.TryParse(minorStr, out var m) ? m : 75;

                events.Add(new ImportedPunch
                {
                    EmployeeNo = empNo,
                    Time = time,
                    EventType = ClassifyEventType(minor),
                    Major = 1,
                    Minor = minor
                });
            }
        }
        catch
        {
            // The payload isn't valid XML — no events, no crash.
        }
        return events;
    }

    private static string? ChildLocalValue(XElement item, string name) =>
        item.Elements().FirstOrDefault(e => e.Name.LocalName == name)?.Value;

    /// <summary>Extract the raw text of the first &lt;tag&gt;…&lt;/tag&gt; pair (case-insensitive).</summary>
    public static string? ExtractXmlValue(string xml, string tagName)
    {
        var start = $"<{tagName}>";
        var end = $"</{tagName}>";
        var s = xml.IndexOf(start, StringComparison.OrdinalIgnoreCase);
        if (s < 0) return null;
        s += start.Length;
        var e = xml.IndexOf(end, s, StringComparison.OrdinalIgnoreCase);
        return e < 0 ? null : xml[s..e].Trim();
    }

    /// <summary>Map a Hikvision AcsEvent minor code to a Ukuu HR event type.</summary>
    public static string ClassifyEventType(int minor) => minor == 76 ? "check_out" : "check_in";
}

/// <summary>
/// An attendance punch imported from a Hikvision terminal.
/// Named ImportedPunch (not ImportedEvent) to avoid clashing with the global
/// ImportedEvent type declared by the Web app's Program.cs — projects that
/// reference both apps must never silently bind to the wrong model.
/// </summary>
public sealed class ImportedPunch
{
    public string EmployeeNo { get; set; } = "";
    public string Time { get; set; } = "";
    public string EventType { get; set; } = "check_in";
    public int Major { get; set; } = 1;
    public int Minor { get; set; } = 75;
}
