using System.Net;
using System.Security.Authentication;
using System.Text;
using DaveHome;

namespace DaveHome.Tests;

public sealed class HueBridgeClientTests
{
    [Fact]
    public async Task DiscoveryParsesBridgeAddress()
    {
        using var client = CreateClient(
            """[{"id":"bridge-id","internalipaddress":"192.168.1.20","port":443}]""");
        var hue = new HueBridgeClient(client);

        var bridges = await hue.DiscoverAsync();

        var bridge = Assert.Single(bridges);
        Assert.Equal("bridge-id", bridge.BridgeId);
        Assert.Equal("https://192.168.1.20/", bridge.Address.AbsoluteUri);
    }

    [Fact]
    public async Task LinkExtractsApplicationKey()
    {
        using var client = CreateClient("""[{"success":{"username":"application-key"}}]""");
        var hue = new HueBridgeClient(client);

        var result = await hue.LinkAsync(new Uri("https://bridge.local/"));

        Assert.Equal(DeviceOperationStatus.Succeeded, result.Status);
        Assert.Equal("application-key", result.ApplicationKey);
    }

    [Fact]
    public async Task LinkButtonErrorIsUnauthorized()
    {
        using var client = CreateClient("""[{"error":{"type":101,"description":"link button not pressed"}}]""");
        var hue = new HueBridgeClient(client);

        var result = await hue.LinkAsync(new Uri("https://bridge.local/"));

        Assert.Equal(DeviceOperationStatus.Unauthorized, result.Status);
        Assert.Equal("link button not pressed", result.Detail);
    }

    [Fact]
    public async Task LinkShowsHueApiErrorDescription()
    {
        using var client = CreateClient(
            """[{"error":{"type":7,"description":"invalid value for parameter, /devicetype"}}]""");
        var hue = new HueBridgeClient(client);

        var result = await hue.LinkAsync(new Uri("https://bridge.local/"));

        Assert.Equal(DeviceOperationStatus.InvalidState, result.Status);
        Assert.Equal("invalid value for parameter, /devicetype", result.Detail);
    }

    [Fact]
    public async Task LinkShowsCertificateFailureDiagnostic()
    {
        using var client = new HttpClient(new QueueHandler(_ =>
            throw new HttpRequestException(
                "TLS connection failed.",
                new AuthenticationException("The certificate chain was issued by an untrusted authority."))));
        var hue = new HueBridgeClient(client);

        var result = await hue.LinkAsync(new Uri("https://bridge.local/"));

        Assert.Equal(DeviceOperationStatus.BridgeUnavailable, result.Status);
        Assert.Contains("untrusted authority", result.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetLightsConvertsClipV2StateAndCapabilities()
    {
        using var client = CreateClient(
            """{"data":[{"id":"light-1","metadata":{"name":"Kitchen"},"on":{"on":true},"dimming":{"brightness":37.6}}]}""");
        var hue = new HueBridgeClient(client);

        var result = await hue.GetLightsAsync(new Uri("https://bridge.local/"), "application-key");

        Assert.Equal(DeviceOperationStatus.Succeeded, result.Status);
        var light = Assert.Single(result.Lights);
        Assert.Equal("light-1", light.ResourceId);
        Assert.Equal("Kitchen", light.Name);
        Assert.Equal(DeviceCapabilities.OnOff | DeviceCapabilities.Brightness, light.Capabilities);
        Assert.Equal(new DeviceState(true, 38), light.State);
    }

    [Fact]
    public async Task SetLightStateUsesApplicationKeyAndNormalizedBrightness()
    {
        var handler = new QueueHandler(
            request =>
            {
                Assert.Equal(HttpMethod.Put, request.Method);
                Assert.Equal("application-key", request.Headers.GetValues("hue-application-key").Single());
                var payload = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
                Assert.Contains("\"brightness\":42", payload, StringComparison.Ordinal);
                return JsonResponse("""{"data":[{"id":"light-1"}]}""");
            },
            _ => JsonResponse(
                """{"data":[{"id":"light-1","metadata":{"name":"Kitchen"},"on":{"on":true},"dimming":{"brightness":42}}]}"""));
        using var client = new HttpClient(handler);
        var hue = new HueBridgeClient(client);

        var result = await hue.SetLightStateAsync(
            new Uri("https://bridge.local/"),
            "application-key",
            "light-1",
            new DeviceStateChange(Brightness: 42));

        Assert.Equal(DeviceOperationStatus.Succeeded, result.Status);
        Assert.Equal(new DeviceState(true, 42), result.State);
    }

    [Fact]
    public async Task ColourCapableLightsAreImportedAndReceiveClipColourPayload()
    {
        var handler = new QueueHandler(
            request =>
            {
                var payload = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
                Assert.Contains("\"color\"", payload, StringComparison.Ordinal);
                Assert.Contains("\"x\":0.168", payload, StringComparison.Ordinal);
                return JsonResponse("""{"data":[{"id":"light-1"}]}""");
            },
            _ => JsonResponse(
                """{"data":[{"id":"light-1","metadata":{"name":"Kitchen"},"color":{"xy":{"x":0.168,"y":0.041}}}]}"""),
            _ => JsonResponse(
                """{"data":[{"id":"light-1","metadata":{"name":"Kitchen"},"color":{"xy":{"x":0.168,"y":0.041}}}]}"""));
        using var client = new HttpClient(handler);
        var hue = new HueBridgeClient(client);

        var updated = await hue.SetLightStateAsync(
            new Uri("https://bridge.local/"),
            "application-key",
            "light-1",
            new DeviceStateChange(Colour: new DeviceColour(0.168, 0.041)));
        var imported = await hue.GetLightsAsync(new Uri("https://bridge.local/"), "application-key");
        var light = Assert.Single(imported.Lights);

        Assert.True(light.Capabilities.HasFlag(DeviceCapabilities.Colour));
        Assert.Equal(new DeviceColour(0.168, 0.041), light.State.Colour);
        Assert.Equal(new DeviceColour(0.168, 0.041), updated.State!.Colour);
    }

    [Fact]
    public async Task ColourCapabilityDoesNotDependOnAReportedCurrentXyValue()
    {
        using var client = CreateClient(
            """{"data":[{"id":"light-1","metadata":{"name":"Kitchen"},"color":{"gamut_type":"C"}}]}""");
        var hue = new HueBridgeClient(client);

        var result = await hue.GetLightsAsync(new Uri("https://bridge.local/"), "application-key");

        var light = Assert.Single(result.Lights);
        Assert.True(light.Capabilities.HasFlag(DeviceCapabilities.Colour));
        Assert.Null(light.State.Colour);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, DeviceOperationStatus.Unauthorized)]
    [InlineData(HttpStatusCode.NotFound, DeviceOperationStatus.ResourceNotFound)]
    public async Task GetLightStateMapsHttpFailures(
        HttpStatusCode statusCode,
        DeviceOperationStatus expected)
    {
        using var client = new HttpClient(new QueueHandler(_ => new HttpResponseMessage(statusCode)));
        var hue = new HueBridgeClient(client);

        var result = await hue.GetLightStateAsync(
            new Uri("https://bridge.local/"),
            "application-key",
            "light-1");

        Assert.Equal(expected, result.Status);
    }

    [Fact]
    public async Task RequestTimeoutMapsToTypedTimeout()
    {
        using var client = new HttpClient(new QueueHandler(_ => throw new TaskCanceledException()));
        var hue = new HueBridgeClient(client);

        var result = await hue.GetLightStateAsync(
            new Uri("https://bridge.local/"),
            "application-key",
            "light-1");

        Assert.Equal(DeviceOperationStatus.TimedOut, result.Status);
    }

    private static HttpClient CreateClient(string content) =>
        new(new QueueHandler(_ => JsonResponse(content)));

    private static HttpResponseMessage JsonResponse(string content) =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(content, Encoding.UTF8, "application/json"),
        };

    private sealed class QueueHandler(params Func<HttpRequestMessage, HttpResponseMessage>[] responses)
        : HttpMessageHandler
    {
        private readonly Queue<Func<HttpRequestMessage, HttpResponseMessage>> _responses =
            new(responses);

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            if (_responses.Count == 0)
            {
                throw new InvalidOperationException("No stub response remains.");
            }

            return Task.FromResult(_responses.Dequeue()(request));
        }
    }
}
