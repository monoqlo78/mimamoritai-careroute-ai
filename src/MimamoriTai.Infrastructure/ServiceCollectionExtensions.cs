using Azure.Core;
using Azure.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using MimamoriTai.Core.Abstractions;
using MimamoriTai.Core.Application;
using MimamoriTai.Core.Domain;
using MimamoriTai.Infrastructure.Ai;
using MimamoriTai.Infrastructure.Auth;
using MimamoriTai.Infrastructure.Data;
using MimamoriTai.Infrastructure.Devices;
using MimamoriTai.Infrastructure.Fabric;
using MimamoriTai.Infrastructure.Line;

namespace MimamoriTai.Infrastructure;

/// <summary>Describes which real integrations are live, for display on the dashboard.</summary>
public sealed record IntegrationStatus(
    string DeviceProvider,
    bool SwitchBotConfigured,
    bool OrcaRouterConfigured,
    bool FabricConfigured,
    bool LineConfigured,
    bool EventhouseConfigured,
    string Database);

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers every integration, always choosing a working mock when the real
    /// service is not configured, so the app runs end to end with zero secrets.
    /// </summary>
    public static IServiceCollection AddMimamoriTaiInfrastructure(
        this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<OrcaRouterOptions>(configuration.GetSection(OrcaRouterOptions.SectionName));
        services.Configure<SwitchBotOptions>(configuration.GetSection(SwitchBotOptions.SectionName));
        services.Configure<FabricOptions>(configuration.GetSection(FabricOptions.SectionName));
        services.Configure<LineOptions>(configuration.GetSection(LineOptions.SectionName));
        services.Configure<EventhouseOptions>(configuration.GetSection(EventhouseOptions.SectionName));
        services.Configure<AuthOptions>(configuration.GetSection(AuthOptions.SectionName));

        var connectionString = configuration.GetConnectionString("AppDb");

        services.AddDbContext<AppDbContext>(options =>
        {
            if (!string.IsNullOrWhiteSpace(connectionString))
            {
                options.UseSqlServer(connectionString, sql =>
                    sql.MigrationsHistoryTable("__EFMigrationsHistory", AppDbContext.DefaultSchema));
            }
            else
            {
                // No connection string: fall back to a local SQLite file so the
                // hackathon demo runs with zero infrastructure setup.
                options.UseSqlite($"Data Source={Path.Combine(AppContext.BaseDirectory, "mimamoritai-demo.db")}");
            }
        });

        services.AddScoped<IAppDbContext>(sp => sp.GetRequiredService<AppDbContext>());
        services.AddSingleton(TimeProvider.System);

        // --- Device provider -------------------------------------------------
        // Both providers are always registered; selection between them happens per
        // household at runtime via IDeviceProviderFactory + IDataSourceContext, not
        // once at startup, so a single running app can serve Sample (mock) households
        // and a user's Production (SwitchBot-backed, when configured) household side
        // by side.
        var switchBot = configuration.GetSection(SwitchBotOptions.SectionName).Get<SwitchBotOptions>() ?? new SwitchBotOptions();

        services.AddHttpClient<ISwitchBotClient, SwitchBotClient>(client =>
        {
            client.Timeout = TimeSpan.FromSeconds(30);
        });

        services.AddSingleton<MockDeviceProvider>();
        services.AddScoped<SwitchBotDeviceProvider>();
        services.AddScoped<IDeviceProviderFactory, DeviceProviderFactory>();
        services.AddScoped<IDataSourceContext, DataSourceContext>();
        services.AddScoped<IDeviceProvider, DataSourceAwareDeviceProvider>();

        // --- AI router -------------------------------------------------------
        var orca = configuration.GetSection(OrcaRouterOptions.SectionName).Get<OrcaRouterOptions>() ?? new OrcaRouterOptions();

        if (orca.IsConfigured)
        {
            services.AddHttpClient<IAiRouterClient, OrcaRouterClient>(client =>
            {
                client.BaseAddress = new Uri(orca.BaseUrl.TrimEnd('/') + "/");
                client.Timeout = TimeSpan.FromSeconds(orca.TimeoutSeconds);
            });
        }
        else
        {
            services.AddSingleton<IAiRouterClient, MockAiRouterClient>();
        }

        // --- Fabric Data Agent -----------------------------------------------
        // The MCP-backed client is registered only once Fabric is configured; until
        // then the mock reports IsConfigured = false and the orchestrator uses local data.
        var fabric = configuration.GetSection(FabricOptions.SectionName).Get<FabricOptions>() ?? new FabricOptions();

        if (fabric.IsConfigured)
        {
            services.TryAddSingleton<TokenCredential>(new DefaultAzureCredential());
            services.AddHttpClient<IFabricDataAgentClient, FabricDataAgentMcpClient>(client =>
            {
                client.Timeout = TimeSpan.FromSeconds(120);
            });
        }
        else
        {
            services.AddSingleton<IFabricDataAgentClient, MockFabricDataAgentClient>();
        }

        // --- LINE ------------------------------------------------------------
        var line = configuration.GetSection(LineOptions.SectionName).Get<LineOptions>() ?? new LineOptions();

        if (line.IsConfigured)
        {
            services.AddHttpClient<ILineMessagingClient, LineMessagingClient>(client =>
            {
                client.Timeout = TimeSpan.FromSeconds(30);
            });
        }
        else
        {
            services.AddSingleton<ILineMessagingClient, MockLineMessagingClient>();
        }

        // --- Fabric Eventhouse (real-time streaming ingestion) ---------------
        var eventhouse = configuration.GetSection(EventhouseOptions.SectionName).Get<EventhouseOptions>() ?? new EventhouseOptions();

        if (eventhouse.IsConfigured)
        {
            services.TryAddSingleton<TokenCredential>(new DefaultAzureCredential());
            services.AddHttpClient<IEventStreamPublisher, EventhouseStreamPublisher>(client =>
            {
                client.BaseAddress = new Uri(eventhouse.ClusterUri.TrimEnd('/') + "/");
                client.Timeout = TimeSpan.FromSeconds(eventhouse.TimeoutSeconds);
            });
        }
        else
        {
            services.AddSingleton<IEventStreamPublisher, MockEventStreamPublisher>();
        }

        // --- Application services --------------------------------------------
        services.AddScoped<ILocalDataQuestionService>(sp =>
            new LocalDataQuestionService(sp.GetRequiredService<IAppDbContext>(), sp.GetRequiredService<TimeProvider>()));

        services.AddScoped<ActivityService>();
        services.AddScoped<RiskAssessmentService>();
        services.AddScoped<DeviceControlService>();
        services.AddScoped<AssistantOrchestrator>();
        services.AddScoped<DeviceSyncService>();

        // --- Multi-user / household access -------------------------------------
        // DevCurrentUserAccessor is the zero-configuration fallback: a single fixed
        // demo user, no login required. A later task swaps this registration for a
        // claims-based implementation (Entra External ID / LINE OIDC); nothing else
        // in the app needs to change, since every caller depends only on
        // ICurrentUserAccessor.
        services.AddScoped<ICurrentUserAccessor, DevCurrentUserAccessor>();
        services.AddScoped<HouseholdAccessService>();

        // --- Watch/risk alert (LINE push) -------------------------------------
        services.AddSingleton(sp =>
        {
            var options = sp.GetRequiredService<IOptions<LineOptions>>().Value;
            var threshold = Enum.TryParse<RiskLevel>(options.AlertRiskThreshold, ignoreCase: true, out var parsed)
                ? parsed
                : RiskLevel.Medium;

            return new WatchAlertSettings
            {
                ToId = options.AlertToId,
                Threshold = threshold,
                Cooldown = TimeSpan.FromHours(Math.Max(options.AlertCooldownHours, 0))
            };
        });
        services.AddScoped<ILineRecipientResolver, LineRecipientResolver>();
        services.AddScoped<WatchAlertService>();

        services.AddScoped(sp => new IntegrationStatus(
            sp.GetRequiredService<IDeviceProvider>().Kind.ToString(),
            sp.GetRequiredService<IOptions<SwitchBotOptions>>().Value.IsConfigured,
            sp.GetRequiredService<IAiRouterClient>().IsConfigured,
            sp.GetRequiredService<IFabricDataAgentClient>().IsConfigured,
            sp.GetRequiredService<ILineMessagingClient>().IsConfigured,
            sp.GetRequiredService<IEventStreamPublisher>().IsConfigured,
            string.IsNullOrWhiteSpace(connectionString) ? "SQLite (demo fallback)" : "SQL Server"));

        return services;
    }
}
