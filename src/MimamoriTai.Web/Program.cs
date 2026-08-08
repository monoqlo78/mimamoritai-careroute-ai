using Microsoft.EntityFrameworkCore;
using MimamoriTai.Infrastructure;
using MimamoriTai.Infrastructure.Data;
using MimamoriTai.Web.Components;
using MimamoriTai.Web.Endpoints;
using MimamoriTai.Web.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

builder.Services.AddMimamoriTaiInfrastructure(builder.Configuration);
builder.Services.AddMimamoriTaiAuthentication(builder.Configuration);
builder.Services.AddScoped<DashboardService>();
builder.Services.AddOpenApi();
builder.Services.AddHostedService<WatchAlertBackgroundService>();
builder.Services.AddHostedService<SwitchBotPollingBackgroundService>();

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    app.UseHsts();
}
else
{
    app.MapOpenApi();
}

app.UseMimamoriTaiForwardedHeaders();

// Status code pages re-execute the pipeline, which would turn API/webhook error
// codes into HTML responses. Restrict the friendly pages to browser navigation.
app.UseWhen(
    ctx => !ctx.Request.Path.StartsWithSegments("/api")
        && !ctx.Request.Path.StartsWithSegments("/webhooks")
        && !ctx.Request.Path.StartsWithSegments("/health"),
    branch => branch.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true));
app.UseHttpsRedirection();
app.UseAuthentication();
app.UseAuthorization();
app.UseAntiforgery();

app.MapStaticAssets();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.MapApiEndpoints();
app.MapWebhookEndpoints();
app.MapSimulatorEndpoints();
app.MapAlertEndpoints();
app.MapDeviceSyncEndpoints();
app.MapAuthEndpoints();

await InitializeDatabaseAsync(app);

app.Run();

/// <summary>
/// Applies migrations when running against SQL Server, or creates the SQLite demo
/// database, and seeds demo data so the app is immediately usable.
/// </summary>
static async Task InitializeDatabaseAsync(WebApplication app)
{
    using var scope = app.Services.CreateScope();
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    var clock = scope.ServiceProvider.GetRequiredService<TimeProvider>();
    var logger = scope.ServiceProvider.GetRequiredService<ILoggerFactory>().CreateLogger("Startup");

    try
    {
        if (db.Database.ProviderName?.Contains("SqlServer", StringComparison.Ordinal) == true)
        {
            await db.Database.MigrateAsync();
        }
        else
        {
            await db.Database.EnsureCreatedAsync();

            // EnsureCreated never upgrades an existing file, so a demo database
            // created before a model change is missing the new tables and every
            // query against them throws at runtime. The SQLite database is a
            // disposable demo artifact, so recreate it when it is out of date.
            var missing = await GetMissingSqliteTablesAsync(db);
            if (missing.Count > 0)
            {
                logger.LogWarning(
                    "Demo database is out of date (missing: {Missing}). Recreating it.",
                    string.Join(", ", missing));
                await db.Database.EnsureDeletedAsync();
                await db.Database.EnsureCreatedAsync();
            }
        }

        await DemoDataSeeder.SeedAsync(db, clock);
        logger.LogInformation("Database ready. Provider: {Provider}", db.Database.ProviderName);
    }
    catch (Exception ex)
    {
        logger.LogError("Database initialization failed: {Type}. The app starts but data features are unavailable.", ex.GetType().Name);
    }
}

/// <summary>
/// Table names that the model expects but the SQLite demo file does not contain.
/// </summary>
static async Task<List<string>> GetMissingSqliteTablesAsync(AppDbContext db)
{
    var expected = db.Model.GetEntityTypes()
        .Select(t => t.GetTableName())
        .Where(n => !string.IsNullOrEmpty(n))
        .Distinct(StringComparer.Ordinal)
        .ToList();

    var actual = new HashSet<string>(StringComparer.Ordinal);
    await using var command = db.Database.GetDbConnection().CreateCommand();
    command.CommandText = "SELECT name FROM sqlite_master WHERE type = 'table'";
    await db.Database.OpenConnectionAsync();
    try
    {
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            actual.Add(reader.GetString(0));
        }
    }
    finally
    {
        await db.Database.CloseConnectionAsync();
    }

    return expected.Where(name => !actual.Contains(name!)).Select(n => n!).ToList();
}

/// <summary>Exposed so tests can reference the generated entry point assembly.</summary>
public partial class Program;