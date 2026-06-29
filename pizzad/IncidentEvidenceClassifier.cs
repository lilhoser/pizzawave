using System.Text.RegularExpressions;

namespace pizzad;

public sealed record IncidentEvidenceProfile(
    IReadOnlySet<string> Concepts,
    string EvidenceClass,
    string Category,
    string FallbackPhrase)
{
    public bool HasSpecificEvent => Concepts.Count > 0;
}

public static class IncidentEvidenceClassifier
{
    private sealed record Rule(string Concept, Regex Pattern, string EvidenceClass, string Category, string FallbackPhrase);

    private static readonly Rule[] Rules =
    [
        BuildRule("non_breathing", @"\b(?:not breathing|turning blue|cpr|cardiac arrest|respiratory arrest|chest (?:is )?not rising (?:or|and) falling|chest (?:is )?not moving)\b", "medical", "ems", "Non-breathing patient"),
        BuildRule("unconscious", @"\b(?:unconscious|unresponsive|not\s+(?:completely\s+)?responsive|not moving|did not react|not reacting|passed out)\b", "medical", "ems", "Unconscious person"),
        BuildRule("chest_pain", @"\bchest pains?\b", "medical", "ems", "Chest pain"),
        BuildRule("heart_problem", @"\b(?:heart (?:issues?|problems?|(?:rate\s+)?(?:is\s+)?(?:racing|fast|high|elevated))|(?:fast|high|elevated)\s+heart\s+rate|palpitations?|(?:feels?|feeling|felt)\s+like\s+(?:it(?:'s| is)|his|her|their|my)?\s*(?:heart\s+)?(?:is\s+)?racing)\b", "medical", "ems", "Heart problems"),
        BuildRule("difficulty_breathing", @"\b(?:difficulty breathing|shortness of breath|respiratory distress)\b", "medical", "ems", "Difficulty breathing"),
        BuildRule("diabetic_emergency", @"\bdiabetic emergency\b", "medical", "ems", "Diabetic emergency"),
        BuildRule("allergic_reaction", @"\ballergic reaction\b", "medical", "ems", "Allergic reaction"),
        BuildRule("seizure", @"\bseizure\b", "medical", "ems", "Seizure"),
        BuildRule("overdose", @"\b(?:overdose|fentanyl)\b", "medical", "ems", "Overdose"),
        BuildRule("stroke", @"\bstroke\b", "medical", "ems", "Stroke"),
        BuildRule("fall", @"\bfall\b", "medical", "ems", "Fall"),
        BuildRule("injury", @"\b(?:injury|injuries|injured|bleeding|laceration)\b", "medical", "ems", string.Empty),

        BuildRule("vehicle_off_roadway", @"\b(?:vehicle\s+(?:off|out of)\s+(?:roadway|road)|(?:off|out of)\s+(?:the\s+)?roadway|hit\s+(?:a\s+|an\s+)?animal|10[- ](?:49|50|51|52))\b", "traffic_crash", "traffic", "Vehicle off roadway"),
        BuildRule("vehicle_fixed_object", @"\bhit\s+(?:(?:a|an|the|their|his|her|our|your|my|its)\s+)?(?:pole|tree|guardrail|barrier|building|wall|fence|ditch)\b|\b(?:vehicle|car|truck|tractor trailer|tanker|trailer)\b.{0,30}\b(?:versus|vs\.?)\b.{0,30}\b(?:guardrail|barrier|pole|tree|ditch|wall|fence)\b", "traffic_crash", "traffic", "Vehicle hit fixed object"),
        BuildRule("hit_and_run", @"\b(?:hit and run|hit-and-run)\b", "traffic_crash", "traffic", "Hit-and-run accident"),
        BuildRule("crash", @"\b(?:mvc|mva|motor vehicle collision|motor vehicle accident|accident|crash|collision|wreck|rear[- ]?end(?:ed|ing)?)\b|\b(?:vehicle|car|truck|tractor trailer|tanker|trailer)\b.{0,30}\b(?:versus|vs\.?)\b.{0,30}\b(?:guardrail|barrier|pole|tree|ditch|wall|fence)\b", "traffic_crash", "traffic", "Vehicle accident"),
        BuildRule("reckless_driver", @"\b(?:reckless driver|driv(?:en|ing) all over (?:the )?(?:road|roadway)|could(?:n't| not) maintain speed)\b", "road_hazard", "traffic", "Reckless driver"),
        BuildRule("road_hazard", @"\b(?:road(?:way)? hazard|blocking (?:the )?road(?:way)?|blocking|debris (?:in|on) (?:the )?road(?:way)?|debris|items? (?:in|on) (?:the )?road(?:way)?|tree down|disabled vehicle|lane blocked|in the roadway|in the road)\b|\b(?:vehicle|car|truck)\b.{0,45}\b(?:park(?:ed|ing)|stopped|disabled)\b.{0,45}\b(?:middle of (?:the )?road|in (?:the )?road(?:way)?|blocking)\b|\bhit\s+(?:his|her|their|the)?\s*windshield\b", "road_hazard", "traffic", "Roadway hazard"),

        BuildRule("vehicle_fire", @"\b(?:vehicle fire|car fire|truck fire)\b", "fire_or_hazard", "fire", "Vehicle fire"),
        BuildRule("fire_alarm", @"\b(?:fire alarm|smoke alarm|automatic fire|firewall|alarm panel)\b", "fire_or_hazard", "fire", "Fire alarm"),
        BuildRule("carbon_monoxide", @"\b(?:carbon monoxide|c\.?o\.?\s+alarm)\b", "fire_or_hazard", "fire", "Carbon monoxide alarm"),
        BuildRule("gas_leak", @"\bgas (?:leak|line)\b", "fire_or_hazard", "fire", "Gas leak"),
        BuildRule("fire", @"\b(?:(?:brush|structure|house|apartment|vehicle|car|truck|building|field|grass|woods?|shed|dumpster|electrical)\s+fire|structure fire|house fire|apartment fire|working fire|commercial fire alarm|smoke|flames?|hazmat|explosion)\b", "fire_or_hazard", "fire", "Fire"),

        BuildRule("shooting", @"\b(?:shooting|person shot|gunshot|shots?\s+fired|shot\s+(?:out|at)|gsw|dsw)\b", "police", "police", "Shooting"),
        BuildRule("stabbing", @"\bstab(?:bing|bed)?\b", "police", "police", "Stabbing"),
        BuildRule("assault", @"\b(?:assault|batter(?:y|ed|ing)|beat(?:en|ing)|fight(?:ing)?)\b", "police", "police", "Assault"),
        BuildRule("suicide", @"\b(?:suicide|suicidal|self harm|harming himself|harming herself)\b", "police", "police", "Suicide threat"),
        BuildRule("domestic", @"\bdomestic(?:\s+(?:unknown|disturbance|dispute|violence))?\b", "police", "police", string.Empty),
        BuildRule("burglary", @"\b(?:burglary|home invasion|robbery|vehicle (?:was )?burglarized|stole(?:n)?|theft)\b", "police", "police", "Burglary"),
        BuildRule("missing_person", @"\b(?:missing (?:person|party|male|female|juvenile)|missing since|reported missing|entered\s+(?:as\s+)?missing)\b", "police", "police", string.Empty),
        BuildRule("welfare_check", @"\b(?:welfare check|well[- ]?being check|check welfare)\b", "police", "police", string.Empty),
        BuildRule("pursuit", @"\b(?:pursuit|chase)\b", "police", "police", "Pursuit")
    ];

    public static IncidentEvidenceProfile Analyze(string? text)
    {
        var source = text ?? string.Empty;
        var concepts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string evidenceClass = string.Empty;
        string category = "other";
        string fallback = string.Empty;

        foreach (var rule in Rules)
        {
            if (!rule.Pattern.IsMatch(source))
                continue;

            concepts.Add(rule.Concept);
            if (string.IsNullOrWhiteSpace(evidenceClass))
                evidenceClass = rule.EvidenceClass;
            if (category == "other" && !string.IsNullOrWhiteSpace(rule.Category))
                category = rule.Category;
            if (string.IsNullOrWhiteSpace(fallback) && !string.IsNullOrWhiteSpace(rule.FallbackPhrase))
                fallback = rule.FallbackPhrase;
        }

        return new IncidentEvidenceProfile(concepts, evidenceClass, category, fallback);
    }

    private static Rule BuildRule(string concept, string pattern, string evidenceClass, string category, string fallbackPhrase) =>
        new(
            concept,
            new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled),
            evidenceClass,
            category,
            fallbackPhrase);
}
