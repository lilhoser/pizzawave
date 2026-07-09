using System.Diagnostics;
using System.Text.RegularExpressions;
using System.Text.Json;

namespace pizzad;

public sealed class SetupJobService
{
    private readonly EngineDatabase _database;
    private readonly EventStream _events;
    private readonly ILogger<SetupJobService> _logger;
    private readonly IServiceProvider _services;
    private sealed record CalibrationSweepTarget(int SourceIndex, string Serial, string TemplateSerial, int BaseErrorHz, int RangeHz, int StepHz, int WarmupSec, int DurationSec, string Gain, int PassCount);

    public SetupJobService(EngineDatabase database, EventStream events, ILogger<SetupJobService> logger, IServiceProvider services)
    {
        _database = database;
        _events = events;
        _logger = logger;
        _services = services;
    }

    public SetupArtifactReportDto CheckTrArtifacts()
    {
        var artifacts = new[]
        {
            Artifact("/etc/systemd/system/trunk-recorder.service", "Existing systemd unit."),
            Artifact("/lib/systemd/system/trunk-recorder.service", "Existing packaged systemd unit."),
            Artifact("/usr/local/bin/trunk-recorder", "Existing trunk-recorder binary."),
            Artifact("/usr/bin/trunk-recorder", "Existing trunk-recorder binary."),
            Artifact("/opt/trunk-recorder", "Existing source/build directory."),
            Artifact("/etc/trunk-recorder/config.json", "Existing TR config. Keep it unless you intentionally replace it."),
            Artifact("/etc/trunk-recorder/talkgroups.csv", "Existing talkgroups CSV. Keep it unless you intentionally replace it.")
        };
        var blocking = artifacts.Any(a => a.Exists && !a.Path.StartsWith("/etc/trunk-recorder/", StringComparison.OrdinalIgnoreCase));
        var commands = new List<string>();
        if (artifacts.Any(a => a.Exists))
        {
            commands.Add("# Existing TR artifacts only block a clean source-build reinstall.");
            commands.Add("# If you are side-loading an existing working TR setup, keep these files.");
            commands.Add("sudo systemctl stop trunk-recorder.service");
            commands.Add("sudo systemctl disable trunk-recorder.service");
            commands.Add("# Remove or move only the artifacts you intentionally want to replace.");
        }
        return new SetupArtifactReportDto(blocking, artifacts, commands);
    }

    public async Task<SetupSdrDetectionDto> DetectSdrsAsync(CancellationToken ct)
    {
        var output = new List<string>();
        var trActive = await RunCaptureAsync("systemctl", "is-active trunk-recorder.service", ct);
        var stoppedTr = trActive.ExitCode == 0 && trActive.Stdout.Contains("active", StringComparison.OrdinalIgnoreCase);
        if (stoppedTr)
        {
            output.Add("trunk-recorder.service was active. Stopping it temporarily so rtl_test can open the SDR devices.");
            var stop = await RunAdminHelperCaptureAsync("stop-tr", ct);
            output.Add(stop.Stdout.Trim());
            await Task.Delay(1000, ct);
        }

        (int ExitCode, string Stdout) rtl;
        (int ExitCode, string Stdout) airspy;
        (int ExitCode, string Stdout) usb;
        try
        {
            rtl = await RunAdminHelperCaptureAsync("detect-sdrs", ct);
            if (rtl.ExitCode != 0 || rtl.Stdout.Contains("Unknown action", StringComparison.OrdinalIgnoreCase))
                rtl = await RunCaptureAsync("bash", "-lc \"command -v rtl_test >/dev/null 2>&1 && timeout 8 rtl_test -t 2>&1 || true\"", ct);
            airspy = await RunCaptureAsync("bash", "-lc \"command -v airspy_info >/dev/null 2>&1 && timeout 8 airspy_info 2>&1 || true\"", ct);
            usb = await RunCaptureAsync("bash", "-lc \"command -v lsusb >/dev/null 2>&1 && lsusb 2>&1 || true\"", ct);
        }
        finally
        {
            if (stoppedTr)
            {
                var start = await RunAdminHelperCaptureAsync("start-tr", CancellationToken.None);
                output.Add("Restored trunk-recorder.service after SDR detection.");
                output.Add(start.Stdout.Trim());
            }
        }
        output.Add("$ rtl_test -t");
        output.Add(rtl.Stdout.Trim());
        output.Add("$ airspy_info");
        output.Add(airspy.Stdout.Trim());
        output.Add("$ lsusb");
        output.Add(usb.Stdout.Trim());

        var devices = new List<SetupSdrDeviceDto>();
        devices.AddRange(ParseRtlDevices(rtl.Stdout));
        devices.AddRange(ParseAirspyDevices(airspy.Stdout, devices.Count));

        if (devices.Count == 0)
        {
            var index = 0;
            foreach (var line in usb.Stdout.Split('\n').Select(l => l.Trim()).Where(l => l.Contains("RTL", StringComparison.OrdinalIgnoreCase) || l.Contains("Realtek", StringComparison.OrdinalIgnoreCase)))
            {
                devices.Add(RtlDevice(index++, string.Empty, "Possible RTL-SDR USB device", line, "Serial unavailable from lsusb; run SDR priming and set/list serials before assigning."));
            }
            foreach (var line in usb.Stdout.Split('\n').Select(l => l.Trim()).Where(l => l.Contains("Airspy", StringComparison.OrdinalIgnoreCase)))
            {
                devices.Add(AirspyDevice(index++, string.Empty, "Possible Airspy USB device", line, "Serial unavailable from lsusb; run airspy_info after the device is connected and accessible."));
            }
        }

        var message = devices.Count == 0
            ? "No RTL-SDR or Airspy devices were detected. Install SDR tools and verify USB passthrough/hardware."
            : $"Detected {devices.Count} possible SDR device(s)." + (stoppedTr ? " TR was stopped for detection and restarted afterward." : "");
        return new SetupSdrDetectionDto(devices, string.Join('\n', output.Where(s => !string.IsNullOrWhiteSpace(s))), message);
    }

    public async Task<JobDto> StartAsync(string action, bool confirmed, JsonElement? parameters, CancellationToken ct)
    {
        action = (action ?? string.Empty).Trim().ToLowerInvariant();
        var (type, message, total) = action switch
        {
            "backup-existing-tr" => ("setup_backup_existing_tr", "Backing up existing trunk-recorder config and service files.", 1),
            "prepare-existing-tr" => ("setup_prepare_existing_tr", "Backing up TR and removing retired app/tr-health artifacts.", 2),
            "remove-legacy-apps" => ("setup_remove_legacy_apps", "Removing retired app/tr-health artifacts while preserving trunk-recorder.", 1),
            "restart-pizzad" => ("setup_restart_pizzad", "Restarting pizzad and verifying service health.", 2),
            "restart-tr" => ("system_restart_tr", "Restarting trunk-recorder service.", 1),
            "stop-tr" => ("system_stop_tr", "Stopping trunk-recorder service.", 1),
            "restart-qdrant" => ("system_restart_qdrant", "Restarting Qdrant vector database service.", 1),
            "lmstudio-prime" => ("setup_lmstudio_prime", "Installing/checking LM Studio CLI and LM Link service support.", 3),
            "qdrant-prime" => ("setup_qdrant_prime", "Installing/checking native Qdrant vector database support.", 3),
            "faster-whisper-prime" => ("setup_faster_whisper_prime", "Installing/checking optional faster-whisper transcription support.", 3),
            "sdr-prime" => ("setup_sdr_prime", "Installing/checking RTL-SDR, Airspy, and GQRX tooling.", 5),
            "diagnostic-tools-prime" => ("setup_diagnostic_tools_prime", "Installing/checking optional RF diagnostic tools.", 5),
            "tr-stop-for-calibration" => ("setup_tr_stop_for_calibration", "Briefly checking trunk-recorder service control for calibration safety.", 1),
            "tr-calibration-cancel" => ("setup_tr_calibration_cancel", "Stopping active TR calibration processes.", 1),
            "tr-calibration-prime" => ("setup_tr_calibration_prime", "Preparing TR calibration checks.", 3),
            "tr-calibration-sweep" => ("setup_tr_calibration_sweep", "Running guided TR calibration sweep.", 1),
            "tr-artifact-check" => ("setup_tr_artifact_check", "Checking existing TR install artifacts.", 1),
            "tr-source-build" => ("setup_tr_source_build", "Building/installing trunk-recorder from source.", 6),
            _ => throw new InvalidOperationException("Unknown setup job action.")
        };

        if (await _database.HasActiveJobAsync(type, ct))
            throw new InvalidOperationException("A setup job of this type is already running.");

        if (action == "tr-source-build")
        {
            var report = CheckTrArtifacts();
            if (report.HasBlockingArtifacts && !confirmed)
                throw new InvalidOperationException("Existing TR install artifacts were found. Review the artifact list and confirm before starting the source build.");
        }

        var job = new JobDto
        {
            Type = type,
            Status = "queued",
            Total = total,
            Message = message,
            CreatedAtUtc = DateTime.UtcNow
        };
        var jobId = await _database.AddJobAsync(job, ct);
        await _database.AddJobLogAsync(jobId, "info", message, ct);
        _ = Task.Run(() => RunAsync(jobId, action, parameters, CancellationToken.None));
        await _events.PublishAsync("job_updated", new { jobId, type, status = "queued" }, ct);
        return job with { Id = jobId };
    }

    private async Task RunAsync(long jobId, string action, JsonElement? parameters, CancellationToken ct)
    {
        try
        {
            await _database.UpdateJobAsync(jobId, "running", null, 0, 0, "Setup job running.", true, false, ct);
            await _events.PublishAsync("job_updated", new { jobId, status = "running" }, ct);
            switch (action)
            {
                case "backup-existing-tr":
                    await RunBackupExistingTrAsync(jobId, ct);
                    break;
                case "prepare-existing-tr":
                    await RunPrepareExistingTrAsync(jobId, ct);
                    break;
                case "remove-legacy-apps":
                    await RunRemoveLegacyAppsAsync(jobId, ct);
                    break;
                case "restart-pizzad":
                    await RunRestartPizzadAsync(jobId, ct);
                    break;
                case "restart-tr":
                    await RunRestartTrAsync(jobId, ct);
                    break;
                case "stop-tr":
                    await RunStopTrAsync(jobId, ct);
                    break;
                case "restart-qdrant":
                    await RunRestartQdrantAsync(jobId, ct);
                    break;
                case "lmstudio-prime":
                    await RunLmStudioPrimeAsync(jobId, ct);
                    break;
                case "qdrant-prime":
                    await RunQdrantPrimeAsync(jobId, ct);
                    break;
                case "faster-whisper-prime":
                    await RunFasterWhisperPrimeAsync(jobId, ct);
                    break;
                case "sdr-prime":
                    await RunSdrPrimeAsync(jobId, ct);
                    break;
                case "diagnostic-tools-prime":
                    await RunDiagnosticToolsPrimeAsync(jobId, ct);
                    break;
                case "tr-stop-for-calibration":
                    await RunStopTrForCalibrationAsync(jobId, ct);
                    break;
                case "tr-calibration-cancel":
                    await RunCancelCalibrationAsync(jobId, ct);
                    break;
                case "tr-calibration-prime":
                    await RunCalibrationPrimeAsync(jobId, ct);
                    break;
                case "tr-calibration-sweep":
                    await RunCalibrationSweepAsync(jobId, parameters, ct);
                    break;
                case "tr-artifact-check":
                    await RunArtifactCheckAsync(jobId, ct);
                    break;
                case "tr-source-build":
                    await RunTrSourceBuildAsync(jobId, ct);
                    break;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Setup job {JobId} failed", jobId);
            await LogAsync(jobId, "error", ex.Message, CancellationToken.None);
            await _database.UpdateJobAsync(jobId, "failed", null, null, 1, ex.Message, false, true, CancellationToken.None);
            await _events.PublishAsync("job_updated", new { jobId, status = "failed" }, CancellationToken.None);
        }
    }

    private async Task RunArtifactCheckAsync(long jobId, CancellationToken ct)
    {
        var report = CheckTrArtifacts();
        foreach (var artifact in report.Artifacts)
            await LogAsync(jobId, artifact.Exists ? "warn" : "info", $"{(artifact.Exists ? "FOUND" : "missing")}: {artifact.Path} - {artifact.Notes}", ct);
        foreach (var command in report.ManualCommands)
            await LogAsync(jobId, "info", command, ct);
        await _database.UpdateJobAsync(jobId, "completed", 1, 1, 0, report.HasBlockingArtifacts ? "Artifact check complete; manual cleanup may be needed." : "Artifact check complete; no blocking TR artifacts found.", false, true, ct);
        await _events.PublishAsync("job_updated", new { jobId, status = "completed" }, ct);
    }

    private async Task RunBackupExistingTrAsync(long jobId, CancellationToken ct)
    {
        await RunAdminHelperAsync(jobId, "backup-existing-tr", ct);
        await _database.UpdateJobAsync(jobId, "completed", 1, 1, 0, "Existing TR backup complete.", false, true, ct);
        await _events.PublishAsync("job_updated", new { jobId, status = "completed" }, ct);
    }

    private async Task RunPrepareExistingTrAsync(long jobId, CancellationToken ct)
    {
        await RunAdminHelperAsync(jobId, "backup-existing-tr", ct);
        await _database.UpdateJobAsync(jobId, "running", 2, 1, 0, "Existing TR backup complete. Removing retired app/tr-health artifacts.", false, false, ct);
        await RunAdminHelperAsync(jobId, "remove-legacy-apps", ct);
        await _database.UpdateJobAsync(jobId, "completed", 2, 2, 0, "Existing TR prepared for PizzaWave.", false, true, ct);
        await _events.PublishAsync("job_updated", new { jobId, status = "completed" }, ct);
    }

    private async Task RunRemoveLegacyAppsAsync(long jobId, CancellationToken ct)
    {
        await RunAdminHelperAsync(jobId, "remove-legacy-apps", ct);
        await _database.UpdateJobAsync(jobId, "completed", 1, 1, 0, "Retired app/tr-health cleanup complete.", false, true, ct);
        await _events.PublishAsync("job_updated", new { jobId, status = "completed" }, ct);
    }

    private async Task RunRestartPizzadAsync(long jobId, CancellationToken ct)
    {
        await RunAdminHelperAsync(jobId, "restart-pizzad", ct);
        await _database.UpdateJobAsync(jobId, "running", 2, 1, 0, "Restart requested. Waiting for pizzad health.", false, false, ct);
        await LogAsync(jobId, "info", "Restart was scheduled out-of-process so pizzad can stop cleanly.", ct);
        await _database.UpdateJobAsync(jobId, "completed", 2, 2, 0, "pizzad restart scheduled. The page will reconnect after the service comes back.", false, true, ct);
        await _events.PublishAsync("job_updated", new { jobId, status = "completed" }, ct);
    }

    private async Task RunRestartTrAsync(long jobId, CancellationToken ct)
    {
        await RunAdminHelperAsync(jobId, "restart-tr", ct);
        await _database.UpdateJobAsync(jobId, "completed", 1, 1, 0, "trunk-recorder restart requested.", false, true, ct);
        await _events.PublishAsync("job_updated", new { jobId, status = "completed" }, ct);
    }

    private async Task RunStopTrAsync(long jobId, CancellationToken ct)
    {
        await RunAdminHelperAsync(jobId, "stop-tr", ct);
        await _database.UpdateJobAsync(jobId, "completed", 1, 1, 0, "trunk-recorder stop requested.", false, true, ct);
        await _events.PublishAsync("job_updated", new { jobId, status = "completed" }, ct);
    }

    private async Task RunRestartQdrantAsync(long jobId, CancellationToken ct)
    {
        await RunAdminHelperAsync(jobId, "restart-qdrant", ct);
        await _database.UpdateJobAsync(jobId, "completed", 1, 1, 0, "Qdrant restart requested.", false, true, ct);
        await _events.PublishAsync("job_updated", new { jobId, status = "completed" }, ct);
    }

    private async Task RunCalibrationPrimeAsync(long jobId, CancellationToken ct)
    {
        var completed = 0;
        var script = FindTrTuneScript();
        await LogAsync(jobId, "info", script == null ? "tr_tune.sh was not found. Copy scripts/tr_tune.sh to /opt/pizzawave/scripts before running sweeps." : $"Found tuning helper: {script}", ct);
        await _database.UpdateJobAsync(jobId, "running", 3, ++completed, 0, "Checked tr_tune helper.", false, false, ct);
        var trActive = await RunCaptureAsync("systemctl", "is-active trunk-recorder.service", ct);
        if (trActive.ExitCode == 0 && trActive.Stdout.Contains("active", StringComparison.OrdinalIgnoreCase))
        {
            await LogAsync(jobId, "warn", "trunk-recorder.service is active, so SDRs are probably claimed by TR. Skipping rtl_test smoke check; stop TR first before live calibration sweeps.", ct);
            await _database.UpdateJobAsync(jobId, "running", 3, ++completed, 0, "Skipped SDR smoke check because TR is active.", false, false, ct);
        }
        else
        {
            await RunCommandAsync(jobId, "bash", "-lc \"command -v rtl_test || true; timeout 5 rtl_test -t || true\"", ct);
            await _database.UpdateJobAsync(jobId, "running", 3, ++completed, 0, "Ran a short RTL-SDR smoke check.", false, false, ct);
        }
        await LogAsync(jobId, "info", "Calibration preflight only. This job does not run the long sweep or modify trunk-recorder config.", ct);
        await LogAsync(jobId, "info", "Use GQRX first to find a stable starting gain and either a PPM correction or observed error in Hz for each SDR/source.", ct);
        await LogAsync(jobId, "info", "Setup turns those defaults into tr_tune.sh commands. With defaults, error-sweep tests 9 candidates: base-error +/- 1200 Hz in 300 Hz steps.", ct);
        await LogAsync(jobId, "info", "Default runtime is about 39 minutes per system/source: 9 passes x (20s warmup + 240s measurement). Short test runs can use --duration-sec 20 --warmup-sec 5.", ct);
        await LogAsync(jobId, "info", "During a full sweep, tr_tune.sh restarts TR per candidate and scores decode samples, zero-decode share, average/max decode rate, retunes, no-transmission, update-not-grant, started calls, and concluded calls.", ct);
        await LogAsync(jobId, "info", "A decode rate above 2 msg/sec is acceptable but marginal; higher and stable is better. Prefer stable decode and fewer retunes over a brief noisy peak.", ct);
        await _database.UpdateJobAsync(jobId, "completed", 3, ++completed, 0, "Calibration prep complete. Review the generated plan in Setup.", false, true, ct);
        await _events.PublishAsync("job_updated", new { jobId, status = "completed" }, ct);
    }

    private async Task RunStopTrForCalibrationAsync(long jobId, CancellationToken ct)
    {
        await LogAsync(jobId, "warn", "Leaving trunk-recorder stopped from the web UI is no longer supported. Setup RF validation jobs now pause and restart TR inside each bounded measurement.", ct);
        await RunAdminHelperAsync(jobId, "stop-tr", ct);
        await RunAdminHelperAsync(jobId, "start-tr", CancellationToken.None);
        await LogAsync(jobId, "info", "trunk-recorder was briefly paused and restarted to verify service control. Use Setup RF validation buttons for bounded SDR measurements.", ct);
        await _database.UpdateJobAsync(jobId, "completed", 1, 1, 0, "TR service-control check completed; trunk-recorder was restarted.", false, true, ct);
        await _events.PublishAsync("job_updated", new { jobId, status = "completed" }, ct);
    }

    private async Task RunCancelCalibrationAsync(long jobId, CancellationToken ct)
    {
        await RunAdminHelperAsync(jobId, "stop-calibration", ct);
        await LogAsync(jobId, "info", "Calibration stop requested. Any active tr_tune.sh process should be terminated.", ct);
        await RunAdminHelperAsync(jobId, "start-tr", CancellationToken.None);
        await LogAsync(jobId, "info", "trunk-recorder restart requested after calibration cancellation.", CancellationToken.None);
        await _database.UpdateJobAsync(jobId, "completed", 1, 1, 0, "Calibration stop requested.", false, true, ct);
        await _events.PublishAsync("job_updated", new { jobId, status = "completed" }, ct);
    }

    private async Task RunCalibrationSweepAsync(long jobId, JsonElement? parameters, CancellationToken ct)
    {
        var script = FindTrTuneScript();
        if (string.IsNullOrWhiteSpace(script))
            throw new FileNotFoundException("tr_tune.sh was not found in /opt/pizzawave/scripts or the application directory.");
        if (parameters == null)
            throw new InvalidOperationException("Calibration sweep parameters were not supplied.");

        var root = parameters.Value;
        var system = RequiredString(root, "systemShortName");
        var control = RequiredLong(root, "controlChannelHz");
        var modulation = OptionalString(root, "modulation");
        if (string.IsNullOrWhiteSpace(modulation))
            modulation = "qpsk";
        var targets = ReadCalibrationSweepTargets(root);
        var totalPassCount = targets.Sum(target => target.PassCount);
        var totalEstimatedSeconds = targets.Sum(target => target.PassCount * (target.WarmupSec + target.DurationSec));
        var completedPasses = 0;

        await LogAsync(jobId, "info", $"Running calibration sweep batch for {system} against {targets.Count} source(s).", ct);
        await LogAsync(jobId, "info", $"This batch has {totalPassCount} pass(es) and is expected to take about {FormatElapsed(totalEstimatedSeconds)}.", ct);
        await _database.UpdateJobAsync(jobId, "running", totalPassCount, 0, 0, "Calibration sweep batch running.", false, false, ct);

        try
        {
            for (var i = 0; i < targets.Count; i++)
            {
                var target = targets[i];
                await LogAsync(jobId, "info", $"Starting source {target.SourceIndex} / rtl={target.Serial} ({i + 1} of {targets.Count}).", ct);
                if (!string.IsNullOrWhiteSpace(target.TemplateSerial))
                    await LogAsync(jobId, "info", $"Borrowing the center/rate from rtl={target.TemplateSerial} so rtl={target.Serial} can be swept against the control channel.", ct);
                await _database.UpdateJobAsync(jobId, "running", totalPassCount, completedPasses, 0, $"Calibration sweep running for source {target.SourceIndex}.", false, false, ct);

                var args = new List<string>
                {
                    script,
                    "error-sweep",
                    "--system", system,
                    "--control-channel", control.ToString(),
                    "--device-serial", target.Serial,
                    "--base-error", target.BaseErrorHz.ToString(),
                    "--range-hz", target.RangeHz.ToString(),
                    "--step-hz", target.StepHz.ToString(),
                    "--modulation", modulation,
                    "--warmup-sec", target.WarmupSec.ToString(),
                    "--duration-sec", target.DurationSec.ToString()
                };
                if (!string.IsNullOrWhiteSpace(target.TemplateSerial))
                {
                    args.Add("--template-serial");
                    args.Add(target.TemplateSerial);
                }
                if (!string.IsNullOrWhiteSpace(target.Gain))
                {
                    args.Add("--gain");
                    args.Add(target.Gain);
                }
                var outputDir = Path.Combine("/var/lib/pizzawave/appdata/rf-survey-sweeps", $"job-{jobId}", $"source-{target.SourceIndex}");
                Directory.CreateDirectory(outputDir);
                args.Add("--output-dir");
                args.Add(outputDir);

                await RunCommandArgsAsync(jobId, "sudo", args, ct);
                await LogSweepResultAsync(jobId, target, outputDir, ct);
                completedPasses += target.PassCount;
                await _database.UpdateJobAsync(jobId, "running", totalPassCount, completedPasses, 0, $"Completed sweep for source {target.SourceIndex}.", false, false, ct);
            }
        }
        finally
        {
            await RunAdminHelperAsync(jobId, "start-tr", CancellationToken.None);
            await LogAsync(jobId, "info", "trunk-recorder restart requested after calibration sweep cleanup.", CancellationToken.None);
        }

        await _database.UpdateJobAsync(jobId, "completed", totalPassCount, totalPassCount, 0, "Calibration sweep batch completed. Review tr_tune output and apply findings only after confirming the best candidate per source.", false, true, ct);
        await _events.PublishAsync("job_updated", new { jobId, status = "completed" }, ct);
    }

    private static List<CalibrationSweepTarget> ReadCalibrationSweepTargets(JsonElement root)
    {
        if (root.TryGetProperty("sources", out var sources) && sources.ValueKind == JsonValueKind.Array)
        {
            var targets = sources.EnumerateArray().Select(ReadCalibrationSweepTarget).ToList();
            if (targets.Count > 0)
                return targets;
        }

        return [ReadCalibrationSweepTarget(root)];
    }

    private static CalibrationSweepTarget ReadCalibrationSweepTarget(JsonElement root)
    {
        var sourceIndex = OptionalInt(root, "sourceIndex", 0);
        var serial = OptionalString(root, "serial");
        if (string.IsNullOrWhiteSpace(serial))
            serial = sourceIndex.ToString();
        var range = Math.Max(0, OptionalInt(root, "rangeHz", 1200));
        var step = Math.Max(1, OptionalInt(root, "stepHz", 300));
        return new CalibrationSweepTarget(
            sourceIndex,
            serial,
            OptionalString(root, "templateSerial"),
            OptionalInt(root, "baseErrorHz", 0),
            range,
            step,
            Math.Max(0, OptionalInt(root, "warmupSec", 20)),
            Math.Max(1, OptionalInt(root, "durationSec", 240)),
            OptionalString(root, "gain"),
            (int)Math.Floor((range * 2.0) / step) + 1);
    }

    private async Task LogSweepResultAsync(long jobId, CalibrationSweepTarget target, string outputDir, CancellationToken ct)
    {
        var summaryPath = Path.Combine(outputDir, "summary.csv");
        var bestPath = Path.Combine(outputDir, "best.txt");
        var rows = File.Exists(summaryPath)
            ? (await File.ReadAllLinesAsync(summaryPath, ct)).Skip(1).Select(ParseSweepCandidate).Where(row => row != null).Cast<object>().ToList()
            : [];
        var bestRowText = File.Exists(bestPath) ? (await File.ReadAllTextAsync(bestPath, ct)).Trim() : string.Empty;
        var best = string.IsNullOrWhiteSpace(bestRowText) || bestRowText == "NO_ROWS" ? null : ParseSweepCandidate(bestRowText);
        var payload = new
        {
            kind = "tr_tune_error_sweep",
            sourceIndex = target.SourceIndex,
            serial = target.Serial,
            gain = target.Gain,
            outputDir,
            summaryPath,
            bestPath,
            best,
            candidates = rows
        };
        await LogAsync(jobId, "result", "SWEEP_RESULT_JSON " + JsonSerializer.Serialize(payload, EngineConfig.JsonOptions()), ct);
    }

    private static object? ParseSweepCandidate(string line)
    {
        var parts = line.Split(',');
        if (parts.Length < 18)
            return null;
        var error = ParseInt(parts[4]);
        var totalDecode = ParseInt(parts[7]);
        var hasDecodeSamples = totalDecode > 0;
        var decode0Pct = hasDecodeSamples ? ParseDouble(parts[10]) : 0;
        var avgDecode = hasDecodeSamples ? ParseDouble(parts[11]) : 0;
        var maxDecode = hasDecodeSamples ? ParseDouble(parts[12]) : 0;
        var callsConcluded = ParseInt(parts[15]);
        var score = hasDecodeSamples
            ? (1000.0 - decode0Pct) * 1_000_000 + avgDecode * 1000 + callsConcluded
            : -1_000_000 + callsConcluded;
        return new
        {
            system = parts[0],
            controlChannelHz = ParseLong(parts[1]),
            serial = parts[2],
            modulation = parts[3],
            errorHz = error,
            start = parts[5],
            end = parts[6],
            totalDecode,
            decode0 = ParseInt(parts[8]),
            decodeNonzero = ParseInt(parts[9]),
            hasDecodeSamples,
            decode0Pct,
            avgDecodeRate = avgDecode,
            maxDecodeRate = maxDecode,
            retunes = ParseInt(parts[13]),
            callsStarted = ParseInt(parts[14]),
            callsConcluded,
            updateNotGrant = ParseInt(parts[16]),
            noTxRecorded = ParseInt(parts[17]),
            metricWarning = hasDecodeSamples ? "" : "No parser-visible CC message-rate samples were found in this measurement window; call counts are informational, but error ranking is advisory until a rerun captures CC message-rate samples.",
            score
        };
    }

    private static int ParseInt(string value) => int.TryParse(value, out var parsed) ? parsed : 0;
    private static long ParseLong(string value) => long.TryParse(value, out var parsed) ? parsed : 0;
    private static double ParseDouble(string value) => double.TryParse(value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var parsed) ? parsed : 0;

    private async Task RunLmStudioPrimeAsync(long jobId, CancellationToken ct)
    {
        var script = FindLmStudioScript();
        if (string.IsNullOrWhiteSpace(script))
            throw new FileNotFoundException("setup-lmstudio.sh was not found in /usr/lib/pizzawave/scripts or the application directory.");
        await LogAsync(jobId, "info", "Installing/checking LM Studio CLI and llmster support. No local chat model download will be attempted; local embedding autoload is conditional on the embeddings settings.", ct);
        await RunCommandAsync(jobId, "sudo", $"{script} --skip-model-load", ct);
        await _database.UpdateJobAsync(jobId, "completed", 3, 3, 0, "LM Link support is installed. Run `lms login` as the target user if linking is not complete. Local embedding autoload runs only when embeddings are enabled and configured for local LM Studio.", false, true, ct);
        await _events.PublishAsync("job_updated", new { jobId, status = "completed" }, ct);
    }

    private async Task RunQdrantPrimeAsync(long jobId, CancellationToken ct)
    {
        await LogAsync(jobId, "info", "Installing/checking native Qdrant under systemd. Docker is not required.", ct);
        await RunAdminHelperAsync(jobId, "install-qdrant", ct);
        await _database.UpdateJobAsync(jobId, "completed", 3, 3, 0, "Native Qdrant is installed and running. Enable embeddings and test the Embeddings / Qdrant settings.", false, true, ct);
        await _events.PublishAsync("job_updated", new { jobId, status = "completed" }, ct);
    }

    private async Task RunFasterWhisperPrimeAsync(long jobId, CancellationToken ct)
    {
        var script = FindFasterWhisperScript();
        if (string.IsNullOrWhiteSpace(script))
            throw new FileNotFoundException("setup-faster-whisper.sh was not found in /usr/lib/pizzawave/scripts or the application directory.");
        await LogAsync(jobId, "info", "Installing/checking optional faster-whisper transcription runtime in /opt/pizzawave/venv/faster-whisper.", ct);
        await RunCommandAsync(jobId, "sudo", script, ct);
        await _database.UpdateJobAsync(jobId, "completed", 3, 3, 0, "faster-whisper support is installed. Select faster-whisper in Transcription settings and restart pizzad.", false, true, ct);
        await _events.PublishAsync("job_updated", new { jobId, status = "completed" }, ct);
    }

    private async Task RunSdrPrimeAsync(long jobId, CancellationToken ct)
    {
        var completed = 0;
        await RunCommandAsync(jobId, "bash", "-lc \"command -v rtl_test || true; command -v airspy_info || true; command -v airspy_rx || true; command -v lsusb || true; command -v gqrx || true\"", ct);
        await _database.UpdateJobAsync(jobId, "running", 5, ++completed, 0, "Checked RTL-SDR, Airspy, and GQRX tool availability.", false, false, ct);
        if (!CommandExists("rtl_test") || !CommandExists("airspy_info") || !CommandExists("airspy_rx") || !CommandExists("lsusb") || !CommandExists("gqrx"))
        {
            await LogAsync(jobId, "info", "Installing rtl-sdr, Airspy tools, usbutils, and the available GQRX package with the setup helper.", ct);
            await RunAdminHelperAsync(jobId, "install-sdr-tools", ct);
        }
        await _database.UpdateJobAsync(jobId, "running", 5, ++completed, 0, "SDR dependencies checked.", false, false, ct);
        await RunCommandAsync(jobId, "bash", "-lc \"for g in plugdev dialout; do if getent group $g >/dev/null 2>&1; then usermod -aG $g pizzawave || true; fi; done\"", ct);
        await LogAsync(jobId, "info", "Ensured pizzawave belongs to SDR access groups when they exist. Restart pizzad after group changes.", ct);
        await RunCommandAsync(jobId, "bash", "-lc \"lsusb || true\"", ct);
        await _database.UpdateJobAsync(jobId, "running", 5, ++completed, 0, "USB devices listed.", false, false, ct);
        await RunCommandAsync(jobId, "bash", "-lc \"timeout 8 rtl_test -t || true; timeout 8 airspy_info || true\"", ct);
        await _database.UpdateJobAsync(jobId, "running", 5, ++completed, 0, "SDR inventory smoke check complete.", false, false, ct);
        await RunCommandAsync(jobId, "bash", "-lc \"gqrx --version || true\"", ct);
        await _database.UpdateJobAsync(jobId, "completed", 5, ++completed, 0, "SDR priming complete. GQRX is installed for manual tuning.", false, true, ct);
        await _events.PublishAsync("job_updated", new { jobId, status = "completed" }, ct);
    }

    private async Task RunDiagnosticToolsPrimeAsync(long jobId, CancellationToken ct)
    {
        var completed = 0;
        await RunCommandAsync(jobId, "bash", "-lc \"command -v rtl_test || true; command -v airspy_info || true; command -v rx.py || true; command -v multi_rx.py || true; command -v op25_rx.py || true\"", ct);
        await _database.UpdateJobAsync(jobId, "running", 5, ++completed, 0, "Checked current RF diagnostic tools.", false, false, ct);
        await LogAsync(jobId, "info", "Installing optional diagnostic tools: RTL-SDR utilities, Airspy utilities, ffmpeg/ffprobe, and OP25/P25 control-channel tooling.", ct);
        await RunAdminHelperAsync(jobId, "install-diagnostic-tools", ct);
        await _database.UpdateJobAsync(jobId, "running", 5, ++completed, 0, "Diagnostic tool installer completed.", false, false, ct);
        await RunCommandAsync(jobId, "bash", "-lc \"command -v rtl_test; command -v ffprobe; command -v python3; test -x /opt/pizzawave/diagnostics/op25/op25/gr-op25_repeater/apps/rx.py\"", ct);
        await _database.UpdateJobAsync(jobId, "running", 5, ++completed, 0, "Verified required diagnostic binaries.", false, false, ct);
        await RunCommandAsync(jobId, "bash", "-lc \"timeout 8 rtl_test -t || true\"", ct);
        await _database.UpdateJobAsync(jobId, "running", 5, ++completed, 0, "RTL-SDR diagnostic smoke check complete.", false, false, ct);
        await RunCommandAsync(jobId, "bash", "-lc \"cd /opt/pizzawave/diagnostics/op25/op25/gr-op25_repeater/apps && ./rx.py --help >/tmp/pizzawave-op25-rx-help.txt 2>&1 || true; head -40 /tmp/pizzawave-op25-rx-help.txt\"", ct);
        await _database.UpdateJobAsync(jobId, "completed", 5, ++completed, 0, "Optional RF diagnostic tools are installed. Configure Setup RF validation with an OP25 command template before control-channel probing.", false, true, ct);
        await _events.PublishAsync("job_updated", new { jobId, status = "completed" }, ct);
    }

    private async Task RunTrSourceBuildAsync(long jobId, CancellationToken ct)
    {
        var script = FindSetupTrScript();
        if (string.IsNullOrWhiteSpace(script))
            throw new FileNotFoundException("setup_trunk_recorder.sh was not found in /opt/pizzawave/scripts or the application directory.");
        await LogAsync(jobId, "info", "Backing up existing trunk-recorder files before source-build.", ct);
        await RunAdminHelperAsync(jobId, "backup-existing-tr", ct);
        await LogAsync(jobId, "info", $"Running {script}", ct);
        await RunCommandAsync(jobId, "bash", $"-lc \"perl -pi -e 's/\\r$//' '{script}' && chmod +x '{script}' && '{script}'\"", ct);
        await _database.UpdateJobAsync(jobId, "completed", 6, 6, 0, "TR source-build helper completed.", false, true, ct);
        await _events.PublishAsync("job_updated", new { jobId, status = "completed" }, ct);
    }

    private async Task RunCommandAsync(long jobId, string fileName, string arguments, CancellationToken ct)
    {
        await LogAsync(jobId, "cmd", $"{fileName} {arguments}", ct);
        var psi = new ProcessStartInfo(fileName, arguments)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        using var process = Process.Start(psi) ?? throw new InvalidOperationException($"Unable to start {fileName}.");
        var stdout = ReadLinesAsync(jobId, "out", process.StandardOutput, ct);
        var stderr = ReadLinesAsync(jobId, "err", process.StandardError, ct);
        await process.WaitForExitAsync(ct);
        await Task.WhenAll(stdout, stderr);
        if (process.ExitCode != 0)
            throw new InvalidOperationException($"{fileName} exited with code {process.ExitCode}.");
    }

    private async Task RunCommandArgsAsync(long jobId, string fileName, IReadOnlyList<string> arguments, CancellationToken ct)
    {
        await LogAsync(jobId, "cmd", fileName + " " + string.Join(" ", arguments.Select(QuoteArg)), ct);
        var psi = new ProcessStartInfo(fileName)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        foreach (var argument in arguments)
            psi.ArgumentList.Add(argument);
        using var process = Process.Start(psi) ?? throw new InvalidOperationException($"Unable to start {fileName}.");
        var stdout = ReadLinesAsync(jobId, "out", process.StandardOutput, ct);
        var stderr = ReadLinesAsync(jobId, "err", process.StandardError, ct);
        await process.WaitForExitAsync(ct);
        await Task.WhenAll(stdout, stderr);
        if (process.ExitCode != 0)
            throw new InvalidOperationException($"{fileName} exited with code {process.ExitCode}.");
    }

    private async Task RunAdminHelperAsync(long jobId, string action, CancellationToken ct)
    {
        var helper = FindAdminHelper();
        if (string.IsNullOrWhiteSpace(helper))
            throw new FileNotFoundException("pizzawave_setup_admin.sh was not found.");
        if (string.Equals(action, "start-tr", StringComparison.OrdinalIgnoreCase) || string.Equals(action, "restart-tr", StringComparison.OrdinalIgnoreCase))
        {
            if (_services.GetService(typeof(RfSurveyService)) is RfSurveyService surveys)
            {
                var output = await surveys.StopActiveWaterfallsBeforeTrStartAsync(ct);
                if (!string.IsNullOrWhiteSpace(output))
                    await LogAsync(jobId, "info", output, ct);
            }
        }
        await RunCommandAsync(jobId, "sudo", $"{helper} {action}", ct);
    }

    private static async Task<(int ExitCode, string Stdout)> RunAdminHelperCaptureAsync(string action, CancellationToken ct)
    {
        var helper = FindAdminHelper();
        return string.IsNullOrWhiteSpace(helper)
            ? (-1, "pizzawave_setup_admin.sh was not found.")
            : await RunCaptureAsync("sudo", $"{helper} {action}", ct);
    }

    private static async Task<(int ExitCode, string Stdout)> RunCaptureAsync(string fileName, string arguments, CancellationToken ct)
    {
        try
        {
            var psi = new ProcessStartInfo(fileName, arguments)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            };
            using var process = Process.Start(psi);
            if (process == null) return (-1, "failed to start process");
            var stdoutTask = process.StandardOutput.ReadToEndAsync(ct);
            var stderrTask = process.StandardError.ReadToEndAsync(ct);
            await process.WaitForExitAsync(ct);
            return (process.ExitCode, (await stdoutTask) + (await stderrTask));
        }
        catch (Exception ex)
        {
            return (-1, ex.Message);
        }
    }

    private async Task ReadLinesAsync(long jobId, string stream, StreamReader reader, CancellationToken ct)
    {
        while (!reader.EndOfStream)
        {
            var line = await reader.ReadLineAsync(ct);
            if (line != null)
                await LogAsync(jobId, stream, line, ct);
        }
    }

    private async Task LogAsync(long jobId, string stream, string text, CancellationToken ct)
    {
        await _database.AddJobLogAsync(jobId, stream, text, ct);
        await _events.PublishAsync("job_updated", new { jobId, log = true }, ct);
    }

    private static SetupArtifactDto Artifact(string path, string notes) => new(path, File.Exists(path) || Directory.Exists(path), notes);

    private static string? FindSetupTrScript()
    {
        var candidates = new[]
        {
            "/opt/pizzawave/scripts/setup_trunk_recorder.sh",
            Path.Combine(AppContext.BaseDirectory, "scripts", "setup_trunk_recorder.sh"),
            Path.Combine(AppContext.BaseDirectory, "setup_trunk_recorder.sh")
        };
        return candidates.FirstOrDefault(File.Exists);
    }

    private static string? FindTrTuneScript()
    {
        var candidates = new[]
        {
            "/usr/lib/pizzawave/scripts/tr_tune.sh",
            "/opt/pizzawave/scripts/tr_tune.sh",
            Path.Combine(AppContext.BaseDirectory, "scripts", "tr_tune.sh"),
            Path.Combine(AppContext.BaseDirectory, "tr_tune.sh")
        };
        return candidates.FirstOrDefault(File.Exists);
    }

    private static string? FindLmStudioScript()
    {
        var candidates = new[]
        {
            "/usr/lib/pizzawave/scripts/setup-lmstudio.sh",
            "/opt/pizzawave/scripts/setup-lmstudio.sh",
            Path.Combine(AppContext.BaseDirectory, "scripts", "setup-lmstudio.sh"),
            Path.Combine(AppContext.BaseDirectory, "setup-lmstudio.sh")
        };
        return candidates.FirstOrDefault(File.Exists);
    }

    private static string? FindFasterWhisperScript()
    {
        var candidates = new[]
        {
            "/usr/lib/pizzawave/scripts/setup-faster-whisper.sh",
            "/opt/pizzawave/scripts/setup-faster-whisper.sh",
            Path.Combine(AppContext.BaseDirectory, "scripts", "setup-faster-whisper.sh"),
            Path.Combine(AppContext.BaseDirectory, "setup-faster-whisper.sh")
        };
        return candidates.FirstOrDefault(File.Exists);
    }

    private static string? FindAdminHelper()
    {
        var candidates = new[]
        {
            "/usr/lib/pizzawave/scripts/pizzawave_setup_admin.sh",
            "/opt/pizzawave/scripts/pizzawave_setup_admin.sh",
            Path.Combine(AppContext.BaseDirectory, "scripts", "pizzawave_setup_admin.sh"),
            Path.Combine(AppContext.BaseDirectory, "pizzawave_setup_admin.sh")
        };
        return candidates.FirstOrDefault(File.Exists);
    }

    private static bool CommandExists(string command)
    {
        var path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        foreach (var dir in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                if (File.Exists(Path.Combine(dir, command)))
                    return true;
            }
            catch
            {
                // Ignore malformed PATH entries.
            }
        }
        return false;
    }

    private static string CleanDeviceText(string value)
    {
        var chars = value.Where(c => c is >= ' ' and <= '~').ToArray();
        var cleaned = Regex.Replace(new string(chars), "\\s+", " ").Trim(' ', ',');
        return string.IsNullOrWhiteSpace(cleaned) ? string.Empty : cleaned;
    }

    private static bool IsPlausibleSdrSerial(string value) =>
        !string.IsNullOrWhiteSpace(value) &&
        value.Length <= 64 &&
        value.All(c => char.IsLetterOrDigit(c) || c is '_' or '-' or '.');

    private static bool IsPlausibleDeviceLabel(string value) =>
        !string.IsNullOrWhiteSpace(value) &&
        value.Count(char.IsLetterOrDigit) >= 2;

    internal static IReadOnlyList<SetupSdrDeviceDto> ParseRtlDevices(string output)
    {
        var devices = new List<SetupSdrDeviceDto>();
        foreach (var line in output.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0))
        {
            var match = Regex.Match(line, @"^(?<idx>\d+):\s*(?<label>.*?)(?:,\s*SN:\s*(?<serial>\S+))?$", RegexOptions.IgnoreCase);
            if (!match.Success || !int.TryParse(match.Groups["idx"].Value, out var index))
                continue;
            var label = CleanDeviceText(Regex.Replace(match.Groups["label"].Value, @",?\s*SN:\s*$", string.Empty, RegexOptions.IgnoreCase));
            var serial = match.Groups["serial"].Success ? CleanDeviceText(match.Groups["serial"].Value.Trim()) : string.Empty;
            if (!IsPlausibleSdrSerial(serial))
                serial = string.Empty;
            if (!IsPlausibleDeviceLabel(label))
                label = string.Empty;
            if (string.IsNullOrWhiteSpace(serial))
                label = $"RTL-SDR #{index}";
            devices.Add(RtlDevice(index, serial, string.IsNullOrWhiteSpace(label) ? $"RTL-SDR #{index}" : label, string.Empty, string.IsNullOrWhiteSpace(serial) ? "Serial was not reported; set unique serials before assigning SDRs." : string.Empty));
        }
        return devices;
    }

    internal static IReadOnlyList<SetupSdrDeviceDto> ParseAirspyDevices(string output, int startIndex = 0)
    {
        if (output.Contains("AIRSPY_ERROR_NOT_FOUND", StringComparison.OrdinalIgnoreCase) ||
            output.Contains("No Airspy", StringComparison.OrdinalIgnoreCase))
            return [];

        var serialMatches = Regex.Matches(output, @"Serial\s+Number\s*:\s*(?:0x)?(?<serial>[0-9A-Fa-f]+)", RegexOptions.IgnoreCase)
            .Cast<Match>()
            .Where(match => match.Success)
            .Select(match => CleanDeviceText(match.Groups["serial"].Value))
            .Where(IsPlausibleSdrSerial)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (serialMatches.Count == 0 && !output.Contains("AirSpy", StringComparison.OrdinalIgnoreCase) && !output.Contains("Airspy", StringComparison.OrdinalIgnoreCase))
            return [];

        var labelMatch = Regex.Match(output, @"Board\s+ID\s+Number\s*:\s*.*?\((?<label>[^)]+)\)", RegexOptions.IgnoreCase);
        var label = labelMatch.Success ? CleanDeviceText(labelMatch.Groups["label"].Value) : "Airspy";
        if (!IsPlausibleDeviceLabel(label))
            label = "Airspy";

        var sampleRates = Regex.Matches(output, @"(?<rate>\d+(?:\.\d+)?)\s*MSPS", RegexOptions.IgnoreCase)
            .Cast<Match>()
            .Select(match => double.TryParse(match.Groups["rate"].Value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var mhz) ? (int)Math.Round(mhz * 1_000_000) : 0)
            .Where(rate => rate > 0)
            .Distinct()
            .OrderBy(rate => rate)
            .ToList();
        if (sampleRates.Count == 0)
            sampleRates = [3_000_000, 6_000_000];
        var defaultRate = sampleRates.Contains(6_000_000) ? 6_000_000 : sampleRates.Min();

        if (serialMatches.Count == 0)
            return [AirspyDevice(startIndex, string.Empty, label, string.Empty, "Airspy was reported, but airspy_info did not expose a serial number.", sampleRates, defaultRate)];

        return serialMatches
            .Select((serial, offset) => AirspyDevice(startIndex + offset, serial, label, string.Empty, "Verify the Airspy osmosdr device argument on hardware before the first controlled TR start.", sampleRates, defaultRate))
            .ToList();
    }

    private static SetupSdrDeviceDto RtlDevice(int index, string serial, string label, string usbLine, string warning) =>
        new(
            index,
            "RTL-SDR",
            serial,
            label,
            "osmosdr",
            string.IsNullOrWhiteSpace(serial) ? $"rtl={index},buflen=65536" : $"rtl={serial},buflen=65536",
            usbLine,
            [2_400_000, 2_048_000],
            2_400_000,
            "rtl-tuner-gain",
            "32",
            warning);

    private static SetupSdrDeviceDto AirspyDevice(int index, string serial, string label, string usbLine, string warning, IReadOnlyList<int>? sampleRates = null, int defaultSampleRate = 6_000_000) =>
        new(
            index,
            "Airspy",
            serial,
            label.Contains("Airspy", StringComparison.OrdinalIgnoreCase) ? label : $"Airspy {label}",
            "osmosdr",
            string.IsNullOrWhiteSpace(serial) ? $"airspy={index}" : $"airspy={serial}",
            usbLine,
            sampleRates ?? [3_000_000, 6_000_000],
            defaultSampleRate,
            "airspy-linearity",
            "15",
            warning);

    private static string RequiredString(JsonElement root, string property)
    {
        var value = OptionalString(root, property);
        if (string.IsNullOrWhiteSpace(value))
            throw new InvalidOperationException($"{property} is required.");
        return value;
    }

    private static string OptionalString(JsonElement root, string property) =>
        root.TryGetProperty(property, out var value) ? value.ValueKind switch
        {
            JsonValueKind.String => value.GetString() ?? string.Empty,
            JsonValueKind.Number => value.ToString(),
            _ => string.Empty
        } : string.Empty;

    private static long RequiredLong(JsonElement root, string property)
    {
        var value = OptionalLong(root, property, 0);
        if (value <= 0)
            throw new InvalidOperationException($"{property} is required.");
        return value;
    }

    private static int OptionalInt(JsonElement root, string property, int fallback) =>
        (int)OptionalLong(root, property, fallback);

    private static long OptionalLong(JsonElement root, string property, long fallback)
    {
        if (!root.TryGetProperty(property, out var value))
            return fallback;
        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number))
            return number;
        if (value.ValueKind == JsonValueKind.String && long.TryParse(value.GetString(), out var parsed))
            return parsed;
        return fallback;
    }

    private static string QuoteArg(string value) =>
        string.IsNullOrWhiteSpace(value) || value.Any(char.IsWhiteSpace)
            ? "'" + value.Replace("'", "'\\''") + "'"
            : value;

    private static string FormatElapsed(int seconds)
    {
        if (seconds < 60) return $"{seconds}s";
        var minutes = seconds / 60;
        if (minutes < 60) return $"{minutes}m";
        return $"{minutes / 60}h {minutes % 60}m";
    }
}
