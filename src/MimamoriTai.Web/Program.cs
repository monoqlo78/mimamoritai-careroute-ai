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
builder.Services.AddScoped<DashboardService>();
builder.Services.AddOpenApi();

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

// Status code pages re-execute the pipeline, which would turn API/webhook error
// codes into HTML responses. Restrict the friendly pages to browser navigation.
app.UseWhen(
    ctx => !ctx.Request.Path.StartsWithSegments("/api")
        && !ctx.Request.Path.StartsWithSegments("/webhooks")
        && !ctx.Request.Path.StartsWithSegments("/health"),
    branch => branch.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true));
app.UseHttpsRedirection();
app.UseAntiforgery();

app.MapStaticAssets();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.MapApiEndpoints();
app.MapWebhookEndpoints();
app.MapSimulatorEndpoints();

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
        }

        await DemoDataSeeder.SeedAsync(db, clock);
        logger.LogInformation("Database ready. Provider: {Provider}", db.Database.ProviderName);
    }
    catch (Exception ex)
    {
        logger.LogError("Database initialization failed: {Type}. The app starts but data features are unavailable.", ex.GetType().Name);
    }
}

/// <summary>Exposed so tests can reference the generated entry point assembly.</summary>
public partial class Program;
