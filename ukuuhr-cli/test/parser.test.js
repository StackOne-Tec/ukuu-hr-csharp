/**
 * Parser unit tests for HikvisionParser
 */

const { parseAcsEventJson, parseAcsEventXml, parseAuditLogXml, classifyEventType, extractXmlValue } = require('../src/parser');

let passed = 0;
let failed = 0;

function assert(condition, msg) {
  if (condition) { passed++; }
  else { failed++; console.log(`  FAIL: ${msg}`); }
}

function assertEqual(actual, expected, msg) {
  if (JSON.stringify(actual) === JSON.stringify(expected)) { passed++; }
  else { failed++; console.log(`  FAIL: ${msg}\n    expected: ${JSON.stringify(expected)}\n    actual:   ${JSON.stringify(actual)}`); }
}

// ── classifyEventType ──
assertEqual(classifyEventType(75), 'check_in', 'minor 75 = check_in');
assertEqual(classifyEventType(76), 'check_out', 'minor 76 = check_out');
assertEqual(classifyEventType(0), 'check_in', 'minor 0 = check_in');

// ── parseAcsEventJson ──

// Shape 1: AcsEvent.InfoList
assertEqual(
  parseAcsEventJson('{"AcsEvent":{"InfoList":[{"employeeNo":"001","time":"2024-01-01T08:00:00","minor":75}]}}').length,
  1,
  'AcsEvent.InfoList parses 1 event'
);

// Shape 2: AcsEvent.EventList
assertEqual(
  parseAcsEventJson('{"AcsEvent":{"EventList":[{"employeeNo":"002","time":"2024-01-01T09:00:00","minor":76}]}}').length,
  1,
  'AcsEvent.EventList parses 1 event'
);

// Shape 3: Top-level InfoList
assertEqual(
  parseAcsEventJson('{"InfoList":[{"employeeNo":"003","time":"2024-01-01T10:00:00","minor":75}]}').length,
  1,
  'Top-level InfoList parses 1 event'
);

// Shape 4: Top-level EventList
assertEqual(
  parseAcsEventJson('{"EventList":[{"employeeNo":"004","time":"2024-01-01T11:00:00","minor":76}]}').length,
  1,
  'Top-level EventList parses 1 event'
);

// Event type classification
const jsonEvents = parseAcsEventJson('{"InfoList":[{"employeeNo":"001","time":"2024-01-01T08:00:00","minor":75},{"employeeNo":"002","time":"2024-01-01T17:00:00","minor":76}]}');
assertEqual(jsonEvents[0].eventType, 'check_in', 'minor 75 → check_in');
assertEqual(jsonEvents[1].eventType, 'check_out', 'minor 76 → check_out');

// Malformed JSON → empty
assertEqual(parseAcsEventJson('not json').length, 0, 'Malformed JSON → 0 events');

// Empty InfoList
assertEqual(parseAcsEventJson('{"InfoList":[]}').length, 0, 'Empty InfoList → 0 events');

// ── parseAcsEventXml ──

// Attribute format
const xmlAttr = '<?xml version="1.0"?><AcsEvent><Info employeeNo="001" time="2024-01-01T08:00:00" minor="75"/></AcsEvent>';
const xmlAttrEvents = parseAcsEventXml(xmlAttr);
assertEqual(xmlAttrEvents.length, 1, 'XML attribute format: 1 event');
if (xmlAttrEvents.length > 0) {
  assertEqual(xmlAttrEvents[0].employeeNo, '001', 'XML attr: employeeNo');
  assertEqual(xmlAttrEvents[0].eventType, 'check_in', 'XML attr: check_in');
}

// Child element format
const xmlChild = '<?xml version="1.0"?><AcsEvent><Info><employeeNo>002</employeeNo><time>2024-01-01T17:00:00</time><minor>76</minor></Info></AcsEvent>';
const xmlChildEvents = parseAcsEventXml(xmlChild);
assertEqual(xmlChildEvents.length, 1, 'XML child format: 1 event');
if (xmlChildEvents.length > 0) {
  assertEqual(xmlChildEvents[0].eventType, 'check_out', 'XML child: check_out');
}

// Malformed XML → empty
assertEqual(parseAcsEventXml('not xml').length, 0, 'Malformed XML → 0 events');

// ── parseAuditLogXml ──

const auditXml = '<?xml version="1.0"?><AuditLog><LogItem><employeeNo>003</employeeNo><time>2024-01-01T08:30:00</time><minor>75</minor></LogItem><LogItem><employeeNo>003</employeeNo><time>2024-01-01T17:30:00</time><minor>76</minor></LogItem></AuditLog>';
const auditEvents = parseAuditLogXml(auditXml);
assertEqual(auditEvents.length, 2, 'AuditLog: 2 events');
if (auditEvents.length >= 2) {
  assertEqual(auditEvents[0].eventType, 'check_in', 'AuditLog[0]: check_in');
  assertEqual(auditEvents[1].eventType, 'check_out', 'AuditLog[1]: check_out');
}

// Malformed audit XML → empty
assertEqual(parseAuditLogXml('broken').length, 0, 'Malformed AuditLog → 0 events');

// ── extractXmlValue ──
assertEqual(extractXmlValue('<deviceName>TestDevice</deviceName>', 'deviceName'), 'TestDevice', 'extractXmlValue basic');
assertEqual(extractXmlValue('<DeviceName>TestDevice</DeviceName>', 'deviceName'), 'TestDevice', 'extractXmlValue case-insensitive');
assertEqual(extractXmlValue('<foo>bar</foo>', 'baz'), null, 'extractXmlValue missing tag');

// ── Results ──
console.log(`\n  ${passed} passed, ${failed} failed`);
process.exit(failed > 0 ? 1 : 0);
