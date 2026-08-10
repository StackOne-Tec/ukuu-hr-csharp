/**
 * HikvisionParser — ISAPI payload parsers for Ukuu HR Sync Bridge
 *
 * Handles:
 *   - AcsEvent JSON (InfoList / EventList array shapes)
 *   - AcsEvent XML (attribute and child-element format)
 *   - AuditLog XML (LogItem elements, namespace-agnostic)
 *
 * Robustness contract:
 *   - A valid payload always parses
 *   - A malformed payload never crashes — yields empty array
 *   - One malformed item never discards the rest — bad items skipped
 */

/**
 * Map a Hikvision AcsEvent minor code to event type
 */
function classifyEventType(minor) {
  return minor === 76 ? 'check_out' : 'check_in';
}

/**
 * Parse an ISAPI AcsEvent JSON response into attendance events
 * Handles 4 container shapes:
 *   {"AcsEvent":{"InfoList":[...]}}
 *   {"AcsEvent":{"EventList":[...]}}
 *   {"InfoList":[...]}
 *   {"EventList":[...]}
 */
function parseAcsEventJson(json) {
  const events = [];
  try {
    const doc = typeof json === 'string' ? JSON.parse(json) : json;

    let infoList = null;

    // Try nested shapes first
    if (doc.AcsEvent) {
      infoList = doc.AcsEvent.InfoList || doc.AcsEvent.EventList || null;
    }
    // Try top-level shapes
    if (!Array.isArray(infoList)) {
      infoList = doc.InfoList || doc.EventList || null;
    }

    if (!Array.isArray(infoList)) return events;

    for (const item of infoList) {
      try {
        if (!item || typeof item !== 'object') continue;

        const empNo = item.employeeNo || item.EmployeeNo || '';
        const time = item.time || item.eventTime || '';
        if (!empNo || !time) continue;

        let minor = 75;
        if (item.minor !== undefined) {
          minor = typeof item.minor === 'number' ? item.minor : parseInt(item.minor, 10);
          if (isNaN(minor)) minor = 75;
        }

        events.push({
          employeeNo: empNo,
          time: time,
          eventType: classifyEventType(minor),
          major: 1,
          minor: minor
        });
      } catch {
        // skip malformed item
      }
    }
  } catch {
    // Not valid JSON — no events, no crash
  }
  return events;
}

/**
 * Parse an ISAPI AcsEvent XML response into attendance events
 * Handles both attribute format and child-element format:
 *   <Info employeeNo="001" time="..." minor="75"/>
 *   <Info><employeeNo>001</employeeNo><time>...</time><minor>75</minor></Info>
 */
function parseAcsEventXml(xml) {
  const events = [];
  try {
    // Simple regex-based parser (no XML dependency needed)
    const infoRegex = /<Info\b[^>]*>([\s\S]*?)<\/Info>/g;
    const selfClosingRegex = /<Info\b([^>]*?)\/>/g;

    let match;

    // Self-closing (attribute) format
    while ((match = selfClosingRegex.exec(xml)) !== null) {
      try {
        const attrs = match[1];
        const empNo = getAttr(attrs, 'employeeNo');
        const time = getAttr(attrs, 'time');
        if (!empNo || !time) continue;

        const minorStr = getAttr(attrs, 'minor') || '75';
        const minor = parseInt(minorStr, 10);
        events.push({
          employeeNo: empNo,
          time: time,
          eventType: classifyEventType(isNaN(minor) ? 75 : minor),
          major: 1,
          minor: isNaN(minor) ? 75 : minor
        });
      } catch {
        // skip malformed
      }
    }

    // Full element format
    while ((match = infoRegex.exec(xml)) !== null) {
      try {
        const content = match[1];
        const fullTag = match[0];

        // Try attributes first (on opening tag)
        const openingTag = fullTag.match(/<Info\b([^>]*?)>/);
        const attrs = openingTag ? openingTag[1] : '';

        let empNo = getAttr(attrs, 'employeeNo');
        let time = getAttr(attrs, 'time');
        let minorStr = getAttr(attrs, 'minor');

        // Fall back to child elements
        if (!empNo) empNo = extractXmlValue(content, 'employeeNo');
        if (!time) time = extractXmlValue(content, 'time');
        if (!minorStr) minorStr = extractXmlValue(content, 'minor');

        if (!empNo || !time) continue;

        const minor = parseInt(minorStr || '75', 10);
        events.push({
          employeeNo: empNo,
          time: time,
          eventType: classifyEventType(isNaN(minor) ? 75 : minor),
          major: 1,
          minor: isNaN(minor) ? 75 : minor
        });
      } catch {
        // skip malformed
      }
    }
  } catch {
    // Not parseable — no events, no crash
  }
  return events;
}

/**
 * Parse an ISAPI AuditLog XML response into attendance events
 * Handles <LogItem> elements, namespace-agnostic via local name matching
 */
function parseAuditLogXml(xml) {
  const events = [];
  try {
    const logItemRegex = /<LogItem\b[^>]*>([\s\S]*?)<\/LogItem>/g;
    let match;

    while ((match = logItemRegex.exec(xml)) !== null) {
      try {
        const content = match[1];
        const empNo = extractXmlValue(content, 'employeeNo') || '';
        const time = extractXmlValue(content, 'time') || '';
        if (!empNo || !time) continue;

        const minorStr = extractXmlValue(content, 'minor') || '75';
        const minor = parseInt(minorStr, 10);

        events.push({
          employeeNo: empNo,
          time: time,
          eventType: classifyEventType(isNaN(minor) ? 75 : minor),
          major: 1,
          minor: isNaN(minor) ? 75 : minor
        });
      } catch {
        // skip malformed
      }
    }
  } catch {
    // Not parseable — no events, no crash
  }
  return events;
}

/**
 * Extract the raw text of the first <tag>...</tag> pair (case-insensitive)
 */
function extractXmlValue(xml, tagName) {
  // Handle self-closing too
  const startTag = `<${tagName}>`;
  const endTag = `</${tagName}>`;
  const s = xml.toLowerCase().indexOf(startTag.toLowerCase());
  if (s < 0) return null;
  const contentStart = s + startTag.length;
  const e = xml.toLowerCase().indexOf(endTag.toLowerCase(), contentStart);
  if (e < 0) return null;
  return xml.substring(contentStart, e).trim();
}

/**
 * Extract an attribute value from an XML attribute string
 */
function getAttr(attrString, name) {
  const regex = new RegExp(`${name}\\s*=\\s*["']([^"']*)["']`, 'i');
  const match = attrString.match(regex);
  return match ? match[1] : null;
}

module.exports = {
  classifyEventType,
  parseAcsEventJson,
  parseAcsEventXml,
  parseAuditLogXml,
  extractXmlValue
};
