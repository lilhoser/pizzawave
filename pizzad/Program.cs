using Microsoft.AspNetCore.StaticFiles;
using pizzad;
using System.Reflection;
using System.Text.Json;

var configPath = args
    .SkipWhile(a => a != "--config")
    .Skip(1)
    .FirstOrDefault()
    ?? Environment.GetEnvironmentVariable("PIZZAD_CONFIG")
    ?? "/etc/pizzawave/pizzad.json";

var config = EngineConfig.Load(configPath);
Directory.CreateDirectory(config.Storage.AudioRoot);
Directory.CreateDirectory(config.Storage.ImportCacheRoot);
Directory.CreateDirectory(Path.GetDirectoryName(config.Storage.DatabasePath) ?? ".");

Environment.SetEnvironmentVariable("HOME", config.Storage.AppDataRoot);
Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", config.Storage.AppDataRoot);
Environment.SetEnvironmentVariable("XDG_DATA_HOME", config.Storage.AppDataRoot);

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseUrls($"http://{config.Server.HttpBind}:{config.Server.HttpPort}");
builder.Services.AddSingleton(config);
builder.Services.AddSingleton<EngineDatabase>();
builder.Services.AddSingleton<EventStream>();
builder.Services.AddSingleton<AuthService>();
builder.Services.AddSingleton<EngineAlertService>();
builder.Services.AddSingleton<TalkgroupResolver>();
builder.Services.AddHttpClient<GeocodingService>();
builder.Services.AddSingleton<AutomaticInsightsService>();
builder.Services.AddSingleton<EnginePipeline>();
builder.Services.AddSingleton<DashboardService>();
builder.Services.AddSingleton<SftpImportService>();
builder.Services.AddSingleton<LocalImportService>();
builder.Services.AddSingleton<SummaryService>();
builder.Services.AddSingleton<TrConfigService>();
builder.Services.AddSingleton<TrHealthTroubleshootService>();
builder.Services.AddSingleton<DiagnosticToolService>();
builder.Services.AddSingleton<SettingsValidationService>();
builder.Services.AddSingleton<SystemManagerService>();
builder.Services.AddSingleton<SetupService>();
builder.Services.AddSingleton<SetupJobService>();
builder.Services.AddHttpClient<SetupTalkgroupService>();
builder.Services.AddHttpClient<SetupTrConfigBuilderService>();
builder.Services.AddSingleton<SetupCalibrationService>();
builder.Services.AddHostedService<CallstreamListener>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<AutomaticInsightsService>());
builder.Services.AddHostedService<TrHealthCollector>();
builder.Services.AddHostedService<RecentReconciliationService>();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

var app = builder.Build();
var db = app.Services.GetRequiredService<EngineDatabase>();
await db.InitializeAsync(app.Lifetime.ApplicationStopping);
var auth = app.Services.GetRequiredService<AuthService>();
auth.Initialize();
await app.Services.GetRequiredService<EnginePipeline>().StartAsync(app.Lifetime.ApplicationStopping);

app.UseSwagger();
app.UseSwaggerUI();

var staticRoot = Path.Combine(AppContext.BaseDirectory, "wwwroot");
if (!Directory.Exists(staticRoot))
    staticRoot = Path.Combine(AppContext.BaseDirectory, "web");
if (Directory.Exists(staticRoot))
{
    app.UseDefaultFiles(new DefaultFilesOptions { FileProvider = new Microsoft.Extensions.FileProviders.PhysicalFileProvider(staticRoot) });
    app.UseStaticFiles(new StaticFileOptions
    {
        FileProvider = new Microsoft.Extensions.FileProviders.PhysicalFileProvider(staticRoot),
        ContentTypeProvider = new FileExtensionContentTypeProvider(),
        OnPrepareResponse = context =>
        {
            if (string.Equals(Path.GetFileName(context.File.PhysicalPath), "index.html", StringComparison.OrdinalIgnoreCase))
            {
                context.Context.Response.Headers.CacheControl = "no-store, no-cache, must-revalidate";
                context.Context.Response.Headers.Pragma = "no-cache";
                context.Context.Response.Headers.Expires = "0";
            }
        }
    });
}

app.MapGet("/api/v1/auth-init", (AuthService authService) => Results.Ok(authService.GetAuthInit()))
    .WithName("AuthInit")
    .WithOpenApi();

app.MapGet("/api/v1/health", (EngineConfig cfg, EnginePipeline pipeline) =>
    Results.Ok(new HealthDto(
        "ok",
        Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "dev",
        cfg.Branding.StackName,
        cfg.Storage.DatabasePath,
        cfg.Storage.AudioRoot,
        pipeline.QueueDepth,
        DateTime.UtcNow)))
    .WithName("Health")
    .WithOpenApi();

app.MapGet("/api/v1/setup/status", async (SetupService setup, HttpContext context) =>
    Results.Ok(await setup.GetStatusAsync(context.RequestAborted)))
.WithName("SetupStatus")
.WithOpenApi();

app.MapGet("/api/v1/setup/detect-tr", async (SetupService setup, HttpContext context) =>
    Results.Ok(await setup.DetectTrAsync(context.RequestAborted)))
.WithName("SetupDetectTr")
.WithOpenApi();

app.MapPost("/api/v1/setup/save", async (SetupSaveRequest request, SetupService setup, HttpContext context) =>
{
    try
    {
        return Results.Ok(await setup.SaveAsync(request, context.RequestAborted));
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { message = ex.Message });
    }
})
.WithName("SetupSave")
.WithOpenApi();

app.MapPost("/api/v1/setup/validate/{section}", async (string section, SetupService setup, HttpContext context) =>
    Results.Ok(await setup.ValidateAsync(section, context.RequestAborted)))
.WithName("SetupValidate")
.WithOpenApi();

app.MapPost("/api/v1/setup/validate-required", async (SetupService setup, HttpContext context) =>
    Results.Ok(await setup.ValidateRequiredAsync(context.RequestAborted)))
.WithName("SetupValidateRequired")
.WithOpenApi();

app.MapPost("/api/v1/setup/complete", async (SetupService setup, HttpContext context) =>
{
    try
    {
        return Results.Ok(await setup.CompleteAsync(context.RequestAborted));
    }
    catch (InvalidOperationException ex)
    {
        return Results.BadRequest(new { message = ex.Message });
    }
})
.WithName("SetupComplete")
.WithOpenApi();

app.MapGet("/api/v1/setup/tr-artifacts", (SetupJobService jobs) =>
    Results.Ok(jobs.CheckTrArtifacts()))
.WithName("SetupTrArtifacts")
.WithOpenApi();

app.MapGet("/api/v1/setup/sdrs", async (SetupJobService jobs, HttpContext context) =>
    Results.Ok(await jobs.DetectSdrsAsync(context.RequestAborted)))
.WithName("SetupSdrDetect")
.WithOpenApi();

app.MapGet("/api/v1/setup/calibration/plan", (SetupCalibrationService calibration) =>
    Results.Ok(calibration.BuildPlan()))
.WithName("SetupCalibrationPlan")
.WithOpenApi();

app.MapPost("/api/v1/setup/calibration/open-gqrx", async (SetupOpenGqrxRequest request, SetupCalibrationService calibration, HttpContext context) =>
    Results.Ok(await calibration.OpenGqrxAsync(request, context.RequestAborted)))
.WithName("SetupCalibrationOpenGqrx")
.WithOpenApi();

app.MapPost("/api/v1/setup/jobs", async (SetupJobRequest request, SetupJobService jobs, HttpContext context) =>
{
    try
    {
        return Results.Ok(await jobs.StartAsync(request.Action, request.Confirmed, request.Parameters, context.RequestAborted));
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { message = ex.Message });
    }
})
.WithName("SetupJobStart")
.WithOpenApi();

app.MapPost("/api/v1/setup/talkgroups/preview", async (SetupTalkgroupParseRequest request, SetupTalkgroupService talkgroups, HttpContext context) =>
{
    try
    {
        return Results.Ok(await talkgroups.PreviewAsync(request, context.RequestAborted));
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { message = ex.Message });
    }
})
.WithName("SetupTalkgroupsPreview")
.WithOpenApi();

app.MapPost("/api/v1/setup/talkgroups/save", async (SetupTalkgroupSaveRequest request, SetupTalkgroupService talkgroups, HttpContext context) =>
{
    try
    {
        return Results.Ok(await talkgroups.SaveAsync(request, context.RequestAborted));
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { message = ex.Message });
    }
})
.WithName("SetupTalkgroupsSave")
.WithOpenApi();

app.MapPost("/api/v1/setup/tr-config/draft", async (SetupTrConfigDraftRequest request, SetupTrConfigBuilderService builder, HttpContext context) =>
{
    try
    {
        return Results.Ok(await builder.DraftAsync(request, context.RequestAborted));
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
})
.WithName("SetupTrConfigDraft")
.WithOpenApi();

app.MapPost("/api/v1/setup/tr-config/save", async (SetupTrConfigSaveRequest request, SetupTrConfigBuilderService builder, HttpContext context) =>
{
    try
    {
        return Results.Ok(await builder.SaveAsync(request, context.RequestAborted));
    }
    catch (JsonException ex)
    {
        return Results.BadRequest(new { error = "Invalid TR config JSON: " + ex.Message });
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
})
.WithName("SetupTrConfigSave")
.WithOpenApi();

app.MapPost("/api/v1/setup/tr-config/patch-callstream", async (SetupTrConfigPatchRequest request, TrConfigService trConfig, HttpContext context) =>
{
    try
    {
        return Results.Ok(await trConfig.PatchCallstreamAsync(request, context.RequestAborted));
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
})
.WithName("SetupTrConfigPatchCallstream")
.WithOpenApi();

app.MapGet("/api/v1/status", async (HttpContext context, long? start, long? end, AuthService authService, EngineDatabase database) =>
{
    if (!authService.IsReadAllowed(context)) return Results.Unauthorized();
    var range = new TimeRangeQuery(start, end).Resolve();
    return Results.Ok(await database.BuildStatusSummaryAsync(range.Start, range.End, context.RequestAborted));
})
.WithName("StatusSummary")
.WithOpenApi();

app.MapGet("/api/v1/events/stream", async (HttpContext context, EventStream events) =>
{
    await events.StreamAsync(context, context.RequestAborted);
})
.WithName("Events")
.WithOpenApi();

app.MapGet("/api/v1/dashboard", async (HttpContext context, long? start, long? end, AuthService authService, DashboardService dashboard) =>
{
    if (!authService.IsReadAllowed(context)) return Results.Unauthorized();
    var range = new TimeRangeQuery(start, end).Resolve();
    return Results.Ok(await dashboard.BuildDashboardAsync(range.Start, range.End, context.RequestAborted));
})
.WithName("Dashboard")
.WithOpenApi();

app.MapGet("/api/v1/categories/{category}", async (HttpContext context, string category, string? groupBy, long? start, long? end, AuthService authService, DashboardService dashboard) =>
{
    if (!authService.IsReadAllowed(context)) return Results.Unauthorized();
    var range = new TimeRangeQuery(start, end).Resolve();
    return Results.Ok(await dashboard.BuildCategoryPageAsync(category, groupBy ?? "time", range.Start, range.End, context.RequestAborted));
})
.WithName("Category")
.WithOpenApi();

app.MapGet("/api/v1/calls/{id:long}", async (HttpContext context, long id, AuthService authService, EngineDatabase database) =>
{
    if (!authService.IsReadAllowed(context)) return Results.Unauthorized();
    var call = await database.GetCallAsync(id, context.RequestAborted);
    return call == null ? Results.NotFound() : Results.Ok(call);
})
.WithName("CallById")
.WithOpenApi();

app.MapGet("/api/v1/calls/{id:long}/audio", async (HttpContext context, long id, AuthService authService, EngineDatabase database, EngineConfig cfg) =>
{
    if (!authService.IsReadAllowed(context)) return Results.Unauthorized();
    var call = await database.GetCallAsync(id, context.RequestAborted);
    if (call == null || string.IsNullOrWhiteSpace(call.AudioPath)) return Results.NotFound();
    var path = Path.GetFullPath(Path.Combine(cfg.Storage.AudioRoot, call.AudioPath));
    var root = Path.GetFullPath(cfg.Storage.AudioRoot);
    if (!path.StartsWith(root, StringComparison.OrdinalIgnoreCase) || !File.Exists(path)) return Results.NotFound();
    return Results.File(path, "audio/wav", enableRangeProcessing: true);
})
.WithName("CallAudio")
.WithOpenApi();

app.MapPost("/api/v1/calls/retry-transcription-errors", async (HttpContext context, RetryTranscriptionErrorsRequest request, AuthService authService, EnginePipeline pipeline) =>
{
    if (!authService.IsWriteAllowed(context)) return Results.Unauthorized();
    try
    {
        var queued = await pipeline.QueueTranscriptionErrorRetriesAsync(request.Limit <= 0 ? 100 : request.Limit, context.RequestAborted);
        return Results.Ok(new { queued, queueDepth = pipeline.QueueDepth });
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { message = ex.Message });
    }
})
.WithName("RetryTranscriptionErrors")
.WithOpenApi();

app.MapGet("/api/v1/alerts", async (HttpContext context, long? start, long? end, AuthService authService, EngineDatabase database) =>
{
    if (!authService.IsReadAllowed(context)) return Results.Unauthorized();
    var range = new TimeRangeQuery(start, end).Resolve();
    return Results.Ok(await database.ListAlertMatchesAsync(range.Start, range.End, context.RequestAborted));
})
.WithName("Alerts")
.WithOpenApi();

app.MapGet("/api/v1/incidents", async (HttpContext context, long? start, long? end, AuthService authService, DashboardService dashboard) =>
{
    if (!authService.IsReadAllowed(context)) return Results.Unauthorized();
    var range = new TimeRangeQuery(start, end).Resolve();
    return Results.Ok(await dashboard.ListIncidentsAsync(range.Start, range.End, context.RequestAborted));
})
.WithName("Incidents")
.WithOpenApi();

app.MapPost("/api/v1/incidents/generate", async (HttpContext context, GenerateSummaryRequest request, AuthService authService, SummaryService summaries) =>
{
    if (!authService.IsWriteAllowed(context)) return Results.Unauthorized();
    try
    {
        return Results.Ok(await summaries.GenerateForRangeAsync(request, context.RequestAborted));
    }
    catch (InvalidOperationException ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
})
.WithName("GenerateIncidents")
.WithOpenApi();

app.MapPost("/api/v1/incidents/rebuild", async (HttpContext context, GenerateSummaryRequest request, AuthService authService, SummaryService summaries) =>
{
    if (!authService.IsWriteAllowed(context)) return Results.Unauthorized();
    return Results.Ok(await summaries.RebuildIncidentsForRangeAsync(request, context.RequestAborted));
})
.WithName("RebuildIncidents")
.WithOpenApi();

app.MapGet("/api/v1/troubleshoot/tr-health", async (HttpContext context, long? start, long? end, AuthService authService, EngineDatabase database) =>
{
    if (!authService.IsReadAllowed(context)) return Results.Unauthorized();
    var range = new TimeRangeQuery(start, end).Resolve();
    return Results.Ok(await database.ListHealthSamplesAsync(range.Start, range.End, context.RequestAborted));
})
.WithName("TrHealth")
.WithOpenApi();

app.MapGet("/api/v1/troubleshoot", async (HttpContext context, long? start, long? end, bool? bySystem, string? baseline, AuthService authService, TrHealthTroubleshootService troubleshoot) =>
{
    if (!authService.IsReadAllowed(context)) return Results.Unauthorized();
    var range = new TimeRangeQuery(start, end).Resolve();
    return Results.Ok(await troubleshoot.BuildAsync(range.Start, range.End, bySystem ?? false, string.IsNullOrWhiteSpace(baseline) ? "7d" : baseline, context.RequestAborted));
})
.WithName("Troubleshoot")
.WithOpenApi();

app.MapPost("/api/v1/troubleshoot/insights", async (HttpContext context, TroubleshootInsightRequest request, AuthService authService, TrHealthTroubleshootService troubleshoot) =>
{
    if (!authService.IsWriteAllowed(context)) return Results.Unauthorized();
    return Results.Ok(await troubleshoot.GenerateInsightsAsync(request.Start, request.End, request.BySystem, string.IsNullOrWhiteSpace(request.Baseline) ? "7d" : request.Baseline, context.RequestAborted));
})
.WithName("TroubleshootInsights")
.WithOpenApi();

app.MapGet("/api/v1/troubleshoot/tr-config", (HttpContext context, AuthService authService, TrConfigService trConfig) =>
{
    if (!authService.IsReadAllowed(context)) return Results.Unauthorized();
    return Results.Ok(trConfig.Validate());
})
.WithName("TrConfigValidate")
.WithOpenApi();

app.MapPost("/api/v1/troubleshoot/tools/audio-experiment", async (HttpContext context, DiagnosticToolRequest request, AuthService authService, DiagnosticToolService tools) =>
{
    if (!authService.IsWriteAllowed(context)) return Results.Unauthorized();
    return Results.Ok(await tools.StartAudioExperimentAsync(request, context.RequestAborted));
})
.WithName("DiagnosticAudioExperiment")
.WithOpenApi();

app.MapPost("/api/v1/troubleshoot/tools/transcription-bakeoff", async (HttpContext context, DiagnosticToolRequest request, AuthService authService, DiagnosticToolService tools) =>
{
    if (!authService.IsWriteAllowed(context)) return Results.Unauthorized();
    return Results.Ok(await tools.StartTranscriptionBakeoffAsync(request, context.RequestAborted));
})
.WithName("DiagnosticTranscriptionBakeoff")
.WithOpenApi();

app.MapGet("/api/v1/troubleshoot/tools/transcription-models", (HttpContext context, AuthService authService, DiagnosticToolService tools) =>
{
    if (!authService.IsReadAllowed(context)) return Results.Unauthorized();
    return Results.Ok(tools.ListDiagnosticModels());
})
.WithName("DiagnosticTranscriptionModels")
.WithOpenApi();

app.MapPost("/api/v1/troubleshoot/tools/transcription-experiment", async (HttpContext context, DiagnosticToolRequest request, AuthService authService, DiagnosticToolService tools) =>
{
    if (!authService.IsWriteAllowed(context)) return Results.Unauthorized();
    try
    {
        return Results.Ok(await tools.StartUnifiedTranscriptionExperimentAsync(request, context.RequestAborted));
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { message = ex.Message });
    }
})
.WithName("DiagnosticTranscriptionExperiment")
.WithOpenApi();

app.MapGet("/api/v1/troubleshoot/tools/results/{jobId:long}", async (HttpContext context, long jobId, AuthService authService, DiagnosticToolService tools) =>
{
    if (!authService.IsReadAllowed(context)) return Results.Unauthorized();
    var result = await tools.GetResultAsync(jobId, context.RequestAborted);
    return result == null ? Results.NotFound() : Results.Ok(result);
})
.WithName("DiagnosticToolResult")
.WithOpenApi();

app.MapDelete("/api/v1/troubleshoot/tools/results", async (HttpContext context, AuthService authService, DiagnosticToolService tools) =>
{
    if (!authService.IsWriteAllowed(context)) return Results.Unauthorized();
    var deleted = await tools.ClearExperimentResultsAsync(context.RequestAborted);
    return Results.Ok(new { deleted });
})
.WithName("DiagnosticToolResultsClear")
.WithOpenApi();

app.MapGet("/api/v1/troubleshoot/tools/results/{jobId:long}/audio/{callId:long}/{fileName}", (HttpContext context, long jobId, long callId, string fileName, AuthService authService, DiagnosticToolService tools) =>
{
    if (!authService.IsReadAllowed(context)) return Results.Unauthorized();
    var path = tools.GetDiagnosticAudioPath(jobId, callId, fileName);
    return string.IsNullOrWhiteSpace(path) ? Results.NotFound() : Results.File(path, "audio/wav", enableRangeProcessing: true);
})
.WithName("DiagnosticToolAudio")
.WithOpenApi();

app.MapGet("/api/v1/jobs", async (HttpContext context, AuthService authService, EngineDatabase database) =>
{
    if (!authService.IsReadAllowed(context)) return Results.Unauthorized();
    return Results.Ok(await database.ListJobsAsync(context.RequestAborted));
})
.WithName("Jobs")
.WithOpenApi();

app.MapGet("/api/v1/jobs/{id:long}/logs", async (HttpContext context, long id, long? afterId, AuthService authService, EngineDatabase database) =>
{
    if (!authService.IsReadAllowed(context)) return Results.Unauthorized();
    return Results.Ok(await database.ListJobLogsAsync(id, afterId ?? 0, context.RequestAborted));
})
.WithName("JobLogs")
.WithOpenApi();

app.MapGet("/api/v1/system/token-usage", async (HttpContext context, long? start, long? end, AuthService authService, EngineDatabase database) =>
{
    if (!authService.IsReadAllowed(context)) return Results.Unauthorized();
    var range = new TimeRangeQuery(start, end).Resolve();
    return Results.Ok(await database.GetTokenUsageAsync(range.Start, range.End, context.RequestAborted));
})
.WithName("TokenUsage")
.WithOpenApi();

app.MapGet("/api/v1/system/runtime", async (HttpContext context, AuthService authService, SystemManagerService system) =>
{
    if (!authService.IsReadAllowed(context)) return Results.Unauthorized();
    return Results.Ok(await system.BuildAsync(context.RequestAborted));
})
.WithName("SystemRuntime")
.WithOpenApi();

app.MapGet("/api/v1/profiles", (HttpContext context, AuthService authService, EngineConfig cfg) =>
{
    if (!authService.IsReadAllowed(context)) return Results.Unauthorized();
    return Results.Ok(new ProfileStateDto(cfg.Profiles.ActiveProfileId, cfg.Profiles.Items));
})
.WithName("ProfilesRead")
.WithOpenApi();

app.MapPost("/api/v1/profiles", (HttpContext context, SaveProfilesRequest request, AuthService authService, EngineConfig cfg) =>
{
    if (!authService.IsWriteAllowed(context)) return Results.Unauthorized();
    var profiles = request.Profiles?.ToList() ?? [];
    if (profiles.Count == 0)
        profiles.Add(new ProcessingProfile { Name = "Default" });
    foreach (var profile in profiles)
    {
        if (profile.Id == Guid.Empty) profile.Id = Guid.NewGuid();
        profile.Name = string.IsNullOrWhiteSpace(profile.Name) ? "Profile" : profile.Name.Trim();
        profile.AllowedTalkgroups ??= new();
        profile.UpdatedAtUtc = DateTime.UtcNow;
    }
    cfg.Profiles.Items = profiles;
    cfg.Profiles.ActiveProfileId = profiles.Any(p => p.Id == request.ActiveProfileId) ? request.ActiveProfileId : profiles[0].Id;
    cfg.Save();
    return Results.Ok(new ProfileStateDto(cfg.Profiles.ActiveProfileId, cfg.Profiles.Items));
})
.WithName("ProfilesSave")
.WithOpenApi();

app.MapGet("/api/v1/talkgroups", (HttpContext context, AuthService authService, TalkgroupResolver talkgroups) =>
{
    if (!authService.IsReadAllowed(context))
        return Results.Unauthorized();
    return Results.Ok(talkgroups.ListOptions());
})
.WithName("TalkgroupsList")
.WithOpenApi();

app.MapPost("/api/v1/jobs/{id:long}/control", async (HttpContext context, long id, JobControlRequest request, AuthService authService, EngineDatabase database, SftpImportService sftpImports, LocalImportService localImports, SummaryService summaries) =>
{
    if (!authService.IsWriteAllowed(context)) return Results.Unauthorized();
    var job = await database.GetJobAsync(id, context.RequestAborted);
    if (job == null) return Results.NotFound();
    try
    {
        JobDto? updated = job.Type switch
        {
            "sftp_import" => await sftpImports.ControlJobAsync(id, request.Action, context.RequestAborted),
            "local_import" => await localImports.ControlJobAsync(id, request.Action, context.RequestAborted),
            "summary_generation" => await summaries.ControlJobAsync(id, request.Action, context.RequestAborted),
            _ => throw new InvalidOperationException("This job type does not support pause/resume/cancel.")
        };
        return updated == null ? Results.NotFound() : Results.Ok(updated);
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { message = ex.Message });
    }
})
.WithName("JobControl")
.WithOpenApi();

app.MapDelete("/api/v1/jobs/{id:long}", async (HttpContext context, long id, AuthService authService, EngineDatabase database) =>
{
    if (!authService.IsWriteAllowed(context)) return Results.Unauthorized();
    var job = await database.GetJobAsync(id, context.RequestAborted);
    if (job == null) return Results.NotFound();
    if (job.Status is "running" or "queued" or "paused")
        return Results.Conflict("Cancel or wait for the job to finish before deleting it.");
    return await database.DeleteJobAsync(id, context.RequestAborted) ? Results.Ok(new { deleted = true, id }) : Results.NotFound();
})
.WithName("JobDelete")
.WithOpenApi();

app.MapPost("/api/v1/imports/sftp/estimate", async (HttpContext context, SftpEstimateRequest request, AuthService authService, SftpImportService imports) =>
{
    if (!authService.IsWriteAllowed(context)) return Results.Unauthorized();
    try
    {
        return Results.Ok(await imports.EstimateAsync(request, context.RequestAborted));
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { message = ex.Message });
    }
})
.WithName("SftpEstimate")
.WithOpenApi();

app.MapGet("/api/v1/imports/sftp/availability", async (HttpContext context, AuthService authService, SftpImportService imports) =>
{
    if (!authService.IsReadAllowed(context)) return Results.Unauthorized();
    try
    {
        return Results.Ok(await imports.GetAvailabilityAsync(context.RequestAborted));
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { message = ex.Message });
    }
})
.WithName("SftpAvailability")
.WithOpenApi();

app.MapPost("/api/v1/imports/sftp/import", async (HttpContext context, SftpImportRequest request, AuthService authService, SftpImportService imports) =>
{
    if (!authService.IsWriteAllowed(context)) return Results.Unauthorized();
    try
    {
        return Results.Ok(await imports.StartImportAsync(request, context.RequestAborted));
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { message = ex.Message });
    }
})
.WithName("SftpImport")
.WithOpenApi();

app.MapPost("/api/v1/imports/local/estimate", async (HttpContext context, LocalImportEstimateRequest request, AuthService authService, LocalImportService imports) =>
{
    if (!authService.IsWriteAllowed(context)) return Results.Unauthorized();
    try
    {
        return Results.Ok(await imports.EstimateAsync(request, context.RequestAborted));
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { message = ex.Message });
    }
})
.WithName("LocalImportEstimate")
.WithOpenApi();

app.MapGet("/api/v1/imports/local/availability", async (HttpContext context, AuthService authService, LocalImportService imports) =>
{
    if (!authService.IsReadAllowed(context)) return Results.Unauthorized();
    try
    {
        return Results.Ok(await imports.GetAvailabilityAsync(context.RequestAborted));
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { message = ex.Message });
    }
})
.WithName("LocalImportAvailability")
.WithOpenApi();

app.MapPost("/api/v1/imports/local/import", async (HttpContext context, LocalImportRequest request, AuthService authService, LocalImportService imports) =>
{
    if (!authService.IsWriteAllowed(context)) return Results.Unauthorized();
    try
    {
        return Results.Ok(await imports.StartImportAsync(request, context.RequestAborted));
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { message = ex.Message });
    }
})
.WithName("LocalImport")
.WithOpenApi();

app.MapGet("/api/v1/settings/{section}", (HttpContext context, string section, AuthService authService, EngineConfig cfg) =>
{
    if (!authService.IsReadAllowed(context)) return Results.Unauthorized();
    object values = section.ToLowerInvariant() switch
    {
        "engine" => new { cfg.Branding, cfg.Server, cfg.Storage, cfg.Ingest },
        "transcription" => cfg.Transcription,
        "ai-insights" => cfg.AiInsights,
        "sftp" => cfg.SftpImport,
        "tr" => cfg.TrunkRecorder,
        "profiles" => cfg.Profiles,
        "auth" => new { cfg.Auth.Mode, cfg.Auth.ReadRequiresAuth, cfg.Auth.WriteRequiresAuth, cfg.Auth.TokenFile },
        "alerts" => cfg.Alerts,
        _ => new { }
    };
    return Results.Ok(new SettingsSectionDto(section, values));
})
.WithName("SettingsRead")
.WithOpenApi();

app.MapPost("/api/v1/settings/{section}", (HttpContext context, string section, SaveSettingsRequest request, AuthService authService, EngineConfig cfg) =>
{
    if (!authService.IsWriteAllowed(context)) return Results.Unauthorized();
    section = section.Trim().ToLowerInvariant();
    var json = request.Values.GetRawText();
    switch (section)
    {
        case "engine":
            var engine = System.Text.Json.JsonSerializer.Deserialize<EngineSectionUpdate>(json, EngineConfig.JsonOptions());
            if (engine?.Branding != null) cfg.Branding = engine.Branding;
            if (engine?.Server != null) cfg.Server = engine.Server;
            if (engine?.Storage != null) cfg.Storage = engine.Storage;
            if (engine?.Ingest != null) cfg.Ingest = engine.Ingest;
            break;
        case "transcription":
            cfg.Transcription = System.Text.Json.JsonSerializer.Deserialize<TranscriptionConfig>(json, EngineConfig.JsonOptions()) ?? cfg.Transcription;
            break;
        case "ai-insights":
            cfg.AiInsights = System.Text.Json.JsonSerializer.Deserialize<AiInsightsConfig>(json, EngineConfig.JsonOptions()) ?? cfg.AiInsights;
            break;
        case "sftp":
            cfg.SftpImport = System.Text.Json.JsonSerializer.Deserialize<SftpImportConfig>(json, EngineConfig.JsonOptions()) ?? cfg.SftpImport;
            break;
        case "tr":
            cfg.TrunkRecorder = System.Text.Json.JsonSerializer.Deserialize<TrunkRecorderConfig>(json, EngineConfig.JsonOptions()) ?? cfg.TrunkRecorder;
            break;
        case "alerts":
            cfg.Alerts = System.Text.Json.JsonSerializer.Deserialize<AlertConfig>(json, EngineConfig.JsonOptions()) ?? cfg.Alerts;
            break;
        case "profiles":
            cfg.Profiles = System.Text.Json.JsonSerializer.Deserialize<ProfileConfig>(json, EngineConfig.JsonOptions()) ?? cfg.Profiles;
            break;
        case "auth":
            cfg.Auth = System.Text.Json.JsonSerializer.Deserialize<AuthConfig>(json, EngineConfig.JsonOptions()) ?? cfg.Auth;
            break;
        default:
            return Results.BadRequest("Unknown settings section.");
    }
    cfg.Save();
    return Results.Ok(new { saved = section });
})
.WithName("SettingsSave")
.WithOpenApi();

app.MapPost("/api/v1/settings/{section}/test", async (HttpContext context, string section, AuthService authService, SettingsValidationService validation) =>
{
    if (!authService.IsWriteAllowed(context)) return Results.Unauthorized();
    return Results.Ok(await validation.TestAsync(section, context.RequestAborted));
})
.WithName("SettingsTest")
.WithOpenApi();

app.MapGet("/api/v1/settings/transcription/models", (HttpContext context, AuthService authService, SettingsValidationService validation) =>
{
    if (!authService.IsReadAllowed(context)) return Results.Unauthorized();
    return Results.Ok(validation.ListWhisperModels());
})
.WithName("WhisperModels")
.WithOpenApi();

app.MapPost("/api/v1/settings/transcription/models/{model}/download", async (HttpContext context, string model, AuthService authService, SettingsValidationService validation) =>
{
    if (!authService.IsWriteAllowed(context)) return Results.Unauthorized();
    try
    {
        return Results.Ok(await validation.DownloadWhisperModelAsync(model, context.RequestAborted));
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { ok = false, message = ex.Message });
    }
})
.WithName("WhisperModelDownload")
.WithOpenApi();

app.MapDelete("/api/v1/settings/transcription/models/{model}", (HttpContext context, string model, AuthService authService, SettingsValidationService validation) =>
{
    if (!authService.IsWriteAllowed(context)) return Results.Unauthorized();
    return Results.Ok(validation.DeleteWhisperModel(model));
})
.WithName("WhisperModelDelete")
.WithOpenApi();

app.MapGet("/api/v1/settings/ai-insights/models", async (HttpContext context, AuthService authService, SettingsValidationService validation) =>
{
    if (!authService.IsReadAllowed(context)) return Results.Unauthorized();
    return Results.Ok(await validation.ListAiInsightModelsAsync(context.RequestAborted));
})
.WithName("AiInsightModels")
.WithOpenApi();

app.MapPost("/api/v1/settings/auth/regenerate-token", (HttpContext context, AuthService authService) =>
{
    if (!authService.IsWriteAllowed(context)) return Results.Unauthorized();
    return Results.Ok(new { tokenFile = authService.RegenerateToken() });
})
.WithName("RegenerateToken")
.WithOpenApi();

app.MapFallback((HttpContext context) =>
{
    var index = Path.Combine(staticRoot, "index.html");
    if (!Directory.Exists(staticRoot) || !File.Exists(index))
        return Results.NotFound("PizzaWave Engine web UI was not found.");

    context.Response.Headers.CacheControl = "no-store, no-cache, must-revalidate";
    context.Response.Headers.Pragma = "no-cache";
    context.Response.Headers.Expires = "0";
    return Results.File(index, "text/html");
});

app.Run();
