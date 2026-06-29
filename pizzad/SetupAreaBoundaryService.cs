using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace pizzad;

public sealed class SetupAreaBoundaryService
{
    private const string StateCountyBase = "https://tigerweb.geo.census.gov/arcgis/rest/services/TIGERweb/State_County/MapServer";
    private const string PlacesBase = "https://tigerweb.geo.census.gov/arcgis/rest/services/TIGERweb/Places_CouSub_ConCity_SubMCD/MapServer";
    private readonly HttpClient _http;

    public SetupAreaBoundaryService(HttpClient http)
    {
        _http = http;
        _http.Timeout = TimeSpan.FromSeconds(20);
    }

    public async Task<SetupAreaBoundaryResponseDto> SearchAsync(SetupAreaBoundaryRequest request, CancellationToken ct)
    {
        var query = (request.Query ?? string.Empty).Trim();
        if (query.Length == 0)
            return new SetupAreaBoundaryResponseDto(query, [], "Enter a county or city label before searching.");

        var parsed = ParseQuery(query);
        if (parsed.State == null)
            return new SetupAreaBoundaryResponseDto(query, [], "Add a state, for example Montgomery County, Tennessee or Clarksville, TN.");

        var candidates = new List<SetupAreaBoundaryCandidateDto>();
        var diagnostics = new List<string>();

        if (!string.IsNullOrWhiteSpace(parsed.CountyName))
        {
            var countyRows = await QueryLayerAsync(
                $"{StateCountyBase}/1/query",
                "County",
                "COUNTY",
                parsed.State,
                parsed.CountyName,
                ct);
            candidates.AddRange(countyRows);
            diagnostics.Add($"County query: {parsed.CountyName}, {parsed.State.Name} returned {countyRows.Count} candidate(s).");
        }

        if (!string.IsNullOrWhiteSpace(parsed.PlaceName))
        {
            var placeRows = await QueryLayerAsync(
                $"{PlacesBase}/4/query",
                "Place",
                "PLACE",
                parsed.State,
                parsed.PlaceName,
                ct);
            candidates.AddRange(placeRows);
            diagnostics.Add($"Place query: {parsed.PlaceName}, {parsed.State.Name} returned {placeRows.Count} candidate(s).");
        }

        var distinct = candidates
            .GroupBy(c => $"{c.Kind}:{c.GeoId}", StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .OrderBy(c => c.Kind == "County" ? 0 : 1)
            .ThenBy(c => c.Label, StringComparer.OrdinalIgnoreCase)
            .Take(10)
            .ToList();

        if (distinct.Count == 0 && diagnostics.Count == 0)
            diagnostics.Add("No county or place query could be derived from the label.");
        if (distinct.Count == 0)
            diagnostics.Add("No matching Census TIGERweb boundaries were found.");

        return new SetupAreaBoundaryResponseDto(query, distinct, string.Join(" ", diagnostics));
    }

    private static ParsedBoundaryQuery ParseQuery(string query)
    {
        var normalized = Regex.Replace(query, @"\s+", " ").Trim();
        var state = FindState(normalized);
        if (state == null)
            return new ParsedBoundaryQuery(null, "", "");

        var withoutState = RemoveState(normalized, state).Trim(' ', ',');
        withoutState = Regex.Replace(withoutState, @"\b(simulcast|subsite|tower|site|rfss)\b", " ", RegexOptions.IgnoreCase);
        withoutState = Regex.Replace(withoutState, @"\s+", " ").Trim(' ', ',');

        var countyName = "";
        var placeName = "";
        var countyMatch = Regex.Match(withoutState, @"(?<name>[A-Za-z0-9 .'\-]+?)\s+County\b", RegexOptions.IgnoreCase);
        if (countyMatch.Success)
        {
            countyName = TitleWords(countyMatch.Groups["name"].Value);
            placeName = TitleWords(withoutState[..countyMatch.Index]);
        }
        else
        {
            var words = withoutState.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (words.Length == 1)
            {
                countyName = TitleWords(words[0]);
                placeName = TitleWords(words[0]);
            }
            else if (words.Length > 1)
            {
                placeName = TitleWords(words[0]);
                countyName = TitleWords(words[^1]);
            }
        }

        return new ParsedBoundaryQuery(state, countyName, placeName);
    }

    private async Task<IReadOnlyList<SetupAreaBoundaryCandidateDto>> QueryLayerAsync(
        string endpoint,
        string kind,
        string idField,
        StateInfo state,
        string name,
        CancellationToken ct)
    {
        var safeName = name.Replace("'", "''", StringComparison.Ordinal);
        var where = $"STATE = '{state.Fips}' AND BASENAME = '{safeName}'";
        var fields = $"NAME,BASENAME,STATE,GEOID,{idField}";
        var uri = $"{endpoint}?where={Uri.EscapeDataString(where)}&outFields={Uri.EscapeDataString(fields)}&returnGeometry=true&outSR=4326&geometryPrecision=6&f=json";
        using var response = await _http.GetAsync(uri, ct);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
        if (!document.RootElement.TryGetProperty("features", out var features) || features.ValueKind != JsonValueKind.Array)
            return [];

        var rows = new List<SetupAreaBoundaryCandidateDto>();
        foreach (var feature in features.EnumerateArray())
        {
            if (!feature.TryGetProperty("attributes", out var attributes) ||
                !feature.TryGetProperty("geometry", out var geometry) ||
                !TryBounds(geometry, out var bounds))
                continue;

            var labelName = GetString(attributes, "NAME");
            var geoId = GetString(attributes, "GEOID");
            if (string.IsNullOrWhiteSpace(labelName) || string.IsNullOrWhiteSpace(geoId))
                continue;

            rows.Add(new SetupAreaBoundaryCandidateDto(
                $"{labelName}, {state.Name}",
                kind,
                "Census TIGERweb",
                geoId,
                Math.Round(bounds.North, 6),
                Math.Round(bounds.South, 6),
                Math.Round(bounds.East, 6),
                Math.Round(bounds.West, 6)));
        }

        return rows;
    }

    private static bool TryBounds(JsonElement geometry, out BoundaryBounds bounds)
    {
        var north = double.MinValue;
        var south = double.MaxValue;
        var east = double.MinValue;
        var west = double.MaxValue;
        var found = false;

        if (!geometry.TryGetProperty("rings", out var rings) || rings.ValueKind != JsonValueKind.Array)
        {
            bounds = default;
            return false;
        }

        foreach (var ring in rings.EnumerateArray())
        {
            foreach (var point in ring.EnumerateArray())
            {
                if (point.ValueKind != JsonValueKind.Array || point.GetArrayLength() < 2)
                    continue;
                var lon = point[0].GetDouble();
                var lat = point[1].GetDouble();
                if (lat < -90 || lat > 90 || lon < -180 || lon > 180)
                    continue;
                north = Math.Max(north, lat);
                south = Math.Min(south, lat);
                east = Math.Max(east, lon);
                west = Math.Min(west, lon);
                found = true;
            }
        }

        bounds = new BoundaryBounds(north, south, east, west);
        return found && north > south && east > west;
    }

    private static string GetString(JsonElement element, string name)
        => element.TryGetProperty(name, out var property) ? property.ToString() : "";

    private static StateInfo? FindState(string query)
    {
        var trailing = Regex.Match(query, @",\s*(?<state>[A-Za-z .]+)$");
        if (trailing.Success)
        {
            var token = trailing.Groups["state"].Value.Trim();
            if (StateByAbbreviation.TryGetValue(token.ToUpperInvariant(), out var byAbbreviation))
                return byAbbreviation;
            return States.FirstOrDefault(s => string.Equals(s.Name, token, StringComparison.OrdinalIgnoreCase));
        }

        return States.FirstOrDefault(s =>
            Regex.IsMatch(query, $@"\b{Regex.Escape(s.Name)}\b", RegexOptions.IgnoreCase) ||
            Regex.IsMatch(query, $@"\b{Regex.Escape(s.Abbreviation)}\b", RegexOptions.IgnoreCase));
    }

    private static string RemoveState(string query, StateInfo state)
    {
        var without = Regex.Replace(query, $@",?\s*{Regex.Escape(state.Name)}\s*$", "", RegexOptions.IgnoreCase).Trim();
        without = Regex.Replace(without, $@",?\s*{Regex.Escape(state.Abbreviation)}\s*$", "", RegexOptions.IgnoreCase).Trim();
        return without;
    }

    private static string TitleWords(string value)
    {
        value = Regex.Replace(value ?? "", @"[^A-Za-z0-9 .'\-]", " ");
        value = Regex.Replace(value, @"\s+", " ").Trim();
        if (value.Length == 0)
            return "";
        return CultureInfo.InvariantCulture.TextInfo.ToTitleCase(value.ToLowerInvariant());
    }

    private sealed record ParsedBoundaryQuery(StateInfo? State, string CountyName, string PlaceName);
    private readonly record struct BoundaryBounds(double North, double South, double East, double West);
    private sealed record StateInfo(string Abbreviation, string Name, string Fips);

    private static readonly StateInfo[] States =
    [
        new("AL", "Alabama", "01"), new("AK", "Alaska", "02"), new("AZ", "Arizona", "04"),
        new("AR", "Arkansas", "05"), new("CA", "California", "06"), new("CO", "Colorado", "08"),
        new("CT", "Connecticut", "09"), new("DE", "Delaware", "10"), new("DC", "District Of Columbia", "11"),
        new("FL", "Florida", "12"), new("GA", "Georgia", "13"), new("HI", "Hawaii", "15"),
        new("ID", "Idaho", "16"), new("IL", "Illinois", "17"), new("IN", "Indiana", "18"),
        new("IA", "Iowa", "19"), new("KS", "Kansas", "20"), new("KY", "Kentucky", "21"),
        new("LA", "Louisiana", "22"), new("ME", "Maine", "23"), new("MD", "Maryland", "24"),
        new("MA", "Massachusetts", "25"), new("MI", "Michigan", "26"), new("MN", "Minnesota", "27"),
        new("MS", "Mississippi", "28"), new("MO", "Missouri", "29"), new("MT", "Montana", "30"),
        new("NE", "Nebraska", "31"), new("NV", "Nevada", "32"), new("NH", "New Hampshire", "33"),
        new("NJ", "New Jersey", "34"), new("NM", "New Mexico", "35"), new("NY", "New York", "36"),
        new("NC", "North Carolina", "37"), new("ND", "North Dakota", "38"), new("OH", "Ohio", "39"),
        new("OK", "Oklahoma", "40"), new("OR", "Oregon", "41"), new("PA", "Pennsylvania", "42"),
        new("RI", "Rhode Island", "44"), new("SC", "South Carolina", "45"), new("SD", "South Dakota", "46"),
        new("TN", "Tennessee", "47"), new("TX", "Texas", "48"), new("UT", "Utah", "49"),
        new("VT", "Vermont", "50"), new("VA", "Virginia", "51"), new("WA", "Washington", "53"),
        new("WV", "West Virginia", "54"), new("WI", "Wisconsin", "55"), new("WY", "Wyoming", "56")
    ];

    private static readonly Dictionary<string, StateInfo> StateByAbbreviation =
        States.ToDictionary(s => s.Abbreviation, StringComparer.OrdinalIgnoreCase);
}
