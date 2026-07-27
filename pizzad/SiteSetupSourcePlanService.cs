using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace pizzad;

public sealed class SiteSetupSourcePlanService
{
    private const double UsableHalfSpanFactor = 0.46875;

    public SiteSetupSourcePlanProjectionDto Project(SiteSetupConfig desired, int? requestedSampleRateHz = null)
    {
        var systems = (desired.Systems ?? []).Where(system => !string.IsNullOrWhiteSpace(system.ShortName)).ToList();
        var sources = (desired.Sources ?? []).OrderBy(source => source.Index).ToList();
        var rate = requestedSampleRateHz is > 0
            ? requestedSampleRateHz.Value
            : sources.Select(source => source.SampleRate).Where(value => value > 0).DefaultIfEmpty(2_400_000).Max();
        var combinations = BuildCombinations(systems);
        var options = new List<SiteSetupSourcePlanOptionDto>();
        foreach (var combination in combinations)
        {
            options.Add(BuildOption(systems, sources, combination, "full", rate));
            if (combination.Count > 1)
                options.Add(BuildOption(systems, sources, combination, "control", rate));
        }

        options = options.DistinctBy(option => option.Id, StringComparer.OrdinalIgnoreCase).ToList();
        var recommended = options
            .Where(option => option.Fits)
            .OrderByDescending(option => option.SystemShortNames.Count)
            .ThenByDescending(option => option.CoveredFrequenciesHz.Count)
            .ThenBy(option => option.Windows.Count)
            .FirstOrDefault();
        var warnings = new List<string>();
        if (systems.Count == 0) warnings.Add("Select at least one site before source planning.");
        if (sources.Count == 0) warnings.Add("Run SDR Inventory before source planning.");
        if (recommended == null && systems.Count > 0 && sources.Count > 0) warnings.Add("No plan fits the detected SDR count at the selected sample rate.");
        var versionInput = JsonSerializer.Serialize(new
        {
            desired.DesiredVersion,
            SampleRateHz = rate,
            Systems = systems.Select(system => new { system.ShortName, system.ControlChannelsHz, system.VoiceFrequenciesHz }),
            Sources = sources.Select(source => new { source.Index, source.Serial, source.Device, source.SampleRate })
        }, EngineConfig.JsonOptions());
        var projectionVersion = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(versionInput))).ToLowerInvariant();
        return new SiteSetupSourcePlanProjectionDto(
            projectionVersion,
            desired.DesiredVersion,
            rate,
            sources.Count,
            recommended?.Id ?? string.Empty,
            options,
            ["Usable tuning width is 93.75% of sample rate.", "Control-channel-only plans may not capture all voice traffic."],
            warnings);
    }

    public SiteSetupDesiredPatch Select(
        SiteSetupConfig desired,
        SiteSetupSourcePlanSelectionRequest request)
    {
        var projection = Project(desired, request.SampleRateHz);
        if (!string.Equals(projection.ProjectionVersion, request.ProjectionVersion, StringComparison.Ordinal))
            throw new InvalidOperationException("Source Coverage changed after this projection was reviewed. Regenerate and review the current server plan.");
        var option = projection.Options.FirstOrDefault(value => string.Equals(value.Id, request.OptionId, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException("The selected source-plan option is not present in the current projection.");
        if (!option.Fits)
            throw new InvalidOperationException(option.Reason);

        var sourcesByIndex = desired.Sources.ToDictionary(source => source.Index);
        var projectedSources = option.SelectedSourceIndexes.Select((sourceIndex, index) =>
        {
            var source = sourcesByIndex[sourceIndex];
            var window = option.Windows[Math.Min(index, option.Windows.Count - 1)];
            return source with { CenterHz = window.CenterHz, SampleRate = projection.SampleRateHz };
        }).ToList();
        var assignments = option.SourceAssignments.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);
        foreach (var pair in request.SourceAssignments ?? new Dictionary<string, int>())
        {
            if (!option.SystemShortNames.Contains(pair.Key, StringComparer.OrdinalIgnoreCase) || !option.SelectedSourceIndexes.Contains(pair.Value))
                throw new InvalidOperationException($"Source {pair.Value} is not a valid assignment for {pair.Key} in this projection.");
            assignments[pair.Key] = pair.Value;
        }
        return new SiteSetupDesiredPatch
        {
            SourcePlanSystemShortNames = option.SystemShortNames.ToList(),
            SourcePlanMode = option.Mode,
            SelectedSourceIndexes = option.SelectedSourceIndexes.ToList(),
            SourceAssignments = assignments,
            Sources = desired.Sources.Select(source => projectedSources.FirstOrDefault(value => value.Index == source.Index) ?? source).ToList()
        };
    }

    private static SiteSetupSourcePlanOptionDto BuildOption(
        IReadOnlyList<RfSurveySystemDto> allSystems,
        IReadOnlyList<RfSurveySourceDto> sources,
        IReadOnlyList<string> selectedNames,
        string mode,
        int sampleRateHz)
    {
        var included = allSystems.Where(system => selectedNames.Contains(system.ShortName, StringComparer.OrdinalIgnoreCase)).ToList();
        var missingControl = included.Where(system => !system.ControlChannelsHz.Any(value => value > 0)).Select(system => system.SiteLabel).ToList();
        var frequencies = included
            .SelectMany(system => mode == "control" ? system.ControlChannelsHz : system.ControlChannelsHz.Concat(system.VoiceFrequenciesHz))
            .Where(value => value > 0).Distinct().Order().ToList();
        var priorities = mode == "control"
            ? included.SelectMany(system => system.ControlChannelsHz).Where(value => value > 0).Distinct().Order().ToList()
            : [];
        var windows = BuildWindows(frequencies, sampleRateHz, priorities);
        var selectedSources = sources.Take(windows.Count).Select(source => source.Index).ToList();
        var assignments = BuildAssignments(included, mode, windows, selectedSources);
        var missed = frequencies.Where(frequency => !windows.Any(window => frequency >= window.LowHz && frequency <= window.HighHz)).ToList();
        var fits = missingControl.Count == 0 && windows.Count > 0 && windows.Count <= sources.Count && missed.Count == 0;
        var reason = missingControl.Count > 0
            ? $"No validated control channel for {string.Join(", ", missingControl)}."
            : windows.Count > sources.Count
                ? $"Needs {windows.Count} SDR source windows; {sources.Count} detected."
                : mode == "control" ? "Fits control-channel coverage; voice coverage may be incomplete." : "Fits detected SDR hardware.";
        var allSelected = selectedNames.Count == allSystems.Count;
        var label = allSelected
            ? mode == "control" ? "All sites: control channels" : "All selected sites"
            : selectedNames.Count == 1
                ? $"Focus: {included[0].SiteLabel}"
                : $"Prioritize {selectedNames.Count} sites";
        var id = $"{mode}:{string.Join("|", selectedNames)}";
        return new SiteSetupSourcePlanOptionDto(
            id,
            label,
            mode,
            selectedNames.ToList(),
            included.Select(system => string.IsNullOrWhiteSpace(system.SiteLabel) ? system.ShortName : system.SiteLabel).ToList(),
            frequencies,
            missed,
            windows,
            selectedSources,
            assignments,
            fits,
            reason);
    }

    private static IReadOnlyDictionary<string, int> BuildAssignments(
        IReadOnlyList<RfSurveySystemDto> systems,
        string mode,
        IReadOnlyList<SiteSetupSourcePlanWindowDto> windows,
        IReadOnlyList<int> sourceIndexes)
    {
        var result = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var system in systems)
        {
            var frequencies = (mode == "control" ? system.ControlChannelsHz : system.ControlChannelsHz.Concat(system.VoiceFrequenciesHz))
                .Where(value => value > 0).Distinct().ToList();
            var windowIndex = Enumerable.Range(0, windows.Count).FirstOrDefault(index => frequencies.Count > 0 && frequencies.All(frequency => frequency >= windows[index].LowHz && frequency <= windows[index].HighHz), -1);
            if (windowIndex < 0)
                windowIndex = Enumerable.Range(0, windows.Count).FirstOrDefault(index => frequencies.Any(frequency => frequency >= windows[index].LowHz && frequency <= windows[index].HighHz), -1);
            if (windowIndex >= 0 && windowIndex < sourceIndexes.Count)
                result[system.ShortName] = sourceIndexes[windowIndex];
        }
        return result;
    }

    private static List<SiteSetupSourcePlanWindowDto> BuildWindows(IReadOnlyList<long> frequencies, int sampleRateHz, IReadOnlyList<long> priorities)
    {
        var span = Math.Max(1L, (long)Math.Floor(Math.Max(0, sampleRateHz) * UsableHalfSpanFactor) * 2L);
        var halfSpan = Math.Max(1L, span / 2L);
        var rows = new List<SiteSetupSourcePlanWindowDto>();
        var index = 0;
        while (index < frequencies.Count)
        {
            var start = frequencies[index];
            var endIndex = index;
            while (endIndex + 1 < frequencies.Count && frequencies[endIndex + 1] - start <= span) endIndex++;
            var end = frequencies[endIndex];
            var midpoint = (start + end) / 2L;
            var minCenter = end - halfSpan;
            var maxCenter = start + halfSpan;
            var priority = priorities.Where(value => value >= start && value <= end).OrderBy(value => Math.Abs(value - midpoint)).FirstOrDefault();
            var center = Math.Min(maxCenter, Math.Max(minCenter, priority > 0 ? priority : midpoint));
            rows.Add(new SiteSetupSourcePlanWindowDto(center - halfSpan, center, center + halfSpan, endIndex - index + 1));
            index = endIndex + 1;
        }
        return rows;
    }

    private static IReadOnlyList<IReadOnlyList<string>> BuildCombinations(IReadOnlyList<RfSurveySystemDto> systems)
    {
        var rows = new List<IReadOnlyList<string>>();
        if (systems.Count > 0) rows.Add(systems.Select(system => system.ShortName).ToList());
        rows.AddRange(systems.Select(system => (IReadOnlyList<string>)[system.ShortName]));
        for (var left = 0; left < systems.Count; left++)
            for (var right = left + 1; right < systems.Count; right++)
                rows.Add([systems[left].ShortName, systems[right].ShortName]);
        return rows.DistinctBy(row => string.Join("|", row.Order(StringComparer.OrdinalIgnoreCase)), StringComparer.OrdinalIgnoreCase).ToList();
    }
}
