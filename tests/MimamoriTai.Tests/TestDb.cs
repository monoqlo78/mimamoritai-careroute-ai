using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using MimamoriTai.Core.Domain;
using MimamoriTai.Infrastructure.Data;

namespace MimamoriTai.Tests;

/// <summary>
/// In-memory SQLite harness. Real relational behaviour (unlike the InMemory provider)
/// but nothing is written to disk and every test gets a private database.
/// </summary>
public sealed class TestDb : IDisposable
{
    private readonly SqliteConnection _connection;

    public TestDb()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(_connection)
            .Options;

        Context = new AppDbContext(options);
        Context.Database.EnsureCreated();
    }

    public AppDbContext Context { get; }

    public Guid HouseholdId { get; private set; }

    public Guid ResidentId { get; private set; }

    /// <summary>Creates one household, one resident and the devices a test asks for.</summary>
    public async Task<TestDb> SeedAsync(params Device[] devices)
    {
        var household = new Household { Name = "テスト家族" };
        var resident = new Person
        {
            HouseholdId = household.Id,
            DisplayName = "母",
            Role = PersonRole.Resident
        };

        HouseholdId = household.Id;
        ResidentId = resident.Id;

        Context.Households.Add(household);
        Context.People.Add(resident);

        foreach (var device in devices)
        {
            device.HouseholdId = household.Id;
            Context.Devices.Add(device);
        }

        await Context.SaveChangesAsync();
        return this;
    }

    public static Device Light(string alias = "living-light", string name = "リビング照明", bool remoteAllowed = true) => new()
    {
        ExternalDeviceId = "demo-living-light",
        Name = name,
        Alias = alias,
        DeviceType = DeviceType.Light,
        Room = "リビング",
        Provider = DeviceProviderKind.Mock,
        RemoteControlAllowed = remoteAllowed,
        SafetyClass = SafetyClass.Safe
    };

    public static Device Heater(bool remoteAllowed = true) => new()
    {
        ExternalDeviceId = "demo-heater",
        Name = "電気ストーブ",
        Alias = "heater",
        DeviceType = DeviceType.Heater,
        Room = "リビング",
        Provider = DeviceProviderKind.Mock,
        RemoteControlAllowed = remoteAllowed,
        SafetyClass = SafetyClass.Restricted
    };

    public void Dispose()
    {
        Context.Dispose();
        _connection.Dispose();
    }
}
