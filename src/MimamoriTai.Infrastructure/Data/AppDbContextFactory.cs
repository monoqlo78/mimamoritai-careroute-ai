using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.Extensions.Configuration;

namespace MimamoriTai.Infrastructure.Data;

/// <summary>
/// Used only by `dotnet ef migrations`. Migrations are generated for SQL Server,
/// which is the production/Fabric-mirroring target.
/// </summary>
public sealed class AppDbContextFactory : IDesignTimeDbContextFactory<AppDbContext>
{
    public AppDbContext CreateDbContext(string[] args)
    {
        var configuration = new ConfigurationBuilder()
            .AddEnvironmentVariables()
            .Build();

        var connection = configuration["ConnectionStrings:AppDb"]
            ?? "Server=(localdb)\\MSSQLLocalDB;Database=MimamoriTai;Trusted_Connection=True;TrustServerCertificate=True";

        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlServer(connection, sql => sql
                .MigrationsAssembly(typeof(AppDbContext).Assembly.FullName)
                .MigrationsHistoryTable("__EFMigrationsHistory", AppDbContext.DefaultSchema))
            .Options;

        return new AppDbContext(options);
    }
}
