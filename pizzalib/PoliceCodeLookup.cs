using System.Text.RegularExpressions;

namespace pizzalib;

public static class PoliceCodeLookup
{
    private static readonly Dictionary<string, string> Canonical = new(StringComparer.OrdinalIgnoreCase)
    {
        // APCO-style defaults. Agencies often customize meanings.
        ["10-0"] = "Use caution",
        ["10-1"] = "Unable to copy/change location",
        ["10-2"] = "Signal good",
        ["10-3"] = "Stop transmitting",
        ["10-4"] = "Acknowledged/OK",
        ["10-5"] = "Relay",
        ["10-6"] = "Busy unless urgent",
        ["10-7"] = "Out of service",
        ["10-8"] = "In service",
        ["10-9"] = "Repeat",
        ["10-10"] = "Fight in progress",
        ["10-11"] = "Dog case",
        ["10-12"] = "Stand by/visitors present",
        ["10-13"] = "Officer needs help",
        ["10-14"] = "Escort/convoy",
        ["10-15"] = "Prisoner in custody",
        ["10-16"] = "Pick up prisoner",
        ["10-17"] = "Paperwork",
        ["10-18"] = "Urgent",
        ["10-19"] = "Return to station",
        ["10-20"] = "Location",
        ["10-21"] = "Call by telephone",
        ["10-22"] = "Disregard",
        ["10-23"] = "Arrived at scene",
        ["10-24"] = "Assignment complete",
        ["10-25"] = "Report to station",
        ["10-26"] = "Detaining subject",
        ["10-27"] = "Driver license information",
        ["10-28"] = "Vehicle registration information",
        ["10-29"] = "Check wanted",
        ["10-30"] = "Unauthorized radio use",
        ["10-31"] = "Crime in progress",
        ["10-32"] = "Person with gun",
        ["10-33"] = "Emergency traffic",
        ["10-34"] = "Riot",
        ["10-35"] = "Major crime alert",
        ["10-36"] = "Correct time",
        ["10-37"] = "Investigate suspicious vehicle",
        ["10-38"] = "Stopping suspicious vehicle",
        ["10-39"] = "Urgent use lights/siren",
        ["10-40"] = "Silent run/no lights or siren",
        ["10-41"] = "Begin duty",
        ["10-42"] = "End duty",
        ["10-43"] = "Information",
        ["10-44"] = "Permission to leave patrol",
        ["10-45"] = "Condition of patient",
        ["10-46"] = "Assist motorist",
        ["10-47"] = "Emergency road repairs",
        ["10-48"] = "Traffic standard repair",
        ["10-49"] = "Traffic light out",
        ["10-50"] = "Accident",
        ["10-51"] = "Wrecker needed",
        ["10-52"] = "Request ambulance",
        ["10-53"] = "Road blocked",
        ["10-54"] = "Livestock on highway",
        ["10-55"] = "Intoxicated driver",
        ["10-56"] = "Intoxicated pedestrian",
        ["10-57"] = "Hit and run",
        ["10-58"] = "Direct traffic",
        ["10-59"] = "Escort",
        ["10-60"] = "Squad in vicinity",
        ["10-61"] = "Personnel in area",
        ["10-62"] = "Reply to message",
        ["10-63"] = "Prepare to copy",
        ["10-64"] = "Message for local delivery",
        ["10-65"] = "Net message assignment",
        ["10-66"] = "Message cancellation",
        ["10-67"] = "Clear to read net messages",
        ["10-68"] = "Dispatch information",
        ["10-69"] = "Message received",
        ["10-70"] = "Fire alarm",
        ["10-71"] = "Advise nature of fire",
        ["10-72"] = "Report progress on fire",
        ["10-73"] = "Smoke report",
        ["10-74"] = "Negative",
        ["10-75"] = "In contact with complainant",
        ["10-76"] = "En route",
        ["10-77"] = "ETA",
        ["10-78"] = "Need assistance",
        ["10-79"] = "Notify coroner",
        ["10-80"] = "Pursuit in progress",
        ["10-81"] = "Breathalyzer report",
        ["10-82"] = "Reserve lodging",
        ["10-83"] = "Work school crossing",
        ["10-84"] = "Meet complainant",
        ["10-85"] = "Delay due to assignment",
        ["10-86"] = "Officer/operator on duty",
        ["10-87"] = "Meet officer",
        ["10-88"] = "Present phone number",
        ["10-89"] = "Bomb threat",
        ["10-90"] = "Bank alarm",
        ["10-91"] = "Pick up prisoner/subject",
        ["10-92"] = "Improperly parked vehicle",
        ["10-93"] = "Blockade",
        ["10-94"] = "Drag racing",
        ["10-95"] = "Prisoner/subject in custody",
        ["10-96"] = "Mental subject",
        ["10-97"] = "On scene",
        ["10-98"] = "Assignment completed",
        ["10-99"] = "Wanted/stolen indicated",
    };

    private static readonly Dictionary<string, string> TokenToCanonical = BuildTokenMap();

    private static Dictionary<string, string> BuildTokenMap()
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var kv in Canonical)
        {
            map[NormalizeToken(kv.Key)] = kv.Key;

            var parts = kv.Key.Split('-');
            if (parts.Length == 2)
            {
                map[$"{parts[0]} {parts[1]}"] = kv.Key;
                map[$"{parts[0]}{parts[1]}"] = kv.Key;
                if (parts[0] == "10")
                {
                    map[$"ten {parts[1]}"] = kv.Key;
                    map[$"ten-{parts[1]}"] = kv.Key;
                    if (int.TryParse(parts[1], out var suffix))
                    {
                        var words = NumberToWords(suffix);
                        if (!string.IsNullOrWhiteSpace(words))
                        {
                            map[$"ten {words}"] = kv.Key;
                            map[$"ten-{words}"] = kv.Key;
                        }
                    }
                }
            }
        }

        return map;
    }

    private static string NumberToWords(int value)
    {
        if (value < 0 || value > 99)
            return string.Empty;

        var ones = new[]
        {
            "zero", "one", "two", "three", "four", "five", "six", "seven", "eight", "nine",
            "ten", "eleven", "twelve", "thirteen", "fourteen", "fifteen", "sixteen",
            "seventeen", "eighteen", "nineteen"
        };
        var tens = new[]
        {
            "", "", "twenty", "thirty", "forty", "fifty", "sixty", "seventy", "eighty", "ninety"
        };

        if (value < 20)
            return ones[value];

        var t = value / 10;
        var o = value % 10;
        return o == 0 ? tens[t] : $"{tens[t]} {ones[o]}";
    }

    private static string NormalizeToken(string value)
    {
        return Regex.Replace(value.Trim().ToLowerInvariant(), "\\s+", " ");
    }

    public static bool TryMatchInText(string? transcription, IEnumerable<string> configuredCodes, out string matchedCode)
    {
        matchedCode = string.Empty;
        if (string.IsNullOrWhiteSpace(transcription))
        {
            return false;
        }

        var normalizedText = NormalizeToken(transcription);
        foreach (var raw in configuredCodes)
        {
            if (string.IsNullOrWhiteSpace(raw))
            {
                continue;
            }

            var normalizedInput = NormalizeToken(raw);
            if (!TokenToCanonical.TryGetValue(normalizedInput, out var canonical))
            {
                canonical = normalizedInput;
            }

            var probes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                canonical,
                canonical.Replace("-", " "),
                canonical.Replace("-", string.Empty)
            };

            if (canonical.StartsWith("10-", StringComparison.OrdinalIgnoreCase))
            {
                var suffix = canonical.Substring(3);
                probes.Add($"ten {suffix}");
            }

            if (probes.Any(p => normalizedText.Contains(p, StringComparison.OrdinalIgnoreCase)))
            {
                matchedCode = canonical;
                return true;
            }
        }

        return false;
    }

    public static IReadOnlyDictionary<string, string> GetSupportedCodes() => Canonical;
}
