using System.Globalization;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;

namespace pizzad;

public sealed class GeocodingService
{
    private readonly EngineDatabase _database;
    private readonly ILogger<GeocodingService> _logger;
    private readonly HttpClient _http;
    private readonly SemaphoreSlim _nominatimGate = new(1, 1);
    private DateTime _lastNominatimRequestUtc = DateTime.MinValue;

    public GeocodingService(EngineDatabase database, ILogger<GeocodingService> logger, HttpClient http)
    {
        _database = database;
        _logger = logger;
        _http = http;
        _http.Timeout = TimeSpan.FromSeconds(8);
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("PizzaWave/1.0 (private dashboard geocoding)");
    }

    public async Task<GeocodeCacheDto?> ResolveAsync(string locationText, MonitoredAreaConfig area, CancellationToken ct)
    {
        var (row, _) = await ResolveWithStatusAsync(locationText, area, ct);
        return row;
    }

    public async Task<GeocodeCacheDto?> GetCachedAsync(string locationText, MonitoredAreaConfig area, CancellationToken ct)
    {
        var cached = await _database.GetGeocodeCacheAsync(CacheKey(area.AreaId, locationText), ct);
        if (cached == null)
            return null;
        if (string.Equals(cached.Provider, "none", StringComparison.OrdinalIgnoreCase))
            return cached;
        return LooksRelevant(locationText, cached.DisplayName) ? cached : null;
    }

    public async Task<(GeocodeCacheDto? Row, bool FromCache)> ResolveWithStatusAsync(string locationText, MonitoredAreaConfig area, CancellationToken ct)
    {
        var query = BuildQuery(locationText, area);
        var cacheKey = CacheKey(area.AreaId, locationText);
        var cached = await _database.GetGeocodeCacheAsync(cacheKey, ct);
        if (cached != null)
        {
            if (string.Equals(cached.Provider, "none", StringComparison.OrdinalIgnoreCase))
                return (cached, true);
            if (!LooksRelevant(locationText, cached.DisplayName))
                return (null, true);
            return (cached, true);
        }

        await _nominatimGate.WaitAsync(ct);
        try
        {
            var elapsed = DateTime.UtcNow - _lastNominatimRequestUtc;
            if (elapsed < TimeSpan.FromSeconds(1.1))
                await Task.Delay(TimeSpan.FromSeconds(1.1) - elapsed, ct);

            var url = "https://nominatim.openstreetmap.org/search" +
                      $"?format=jsonv2&limit=1&countrycodes=us&addressdetails=1&bounded=1" +
                      $"&viewbox={area.West.ToString(CultureInfo.InvariantCulture)},{area.North.ToString(CultureInfo.InvariantCulture)},{area.East.ToString(CultureInfo.InvariantCulture)},{area.South.ToString(CultureInfo.InvariantCulture)}" +
                      $"&q={Uri.EscapeDataString(query)}";
            _lastNominatimRequestUtc = DateTime.UtcNow;
            var rows = await _http.GetFromJsonAsync<List<NominatimResult>>(url, ct);
            var best = rows?.FirstOrDefault();
            if (best == null ||
                !double.TryParse(best.Lat, NumberStyles.Float, CultureInfo.InvariantCulture, out var lat) ||
                !double.TryParse(best.Lon, NumberStyles.Float, CultureInfo.InvariantCulture, out var lon))
            {
                _logger.LogInformation("No geocode match for '{Query}' in {Area}", query, area.AreaLabel);
                return (await CacheNegativeAsync(cacheKey, query, locationText, area, ct), false);
            }

            if (!WithinBounds(lat, lon, area) && !IsKnownNearbyPlace(locationText))
            {
                _logger.LogInformation("Rejected geocode match outside bounds for '{Query}': {DisplayName}", query, best.DisplayName);
                return (await CacheNegativeAsync(cacheKey, query, locationText, area, ct), false);
            }
            if (!LooksRelevant(locationText, best.DisplayName ?? string.Empty))
            {
                _logger.LogInformation("Rejected weak geocode match for '{Query}': {DisplayName}", query, best.DisplayName);
                return (await CacheNegativeAsync(cacheKey, query, locationText, area, ct), false);
            }

            var row = new GeocodeCacheDto
            {
                CacheKey = cacheKey,
                Provider = "nominatim",
                Query = query,
                AreaId = area.AreaId,
                LocationText = locationText,
                DisplayName = best.DisplayName ?? string.Empty,
                Precision = Precision(locationText, best),
                Confidence = Confidence(locationText, best),
                Latitude = lat,
                Longitude = lon,
                CreatedAtUtc = DateTime.UtcNow,
                UpdatedAtUtc = DateTime.UtcNow
            };
            await _database.UpsertGeocodeCacheAsync(row, ct);
            return (row, false);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            _logger.LogWarning(ex, "Geocoding failed for '{Query}'", query);
            return (await CacheNegativeAsync(cacheKey, query, locationText, area, CancellationToken.None), false);
        }
        finally
        {
            _nominatimGate.Release();
        }
    }

    private async Task<GeocodeCacheDto> CacheNegativeAsync(string cacheKey, string query, string locationText, MonitoredAreaConfig area, CancellationToken ct)
    {
        var row = new GeocodeCacheDto
        {
            CacheKey = cacheKey,
            Provider = "none",
            Query = query,
            AreaId = area.AreaId,
            LocationText = locationText,
            DisplayName = string.Empty,
            Precision = "none",
            Confidence = 0,
            Latitude = 0,
            Longitude = 0,
            CreatedAtUtc = DateTime.UtcNow,
            UpdatedAtUtc = DateTime.UtcNow
        };
        await _database.UpsertGeocodeCacheAsync(row, ct);
        return row;
    }

    private static bool WithinBounds(double lat, double lon, MonitoredAreaConfig area) =>
        lat <= area.North && lat >= area.South && lon >= area.West && lon <= area.East;

    private static string BuildQuery(string locationText, MonitoredAreaConfig area)
    {
        if (locationText.Contains("fort oglethorpe", StringComparison.OrdinalIgnoreCase))
            return $"{locationText}, Georgia, USA";
        return $"{locationText}, {area.AreaLabel}, Tennessee, USA";
    }

    private static bool IsKnownNearbyPlace(string locationText) =>
        locationText.Contains("fort oglethorpe", StringComparison.OrdinalIgnoreCase);

    private static bool LooksRelevant(string locationText, string displayName)
    {
        var query = Normalize(locationText);
        var display = Normalize(displayName);
        var queryTokens = query.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Where(t => t.Length > 1)
            .ToList();
        if (queryTokens.Count == 0)
            return false;

        var numericTokens = queryTokens.Where(t => t.All(char.IsDigit)).ToList();
        if (query.StartsWith("i ") || query.StartsWith("interstate ") || query.StartsWith("us ") ||
            query.StartsWith("hwy ") || query.StartsWith("highway ") || query.StartsWith("sr ") ||
            query.StartsWith("state route "))
            return numericTokens.Count > 0 && numericTokens.All(t => display.Contains(t, StringComparison.Ordinal));

        var streetTokens = queryTokens.Where(t => !t.All(char.IsDigit) && !IsStreetSuffix(t)).ToList();
        return streetTokens.Count == 0 || streetTokens.Any(t => display.Contains(t, StringComparison.Ordinal));
    }

    private static string Normalize(string value)
    {
        value = value.ToLowerInvariant();
        value = value.Replace("-", " ");
        var chars = value.Select(c => char.IsLetterOrDigit(c) ? c : ' ').ToArray();
        return string.Join(' ', new string(chars).Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }

    private static bool IsStreetSuffix(string token) =>
        token is "street" or "st" or "road" or "rd" or "avenue" or "ave" or "drive" or "dr" or "lane" or "ln" or
            "boulevard" or "blvd" or "highway" or "hwy" or "pike" or "place" or "pl" or "court" or "ct" or
            "way" or "circle" or "cir" or "terrace" or "ter" or "trail" or "trl" or "parkway" or "pkwy";

    public static string CacheKey(string areaId, string locationText)
    {
        var raw = $"{areaId.Trim().ToLowerInvariant()}|{locationText.Trim().ToLowerInvariant()}";
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(raw));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static string Precision(string locationText, NominatimResult result)
    {
        if (locationText.Any(char.IsDigit) && IsClass(result, "building", "place", "amenity", "shop", "office"))
            return "address";
        if (IsType(result, "residential", "primary", "secondary", "tertiary", "trunk", "motorway", "service", "unclassified"))
            return "street";
        if (IsClass(result, "highway"))
            return "road";
        return string.IsNullOrWhiteSpace(result.Type) ? "unknown" : result.Type;
    }

    private static double Confidence(string locationText, NominatimResult result)
    {
        var score = Math.Clamp(result.Importance ?? 0.4, 0.1, 1.0);
        if (locationText.Any(char.IsDigit)) score += 0.25;
        if (IsClass(result, "highway", "building", "place", "amenity", "shop")) score += 0.15;
        return Math.Clamp(score, 0.1, 0.99);
    }

    private static bool IsClass(NominatimResult result, params string[] values) =>
        values.Any(v => string.Equals(result.Class, v, StringComparison.OrdinalIgnoreCase));

    private static bool IsType(NominatimResult result, params string[] values) =>
        values.Any(v => string.Equals(result.Type, v, StringComparison.OrdinalIgnoreCase));

    private sealed record NominatimResult
    {
        [JsonPropertyName("lat")]
        public string Lat { get; init; } = string.Empty;

        [JsonPropertyName("lon")]
        public string Lon { get; init; } = string.Empty;

        [JsonPropertyName("display_name")]
        public string? DisplayName { get; init; }

        [JsonPropertyName("class")]
        public string? Class { get; init; }

        [JsonPropertyName("type")]
        public string? Type { get; init; }

        [JsonPropertyName("importance")]
        public double? Importance { get; init; }
    }
}
