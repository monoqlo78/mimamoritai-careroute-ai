using Microsoft.Extensions.Logging.Abstractions;
using MimamoriTai.Core.Abstractions;
using MimamoriTai.Core.Application;
using MimamoriTai.Core.Domain;
using MimamoriTai.Infrastructure.Devices;

namespace MimamoriTai.Tests;

/// <summary>Returns canned JSON without ever touching the network, mirroring FakeLineMessagingClient's role.</summary>
public sealed class FakeSwitchBotClient : ISwitchBotClient
{
    public bool IsConfigured { get; init; } = true;

    public string DeviceListResponse { get; set; } = "{}";
    public string DeviceStatusResponse { get; set; } = "{}";
    public string CommandResponse { get; set; } = """{"statusCode":100,"message":"success","body":{}}""";

    public List<(string DeviceId, string Command, string Parameter, string CommandType)> SentCommands { get; } = [];

    public Task<string> GetDeviceListRawAsync(CancellationToken ct = default) =>
        Task.FromResult(DeviceListResponse);

    public Task<string> GetDeviceStatusRawAsync(string deviceId, CancellationToken ct = default) =>
        Task.FromResult(DeviceStatusResponse);

    public Task<string> SendCommandRawAsync(string deviceId, string command, string parameter, string commandType, CancellationToken ct = default)
    {
        SentCommands.Add((deviceId, command, parameter, commandType));
        return Task.FromResult(CommandResponse);
    }
}

public class SwitchBotDeviceProviderTests
{
    // Realistic shape taken from the official SwitchBot OpenAPI v1.1 documentation
    // (README.md "Get device list" example), trimmed to the fields this provider reads.
    private const string RealisticDeviceListJson = """
        {
            "statusCode": 100,
            "message": "success",
            "body": {
                "deviceList": [
                    {
                        "deviceId": "AAAAAAAAAAAA",
                        "deviceName": "リビング照明",
                        "deviceType": "Color Bulb",
                        "enableCloudService": true,
                        "hubDeviceId": "000000000000"
                    },
                    {
                        "deviceId": "BBBBBBBBBBBB",
                        "deviceName": "扇風機プラグ",
                        "deviceType": "Plug Mini (JP)",
                        "enableCloudService": true,
                        "hubDeviceId": "CCCCCCCCCCCC"
                    },
                    {
                        "deviceId": "DDDDDDDDDDDD",
                        "deviceName": "謎のセンサー",
                        "deviceType": "Some Future Device",
                        "enableCloudService": true,
                        "hubDeviceId": "000000000000"
                    }
                ],
                "infraredRemoteList": [
                    {
                        "deviceId": "EEEEEEEEEEEE",
                        "deviceName": "エアコン",
                        "remoteType": "Air Conditioner",
                        "hubDeviceId": "CCCCCCCCCCCC"
                    },
                    {
                        "deviceId": "FFFFFFFFFFFF",
                        "deviceName": "テレビ",
                        "remoteType": "TV",
                        "hubDeviceId": "CCCCCCCCCCCC"
                    }
                ]
            }
        }
        """;

    private static SwitchBotDeviceProvider Create(FakeSwitchBotClient client) =>
        new(client, NullLogger<SwitchBotDeviceProvider>.Instance);

    [Fact]
    public async Task GetDevicesAsync_Maps_Both_Physical_And_Infrared_Devices()
    {
        var client = new FakeSwitchBotClient { DeviceListResponse = RealisticDeviceListJson };
        var provider = Create(client);

        var devices = await provider.GetDevicesAsync();

        Assert.Equal(5, devices.Count);

        var light = devices.Single(d => d.ExternalDeviceId == "AAAAAAAAAAAA");
        Assert.Equal("リビング照明", light.Name);
        Assert.Equal(DeviceType.Light, light.DeviceType);

        var plugMini = devices.Single(d => d.ExternalDeviceId == "BBBBBBBBBBBB");
        Assert.Equal(DeviceType.Plug, plugMini.DeviceType);
        Assert.Contains("CCCCCCCCCCCC", plugMini.Room);

        var aircon = devices.Single(d => d.ExternalDeviceId == "EEEEEEEEEEEE");
        Assert.Equal(DeviceType.Heater, aircon.DeviceType);

        var tv = devices.Single(d => d.ExternalDeviceId == "FFFFFFFFFFFF");
        Assert.Equal(DeviceType.Unknown, tv.DeviceType);
    }

    [Fact]
    public async Task GetDevicesAsync_Maps_Unknown_Device_Type_To_Restricted_Safety_Class()
    {
        var client = new FakeSwitchBotClient { DeviceListResponse = RealisticDeviceListJson };
        var provider = Create(client);

        var devices = await provider.GetDevicesAsync();
        var unknown = devices.Single(d => d.ExternalDeviceId == "DDDDDDDDDDDD");

        Assert.Equal(DeviceType.Unknown, unknown.DeviceType);
        Assert.Equal(SafetyClass.Restricted, DeviceSafetyPolicy.Classify(unknown.DeviceType));
    }

    [Fact]
    public async Task GetDevicesAsync_Returns_Empty_When_StatusCode_Is_Not_100()
    {
        var client = new FakeSwitchBotClient
        {
            DeviceListResponse = """{"statusCode":190,"message":"System error","body":{}}"""
        };
        var provider = Create(client);

        var devices = await provider.GetDevicesAsync();

        Assert.Empty(devices);
    }

    [Fact]
    public async Task GetDevicesAsync_Returns_Empty_On_Malformed_Json()
    {
        var client = new FakeSwitchBotClient { DeviceListResponse = "{not valid json!!" };
        var provider = Create(client);

        var devices = await provider.GetDevicesAsync();

        Assert.Empty(devices);
    }

    [Fact]
    public async Task GetStatusAsync_Maps_Power_Field_For_Bot()
    {
        var client = new FakeSwitchBotClient
        {
            DeviceStatusResponse = """
                {"statusCode":100,"message":"success","body":{"deviceId":"AAAAAAAAAAAA","deviceType":"Bot","power":"on","battery":100}}
                """
        };
        var provider = Create(client);

        var status = await provider.GetStatusAsync("AAAAAAAAAAAA");

        Assert.NotNull(status);
        Assert.True(status!.IsOn);
    }

    [Fact]
    public async Task GetStatusAsync_Infers_State_From_ElectricCurrent_For_Plug_Mini()
    {
        // Plug Mini (JP) status has no "power" field per the official spec -- only
        // voltage/weight/electricityOfDay/electricCurrent.
        var client = new FakeSwitchBotClient
        {
            DeviceStatusResponse = """
                {"statusCode":100,"message":"success","body":{"deviceId":"BBBBBBBBBBBB","deviceType":"Plug Mini (JP)","voltage":100.5,"weight":12.3,"electricityOfDay":30,"electricCurrent":0.5}}
                """
        };
        var provider = Create(client);

        var status = await provider.GetStatusAsync("BBBBBBBBBBBB");

        Assert.NotNull(status);
        Assert.True(status!.IsOn);
        Assert.Equal(12.3, status.PowerWatts);
    }

    [Fact]
    public async Task GetStatusAsync_Returns_Null_When_StatusCode_Is_Not_100()
    {
        var client = new FakeSwitchBotClient
        {
            DeviceStatusResponse = """{"statusCode":401,"message":"Unauthorized","body":{}}"""
        };
        var provider = Create(client);

        var status = await provider.GetStatusAsync("AAAAAAAAAAAA");

        Assert.Null(status);
    }

    [Fact]
    public async Task GetStatusAsync_Returns_Null_On_Malformed_Json()
    {
        var client = new FakeSwitchBotClient { DeviceStatusResponse = "not json at all" };
        var provider = Create(client);

        var status = await provider.GetStatusAsync("AAAAAAAAAAAA");

        Assert.Null(status);
    }

    [Fact]
    public async Task TurnOnAsync_Sends_TurnOn_Command_And_Succeeds()
    {
        var client = new FakeSwitchBotClient();
        var provider = Create(client);

        var result = await provider.TurnOnAsync("AAAAAAAAAAAA");

        Assert.True(result.Success);
        Assert.Single(client.SentCommands);
        Assert.Equal("turnOn", client.SentCommands[0].Command);
    }

    [Fact]
    public async Task TurnOnAsync_Fails_Without_Throwing_When_StatusCode_Is_Not_100()
    {
        var client = new FakeSwitchBotClient
        {
            CommandResponse = """{"statusCode":151,"message":"Device internal error","body":{}}"""
        };
        var provider = Create(client);

        var result = await provider.TurnOnAsync("AAAAAAAAAAAA");

        Assert.False(result.Success);
    }

    [Fact]
    public async Task ToggleAsync_Turns_Off_When_Currently_On()
    {
        var client = new FakeSwitchBotClient
        {
            DeviceStatusResponse = """{"statusCode":100,"message":"success","body":{"deviceId":"AAAAAAAAAAAA","deviceType":"Bot","power":"on"}}"""
        };
        var provider = Create(client);

        var result = await provider.ToggleAsync("AAAAAAAAAAAA");

        Assert.True(result.Success);
        Assert.Single(client.SentCommands);
        Assert.Equal("turnOff", client.SentCommands[0].Command);
    }

    [Fact]
    public async Task ToggleAsync_Fails_Without_Throwing_When_Status_Cannot_Be_Determined()
    {
        // Infrared remotes have no status endpoint; SwitchBot returns an error for them.
        var client = new FakeSwitchBotClient
        {
            DeviceStatusResponse = """{"statusCode":190,"message":"System error","body":{}}"""
        };
        var provider = Create(client);

        var result = await provider.ToggleAsync("EEEEEEEEEEEE");

        Assert.False(result.Success);
        Assert.Empty(client.SentCommands);
    }
}
