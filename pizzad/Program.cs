using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.AspNetCore.Http.Features;
using pizzad;
using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Text;
using System.Text.Json;

var configPath = args
    .SkipWhile(a => a != "--config")
    .Skip(1)
    .FirstOrDefault()
    ?? Environment.GetEnvironmentVariable("PIZZAD_CONFIG")
    ?? "/etc/pizzawave/pizzad.json";

var config = EngineConfig.Load(configPath);
Directory.CreateDirectory(config.Storage.AudioRoot);
Directory.CreateDirectory(Path.GetDirectoryName(config.Storage.DatabasePath) ?? ".");

Environment.SetEnvironmentVariable("HOME", config.Storage.AppDataRoot);
Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", config.Storage.AppDataRoot);
Environment.SetEnvironmentVariable("XDG_DATA_HOME", config.Storage.AppDataRoot);

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseUrls($"http://{config.Server.HttpBind}:{config.Server.HttpPort}");
builder.WebHost.ConfigureKestrel(options => options.Limits.MaxRequestBodySize = 1024L * 1024L * 1024L * 200L);
builder.Services.AddSingleton(config);
builder.Services.AddSingleton<EngineDatabase>();
builder.Services.AddSingleton<EventStream>();
builder.Services.AddSingleton<AuthService>();
builder.Services.AddSingleton<CredentialStore>();
builder.Services.AddSingleton<IngestControlService>();
builder.Services.AddSingleton<EngineAlertService>();
builder.Services.AddSingleton<TalkgroupCatalogService>();
builder.Services.AddSingleton<TalkgroupResolver>();
builder.Services.AddSingleton<CallAudioService>();
builder.Services.AddSingleton<PoliceCodeService>();
builder.Services.AddSingleton<TranscriptLocationService>();
builder.Services.AddSingleton<CallAnchorExtractionService>();
builder.Services.AddSingleton<TranscriptPostProcessingService>();
builder.Services.AddSingleton<EmbeddingService>();
builder.Services.AddSingleton<IncidentReconciliationService>();
builder.Services.AddSingleton<RemoteBandwidthEstimatorService>();
builder.Services.AddHttpClient<GeocodingService>();
builder.Services.AddSingleton<AutomaticInsightsService>();
builder.Services.AddSingleton<LiveTrActivityMonitor>();
builder.Services.AddSingleton<HealthStatusService>();
builder.Services.AddSingleton<EnginePipeline>();
builder.Services.AddSingleton<DashboardService>();
builder.Services.AddSingleton<TrConfigService>();
builder.Services.AddSingleton<TrHealthTroubleshootService>();
builder.Services.AddSingleton<SettingsValidationService>();
builder.Services.AddSingleton<SystemManagerService>();
builder.Services.AddSingleton<SystemRecommendationService>();
builder.Services.AddSingleton<SystemCpuSnapshotService>();
builder.Services.AddSingleton<BackupRestoreService>();
builder.Services.AddSingleton<MigrationService>();
builder.Services.AddSingleton<SetupService>();
builder.Services.AddSingleton<SiteSetupService>();
builder.Services.AddSingleton<SetupJobService>();
builder.Services.AddHttpClient<SetupTalkgroupService>();
builder.Services.AddHttpClient<SetupTrConfigBuilderService>();
builder.Services.AddHttpClient<SetupAreaBoundaryService>();
builder.Services.AddSingleton<SetupCalibrationService>();
builder.Services.AddSingleton<RfSurveyService>();
builder.Services.AddSingleton<RfSurveyInsightService>();
builder.Services.AddHostedService<CallstreamListener>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<AutomaticInsightsService>());
builder.Services.AddHostedService(sp => sp.GetRequiredService<TranscriptPostProcessingService>());
builder.Services.AddHostedService(sp => sp.GetRequiredService<EmbeddingService>());
builder.Services.AddHostedService<TrHealthCollector>();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();
builder.Services.Configure<FormOptions>(options =>
{
    options.MultipartBodyLengthLimit = 1024L * 1024L * 1024L * 200L;
});

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

app.MapGet("/api/v1/app-version", () =>
{
    var index = Path.Combine(staticRoot, "index.html");
    var version = File.Exists(index)
        ? File.GetLastWriteTimeUtc(index).Ticks.ToString(CultureInfo.InvariantCulture)
        : DateTime.UtcNow.Ticks.ToString(CultureInfo.InvariantCulture);
    return Results.Ok(new { version });
})
.WithName("AppVersion")
.WithOpenApi();

app.MapGet("/api/v1/health", async (HealthStatusService health, CancellationToken ct) => Results.Ok(await health.GetAsync(ct)))
    .WithName("Health")
    .WithOpenApi();

app.MapGet("/api/v1/setup/status", async (SetupService setup, HttpContext context, AuthService authService) =>
{
    if (!authService.IsReadAllowed(context)) return Results.Unauthorized();
    return Results.Ok(await setup.GetStatusAsync(context.RequestAborted));
})
.WithName("SetupStatus")
.WithOpenApi();

app.MapGet("/api/v1/setup/detect-tr", async (SetupService setup, HttpContext context, AuthService authService) =>
{
    if (!authService.IsWriteAllowed(context)) return Results.Unauthorized();
    return Results.Ok(await setup.DetectTrAsync(context.RequestAborted));
})
.WithName("SetupDetectTr")
.WithOpenApi();

app.MapPost("/api/v1/setup/save", async (SetupSaveRequest request, SetupService setup, HttpContext context, AuthService authService) =>
{
    if (!authService.IsWriteAllowed(context)) return Results.Unauthorized();
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

app.MapGet("/api/v1/setup/site", async (HttpContext context, AuthService authService, SiteSetupService siteSetup) =>
{
    if (!authService.IsReadAllowed(context)) return Results.Unauthorized();
    return Results.Ok(await siteSetup.GetAsync(context.RequestAborted));
})
.WithName("SiteSetupGet")
.WithOpenApi();

app.MapPatch("/api/v1/setup/site", async (HttpContext context, SiteSetupUpdateRequest request, AuthService authService, SiteSetupService siteSetup) =>
{
    if (!authService.IsWriteAllowed(context)) return Results.Unauthorized();
    try
    {
        return Results.Ok(await siteSetup.UpdateDesiredAsync(request, context.RequestAborted));
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { message = ex.Message });
    }
})
.WithName("SiteSetupUpdate")
.WithOpenApi();

app.MapGet("/api/v1/setup/site/activity", async (HttpContext context, int? limit, AuthService authService, EngineDatabase database) =>
{
    if (!authService.IsReadAllowed(context)) return Results.Unauthorized();
    return Results.Ok(await database.ListSiteSetupActivityAsync(limit ?? 100, context.RequestAborted));
})
.WithName("SiteSetupActivityList")
.WithOpenApi();

app.MapPost("/api/v1/setup/site/activity", async (HttpContext context, SiteSetupActivityRequest request, AuthService authService, SiteSetupService siteSetup) =>
{
    if (!authService.IsWriteAllowed(context)) return Results.Unauthorized();
    try
    {
        return Results.Ok(await siteSetup.AddActivityAsync(request, context.RequestAborted));
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { message = ex.Message });
    }
})
.WithName("SiteSetupActivityAdd")
.WithOpenApi();

app.MapPost("/api/v1/setup/site/mark-applied", async (HttpContext context, SiteSetupMarkAppliedRequest request, AuthService authService, SiteSetupService siteSetup) =>
{
    if (!authService.IsWriteAllowed(context)) return Results.Unauthorized();
    try
    {
        return Results.Ok(await siteSetup.MarkAppliedAsync(request, context.RequestAborted));
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { message = ex.Message });
    }
})
.WithName("SiteSetupMarkApplied")
.WithOpenApi();

app.MapGet("/api/v1/setup/site/rf", async (HttpContext context, AuthService authService, SiteSetupService siteSetup, RfSurveyService surveys) =>
{
    if (!authService.IsReadAllowed(context)) return Results.Unauthorized();
    var setup = await siteSetup.GetAsync(context.RequestAborted);
    return Results.Ok(await surveys.UpsertSiteSetupAsync(setup.Desired, context.RequestAborted));
})
.WithName("SiteSetupRfGet")
.WithOpenApi();

app.MapGet("/api/v1/setup/site/rf/{id}", async (HttpContext context, string id, bool? compact, AuthService authService, SiteSetupService siteSetup, RfSurveyService surveys) =>
{
    if (!authService.IsReadAllowed(context)) return Results.Unauthorized();
    var setup = await siteSetup.GetAsync(context.RequestAborted);
    var detail = await surveys.UpsertSiteSetupAsync(setup.Desired, context.RequestAborted);
    return compact == true
        ? Results.Ok(await surveys.GetAsync(detail.Session.Id, context.RequestAborted, compactExperiments: true) ?? detail)
        : Results.Ok(detail);
})
.WithName("SiteSetupRfGetById")
.WithOpenApi();

app.MapPost("/api/v1/setup/site/rf/{id}/experiments/run", async (HttpContext context, string id, RfSurveyRunExperimentRequest request, AuthService authService, SiteSetupService siteSetup, RfSurveyService surveys) =>
{
    if (!authService.IsWriteAllowed(context)) return Results.Unauthorized();
    try
    {
        var setup = await siteSetup.GetAsync(context.RequestAborted);
        var detail = await surveys.UpsertSiteSetupAsync(setup.Desired, context.RequestAborted);
        return Results.Ok(await surveys.RunExperimentAsync(detail.Session.Id, request, context.RequestAborted));
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { message = ex.Message });
    }
})
.WithName("SiteSetupRfRunExperiment")
.WithOpenApi();

app.MapPost("/api/v1/setup/site/rf/{id}/experiments/cancel", async (HttpContext context, string id, AuthService authService, SiteSetupService siteSetup, RfSurveyService surveys) =>
{
    if (!authService.IsWriteAllowed(context)) return Results.Unauthorized();
    var setup = await siteSetup.GetAsync(context.RequestAborted);
    var detail = await surveys.UpsertSiteSetupAsync(setup.Desired, context.RequestAborted);
    return Results.Ok(await surveys.CancelActiveExperimentAsync(detail.Session.Id, context.RequestAborted));
})
.WithName("SiteSetupRfCancelExperiment")
.WithOpenApi();

app.MapGet("/api/v1/setup/site/rf/{id}/sweep-progress", async (HttpContext context, string id, AuthService authService, SiteSetupService siteSetup, RfSurveyService surveys) =>
{
    if (!authService.IsReadAllowed(context)) return Results.Unauthorized();
    var setup = await siteSetup.GetAsync(context.RequestAborted);
    var detail = await surveys.UpsertSiteSetupAsync(setup.Desired, context.RequestAborted);
    return Results.Ok(await surveys.GetSweepProgressAsync(detail.Session.Id, context.RequestAborted));
})
.WithName("SiteSetupRfSweepProgress")
.WithOpenApi();

app.MapPost("/api/v1/setup/site/rf/{id}/sweep-insights", async (HttpContext context, string id, RfSweepInsightRequest request, AuthService authService, SiteSetupService siteSetup, RfSurveyService surveys, RfSurveyInsightService insights) =>
{
    if (!authService.IsWriteAllowed(context)) return Results.Unauthorized();
    try
    {
        var setup = await siteSetup.GetAsync(context.RequestAborted);
        var detail = await surveys.UpsertSiteSetupAsync(setup.Desired, context.RequestAborted);
        var effective = request with { SurveyId = detail.Session.Id };
        return Results.Ok(await insights.AnalyzeSweepAsync(effective, context.RequestAborted));
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { message = ex.Message });
    }
})
.WithName("SiteSetupRfSweepInsights")
.WithOpenApi();

app.MapPost("/api/v1/setup/site/rf/{id}/waterfall/start", async (HttpContext context, string id, RfSurveyWaterfallStartRequest request, AuthService authService, SiteSetupService siteSetup, RfSurveyService surveys) =>
{
    if (!authService.IsWriteAllowed(context)) return Results.Unauthorized();
    try
    {
        var setup = await siteSetup.GetAsync(context.RequestAborted);
        var detail = await surveys.UpsertSiteSetupAsync(setup.Desired, context.RequestAborted);
        return Results.Ok(await surveys.StartWaterfallAsync(detail.Session.Id, request, CancellationToken.None));
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { message = ex.Message });
    }
})
.WithName("SiteSetupRfWaterfallStart")
.WithOpenApi();

app.MapGet("/api/v1/setup/site/rf/{id}/waterfall", async (HttpContext context, string id, AuthService authService, SiteSetupService siteSetup, RfSurveyService surveys, bool history = false) =>
{
    if (!authService.IsReadAllowed(context)) return Results.Unauthorized();
    var setup = await siteSetup.GetAsync(context.RequestAborted);
    var detail = await surveys.UpsertSiteSetupAsync(setup.Desired, context.RequestAborted);
    return Results.Ok(await surveys.GetWaterfallAsync(detail.Session.Id, history, context.RequestAborted));
})
.WithName("SiteSetupRfWaterfall")
.WithOpenApi();

app.MapPost("/api/v1/setup/site/rf/{id}/waterfall/stop", async (HttpContext context, string id, AuthService authService, SiteSetupService siteSetup, RfSurveyService surveys) =>
{
    if (!authService.IsWriteAllowed(context)) return Results.Unauthorized();
    try
    {
        var setup = await siteSetup.GetAsync(context.RequestAborted);
        var detail = await surveys.UpsertSiteSetupAsync(setup.Desired, context.RequestAborted);
        return Results.Ok(await surveys.StopWaterfallAsync(detail.Session.Id, CancellationToken.None));
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { message = ex.Message });
    }
})
.WithName("SiteSetupRfWaterfallStop")
.WithOpenApi();

app.MapGet("/api/v1/setup/site/rf/{id}/config-draft", async (HttpContext context, string id, AuthService authService, SiteSetupService siteSetup, RfSurveyService surveys) =>
{
    if (!authService.IsReadAllowed(context)) return Results.Unauthorized();
    try
    {
        var setup = await siteSetup.GetAsync(context.RequestAborted);
        var detail = await surveys.UpsertSiteSetupAsync(setup.Desired, context.RequestAborted);
        return Results.Ok(await surveys.BuildConfigDraftAsync(detail.Session.Id, context.RequestAborted));
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
})
.WithName("SiteSetupRfConfigDraft")
.WithOpenApi();

app.MapPost("/api/v1/setup/site/rf/{id}/tr/apply-source-draft", async (HttpContext context, string id, RfSurveyApplySourceDraftRequest request, AuthService authService, SiteSetupService siteSetup, RfSurveyService surveys, TrConfigService trConfig) =>
{
    if (!authService.IsWriteAllowed(context)) return Results.Unauthorized();
    try
    {
        var setup = await siteSetup.GetAsync(context.RequestAborted);
        var detail = await surveys.UpsertSiteSetupAsync(setup.Desired, context.RequestAborted);
        var result = await surveys.ApplySourceDraftAsync(detail.Session.Id, request, context.RequestAborted);
        await trConfig.ClearEditorDraftAsync(context.RequestAborted);
        return Results.Ok(result);
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
})
.WithName("SiteSetupRfApplySourceDraft")
.WithOpenApi();

app.MapPost("/api/v1/setup/validate/{section}", async (string section, SetupService setup, HttpContext context, AuthService authService) =>
{
    if (!authService.IsWriteAllowed(context)) return Results.Unauthorized();
    return Results.Ok(await setup.ValidateAsync(section, context.RequestAborted));
})
.WithName("SetupValidate")
.WithOpenApi();

app.MapPost("/api/v1/setup/validate-required", async (SetupService setup, HttpContext context, AuthService authService) =>
{
    if (!authService.IsWriteAllowed(context)) return Results.Unauthorized();
    return Results.Ok(await setup.ValidateRequiredAsync(context.RequestAborted));
})
.WithName("SetupValidateRequired")
.WithOpenApi();

app.MapPost("/api/v1/setup/complete", async (SetupService setup, HttpContext context, AuthService authService) =>
{
    if (!authService.IsWriteAllowed(context)) return Results.Unauthorized();
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

app.MapGet("/api/v1/setup/tr-artifacts", (SetupJobService jobs, HttpContext context, AuthService authService) =>
{
    if (!authService.IsWriteAllowed(context)) return Results.Unauthorized();
    return Results.Ok(jobs.CheckTrArtifacts());
})
.WithName("SetupTrArtifacts")
.WithOpenApi();

app.MapGet("/api/v1/setup/sdrs", async (SetupJobService jobs, HttpContext context, AuthService authService) =>
{
    if (!authService.IsWriteAllowed(context)) return Results.Unauthorized();
    return Results.Ok(await jobs.DetectSdrsAsync(context.RequestAborted));
})
.WithName("SetupSdrDetect")
.WithOpenApi();

app.MapGet("/api/v1/setup/calibration/plan", (SetupCalibrationService calibration, HttpContext context, AuthService authService) =>
{
    if (!authService.IsWriteAllowed(context)) return Results.Unauthorized();
    return Results.Ok(calibration.BuildPlan());
})
.WithName("SetupCalibrationPlan")
.WithOpenApi();

app.MapPost("/api/v1/setup/areas/boundaries", async (SetupAreaBoundaryRequest request, SetupAreaBoundaryService boundaries, HttpContext context, AuthService authService) =>
{
    if (!authService.IsWriteAllowed(context)) return Results.Unauthorized();
    try
    {
        return Results.Ok(await boundaries.SearchAsync(request, context.RequestAborted));
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { message = ex.Message });
    }
})
.WithName("SetupAreaBoundaries")
.WithOpenApi();

app.MapPost("/api/v1/setup/calibration/open-gqrx", async (SetupOpenGqrxRequest request, SetupCalibrationService calibration, HttpContext context, AuthService authService) =>
{
    if (!authService.IsWriteAllowed(context)) return Results.Unauthorized();
    return Results.Ok(await calibration.OpenGqrxAsync(request, context.RequestAborted));
})
.WithName("SetupCalibrationOpenGqrx")
.WithOpenApi();

app.MapPost("/api/v1/setup/jobs", async (SetupJobRequest request, SetupJobService jobs, HttpContext context, AuthService authService) =>
{
    if (!authService.IsWriteAllowed(context)) return Results.Unauthorized();
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

app.MapPost("/api/v1/setup/talkgroups/preview", async (SetupTalkgroupParseRequest request, SetupTalkgroupService talkgroups, HttpContext context, AuthService authService) =>
{
    if (!authService.IsWriteAllowed(context)) return Results.Unauthorized();
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

app.MapPost("/api/v1/setup/talkgroups/save", async (SetupTalkgroupSaveRequest request, SetupTalkgroupService talkgroups, HttpContext context, AuthService authService) =>
{
    if (!authService.IsWriteAllowed(context)) return Results.Unauthorized();
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

app.MapPost("/api/v1/setup/tr-config/draft", async (SetupTrConfigDraftRequest request, SetupTrConfigBuilderService builder, HttpContext context, AuthService authService) =>
{
    if (!authService.IsWriteAllowed(context)) return Results.Unauthorized();
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

app.MapPost("/api/v1/setup/tr-config/sites", async (SetupTrConfigSitesRequest request, SetupTrConfigBuilderService builder, HttpContext context, AuthService authService) =>
{
    if (!authService.IsWriteAllowed(context)) return Results.Unauthorized();
    try
    {
        return Results.Ok(await builder.ListSitesAsync(request, context.RequestAborted));
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
})
.WithName("SetupTrConfigSites")
.WithOpenApi();

app.MapPost("/api/v1/setup/tr-config/source-plan", async (SetupTrConfigSourcePlanRequest request, SetupTrConfigBuilderService builder, HttpContext context, AuthService authService) =>
{
    if (!authService.IsWriteAllowed(context)) return Results.Unauthorized();
    try
    {
        return Results.Ok(await builder.SourcePlanAsync(request, context.RequestAborted));
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
})
.WithName("SetupTrConfigSourcePlan")
.WithOpenApi();

app.MapPost("/api/v1/setup/tr-config/save", async (SetupTrConfigSaveRequest request, SetupTrConfigBuilderService builder, HttpContext context, AuthService authService) =>
{
    if (!authService.IsWriteAllowed(context)) return Results.Unauthorized();
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

app.MapPost("/api/v1/setup/tr-config/patch-callstream", async (SetupTrConfigPatchRequest request, TrConfigService trConfig, HttpContext context, AuthService authService, RfSurveyService surveys) =>
{
    if (!authService.IsWriteAllowed(context)) return Results.Unauthorized();
    try
    {
        if (request.RestartTr)
            await surveys.StopActiveWaterfallsBeforeTrStartAsync(context.RequestAborted);
        return Results.Ok(await trConfig.PatchCallstreamAsync(request, context.RequestAborted));
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
})
.WithName("SetupTrConfigPatchCallstream")
.WithOpenApi();

app.MapGet("/api/v1/system/tr-config/editor", async (HttpContext context, AuthService authService, TrConfigService trConfig) =>
{
    if (!authService.IsReadAllowed(context)) return Results.Unauthorized();
    return Results.Ok(await trConfig.GetEditorAsync(context.RequestAborted));
})
.WithName("SystemTrConfigEditor")
.WithOpenApi();

app.MapPost("/api/v1/system/tr-config/editor/draft", async (TrConfigEditorSaveRequest request, HttpContext context, AuthService authService, TrConfigService trConfig) =>
{
    if (!authService.IsWriteAllowed(context)) return Results.Unauthorized();
    try
    {
        return Results.Ok(await trConfig.SaveEditorDraftAsync(request, context.RequestAborted));
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
})
.WithName("SystemTrConfigEditorDraft")
.WithOpenApi();

app.MapPost("/api/v1/system/tr-config/editor/apply", async (TrConfigEditorSaveRequest request, HttpContext context, AuthService authService, TrConfigService trConfig, SetupTrConfigBuilderService builder, SetupJobService jobs, RfSurveyService surveys) =>
{
    if (!authService.IsWriteAllowed(context)) return Results.Unauthorized();
    try
    {
        var configJson = await trConfig.GetEditorConfigForApplyAsync(request.ConfigJson, context.RequestAborted);
        using var _ = JsonDocument.Parse(configJson);
        var save = await builder.SaveAsync(new SetupTrConfigSaveRequest(configJson), context.RequestAborted);
        if (!save.Ok)
            return Results.Ok(new { ok = false, message = save.Message, save, restartJob = (JobDto?)null, editor = await trConfig.GetEditorAsync(context.RequestAborted) });
        await trConfig.ClearEditorDraftAsync(context.RequestAborted);
        await surveys.StopActiveWaterfallsBeforeTrStartAsync(context.RequestAborted);
        var restartJob = await jobs.StartAsync("restart-tr", confirmed: true, parameters: null, context.RequestAborted);
        return Results.Ok(new { ok = true, message = "Saved TR config and queued trunk-recorder restart.", save, restartJob, editor = await trConfig.GetEditorAsync(context.RequestAborted) });
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
.WithName("SystemTrConfigEditorApply")
.WithOpenApi();

app.MapGet("/api/v1/system/tr-config/backups", (HttpContext context, AuthService authService, TrConfigService trConfig) =>
{
    if (!authService.IsReadAllowed(context)) return Results.Unauthorized();
    return Results.Ok(trConfig.ListConfigBackups());
})
.WithName("SystemTrConfigBackups")
.WithOpenApi();

app.MapPost("/api/v1/system/tr-config/restore", async (TrConfigRestoreRequest request, HttpContext context, AuthService authService, TrConfigService trConfig, RfSurveyService surveys) =>
{
    if (!authService.IsWriteAllowed(context)) return Results.Unauthorized();
    if (request.RestartTr)
        await surveys.StopActiveWaterfallsBeforeTrStartAsync(context.RequestAborted);
    return Results.Ok(await trConfig.RestoreConfigBackupAsync(request, context.RequestAborted));
})
.WithName("SystemTrConfigRestore")
.WithOpenApi();

app.MapGet("/api/v1/status", async (HttpContext context, long? start, long? end, AuthService authService, DashboardService dashboard) =>
{
    if (!authService.IsReadAllowed(context)) return Results.Unauthorized();
    var range = new TimeRangeQuery(start, end).Resolve();
    return Results.Ok(await dashboard.BuildStatusSummaryAsync(range.Start, range.End, context.RequestAborted));
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

app.MapGet("/api/v1/categories/{category}", async (HttpContext context, string category, string? groupBy, string? q, long? start, long? end, AuthService authService, DashboardService dashboard) =>
{
    if (!authService.IsReadAllowed(context)) return Results.Unauthorized();
    var range = new TimeRangeQuery(start, end).Resolve();
    return Results.Ok(await dashboard.BuildCategoryPageAsync(category, groupBy ?? "time", range.Start, range.End, q ?? string.Empty, context.RequestAborted));
})
.WithName("Category")
.WithOpenApi();

app.MapGet("/api/v1/categories/{category}/talkgroups/{talkgroup:long}/calls", async (HttpContext context, string category, long talkgroup, int? limit, long? start, long? end, AuthService authService, DashboardService dashboard) =>
{
    if (!authService.IsReadAllowed(context)) return Results.Unauthorized();
    var range = new TimeRangeQuery(start, end).Resolve();
    return Results.Ok(await dashboard.BuildCategoryTalkgroupCallsAsync(category, talkgroup.ToString(CultureInfo.InvariantCulture), range.Start, range.End, limit ?? 100, context.RequestAborted));
})
.WithName("CategoryTalkgroupCalls")
.WithOpenApi();

app.MapGet("/api/v1/categories/{category}/talkgroup-keys/{talkgroupKey}/calls", async (HttpContext context, string category, string talkgroupKey, int? limit, long? start, long? end, AuthService authService, DashboardService dashboard) =>
{
    if (!authService.IsReadAllowed(context)) return Results.Unauthorized();
    var range = new TimeRangeQuery(start, end).Resolve();
    return Results.Ok(await dashboard.BuildCategoryTalkgroupCallsAsync(category, Uri.UnescapeDataString(talkgroupKey), range.Start, range.End, limit ?? 100, context.RequestAborted));
})
.WithName("CategoryTalkgroupKeyCalls")
.WithOpenApi();

app.MapGet("/api/v1/calls/{id:long}", async (HttpContext context, long id, AuthService authService, EngineDatabase database, EngineConfig cfg, TalkgroupCatalogService catalog) =>
{
    if (!authService.IsReadAllowed(context)) return Results.Unauthorized();
    var call = await database.GetCallAsync(id, context.RequestAborted);
    if (call != null && !DownstreamProfilePolicy.Allows(cfg, catalog, call)) return Results.NotFound();
    return call == null ? Results.NotFound() : Results.Ok(call);
})
.WithName("CallById")
.WithOpenApi();

app.MapGet("/api/v1/calls/{id:long}/audio", async (HttpContext context, long id, AuthService authService, EngineDatabase database, EngineConfig cfg, TalkgroupCatalogService catalog) =>
{
    if (!authService.IsReadAllowed(context)) return Results.Unauthorized();
    var call = await database.GetCallAsync(id, context.RequestAborted);
    if (call != null && !DownstreamProfilePolicy.Allows(cfg, catalog, call)) return Results.NotFound();
    if (call == null || string.IsNullOrWhiteSpace(call.AudioPath)) return Results.NotFound();
    var path = Path.GetFullPath(Path.Combine(cfg.Storage.AudioRoot, call.AudioPath));
    var root = Path.GetFullPath(cfg.Storage.AudioRoot);
    if (!path.StartsWith(root, StringComparison.OrdinalIgnoreCase) || !File.Exists(path)) return Results.NotFound();
    return Results.File(path, "audio/wav", enableRangeProcessing: true);
})
.WithName("CallAudio")
.WithOpenApi();

app.MapGet("/api/v1/alerts", async (HttpContext context, long? start, long? end, AuthService authService, DashboardService dashboard) =>
{
    if (!authService.IsReadAllowed(context)) return Results.Unauthorized();
    var range = new TimeRangeQuery(start, end).Resolve();
    return Results.Ok(await dashboard.ListAlertMatchesAsync(range.Start, range.End, context.RequestAborted));
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

app.MapPost("/api/v1/incidents/{id:long}/alerts/dismiss", async (HttpContext context, long id, AuthService authService, EngineDatabase database, DashboardService dashboard, EventStream events) =>
{
    if (!authService.IsReadAllowed(context)) return Results.Unauthorized();
    var dismissed = await database.DismissIncidentAlertsAsync(id, context.RequestAborted);
    await dashboard.InvalidateCacheAsync(context.RequestAborted);
    await events.PublishAsync("alert_matched", new { incidentId = id, dismissed }, context.RequestAborted);
    return Results.Ok(new { id, dismissed });
})
.WithName("IncidentAlertsDismiss")
.WithOpenApi();

app.MapPost("/api/v1/alerts/{id:long}/dismiss", async (HttpContext context, long id, AuthService authService, EngineDatabase database, DashboardService dashboard, EventStream events) =>
{
    if (!authService.IsReadAllowed(context)) return Results.Unauthorized();
    var dismissed = await database.DismissAlertMatchAsync(id, context.RequestAborted);
    await dashboard.InvalidateCacheAsync(context.RequestAborted);
    await events.PublishAsync("alert_matched", new { alertId = id, dismissed }, context.RequestAborted);
    return Results.Ok(new { id, dismissed });
})
.WithName("AlertMatchDismiss")
.WithOpenApi();

app.MapGet("/api/v1/incidents/audit", async (HttpContext context, int? hours, int? limit, AuthService authService, EngineDatabase database) =>
{
    if (!authService.IsReadAllowed(context)) return Results.Unauthorized();
    var since = DateTime.UtcNow.AddHours(-Math.Clamp(hours ?? 6, 1, 168));
    return Results.Ok(await database.ListIncidentOperationAuditAsync(since, limit ?? 80, context.RequestAborted));
})
.WithName("IncidentOperationAudit")
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

app.MapGet("/api/v1/troubleshoot/rf-analysis", async (HttpContext context, string? system, long? start, long? end, AuthService authService, TrHealthTroubleshootService troubleshoot) =>
{
    if (!authService.IsReadAllowed(context)) return Results.Unauthorized();
    var range = new TimeRangeQuery(start, end).Resolve();
    return Results.Ok(await troubleshoot.BuildRfAnalysisAsync(system, range.Start, range.End, context.RequestAborted));
})
.WithName("TrRfAnalysis")
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

app.MapGet("/api/v1/jobs", async (HttpContext context, AuthService authService, EngineDatabase database) =>
{
    if (!authService.IsReadAllowed(context)) return Results.Unauthorized();
    await database.PruneJobsOlderThanAsync(DateTime.UtcNow.AddDays(-30), context.RequestAborted);
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

app.MapGet("/api/v1/system/remote-bandwidth", async (HttpContext context, long? start, long? end, AuthService authService, RemoteBandwidthEstimatorService bandwidth) =>
{
    if (!authService.IsReadAllowed(context)) return Results.Unauthorized();
    var range = new TimeRangeQuery(start, end).Resolve();
    return Results.Ok(await bandwidth.BuildReportAsync(range.Start, range.End, context.RequestAborted));
})
.WithName("RemoteBandwidth")
.WithOpenApi();

app.MapGet("/api/v1/system/quality-check", async (HttpContext context, long? start, long? end, int? hours, AuthService authService, EngineDatabase database) =>
{
    if (!authService.IsReadAllowed(context)) return Results.Unauthorized();
    var range = ResolveQualityCheckRange(start, end, hours);
    return Results.Ok(await database.GetQualityCheckSnapshotAsync(range.Start, range.End, context.RequestAborted));
})
.WithName("QualityCheck")
.WithOpenApi();

app.MapGet("/api/v1/system/runtime", async (HttpContext context, AuthService authService, SystemManagerService system) =>
{
    if (!authService.IsReadAllowed(context)) return Results.Unauthorized();
    return Results.Ok(await system.BuildAsync(context.RequestAborted));
})
.WithName("SystemRuntime")
.WithOpenApi();

app.MapGet("/api/v1/system/backups", (HttpContext context, AuthService authService, BackupRestoreService backups) =>
{
    if (!authService.IsReadAllowed(context)) return Results.Unauthorized();
    return Results.Ok(backups.ListBackups());
})
.WithName("SystemBackupsList")
.WithOpenApi();

app.MapPost("/api/v1/system/backups", async (HttpContext context, BackupCreateRequestDto request, AuthService authService, BackupRestoreService backups) =>
{
    if (!authService.IsWriteAllowed(context)) return Results.Unauthorized();
    return Results.Ok(await backups.CreateBackupAsync(request, context.RequestAborted));
})
.WithName("SystemBackupsCreate")
.WithOpenApi();

app.MapGet("/api/v1/system/backups/estimate", (HttpContext context, string? audioWindow, AuthService authService, BackupRestoreService backups) =>
{
    if (!authService.IsReadAllowed(context)) return Results.Unauthorized();
    return Results.Ok(backups.EstimateBackup(new BackupCreateRequestDto(audioWindow)));
})
.WithName("SystemBackupsEstimate")
.WithOpenApi();

app.MapGet("/api/v1/system/backups/{name}", (HttpContext context, string name, AuthService authService, BackupRestoreService backups) =>
{
    if (!authService.IsReadAllowed(context)) return Results.Unauthorized();
    var row = backups.ListBackups().FirstOrDefault(b => string.Equals(b.Name, name, StringComparison.OrdinalIgnoreCase));
    return row == null ? Results.NotFound() : Results.File(row.Path, "application/zip", row.Name);
})
.WithName("SystemBackupsDownload")
.WithOpenApi();

app.MapDelete("/api/v1/system/backups/{name}", (HttpContext context, string name, AuthService authService, BackupRestoreService backups) =>
{
    if (!authService.IsWriteAllowed(context)) return Results.Unauthorized();
    return backups.DeleteBackup(name) ? Results.Ok(new { deleted = name }) : Results.NotFound();
})
.WithName("SystemBackupsDelete")
.WithOpenApi();

app.MapPost("/api/v1/system/backups/{name}/restore", async (HttpContext context, string name, AuthService authService, BackupRestoreService backups) =>
{
    if (!authService.IsWriteAllowed(context)) return Results.Unauthorized();
    var preview = await backups.StageLocalRestoreAsync(name, context.RequestAborted);
    return preview == null ? Results.NotFound() : Results.Ok(preview);
})
.WithName("SystemBackupsStageLocalRestore")
.WithOpenApi();

app.MapPost("/api/v1/system/backups/restore", async (HttpContext context, AuthService authService, BackupRestoreService backups) =>
{
    if (!authService.IsWriteAllowed(context)) return Results.Unauthorized();
    if (!context.Request.HasFormContentType)
        return Results.BadRequest(new { error = "Restore upload must be multipart/form-data with a file field." });
    var form = await context.Request.ReadFormAsync(context.RequestAborted);
    var file = form.Files.GetFile("file") ?? form.Files.FirstOrDefault();
    if (file == null || file.Length == 0)
        return Results.BadRequest(new { error = "Backup file is required." });
    await using var stream = file.OpenReadStream();
    return Results.Ok(await backups.StageRestoreAsync(stream, file.FileName, context.RequestAborted));
})
.WithName("SystemBackupsStageRestore")
.WithOpenApi();

app.MapPost("/api/v1/system/migration/begin", async (HttpContext context, AuthService authService, MigrationService migration) =>
{
    if (!authService.IsWriteAllowed(context)) return Results.Unauthorized();
    return Results.Ok(await migration.BeginAsync(context.RequestAborted));
})
.WithName("SystemMigrationBegin")
.WithOpenApi();

app.MapGet("/api/v1/system/migration/profile", (HttpContext context, AuthService authService, MigrationService migration) =>
{
    if (!authService.IsReadAllowed(context)) return Results.Unauthorized();
    var profile = migration.ExportProfile();
    var fileName = $"pizzawave-migration-profile-{DateTime.UtcNow:yyyyMMddTHHmmssZ}.json";
    return Results.File(JsonSerializer.SerializeToUtf8Bytes(profile, EngineConfig.JsonOptions()), "application/json", fileName);
})
.WithName("SystemMigrationProfileExport")
.WithOpenApi();

app.MapPost("/api/v1/setup/migration/profile", async (HttpContext context, AuthService authService, MigrationService migration) =>
{
    if (!authService.IsWriteAllowed(context)) return Results.Unauthorized();
    var profile = await JsonSerializer.DeserializeAsync<MigrationProfileDto>(context.Request.Body, EngineConfig.JsonOptions(), context.RequestAborted);
    if (profile == null) return Results.BadRequest(new { message = "Migration profile JSON is required." });
    try
    {
        return Results.Ok(await migration.ImportProfileAsync(profile, context.RequestAborted));
    }
    catch (InvalidOperationException ex)
    {
        return Results.BadRequest(new { message = ex.Message });
    }
})
.WithName("SetupMigrationProfileImport")
.WithOpenApi();

app.MapPost("/api/v1/setup/migration/reset-new-site", async (HttpContext context, AuthService authService, MigrationService migration) =>
{
    if (!authService.IsWriteAllowed(context)) return Results.Unauthorized();
    try
    {
        var request = await ReadMigrationResetRequestAsync(context);
        return Results.Ok(await migration.ResetForNewSiteAsync(request, context.RequestAborted));
    }
    catch (InvalidOperationException ex)
    {
        return Results.BadRequest(new { message = ex.Message });
    }
})
.WithName("SetupMigrationResetNewSite")
.WithOpenApi();

app.MapPost("/api/v1/setup/migration/cancel", async (HttpContext context, AuthService authService, MigrationService migration) =>
{
    if (!authService.IsWriteAllowed(context)) return Results.Unauthorized();
    try
    {
        return Results.Ok(await migration.CancelAsync(context.RequestAborted));
    }
    catch (InvalidOperationException ex)
    {
        return Results.BadRequest(new { message = ex.Message });
    }
})
.WithName("SetupMigrationCancel")
.WithOpenApi();

app.MapGet("/api/v1/setup/restore", (HttpContext context, AuthService authService, BackupRestoreService backups) =>
{
    if (!authService.IsReadAllowed(context)) return Results.Unauthorized();
    var pending = backups.PendingRestore();
    return pending == null ? Results.NotFound() : Results.Ok(pending);
})
.WithName("SetupRestorePending")
.WithOpenApi();

app.MapPost("/api/v1/setup/restore/apply", async (HttpContext context, AuthService authService, BackupRestoreService backups) =>
{
    if (!authService.IsWriteAllowed(context)) return Results.Unauthorized();
    return Results.Ok(await backups.ApplyPendingRestoreAsync(context.RequestAborted));
})
.WithName("SetupRestoreApply")
.WithOpenApi();

app.MapPost("/api/v1/setup/restore/cancel", async (HttpContext context, AuthService authService, BackupRestoreService backups) =>
{
    if (!authService.IsWriteAllowed(context)) return Results.Unauthorized();
    return Results.Ok(await backups.CancelPendingRestoreAsync(context.RequestAborted));
})
.WithName("SetupRestoreCancel")
.WithOpenApi();

app.MapPost("/api/v1/system/storage/maintenance/{action}", async (string action, HttpContext context, AuthService authService, EngineDatabase database) =>
{
    if (!authService.IsWriteAllowed(context)) return Results.Unauthorized();
    try
    {
        switch (action.Trim().ToLowerInvariant())
        {
            case "vacuum":
                await database.VacuumAsync(context.RequestAborted);
                return Results.Ok(new { ok = true, message = "Database vacuum completed." });
            case "analyze":
                await database.AnalyzeAsync(context.RequestAborted);
                return Results.Ok(new { ok = true, message = "Database statistics optimized." });
            case "prune-jobs":
                var removed = await database.PruneJobsOlderThanAsync(DateTime.UtcNow.AddDays(-30), context.RequestAborted);
                return Results.Ok(new { ok = true, message = $"Pruned {removed:N0} old job row(s) or log row(s)." });
            default:
                return Results.BadRequest(new { message = "Unsupported storage maintenance action." });
        }
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { message = ex.Message });
    }
})
.WithName("SystemStorageMaintenance")
.WithOpenApi();

app.MapGet("/api/v1/system/recommendations", async (HttpContext context, AuthService authService, SystemRecommendationService recommendations) =>
{
    if (!authService.IsReadAllowed(context)) return Results.Unauthorized();
    return Results.Ok(await recommendations.BuildAsync(context.RequestAborted));
})
.WithName("SystemRecommendations")
.WithOpenApi();

app.MapGet("/api/v1/system/cpu", async (HttpContext context, AuthService authService, SystemCpuSnapshotService cpu) =>
{
    if (!authService.IsReadAllowed(context)) return Results.Unauthorized();
    return Results.Ok(await cpu.BuildAsync(context.RequestAborted));
})
.WithName("SystemCpu")
.WithOpenApi();

app.MapPost("/api/v1/system/recommendations/{id}/state", async (string id, RecommendationStateRequest request, HttpContext context, AuthService authService, SystemRecommendationService recommendations) =>
{
    if (!authService.IsWriteAllowed(context)) return Results.Unauthorized();
    try
    {
        await recommendations.SetStateAsync(id, request.Action, context.RequestAborted);
        return Results.Ok(await recommendations.BuildAsync(context.RequestAborted));
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { message = ex.Message });
    }
})
.WithName("SystemRecommendationState")
.WithOpenApi();

app.MapGet("/api/v1/system/queue", async (HttpContext context, AuthService authService, EngineConfig cfg, EnginePipeline pipeline, EngineDatabase database, IngestControlService ingestControl, EmbeddingService embeddings) =>
{
    if (!authService.IsReadAllowed(context)) return Results.Unauthorized();
    const int throughputWindowMinutes = 10;
    var now = DateTime.UtcNow;
    var recentStartUnix = new DateTimeOffset(now.AddMinutes(-throughputWindowMinutes)).ToUnixTimeSeconds();
    var pendingTranscriptions = await database.CountPendingTranscriptionCallsAsync(context.RequestAborted);
    var recentCalls = await database.CountCallsStartedSinceAsync(recentStartUnix, context.RequestAborted);
    var recentTranscribed = await database.CountTranscriptionCompletionsSinceAsync(now.AddMinutes(-throughputWindowMinutes), context.RequestAborted);
    var recentAudioIngested = await database.SumAudioSecondsStartedSinceAsync(recentStartUnix, context.RequestAborted);
    var recentAudioTranscribed = await database.SumAudioSecondsTranscriptionCompletionsSinceAsync(now.AddMinutes(-throughputWindowMinutes), context.RequestAborted);
    var pendingAudioSeconds = await database.SumPendingTranscriptionAudioSecondsAsync(context.RequestAborted);
    var performance = pipeline.GetTranscriptionPerformance(TimeSpan.FromMinutes(throughputWindowMinutes));
    var aiQueueLimit = cfg.AiInsights.MaxQueueDepthForManualSummary;
    var aiBlockedReason = aiQueueLimit > 0 && pipeline.QueueDepth > aiQueueLimit
        ? $"AI summary generation is paused while transcription queue depth is above the configured limit. Queue depth: {pipeline.QueueDepth:N0}; limit: {aiQueueLimit:N0}."
        : null;
    var aiCompletionHealth = await database.GetAiCompletionHealthAsync(30, context.RequestAborted);
    var embeddingHealth = await embeddings.GetHealthAsync(context.RequestAborted);
    var pendingCalls = (await database.ListPendingTranscriptionCallsAsync(25, context.RequestAborted))
        .Select(call => new QueuePendingCallDto(
            call.Id,
            call.StartTime,
            call.SystemShortName,
            call.Talkgroup,
            call.TalkgroupName,
            call.Category,
            call.IsImported,
            call.AudioPath))
        .ToList();
    var topAudioTalkgroups = await database.ListTopAudioTalkgroupsAsync(recentStartUnix, 20, context.RequestAborted);
    return Results.Ok(new QueueSnapshotDto(
        pipeline.QueueDepth,
        pipeline.LiveQueueDepth,
        pipeline.PriorityLiveQueueDepth,
        pipeline.BacklogQueueDepth,
        pipeline.IsUnderLivePressure,
        pipeline.LivePressureQueueDepth,
        pendingTranscriptions,
        pipeline.LiveTranscriptionWorkerCount,
        pipeline.WhisperThreadsPerWorker,
        throughputWindowMinutes,
        pipeline.DeferredLiveQueueDepth,
        recentCalls,
        recentTranscribed,
        recentCalls / (double)throughputWindowMinutes,
        recentTranscribed / (double)throughputWindowMinutes,
        recentAudioIngested,
        recentAudioTranscribed,
        recentAudioIngested / (double)throughputWindowMinutes,
        recentAudioTranscribed / (double)throughputWindowMinutes,
        pendingAudioSeconds,
        performance.Count,
        performance.AverageWallSeconds,
        performance.AverageAudioSeconds,
        performance.AverageRealtimeFactor,
        ingestControl.GetStatus(pipeline.QueueDepth),
        aiBlockedReason,
        aiCompletionHealth,
        embeddingHealth,
        pendingCalls,
        topAudioTalkgroups));
})
.WithName("SystemQueue")
.WithOpenApi();

app.MapPost("/api/v1/system/services/{service}/restart", async (string service, HttpContext context, AuthService authService, SetupJobService jobs, RfSurveyService surveys) =>
{
    if (!authService.IsWriteAllowed(context)) return Results.Unauthorized();
    var action = service.Trim().ToLowerInvariant() switch
    {
        "pizzad" => "restart-pizzad",
        "tr" or "trunk-recorder" or "trunkrecorder" => "restart-tr",
        "qdrant" or "vector-db" or "vectordb" => "restart-qdrant",
        _ => string.Empty
    };
    if (string.IsNullOrWhiteSpace(action))
        return Results.BadRequest(new { message = "Unsupported service restart target." });
    try
    {
        if (action == "restart-tr")
            await surveys.StopActiveWaterfallsBeforeTrStartAsync(context.RequestAborted);
        return Results.Ok(await jobs.StartAsync(action, confirmed: true, parameters: null, context.RequestAborted));
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { message = ex.Message });
    }
})
.WithName("SystemServiceRestart")
.WithOpenApi();

app.MapPost("/api/v1/system/services/{service}/stop", async (string service, HttpContext context, AuthService authService, SetupJobService jobs) =>
{
    if (!authService.IsWriteAllowed(context)) return Results.Unauthorized();
    var action = service.Trim().ToLowerInvariant() switch
    {
        "tr" or "trunk-recorder" or "trunkrecorder" => "stop-tr",
        _ => string.Empty
    };
    if (string.IsNullOrWhiteSpace(action))
        return Results.BadRequest(new { message = "Unsupported service stop target." });
    try
    {
        return Results.Ok(await jobs.StartAsync(action, confirmed: true, parameters: null, context.RequestAborted));
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { message = ex.Message });
    }
})
.WithName("SystemServiceStop")
.WithOpenApi();

app.MapPost("/api/v1/system/ingest", (HttpContext context, IngestControlRequest request, AuthService authService, IngestControlService ingestControl, EnginePipeline pipeline) =>
{
    if (!authService.IsWriteAllowed(context)) return Results.Unauthorized();
    var status = request.Pause
        ? ingestControl.Pause(request.UntilQueueClear, request.Reason, pipeline.QueueDepth)
        : ingestControl.Resume("resumed by user", pipeline.QueueDepth);
    return Results.Ok(status);
})
.WithName("SystemIngestControl")
.WithOpenApi();

app.MapGet("/api/v1/profiles", (HttpContext context, AuthService authService, EngineConfig cfg) =>
{
    if (!authService.IsReadAllowed(context)) return Results.Unauthorized();
    return Results.Ok(new ProfileStateDto(cfg.Profiles.ActiveProfileId, cfg.Profiles.Items));
})
.WithName("ProfilesRead")
.WithOpenApi();

app.MapPost("/api/v1/profiles", async (HttpContext context, SaveProfilesRequest request, AuthService authService, EngineConfig cfg) =>
{
    if (!authService.IsWriteAllowed(context)) return Results.Unauthorized();
    var profiles = request.Profiles?.ToList() ?? [];
    if (profiles.Count == 0)
        profiles.Add(new ProcessingProfile { Name = "Default" });
    if (!profiles.Any(IsDefaultProfile))
        profiles.Insert(0, new ProcessingProfile { Name = "Default" });
    foreach (var profile in profiles)
    {
        if (profile.Id == Guid.Empty) profile.Id = Guid.NewGuid();
        profile.Name = string.IsNullOrWhiteSpace(profile.Name) ? "Profile" : profile.Name.Trim();
        if (IsDefaultProfile(profile))
        {
            NormalizeDefaultProfile(profile);
            continue;
        }
        profile.AllowedTalkgroups ??= new();
        profile.Talkgroups ??= new();
        profile.Talkgroups = profile.Talkgroups
            .Where(t => t.Id > 0)
            .Select(t =>
            {
                t.SystemShortName = TalkgroupCatalogService.SystemFromKeyOrValue(t.Key, t.SystemShortName, t.Id);
                t.Key = TalkgroupCatalogService.CatalogKey(t.SystemShortName, t.Id);
                return t;
            })
            .GroupBy(TalkgroupCatalogService.SettingKey, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.Last())
            .ToList();
        profile.UpdatedAtUtc = DateTime.UtcNow;
    }
    var candidate = cfg.Clone();
    candidate.Profiles.Items = profiles;
    candidate.Profiles.ActiveProfileId = profiles.Any(p => p.Id == request.ActiveProfileId) ? request.ActiveProfileId : profiles[0].Id;
    candidate.ApplyDefaults();
    try
    {
        await SaveConfigAsync(candidate, context.RequestAborted);
        cfg.Profiles = candidate.Profiles;
        cfg.ApplyDefaults();
    }
    catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or InvalidOperationException)
    {
        return Results.BadRequest(new { error = $"Unable to save profiles to {cfg.ConfigPath}.", detail = ex.Message });
    }

    return Results.Ok(new ProfileStateDto(cfg.Profiles.ActiveProfileId, cfg.Profiles.Items, false, string.Empty, "Profile saved. Profile changes now affect dashboard filters, alerts, embeddings, and incident creation without restarting services."));
})
.WithName("ProfilesSave")
.WithOpenApi();

app.MapPost("/api/v1/profiles/active", async (HttpContext context, SetActiveProfileRequest request, AuthService authService, EngineConfig cfg) =>
{
    if (!authService.IsReadAllowed(context)) return Results.Unauthorized();
    var profiles = cfg.Profiles.Items?.ToList() ?? [];
    var active = profiles.FirstOrDefault(p => p.Id == request.ActiveProfileId);
    if (active == null)
        return Results.BadRequest(new { error = "Unknown profile." });
    if (cfg.Profiles.ActiveProfileId == request.ActiveProfileId)
        return Results.Ok(new ProfileStateDto(cfg.Profiles.ActiveProfileId, profiles, false, string.Empty, $"Profile already active: {active.Name}."));

    var candidate = cfg.Clone();
    candidate.Profiles.ActiveProfileId = request.ActiveProfileId;
    candidate.ApplyDefaults();
    try
    {
        await SaveConfigAsync(candidate, context.RequestAborted);
        cfg.Profiles = candidate.Profiles;
        cfg.ApplyDefaults();
    }
    catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or InvalidOperationException)
    {
        return Results.BadRequest(new { error = $"Unable to save active profile to {cfg.ConfigPath}.", detail = ex.Message });
    }

    var selected = cfg.Profiles.Items.FirstOrDefault(p => p.Id == cfg.Profiles.ActiveProfileId);
    return Results.Ok(new ProfileStateDto(cfg.Profiles.ActiveProfileId, cfg.Profiles.Items ?? [], false, string.Empty, $"Profile active: {selected?.Name ?? active.Name}."));
})
.WithName("ProfilesSetActive")
.WithOpenApi();

app.MapPost("/api/v1/profiles/{profileId:guid}/talkgroups/hide", async (HttpContext context, Guid profileId, HideProfileTalkgroupsRequest request, AuthService authService, EngineConfig cfg) =>
{
    if (!authService.IsReadAllowed(context)) return Results.Unauthorized();
    var profiles = cfg.Profiles.Items?.ToList() ?? [];
    var target = profiles.FirstOrDefault(p => p.Id == profileId);
    if (target == null)
        return Results.BadRequest(new { error = "Unknown profile." });
    if (IsDefaultProfile(target))
        return Results.BadRequest(new { error = "Default profile is read-only. Create or select another profile before hiding talkgroups." });

    var settings = (request.Talkgroups ?? [])
        .Where(t => t.Id > 0)
        .Select(t =>
        {
            var system = TalkgroupCatalogService.SystemFromKeyOrValue(t.Key, t.SystemShortName, t.Id);
            return new ProfileTalkgroupSetting
            {
                Key = TalkgroupCatalogService.CatalogKey(system, t.Id),
                SystemShortName = system,
                Id = t.Id,
                Enabled = false,
                Label = (t.Label ?? string.Empty).Trim(),
                Category = TalkgroupCatalogService.NormalizeOpsCategory(t.Category),
                IncidentEligible = t.IncidentEligible
            };
        })
        .GroupBy(TalkgroupCatalogService.SettingKey, StringComparer.OrdinalIgnoreCase)
        .Select(g => g.Last())
        .ToList();
    if (settings.Count == 0)
        return Results.BadRequest(new { error = "At least one talkgroup is required." });

    var settingKeys = settings.Select(TalkgroupCatalogService.SettingKey).ToHashSet(StringComparer.OrdinalIgnoreCase);
    var candidate = cfg.Clone();
    var candidateTarget = candidate.Profiles.Items.FirstOrDefault(p => p.Id == profileId);
    if (candidateTarget == null)
        return Results.BadRequest(new { error = "Unknown profile." });
    candidateTarget.Talkgroups = (candidateTarget.Talkgroups ?? [])
        .Where(t => !settingKeys.Contains(TalkgroupCatalogService.SettingKey(t)))
        .Concat(settings)
        .ToList();
    candidateTarget.UpdatedAtUtc = DateTime.UtcNow;
    candidate.ApplyDefaults();
    try
    {
        await SaveConfigAsync(candidate, context.RequestAborted);
        cfg.Profiles = candidate.Profiles;
        cfg.ApplyDefaults();
    }
    catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or InvalidOperationException)
    {
        return Results.BadRequest(new { error = $"Unable to save profile rules to {cfg.ConfigPath}.", detail = ex.Message });
    }

    return Results.Ok(new ProfileStateDto(cfg.Profiles.ActiveProfileId, cfg.Profiles.Items ?? [], false, string.Empty, $"{settings.Count:N0} talkgroup rule(s) hidden in {candidateTarget.Name}."));
})
.WithName("ProfilesHideTalkgroups")
.WithOpenApi();

app.MapGet("/api/v1/talkgroups", (HttpContext context, AuthService authService, TalkgroupResolver talkgroups) =>
{
    if (!authService.IsReadAllowed(context))
        return Results.Unauthorized();
    return Results.Ok(talkgroups.ListOptions());
})
.WithName("TalkgroupsList")
.WithOpenApi();

app.MapGet("/api/v1/talkgroups/catalog", (HttpContext context, AuthService authService, TalkgroupCatalogService catalog, EngineConfig cfg) =>
{
    if (!authService.IsReadAllowed(context))
        return Results.Unauthorized();
    var document = catalog.Load();
    return Results.Ok(new
    {
        catalogPath = cfg.TrunkRecorder.TalkgroupCatalogPath,
        generatedCsvPath = cfg.TrunkRecorder.TalkgroupsPath,
        activeProfileId = cfg.Profiles.ActiveProfileId,
        trRestartRecommended = false,
        warning = "Talkgroup catalog changes affect new calls only. Existing calls keep their stored label and category.",
        document,
        effectiveItems = catalog.EffectiveItemsForActiveProfile(document)
    });
})
.WithName("TalkgroupCatalogRead")
.WithOpenApi();

app.MapPut("/api/v1/talkgroups/catalog", async (HttpContext context, TalkgroupCatalogDocument document, AuthService authService, TalkgroupCatalogService catalog) =>
{
    if (!authService.IsWriteAllowed(context))
        return Results.Unauthorized();
    try
    {
        return Results.Ok(await catalog.SaveAsync(document, generateTrCsv: true, context.RequestAborted));
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { message = ex.Message });
    }
})
.WithName("TalkgroupCatalogReplace")
.WithOpenApi();

app.MapPost("/api/v1/jobs/{id:long}/control", async (HttpContext context, long id, JobControlRequest request, AuthService authService, EngineDatabase database) =>
{
    if (!authService.IsWriteAllowed(context)) return Results.Unauthorized();
    var job = await database.GetJobAsync(id, context.RequestAborted);
    if (job == null) return Results.NotFound();
    try
    {
        JobDto? updated = job.Type switch
        {
            "sftp_import" or "local_import" => throw new InvalidOperationException("Historical import jobs are no longer supported from the web application."),
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

app.MapGet("/api/v1/settings/{section}", (HttpContext context, string section, AuthService authService, EngineConfig cfg, CredentialStore credentials) =>
{
    if (!authService.IsReadAllowed(context)) return Results.Unauthorized();
    object values = section.ToLowerInvariant() switch
    {
        "engine" => new { cfg.Branding, cfg.Server, cfg.Storage, cfg.Ingest },
        "transcription" => cfg.Transcription,
        "ai-insights" => cfg.AiInsights,
        "embeddings" => cfg.Embeddings,
        "rf-survey" => cfg.RfSurvey,
        "tr" => cfg.TrunkRecorder,
        "profiles" => cfg.Profiles,
        "locations" => cfg.Locations,
        "auth" => new { cfg.Auth.Mode, cfg.Auth.ReadRequiresAuth, cfg.Auth.WriteRequiresAuth, cfg.Auth.TokenFile },
        "alerts" => credentials.SanitizeAlertsForClient(cfg.Alerts),
        _ => new { }
    };
    return Results.Ok(new SettingsSectionDto(section, values));
})
.WithName("SettingsRead")
.WithOpenApi();

app.MapPost("/api/v1/settings/{section}", async (HttpContext context, string section, SaveSettingsRequest request, AuthService authService, EngineConfig cfg, CredentialStore credentials) =>
{
    if (!authService.IsWriteAllowed(context)) return Results.Unauthorized();
    section = section.Trim().ToLowerInvariant();
    var candidate = cfg.Clone();
    if (!ApplySettingsSection(candidate, section, request.Values, credentials, persistSecrets: true))
        return Results.BadRequest("Unknown settings section.");
    if (section == "transcription" &&
        string.Equals(candidate.Transcription.Provider, "none", StringComparison.OrdinalIgnoreCase))
        return Results.BadRequest(new { error = "Transcription is required. Choose and configure a transcription engine before saving." });

    try
    {
        await SaveConfigAsync(candidate, context.RequestAborted);
        ApplySettingsSection(cfg, section, request.Values, credentials, persistSecrets: false);
        cfg.ApplyDefaults();
        if (string.Equals(section, "auth", StringComparison.OrdinalIgnoreCase) &&
            string.Equals(cfg.Auth.Mode, "token", StringComparison.OrdinalIgnoreCase))
            authService.EnsureToken();
    }
    catch (UnauthorizedAccessException ex)
    {
        return Results.BadRequest(new
        {
            error = $"Unable to save settings because {cfg.ConfigPath} is not writable by the pizzad service user.",
            detail = ex.Message
        });
    }
    catch (IOException ex)
    {
        return Results.BadRequest(new
        {
            error = $"Unable to save settings to {cfg.ConfigPath}.",
            detail = ex.Message
        });
    }
    catch (InvalidOperationException ex)
    {
        return Results.BadRequest(new
        {
            error = $"Unable to save settings to {cfg.ConfigPath}.",
            detail = ex.Message
        });
    }

    var restartRequired = SettingsRestartRequired(section);
    return Results.Ok(new { saved = section, restartRequired, message = restartRequired ? "Settings saved. Restart pizzad for all changes in this section to take effect." : "Settings saved." });
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
        return Results.Ok(await validation.StartModelDownloadAsync(model, context.RequestAborted));
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
    var tokenFile = authService.RegenerateToken();
    return Results.Ok(new { tokenFile, token = authService.CurrentToken() });
})
.WithName("RegenerateToken")
.WithOpenApi();

app.MapGet("/api/v1/settings/auth/token", (HttpContext context, AuthService authService, EngineConfig cfg) =>
{
    if (!authService.IsReadAllowed(context)) return Results.Unauthorized();
    return Results.Ok(new { tokenFile = cfg.Auth.TokenFile, token = authService.CurrentToken() });
})
.WithName("ReadAuthToken")
.WithOpenApi();

app.Map("/api/{**path}", (string? path) => Results.NotFound(new { message = "API endpoint not found." }));

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

static bool ApplySettingsSection(EngineConfig cfg, string section, JsonElement values, CredentialStore? credentials = null, bool persistSecrets = false)
{
    var json = values.GetRawText();
    switch (section.Trim().ToLowerInvariant())
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
        case "embeddings":
            cfg.Embeddings = System.Text.Json.JsonSerializer.Deserialize<EmbeddingsConfig>(json, EngineConfig.JsonOptions()) ?? cfg.Embeddings;
            break;
        case "rf-survey":
            cfg.RfSurvey = System.Text.Json.JsonSerializer.Deserialize<RfSurveyConfig>(json, EngineConfig.JsonOptions()) ?? cfg.RfSurvey;
            break;
        case "tr":
            cfg.TrunkRecorder = System.Text.Json.JsonSerializer.Deserialize<TrunkRecorderConfig>(json, EngineConfig.JsonOptions()) ?? cfg.TrunkRecorder;
            break;
        case "alerts":
            var alerts = System.Text.Json.JsonSerializer.Deserialize<AlertConfig>(json, EngineConfig.JsonOptions()) ?? cfg.Alerts;
            alerts = MergeAlertSettings(cfg.Alerts, alerts, values);
            cfg.Alerts = credentials == null
                ? alerts
                : credentials.ApplyAlertSecrets(alerts, cfg.Alerts, persistSecrets);
            break;
        case "profiles":
            cfg.Profiles = System.Text.Json.JsonSerializer.Deserialize<ProfileConfig>(json, EngineConfig.JsonOptions()) ?? cfg.Profiles;
            break;
        case "locations":
            cfg.Locations = System.Text.Json.JsonSerializer.Deserialize<LocationConfig>(json, EngineConfig.JsonOptions()) ?? cfg.Locations;
            break;
        case "auth":
            cfg.Auth = System.Text.Json.JsonSerializer.Deserialize<AuthConfig>(json, EngineConfig.JsonOptions()) ?? cfg.Auth;
            break;
        default:
            return false;
    }

    cfg.ApplyDefaults();
    return true;
}

static AlertConfig MergeAlertSettings(AlertConfig current, AlertConfig incoming, JsonElement values)
{
    if (!values.TryGetProperty("rules", out var rulesElement))
    {
        incoming.Rules = current.Rules;
    }
    else if (rulesElement.ValueKind == JsonValueKind.Array &&
             rulesElement.GetArrayLength() == 0 &&
             current.Rules.Count > 0 &&
             !BoolProperty(values, "_rulesExplicit"))
    {
        incoming.Rules = current.Rules;
    }

    if (!values.TryGetProperty("playback", out var playbackElement) || playbackElement.ValueKind != JsonValueKind.Object)
    {
        incoming.Playback = current.Playback;
    }
    else
    {
        incoming.Playback = new AlertPlaybackConfig
        {
            Enabled = playbackElement.TryGetProperty("enabled", out _) ? incoming.Playback.Enabled : current.Playback.Enabled,
            AlertMatches = playbackElement.TryGetProperty("alertMatches", out _) ? incoming.Playback.AlertMatches : current.Playback.AlertMatches,
            NewIncidents = playbackElement.TryGetProperty("newIncidents", out _) ? incoming.Playback.NewIncidents : current.Playback.NewIncidents,
            TrafficIncidents = playbackElement.TryGetProperty("trafficIncidents", out _) ? incoming.Playback.TrafficIncidents : current.Playback.TrafficIncidents,
            PoliceCalls = playbackElement.TryGetProperty("policeCalls", out _) ? incoming.Playback.PoliceCalls : current.Playback.PoliceCalls,
            CooldownSeconds = playbackElement.TryGetProperty("cooldownSeconds", out _) ? incoming.Playback.CooldownSeconds : current.Playback.CooldownSeconds,
            RepeatCount = playbackElement.TryGetProperty("repeatCount", out _) ? incoming.Playback.RepeatCount : current.Playback.RepeatCount
        };
    }

    return incoming;
}

static bool BoolProperty(JsonElement values, string name) =>
    values.TryGetProperty(name, out var element) &&
    element.ValueKind is JsonValueKind.True or JsonValueKind.False &&
    element.GetBoolean();

static bool SettingsRestartRequired(string section) =>
    section.Trim().ToLowerInvariant() is "engine" or "transcription" or "embeddings" or "tr" or "auth";

static (long Start, long End) ResolveQualityCheckRange(long? start, long? end, int? hours)
{
    if (start.HasValue || end.HasValue)
        return new TimeRangeQuery(start, end).Resolve();

    var now = DateTimeOffset.UtcNow;
    var boundedHours = Math.Clamp(hours ?? 24, 1, 168);
    return (now.AddHours(-boundedHours).ToUnixTimeSeconds(), now.ToUnixTimeSeconds());
}

static async Task<MigrationResetRequestDto> ReadMigrationResetRequestAsync(HttpContext context)
{
    if (context.Request.ContentLength is null or 0)
        return new MigrationResetRequestDto();

    var request = await JsonSerializer.DeserializeAsync<MigrationResetRequestDto>(
        context.Request.Body,
        EngineConfig.JsonOptions(),
        context.RequestAborted);
    return request ?? new MigrationResetRequestDto();
}

static bool IsDefaultProfile(ProcessingProfile profile) =>
    string.Equals((profile.Name ?? string.Empty).Trim(), "Default", StringComparison.OrdinalIgnoreCase);

static void NormalizeDefaultProfile(ProcessingProfile profile)
{
    profile.Name = "Default";
    profile.IncludePolice = true;
    profile.IncludeFire = true;
    profile.IncludeEMS = true;
    profile.IncludeTraffic = true;
    profile.IncludeOther = true;
    profile.AllowedTalkgroups = new();
    profile.Talkgroups = new();
    profile.UpdatedAtUtc = DateTime.UtcNow;
}

static async Task SaveConfigAsync(EngineConfig cfg, CancellationToken ct)
{
    if (OperatingSystem.IsWindows() || !cfg.ConfigPath.StartsWith("/etc/", StringComparison.Ordinal))
    {
        cfg.Save();
        return;
    }

    var stagingRoot = Path.Combine(cfg.Storage.AppDataRoot, "protected-config");
    Directory.CreateDirectory(stagingRoot);
    var candidatePath = Path.Combine(stagingRoot, $"pizzad-{DateTimeOffset.UtcNow:yyyyMMddHHmmssfff}-{Guid.NewGuid():N}.json");
    await File.WriteAllTextAsync(candidatePath, JsonSerializer.Serialize(cfg, EngineConfig.JsonOptions()) + Environment.NewLine, ct);
    try
    {
        var helper = FindAdminHelper();
        var psi = new ProcessStartInfo
        {
            FileName = "sudo",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        psi.ArgumentList.Add(helper);
        psi.ArgumentList.Add("install-pizzad-config");
        psi.ArgumentList.Add(candidatePath);
        psi.ArgumentList.Add(cfg.ConfigPath);

        using var process = Process.Start(psi) ?? throw new InvalidOperationException("Unable to start sudo helper.");
        var stdoutTask = process.StandardOutput.ReadToEndAsync(ct);
        var stderrTask = process.StandardError.ReadToEndAsync(ct);
        await process.WaitForExitAsync(ct);
        var stdout = await stdoutTask;
        var stderr = await stderrTask;
        if (process.ExitCode != 0)
            throw new InvalidOperationException($"Protected config helper failed with exit code {process.ExitCode}: {(string.IsNullOrWhiteSpace(stderr) ? stdout : stderr).Trim()}");
    }
    finally
    {
        try { File.Delete(candidatePath); } catch { }
    }
}

static string FindAdminHelper()
{
    var candidates = new[]
    {
        "/usr/lib/pizzawave/scripts/pizzawave_setup_admin.sh",
        "/opt/pizzawave/pizzad/scripts/pizzawave_setup_admin.sh",
        "/opt/pizzawave/scripts/pizzawave_setup_admin.sh",
        Path.Combine(AppContext.BaseDirectory, "scripts", "pizzawave_setup_admin.sh")
    };
    var helper = candidates.FirstOrDefault(File.Exists);
    return helper ?? throw new InvalidOperationException("PizzaWave admin helper was not found.");
}

public partial class Program { }
