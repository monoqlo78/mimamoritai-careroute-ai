using MimamoriTai.Core.Application;
using MimamoriTai.Core.Domain;

namespace MimamoriTai.Tests;

public class IntentParserTests
{
    [Fact]
    public void Parses_Clean_Json()
    {
        var plan = IntentParser.TryParse(
            """{"intent":"control_device","deviceAlias":"living-light","action":"turn_on","confidence":0.95,"question":null}""");

        Assert.NotNull(plan);
        Assert.Equal(AssistantIntent.ControlDevice, plan!.Intent);
        Assert.Equal("living-light", plan.DeviceAlias);
        Assert.Equal(DeviceAction.TurnOn, plan.Action);
        Assert.Equal(0.95, plan.Confidence, 3);
    }

    [Fact]
    public void Parses_Json_Wrapped_In_Code_Fence_And_Prose()
    {
        var plan = IntentParser.TryParse(
            """
            はい、こちらです。
            ```json
            {"intent":"query_data","deviceAlias":null,"action":null,"confidence":0.9,"question":"今日の様子は？"}
            ```
            """);

        Assert.NotNull(plan);
        Assert.Equal(AssistantIntent.QueryData, plan!.Intent);
        Assert.Equal("今日の様子は？", plan.Question);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("これはJSONではありません")]
    [InlineData("{ broken json")]
    [InlineData("""{"intent":"launch_missile","confidence":1.0}""")]
    public void Returns_Null_For_Unusable_Output(string raw)
    {
        Assert.Null(IntentParser.TryParse(raw));
    }

    [Fact]
    public void ControlDevice_Without_Action_Is_Not_Actionable()
    {
        Assert.Null(IntentParser.TryParse(
            """{"intent":"control_device","deviceAlias":"living-light","action":null,"confidence":0.99}"""));
    }

    [Fact]
    public void Confidence_Is_Clamped()
    {
        var plan = IntentParser.TryParse(
            """{"intent":"conversation","confidence":9.9}""");

        Assert.NotNull(plan);
        Assert.Equal(1.0, plan!.Confidence);
    }

    [Fact]
    public void Literal_Null_Strings_Are_Treated_As_Null()
    {
        var plan = IntentParser.TryParse(
            """{"intent":"conversation","deviceAlias":"null","question":"null","confidence":0.8}""");

        Assert.NotNull(plan);
        Assert.Null(plan!.DeviceAlias);
        Assert.Null(plan.Question);
    }

    [Fact]
    public void MinimumConfidence_Is_The_Documented_Threshold()
    {
        Assert.Equal(0.85, IntentParser.MinimumConfidence);
    }
}

public class DeviceSafetyPolicyTests
{
    [Theory]
    [InlineData(DeviceType.Light, SafetyClass.Safe)]
    [InlineData(DeviceType.Fan, SafetyClass.Safe)]
    [InlineData(DeviceType.DemoDevice, SafetyClass.Safe)]
    [InlineData(DeviceType.Heater, SafetyClass.Restricted)]
    [InlineData(DeviceType.Kettle, SafetyClass.Restricted)]
    [InlineData(DeviceType.Unknown, SafetyClass.Restricted)]
    public void Classify_Puts_Heat_Sources_In_Restricted(DeviceType type, SafetyClass expected)
    {
        Assert.Equal(expected, DeviceSafetyPolicy.Classify(type));
    }

    [Fact]
    public void GetStatus_Is_Allowed_Even_With_Low_Confidence()
    {
        var device = TestDb.Heater();
        Assert.Null(DeviceSafetyPolicy.Validate(device, DeviceAction.GetStatus, 0.1));
    }

    [Fact]
    public void Disabled_Device_Is_Always_Blocked()
    {
        var device = TestDb.Light();
        device.IsEnabled = false;
        Assert.NotNull(DeviceSafetyPolicy.Validate(device, DeviceAction.TurnOff, 1.0));
    }

    [Fact]
    public void IsStateChanging_Excludes_GetStatus()
    {
        Assert.True(DeviceSafetyPolicy.IsStateChanging(DeviceAction.TurnOn));
        Assert.True(DeviceSafetyPolicy.IsStateChanging(DeviceAction.TurnOff));
        Assert.True(DeviceSafetyPolicy.IsStateChanging(DeviceAction.Toggle));
        Assert.False(DeviceSafetyPolicy.IsStateChanging(DeviceAction.GetStatus));
    }
}
