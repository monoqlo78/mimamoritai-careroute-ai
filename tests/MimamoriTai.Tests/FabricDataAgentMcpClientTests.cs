using System.Net;
using System.Text.Json;
using Azure.Core;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MimamoriTai.Infrastructure.Fabric;

namespace MimamoriTai.Tests;

/// <summary>Queues canned responses (by content type + body) and returns them in order, capturing every outgoing request.</summary>
public sealed class QueueingHttpMessageHandler : HttpMessageHandler
{
    private readonly Queue<(HttpStatusCode Status, string? Body, string ContentType)> _responses = new();

    public List<HttpRequestMessage> Requests { get; } = [];

    public void Enqueue(string body, string contentType = "application/json", HttpStatusCode status = HttpStatusCode.OK) =>
        _responses.Enqueue((status, body, contentType));

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Requests.Add(request);
        if (request.Content is not null)
        {
            // Force materialization so LastRequestBody-style assertions could be added later.
            await request.Content.ReadAsStringAsync(cancellationToken);
        }

        var (status, body, contentType) = _responses.Count > 0
            ? _responses.Dequeue()
            : (HttpStatusCode.OK, null, "application/json");

        var response = new HttpResponseMessage(status);
        if (body is not null)
        {
            response.Content = new StringContent(body, System.Text.Encoding.UTF8, contentType);
        }

        return response;
    }
}

public class FabricDataAgentMcpClientTests
{
    private static FabricOptions Options(bool enabled = true) => new()
    {
        Enabled = enabled,
        WorkspaceId = "e2a48a60-0b5f-421f-91bb-51a33fe528bc",
        DataAgentId = "bd915a90-2bc1-4a4f-bcae-749622366f97",
        McpUrl = "https://api.fabric.microsoft.com/v1/mcp/workspaces/e2a48a60-0b5f-421f-91bb-51a33fe528bc/dataagents/bd915a90-2bc1-4a4f-bcae-749622366f97/agent"
    };

    private static (FabricDataAgentMcpClient Client, QueueingHttpMessageHandler Handler) Create(FabricOptions? options = null)
    {
        var handler = new QueueingHttpMessageHandler();
        var http = new HttpClient(handler);
        var credential = new FakeTokenCredential();
        var client = new FabricDataAgentMcpClient(
            http,
            Microsoft.Extensions.Options.Options.Create(options ?? Options()),
            credential,
            NullLogger<FabricDataAgentMcpClient>.Instance);

        return (client, handler);
    }

    // --- MCP URL construction ------------------------------------------------

    [Fact]
    public void BuildMcpUrl_Uses_Explicit_McpUrl_When_Set()
    {
        var options = Options();
        options.McpUrl = "https://custom.example.com/mcp";

        var url = FabricDataAgentMcpClient.BuildMcpUrl(options);

        Assert.Equal("https://custom.example.com/mcp", url);
    }

    [Fact]
    public void BuildMcpUrl_Builds_From_WorkspaceId_And_DataAgentId_When_McpUrl_Empty()
    {
        var options = new FabricOptions
        {
            Enabled = true,
            WorkspaceId = "e2a48a60-0b5f-421f-91bb-51a33fe528bc",
            DataAgentId = "bd915a90-2bc1-4a4f-bcae-749622366f97",
            McpUrl = string.Empty
        };

        var url = FabricDataAgentMcpClient.BuildMcpUrl(options);

        Assert.Equal(
            "https://api.fabric.microsoft.com/v1/mcp/workspaces/e2a48a60-0b5f-421f-91bb-51a33fe528bc/dataagents/bd915a90-2bc1-4a4f-bcae-749622366f97/agent",
            url);
    }

    // --- IsConfigured ---------------------------------------------------------

    [Fact]
    public void IsConfigured_Is_False_When_Options_Incomplete()
    {
        var (client, _) = Create(new FabricOptions { Enabled = true });

        Assert.False(client.IsConfigured);
    }

    [Fact]
    public void IsConfigured_Is_True_When_Options_Complete_And_Enabled()
    {
        var (client, _) = Create();

        Assert.True(client.IsConfigured);
    }

    [Fact]
    public async Task AskAsync_Returns_NotConfigured_Failure_When_Disabled()
    {
        var (client, handler) = Create(Options(enabled: false));

        var result = await client.AskAsync("test?");

        Assert.False(result.Success);
        Assert.Empty(handler.Requests);
    }

    // --- SSE / plain JSON response parsing ------------------------------------

    [Fact]
    public void ParseEnvelopes_Parses_Plain_Json_Body()
    {
        var body = """{"jsonrpc":"2.0","id":1,"result":{"ok":true}}""";

        var envelopes = FabricDataAgentMcpClient.ParseEnvelopes(body, "application/json");

        var envelope = Assert.Single(envelopes);
        Assert.Equal(1, envelope.GetProperty("id").GetInt32());
        Assert.True(envelope.GetProperty("result").GetProperty("ok").GetBoolean());
    }

    [Fact]
    public void ParseEnvelopes_Parses_Sse_Body_With_Data_Lines()
    {
        var body = "event: message\n" +
                    """data: {"jsonrpc":"2.0","id":2,"result":{"answer":"42"}}""" + "\n\n";

        var envelopes = FabricDataAgentMcpClient.ParseEnvelopes(body, "text/event-stream");

        var envelope = Assert.Single(envelopes);
        Assert.Equal(2, envelope.GetProperty("id").GetInt32());
        Assert.Equal("42", envelope.GetProperty("result").GetProperty("answer").GetString());
    }

    [Fact]
    public void ParseEnvelopes_Returns_Empty_For_Malformed_Body()
    {
        var envelopes = FabricDataAgentMcpClient.ParseEnvelopes("not json at all", "application/json");

        Assert.Empty(envelopes);
    }

    [Fact]
    public void ParseEnvelopes_Returns_Empty_For_Empty_Body()
    {
        var envelopes = FabricDataAgentMcpClient.ParseEnvelopes(string.Empty, "application/json");

        Assert.Empty(envelopes);
    }

    // --- ExtractFirstToolName / ExtractAnswerText helpers ---------------------

    [Fact]
    public void ExtractFirstToolName_Returns_First_Tool()
    {
        var json = JsonDocument.Parse("""{"tools":[{"name":"ask_data_agent"},{"name":"other"}]}""").RootElement;

        var name = FabricDataAgentMcpClient.ExtractFirstToolName(json);

        Assert.Equal("ask_data_agent", name);
    }

    [Fact]
    public void ExtractFirstToolArgumentName_Uses_Required_From_InputSchema()
    {
        var json = JsonDocument.Parse(
            """{"tools":[{"name":"DataAgent_X","inputSchema":{"type":"object","properties":{"userQuestion":{"type":"string"}},"required":["userQuestion"]}}]}""")
            .RootElement;

        Assert.Equal("userQuestion", FabricDataAgentMcpClient.ExtractFirstToolArgumentName(json));
    }

    [Fact]
    public void ExtractFirstToolArgumentName_Falls_Back_To_First_Property()
    {
        var json = JsonDocument.Parse(
            """{"tools":[{"name":"DataAgent_X","inputSchema":{"type":"object","properties":{"query":{"type":"string"}}}}]}""")
            .RootElement;

        Assert.Equal("query", FabricDataAgentMcpClient.ExtractFirstToolArgumentName(json));
    }

    [Fact]
    public void ExtractFirstToolArgumentName_Defaults_When_Schema_Missing()
    {
        var json = JsonDocument.Parse("""{"tools":[{"name":"DataAgent_X"}]}""").RootElement;

        Assert.Equal("userQuestion", FabricDataAgentMcpClient.ExtractFirstToolArgumentName(json));
    }

    [Fact]
    public void ExtractAnswerText_Returns_First_Text_Block()
    {
        var json = JsonDocument.Parse("""{"content":[{"type":"text","text":"answer text"}]}""").RootElement;

        var text = FabricDataAgentMcpClient.ExtractAnswerText(json);

        Assert.Equal("answer text", text);
    }

    // --- End-to-end AskAsync happy path (SSE responses) ------------------------

    [Fact]
    public async Task AskAsync_Runs_Full_Initialize_ToolsList_ToolsCall_Sequence_And_Returns_Answer()
    {
        var (client, handler) = Create();

        // initialize
        handler.Enqueue(
            """data: {"jsonrpc":"2.0","id":1,"result":{"protocolVersion":"2024-11-05"}}""",
            "text/event-stream");
        // notifications/initialized (no body needed, empty ack)
        handler.Enqueue(string.Empty, "application/json");
        // tools/list
        handler.Enqueue(
            """{"jsonrpc":"2.0","id":2,"result":{"tools":[{"name":"ask_data_agent"}]}}""",
            "application/json");
        // tools/call
        handler.Enqueue(
            """{"jsonrpc":"2.0","id":3,"result":{"content":[{"type":"text","text":"7時に活動を開始しました。"}]}}""",
            "application/json");

        var result = await client.AskAsync("今日最初に活動したのは何時？");

        Assert.True(result.Success);
        Assert.Equal("7時に活動を開始しました。", result.Answer);
        Assert.Equal("Fabric", result.Source);
        Assert.Equal(4, handler.Requests.Count);
    }

    // --- Graceful failure handling ----------------------------------------------

    [Fact]
    public async Task AskAsync_Handles_JsonRpc_Error_Response_Gracefully()
    {
        var (client, handler) = Create();

        handler.Enqueue(
            """{"jsonrpc":"2.0","id":1,"result":{}}""");
        handler.Enqueue(string.Empty);
        handler.Enqueue(
            """{"jsonrpc":"2.0","id":2,"error":{"code":-32000,"message":"capacity paused"}}""");

        var result = await client.AskAsync("test?");

        Assert.False(result.Success);
        Assert.NotNull(result.Error);
        Assert.Equal(string.Empty, result.Answer);
    }

    [Fact]
    public async Task AskAsync_Handles_Malformed_Body_Gracefully()
    {
        var (client, handler) = Create();

        handler.Enqueue("this is not json");

        var result = await client.AskAsync("test?");

        Assert.False(result.Success);
        Assert.NotNull(result.Error);
    }

    [Fact]
    public async Task AskAsync_Handles_Http_Failure_Gracefully()
    {
        var (client, handler) = Create();
        handler.Enqueue(string.Empty, status: HttpStatusCode.ServiceUnavailable);
        // Empty responses parse to no envelopes -> InvalidOperationException -> caught internally.

        var result = await client.AskAsync("test?");

        Assert.False(result.Success);
        Assert.NotNull(result.Error);
    }

    // --- Quality gating: apology / data-access failure detection ----------------

    [Theory]
    [InlineData("申し訳ありません、現在、データベースに接続できず、情報を取得できませんでした。")]
    [InlineData("システムのエラーにより、ご質問の内容を取得できませんでした。")]
    [InlineData("データベースに接続できませんでした。")]
    [InlineData("I'm sorry, but I encountered an error while trying to retrieve the data.")]
    public void LooksLikeFailureAnswer_Returns_True_For_Apology_Text(string answer)
    {
        Assert.True(FabricDataAgentMcpClient.LooksLikeFailureAnswer(answer));
    }

    [Theory]
    [InlineData("DeviceEventsテーブルの行数は162行です。")]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void LooksLikeFailureAnswer_Returns_False_For_Good_Or_Empty_Text(string? answer)
    {
        Assert.False(FabricDataAgentMcpClient.LooksLikeFailureAnswer(answer));
    }
}
