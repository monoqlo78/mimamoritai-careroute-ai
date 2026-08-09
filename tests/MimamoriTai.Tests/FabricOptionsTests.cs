using Azure.Identity;
using MimamoriTai.Infrastructure;
using MimamoriTai.Infrastructure.Fabric;

namespace MimamoriTai.Tests;

public class FabricOptionsTests
{
    [Fact]
    public void HasServicePrincipalCredentials_True_When_All_Three_Set()
    {
        var options = new FabricOptions
        {
            TenantId = "tenant-id",
            ClientId = "client-id",
            ClientSecret = "client-secret"
        };

        Assert.True(options.HasServicePrincipalCredentials);
    }

    [Theory]
    [InlineData(null, "client-id", "client-secret")]
    [InlineData("tenant-id", null, "client-secret")]
    [InlineData("tenant-id", "client-id", null)]
    [InlineData("", "client-id", "client-secret")]
    [InlineData("tenant-id", "", "client-secret")]
    [InlineData("tenant-id", "client-id", "")]
    [InlineData("   ", "client-id", "client-secret")]
    public void HasServicePrincipalCredentials_False_When_Any_Missing(string? tenantId, string? clientId, string? clientSecret)
    {
        var options = new FabricOptions
        {
            TenantId = tenantId,
            ClientId = clientId,
            ClientSecret = clientSecret
        };

        Assert.False(options.HasServicePrincipalCredentials);
    }

    [Fact]
    public void HasServicePrincipalCredentials_False_When_All_Blank_Defaults()
    {
        var options = new FabricOptions();

        Assert.False(options.HasServicePrincipalCredentials);
    }

    [Fact]
    public void CreateFabricTokenCredential_Returns_ClientSecretCredential_When_ServicePrincipal_Configured()
    {
        var options = new FabricOptions
        {
            TenantId = "11111111-1111-1111-1111-111111111111",
            ClientId = "22222222-2222-2222-2222-222222222222",
            ClientSecret = "super-secret-value"
        };

        var credential = ServiceCollectionExtensions.CreateFabricTokenCredential(options);

        Assert.IsType<ClientSecretCredential>(credential);
    }

    [Fact]
    public void CreateFabricTokenCredential_Returns_DefaultAzureCredential_When_ServicePrincipal_Not_Configured()
    {
        var options = new FabricOptions();

        var credential = ServiceCollectionExtensions.CreateFabricTokenCredential(options);

        Assert.IsType<DefaultAzureCredential>(credential);
    }

    [Fact]
    public void CreateFabricTokenCredential_Returns_DefaultAzureCredential_When_Partially_Configured()
    {
        var options = new FabricOptions
        {
            TenantId = "11111111-1111-1111-1111-111111111111",
            ClientId = "22222222-2222-2222-2222-222222222222"
            // ClientSecret intentionally left blank.
        };

        var credential = ServiceCollectionExtensions.CreateFabricTokenCredential(options);

        Assert.IsType<DefaultAzureCredential>(credential);
    }
}
