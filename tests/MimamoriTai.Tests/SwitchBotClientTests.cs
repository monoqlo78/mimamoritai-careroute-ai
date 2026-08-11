using System.Net;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MimamoriTai.Infrastructure.Devices;

namespace MimamoriTai.Tests;

/// <summary>
/// Proves the exact HTTP request the app sends to the SwitchBot OpenAPI without a
/// physical device: URL, the four v1.1 auth headers, the HMAC signature shape and the
/// command body. Also asserts that the log line never contains the token or the signature.
/// </summary>
public sealed class SwitchBotClientTests(Xunit.Abstractions.ITestOutputHelper output)
{
    private const string Token = "test-token-abcdef0123456789";
    private const string Secret = "test-secret-9876543210";

    private sealed class CapturingHandler : HttpMessageHandler
    {
        public HttpRequestMessage? Request { get; private set; }

        public string? Body { get; private set; }

        public string Response { get; set; } = """{"statusCode":100,"message":"success","body":{}}""";

        public HttpStatusCode Status { get; set; } = HttpStatusCode.OK;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Request = request;
            Body = request.Content is null ? null : await request.Content.ReadAsStringAsync(ct);

            return new HttpResponseMessage(Status)
            {
                Content = new StringContent(Response, Encoding.UTF8, "application/json")
            };
        }
    }

    private sealed class RecordingLogger : ILogger<SwitchBotClient>
    {
        public List<string> Lines { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter) => Lines.Add(formatter(state, exception));
    }

    private static (SwitchBotClient Client, CapturingHandler Handler, RecordingLogger Logger) Create(
        bool enabled = true, string token = Token, string secret = Secret)
    {
        var handler = new CapturingHandler();
        var http = new HttpClient(handler);
        var logger = new RecordingLogger();

        var options = Options.Create(new SwitchBotOptions
        {
            Enabled = enabled,
            BaseUrl = "https://api.switch-bot.com",
            Token = token,
            Secret = secret
        });

        return (new SwitchBotClient(http, options, logger), handler, logger);
    }

    [Fact]
    public async Task TurnOff_command_sends_documented_v11_request()
    {
        var (client, handler, _) = Create();

        await client.SendCommandRawAsync("01-202410-12345678", "turnOff", "default", "command");

        var request = handler.Request!;
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal(
            "https://api.switch-bot.com/v1.1/devices/01-202410-12345678/commands",
            request.RequestUri!.ToString());

        Assert.Equal("""{"command":"turnOff","parameter":"default","commandType":"command"}""", handler.Body);
    }

    [Fact]
    public async Task Request_carries_the_four_v11_auth_headers()
    {
        var (client, handler, _) = Create();

        await client.GetDeviceListRawAsync();

        var headers = handler.Request!.Headers;
        Assert.Equal(Token, headers.GetValues("Authorization").Single());

        // sign = base64(HMACSHA256(secret, token + t + nonce)) -> 32 bytes -> 44 base64 chars.
        var sign = headers.GetValues("sign").Single();
        Assert.Equal(44, sign.Length);
        Assert.Equal(32, Convert.FromBase64String(sign).Length);

        // t is Unix milliseconds; nonce is a hyphen-free GUID.
        var t = long.Parse(headers.GetValues("t").Single());
        var skewMs = Math.Abs(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - t);
        Assert.True(skewMs < 60_000, $"timestamp skew was {skewMs}ms");

        var nonce = headers.GetValues("nonce").Single();
        Assert.Equal(32, nonce.Length);
        Assert.DoesNotContain('-', nonce);
    }

    [Fact]
    public async Task Signature_differs_per_request_because_nonce_and_timestamp_vary()
    {
        var (client, handler, _) = Create();

        await client.GetDeviceListRawAsync();
        var first = handler.Request!.Headers.GetValues("sign").Single();

        await client.GetDeviceListRawAsync();
        var second = handler.Request!.Headers.GetValues("sign").Single();

        Assert.NotEqual(first, second);
    }

    [Fact]
    public async Task Status_endpoint_url_is_built_from_the_device_id()
    {
        var (client, handler, _) = Create();

        await client.GetDeviceStatusRawAsync("01-202410-12345678");

        Assert.Equal(HttpMethod.Get, handler.Request!.Method);
        Assert.Equal(
            "https://api.switch-bot.com/v1.1/devices/01-202410-12345678/status",
            handler.Request.RequestUri!.ToString());
    }

    [Fact]
    public async Task Outgoing_log_shows_the_request_but_redacts_token_and_signature()
    {
        var (client, handler, logger) = Create();

        await client.SendCommandRawAsync("01-202410-12345678", "turnOff", "default", "command");

        var outgoing = Assert.Single(logger.Lines, l => l.StartsWith("SwitchBot ->", StringComparison.Ordinal));

        // Emitted so the exact wire format is visible in the test output as evidence.
        foreach (var line in logger.Lines)
        {
            output.WriteLine(line);
        }

        // The wire details a reviewer needs are present...
        Assert.Contains("POST", outgoing, StringComparison.Ordinal);
        Assert.Contains("/v1.1/devices/01-202410-12345678/commands", outgoing, StringComparison.Ordinal);
        Assert.Contains("\"command\":\"turnOff\"", outgoing, StringComparison.Ordinal);
        Assert.Contains("t: ", outgoing, StringComparison.Ordinal);
        Assert.Contains("nonce: ", outgoing, StringComparison.Ordinal);

        // ...but no secret-bearing value ever appears, in any log line.
        var sign = handler.Request!.Headers.GetValues("sign").Single();
        foreach (var line in logger.Lines)
        {
            Assert.DoesNotContain(Token, line, StringComparison.Ordinal);
            Assert.DoesNotContain(Secret, line, StringComparison.Ordinal);
            Assert.DoesNotContain(sign, line, StringComparison.Ordinal);
        }

        Assert.Contains($"***(len={Token.Length})", outgoing, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Failure_response_never_leaks_credentials_in_the_exception()
    {
        var (client, handler, _) = Create();
        handler.Status = HttpStatusCode.Unauthorized;
        handler.Response = """{"statusCode":401,"message":"Unauthorized"}""";

        var ex = await Assert.ThrowsAsync<HttpRequestException>(() => client.GetDeviceListRawAsync());

        Assert.Contains("401", ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(Token, ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(Secret, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Unconfigured_client_fails_before_any_http_call()
    {
        var (client, handler, _) = Create(enabled: false);

        await Assert.ThrowsAsync<InvalidOperationException>(() => client.GetDeviceListRawAsync());

        Assert.Null(handler.Request);
    }
}
