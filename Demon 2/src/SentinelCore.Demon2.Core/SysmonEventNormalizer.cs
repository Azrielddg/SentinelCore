using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;

namespace SentinelCore.Demon2.Core;

public sealed class SysmonEventNormalizer : IEventNormalizer<SysmonRecord>
{
    private static readonly HashSet<string> Interpreters = new(StringComparer.OrdinalIgnoreCase)
    { "powershell.exe", "pwsh.exe", "wscript.exe", "cscript.exe", "mshta.exe", "cmd.exe", "rundll32.exe" };

    public bool TryNormalize(SysmonRecord record, out CanonicalEvent? result, out string? rejectionReason)
    {
        result = null;
        rejectionReason = null;
        try
        {
            var document = XDocument.Parse(record.Xml, LoadOptions.None);
            var ns = (XNamespace)"http://schemas.microsoft.com/win/2004/08/events/event";
            var system = document.Root?.Element(ns + "System");
            var eventId = (int?)system?.Element(ns + "EventID");
            var recordId = (string?)system?.Element(ns + "EventRecordID");
            var timeText = (string?)system?.Element(ns + "TimeCreated")?.Attribute("SystemTime");
            if (eventId is null || string.IsNullOrWhiteSpace(recordId) ||
                !DateTimeOffset.TryParse(timeText, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var timestamp))
            {
                rejectionReason = "Missing Sysmon event ID, record ID, or UTC timestamp.";
                return false;
            }

            var data = document.Descendants(ns + "Data")
                .Where(x => x.Attribute("Name") is not null)
                .GroupBy(x => (string)x.Attribute("Name")!, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.Last().Value, StringComparer.OrdinalIgnoreCase);

            string? Get(string name) => data.GetValueOrDefault(name);
            int? Int(string? value)
            {
                if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)) return parsed;
                if (value?.StartsWith("0x", StringComparison.OrdinalIgnoreCase) == true && int.TryParse(value[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out parsed)) return parsed;
                return null;
            }
            ThreatEventType? type = eventId.Value switch
            {
                1 => IsInterpreter(Get("Image")) ? ThreatEventType.SCRIPT_ACTIVITY : ThreatEventType.PROCESS_STARTED,
                2 or 11 or 23 or 26 => ThreatEventType.FILE_CREATED_OR_CHANGED,
                3 => ThreatEventType.NETWORK_CONNECTION,
                22 => ThreatEventType.DOMAIN_QUERY,
                7 => ThreatEventType.IMAGE_LOAD,
                10 => ThreatEventType.PROCESS_ACCESS,
                12 or 13 or 14 when IsPersistenceTarget(Get("TargetObject")) => ThreatEventType.PERSISTENCE_CHANGE,
                15 when IsZoneIdentifier(Get("TargetFilename")) => ThreatEventType.DOWNLOAD_OBSERVED,
                4 when string.Equals(Get("State"), "Stopped", StringComparison.OrdinalIgnoreCase) => ThreatEventType.TELEMETRY_GAP,
                16 => ThreatEventType.TELEMETRY_GAP,
                _ => null
            };
            if (type is null)
            {
                rejectionReason = $"Sysmon event ID {eventId.Value} is outside the MVP event map.";
                return false;
            }

            var path = Get("TargetFilename");
            if (type == ThreatEventType.DOWNLOAD_OBSERVED && path is not null)
                path = path[..path.IndexOf(":Zone.Identifier", StringComparison.OrdinalIgnoreCase)];

            var fields = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["providerEventId"] = eventId.Value.ToString(CultureInfo.InvariantCulture)
            };
            AddIfPresent(fields, "parentImage", Get("ParentImage"));
            AddIfPresent(fields, "targetImage", Get("TargetImage"));
            AddIfPresent(fields, "targetProcessId", Get("TargetProcessId"));
            AddIfPresent(fields, "targetProcessGuid", Get("TargetProcessGuid"));
            AddIfPresent(fields, "grantedAccess", Get("GrantedAccess"));
            AddIfPresent(fields, "imageLoaded", Get("ImageLoaded"));
            AddIfPresent(fields, "signatureStatus", Get("Signed"));
            AddIfPresent(fields, "queryName", Get("QueryName"));
            AddIfPresent(fields, "queryStatus", Get("QueryStatus"));
            AddIfPresent(fields, "fixtureDescription", Get("FixtureDescription"));
            AddIfPresent(fields, "synthetic", Get("FixtureSynthetic"));
            AddIfPresent(fields, "targetObject", Get("TargetObject"));

            var hash = Get("Hashes")?.Split(',', StringSplitOptions.TrimEntries)
                .FirstOrDefault(x => x.StartsWith("SHA256=", StringComparison.OrdinalIgnoreCase))?[7..];
            var stable = Encoding.UTF8.GetBytes($"Sysmon|{recordId}");
            result = new CanonicalEvent(
                Convert.ToHexString(SHA256.HashData(stable)).ToLowerInvariant(), timestamp.ToUniversalTime(), "Sysmon",
                recordId, Environment.MachineName, type.Value,
                Int(Get("ProcessId") ?? (eventId.Value == 10 ? Get("SourceProcessId") : null)),
                Get("ProcessGuid") ?? (eventId.Value == 10 ? Get("SourceProcessGuid") : null),
                Int(Get("ParentProcessId")), Get("ParentProcessGuid"),
                Get("Image") ?? (eventId.Value == 10 ? Get("SourceImage") : null), Get("Signed"), hash,
                path ?? (eventId.Value == 7 ? Get("ImageLoaded") : eventId.Value == 10 ? Get("TargetImage") : null),
                Get("DestinationHostname") ?? Get("DestinationIp") ?? Get("QueryName"), Int(Get("DestinationPort")),
                (string?)system?.Element(ns + "Security")?.Attribute("UserID"), null, fields, 1, record.BookmarkXml);
            return true;
        }
        catch (Exception ex) when (ex is System.Xml.XmlException or FormatException or ArgumentException)
        {
            rejectionReason = $"Invalid Sysmon XML: {ex.Message}";
            return false;
        }
    }

    private static bool IsInterpreter(string? image) => image is not null && Interpreters.Contains(Path.GetFileName(image));
    private static bool IsZoneIdentifier(string? target) => target?.Contains(":Zone.Identifier", StringComparison.OrdinalIgnoreCase) == true;
    private static bool IsPersistenceTarget(string? target) => target is not null &&
        (target.Contains(@"\SOFTWARE\MICROSOFT\WINDOWS\CURRENTVERSION\RUN", StringComparison.OrdinalIgnoreCase)
         || target.Contains(@"\SYSTEM\CURRENTCONTROLSET\SERVICES\", StringComparison.OrdinalIgnoreCase)
         || target.Contains(@"\SOFTWARE\MICROSOFT\WINDOWS NT\CURRENTVERSION\SCHEDULE\TASKCACHE\", StringComparison.OrdinalIgnoreCase));
    private static void AddIfPresent(IDictionary<string, string> fields, string name, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value)) fields[name] = value;
    }
}
