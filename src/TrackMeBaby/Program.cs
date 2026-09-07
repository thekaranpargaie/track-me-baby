using System.IO.Compression;
using System.Text;
using Microsoft.EntityFrameworkCore;
using TrackMeBaby;
using TrackMeBaby.Data;
using TrackMeBaby.GitHub;
using TrackMeBaby.Services;
using TrackMeBaby.Sync;
using TrackMeBaby.Ui;

// Two ways to run the same server:
//   dotnet run                  HTTP MCP endpoint at /mcp, status page at /, background sync worker
//   dotnet run -- --stdio       stdio MCP server for clients that launch the process themselves
//
// stdio mode builds a plain host with no Kestrel, so it can run alongside the HTTP host without
// fighting over the port, and keeps stdout exclusively for JSON-RPC by logging to stderr.
var useStdio = args.Contains("--stdio");

if (useStdio)
{
    var stdio = Host.CreateApplicationBuilder(args);
    ConfigureConfiguration(stdio.Configuration);
    stdio.Logging.ClearProviders();
    stdio.Logging.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace);
    ConfigureServices(stdio.Services, stdio.Configuration);
    stdio.Services.AddMcpServer(McpOptions).WithToolsFromAssembly().WithPromptsFromAssembly().WithStdioServerTransport();

    var stdioHost = stdio.Build();
    await PrepareDatabaseAsync(stdioHost.Services);
    await stdioHost.RunAsync();
    return;
}

var builder = WebApplication.CreateBuilder(args);
ConfigureConfiguration(builder.Configuration);
ConfigureServices(builder.Services, builder.Configuration);
builder.Services.AddMcpServer(McpOptions).WithToolsFromAssembly().WithPromptsFromAssembly().WithHttpTransport();

var app = builder.Build();
await PrepareDatabaseAsync(app.Services);

app.MapMcp("/mcp");
app.MapGet("/", StatusPage.RenderAsync);
app.MapGet("/status", async (TrackerDbContext db, SyncCoordinator coordinator, PeriodResolver periods) =>
    Results.Json(await StatusPage.BuildAsync(db, coordinator, periods)));
app.MapPost("/sync", async (SyncCoordinator coordinator, bool? force) =>
{
    await coordinator.RunAsync(force: force ?? false);
    return Results.Redirect("/");
});

app.MapGet("/dashboard", DashboardPage.RenderAsync);

// Exports. Each one resolves the same period keyword the dashboard uses, so a link carries its
// range with it and the file matches the page it was taken from.
app.MapGet("/export/report.html", async (HttpContext http, ReportBuilder reports, PeriodResolver periods) =>
{
    var period = periods.FromKeyword(http.Request.Query["period"].FirstOrDefault() ?? "last_30_days");
    var html = await reports.HtmlAsync(period, http.RequestAborted);
    return Results.File(Encoding.UTF8.GetBytes(html), "text/html",
        $"work-report-{ReportBuilder.Slug(period.Label)}.html");
});

app.MapGet("/export/report.md", async (HttpContext http, ReportBuilder reports, PeriodResolver periods) =>
{
    var period = periods.FromKeyword(http.Request.Query["period"].FirstOrDefault() ?? "last_30_days");
    var markdown = await reports.MarkdownAsync(period, ct: http.RequestAborted);
    return Results.File(Encoding.UTF8.GetBytes(markdown), "text/markdown",
        $"work-report-{ReportBuilder.Slug(period.Label)}.md");
});

app.MapGet("/export/data.zip", async (HttpContext http, ReportBuilder reports, PeriodResolver periods) =>
{
    var period = periods.FromKeyword(http.Request.Query["period"].FirstOrDefault() ?? "last_30_days");
    var files = await reports.CsvAsync(period, http.RequestAborted);
    var charts = await reports.ChartsAsync(period, http.RequestAborted);

    using var buffer = new MemoryStream();
    using (var archive = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
    {
        foreach (var (name, content) in files)
        {
            await using var entry = archive.CreateEntry($"data/{name}").Open();
            await entry.WriteAsync(Encoding.UTF8.GetBytes(content), http.RequestAborted);
        }

        foreach (var (name, svg) in charts)
        {
            await using var entry = archive.CreateEntry($"charts/{name}").Open();
            await entry.WriteAsync(Encoding.UTF8.GetBytes(svg), http.RequestAborted);
        }
    }

    return Results.File(buffer.ToArray(), "application/zip",
        $"work-report-{ReportBuilder.Slug(period.Label)}.zip");
});

await app.RunAsync();
return;

static void ConfigureConfiguration(IConfigurationManager configuration)
{
    // Configuration comes from the environment, and docker-compose.yml is where it is written.
    // There is deliberately no local settings file: one place to look, one place to edit, and
    // the host start scripts export the same "Tracker__*" variables the container gets, so both
    // ways of running read the identical configuration. Added last so it outranks appsettings.json.
    configuration.AddEnvironmentVariables();

    // Convenience: the conventional env var name works without the Tracker__GitHub__Token
    // spelling, so a token can be kept in the shell rather than in a tracked file. Only applied
    // when configuration has not supplied one, so an explicit setting still wins.
    var envToken = Environment.GetEnvironmentVariable("GITHUB_TOKEN");
    if (!string.IsNullOrWhiteSpace(envToken)
        && string.IsNullOrWhiteSpace(configuration["Tracker:GitHub:Token"]))
        configuration["Tracker:GitHub:Token"] = envToken;
}

static void ConfigureServices(IServiceCollection services, IConfiguration configuration)
{
    services.Configure<TrackerOptions>(configuration.GetSection(TrackerOptions.SectionName));
    // The binder appends to list defaults instead of replacing them, so DeliveryOptions starts
    // every list empty and the fallbacks are filled in here, after binding, and only where
    // configuration said nothing at all.
    services.PostConfigure<TrackerOptions>(o => o.Delivery.ApplyDefaults());

    var connectionString = configuration.GetConnectionString("Tracker")
                           ?? "Host=localhost;Port=5433;Database=trackmebaby;Username=tracker;Password=tracker";

    services.AddDbContext<TrackerDbContext>(o => o.UseNpgsql(connectionString));

    services.AddHttpClient<GitHubAccess>();
    services.AddScoped<GitHubSyncService>();
    services.AddScoped<ProjectsSyncService>();
    services.AddScoped<TagSyncService>();
    services.AddScoped<ActivityQueryService>();
    services.AddScoped<MemoryService>();
    services.AddScoped<PerformanceService>();
    services.AddScoped<ChangeLogService>();
    services.AddScoped<DeliveryService>();
    services.AddScoped<FlowMetricsService>();
    services.AddScoped<GitHubActionService>();
    services.AddScoped<ReportBuilder>();
    services.AddSingleton<PeriodResolver>();
    services.AddSingleton<SyncCoordinator>();
    services.AddHostedService<SyncWorker>();
}

static void McpOptions(ModelContextProtocol.Server.McpServerOptions options)
{
    options.ServerInfo = new() { Name = "track-me-baby", Version = "1.0.0" };
    options.ServerInstructions = """
        This server is the user's personal work history: GitHub activity collected hourly into
        PostgreSQL, plus memories the user has recorded about why that work mattered.

        Answer questions about what the user did from these tools rather than from GitHub directly
        or from the conversation, because the database holds months of history that nobody
        remembers. When summarising performance, combine the numbers with the memories and cite
        specific pull requests, issues and board items as evidence. Never present a metric as an
        accomplishment on its own.

        If results look empty or stale, call get_sync_status before concluding nothing happened.
        """;
}

// Migrations run at startup: this is a single-user local system, so there is no separate
// deployment step to hang them off.
static async Task PrepareDatabaseAsync(IServiceProvider services)
{
    await using var scope = services.CreateAsyncScope();
    var db = scope.ServiceProvider.GetRequiredService<TrackerDbContext>();
    var logger = scope.ServiceProvider.GetRequiredService<ILogger<Program>>();
    try
    {
        await db.Database.MigrateAsync();

        if (!await db.Sources.AnyAsync(s => s.Name == GitHubSyncService.SourceName))
        {
            db.Sources.Add(new Source
            {
                Name = GitHubSyncService.SourceName,
                DisplayName = "GitHub",
                CreatedAt = DateTimeOffset.UtcNow
            });
            await db.SaveChangesAsync();
        }

        // A row still marked Running at startup belongs to a process that died mid-sync (Ctrl-C,
        // crash, machine off). Nothing is running yet in this one, so release the lease rather
        // than making the user wait for it to expire.
        var stale = await db.SyncStates.Where(s => s.Status == SyncStatuses.Running).ToListAsync();
        foreach (var state in stale)
        {
            state.Status = SyncStatuses.Failed;
            state.Error = "Interrupted before finishing. Resuming from the last completed window.";
            state.UpdatedAt = DateTimeOffset.UtcNow;
            logger.LogWarning("Previous sync for {Source} was interrupted; resuming from {From:u}",
                state.Source, state.LastSuccessfulSync);
        }

        if (stale.Count > 0) await db.SaveChangesAsync();

        logger.LogInformation("Database ready");
    }
    catch (Exception ex)
    {
        logger.LogError(ex, "Could not prepare the database. Is Postgres running? Try: docker compose up -d");
    }
}
