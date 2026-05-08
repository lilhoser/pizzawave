using Microsoft.AspNetCore.StaticFiles;
using pizzad;
using System.Reflection;

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
builder.Services.AddSingleton<AutomaticInsightsService>();
builder.Services.AddSingleton<EnginePipeline>();
builder.Services.AddSingleton<DashboardService>();
builder.Services.AddSingleton<SftpImportService>();
builder.Services.AddSingleton<SummaryService>();
builder.Services.AddSingleton<TrConfigService>();
builder.Services.AddSingleton<TrHealthTroubleshootService>();
builder.Services.AddSingleton<DiagnosticToolService>();
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
        ContentTypeProvider = new FileExtensionContentTypeProvider()
    });
}

app.MapGet("/api/v1/auth-init", (AuthService authService) => Results.Ok(authService.GetAuthInit()))
    .WithName("AuthInit")
    .WithOpenApi();

app.MapGet("/api/v1/health", (EngineConfig cfg, EnginePipeline pipeline) =>
    Results.Ok(new HealthDto(
        "ok",
        Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "dev",
        cfg.Storage.DatabasePath,
        cfg.Storage.AudioRoot,
        pipeline.QueueDepth,
        DateTime.UtcNow)))
    .WithName("Health")
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

app.MapGet("/api/v1/troubleshoot/tools/results/{jobId:long}", async (HttpContext context, long jobId, AuthService authService, DiagnosticToolService tools) =>
{
    if (!authService.IsReadAllowed(context)) return Results.Unauthorized();
    var result = await tools.GetResultAsync(jobId, context.RequestAborted);
    return result == null ? Results.NotFound() : Results.Ok(result);
})
.WithName("DiagnosticToolResult")
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

app.MapPost("/api/v1/jobs/{id:long}/control", async (HttpContext context, long id, JobControlRequest request, AuthService authService, SftpImportService imports) =>
{
    if (!authService.IsWriteAllowed(context)) return Results.Unauthorized();
    var job = await imports.ControlJobAsync(id, request.Action, context.RequestAborted);
    return job == null ? Results.NotFound() : Results.Ok(job);
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
    return Results.Ok(await imports.EstimateAsync(request, context.RequestAborted));
})
.WithName("SftpEstimate")
.WithOpenApi();

app.MapPost("/api/v1/imports/sftp/import", async (HttpContext context, SftpImportRequest request, AuthService authService, SftpImportService imports) =>
{
    if (!authService.IsWriteAllowed(context)) return Results.Unauthorized();
    return Results.Ok(await imports.StartImportAsync(request, context.RequestAborted));
})
.WithName("SftpImport")
.WithOpenApi();

app.MapGet("/api/v1/settings/{section}", (HttpContext context, string section, AuthService authService, EngineConfig cfg) =>
{
    if (!authService.IsReadAllowed(context)) return Results.Unauthorized();
    object values = section.ToLowerInvariant() switch
    {
        "engine" => new { cfg.Server, cfg.Storage, cfg.Ingest },
        "transcription" => cfg.Transcription,
        "ai-insights" => cfg.AiInsights,
        "sftp" => cfg.SftpImport,
        "tr" => cfg.TrunkRecorder,
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
    return Directory.Exists(staticRoot) && File.Exists(index)
        ? Results.File(index, "text/html")
        : Results.NotFound("PizzaWave Engine web UI was not found.");
});

app.Run();
