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
    public async Task GetSwitchesMapsClipV2ButtonEvent()
    {
        using var client = new HttpClient(new QueueHandler(
            request =>
            {
                Assert.Equal("/clip/v2/resource/button", request.RequestUri!.AbsolutePath);
                return JsonResponse(
                    """{"data":[{"id":"button-1","owner":{"rid":"device-1","rtype":"device"},"metadata":{"control_id":1},"button":{"last_event":"initial_press","last_event_date":"2026-01-01T10:00:00Z"}}]}""");
            },
            request =>
            {
                Assert.Equal("/clip/v2/resource/device", request.RequestUri!.AbsolutePath);
                return JsonResponse(
                    """{"data":[{"id":"device-1","metadata":{"name":"Kitchen dimmer"},"services":[{"rid":"button-1","rtype":"button"}]}]}""");
            }));
        var hue = new HueBridgeClient(client);

        var result = await hue.GetSwitchesAsync(new Uri("https://bridge.local/"), "application-key");

        Assert.True(result.Succeeded);
        var switchResource = Assert.Single(result.Switches);
        Assert.Equal("button-1", switchResource.ResourceId);
        Assert.Equal("Kitchen dimmer - Button 1", switchResource.Name);
        Assert.Equal("initial_press", switchResource.State.Attributes!["event"]);
        Assert.Equal("1", switchResource.State.Attributes["button"]);
        Assert.Equal("button-1:2026-01-01T10:00:00Z", switchResource.EventId);
    }

    [Fact]
    public async Task GetSwitchesUsesDeviceServicesWhenButtonDoesNotDeclareAnOwner()
    {
        using var client = new HttpClient(new QueueHandler(
            request =>
            {
                Assert.Equal("/clip/v2/resource/button", request.RequestUri!.AbsolutePath);
                return JsonResponse(
                    """{"data":[{"id":"button-2","metadata":{"control_id":2},"button":{"last_event":"initial_press","last_event_date":"2026-01-01T10:00:00Z"}}]}""");
            },
            request =>
            {
                Assert.Equal("/clip/v2/resource/device", request.RequestUri!.AbsolutePath);
                return JsonResponse(
                    """{"data":[{"id":"device-2","metadata":{"name":"Hallway switch"},"services":[{"rid":"button-2","rtype":"button"}]}]}""");
            }));
        var hue = new HueBridgeClient(client);

        var result = await hue.GetSwitchesAsync(new Uri("https://bridge.local/"), "application-key");

        var switchResource = Assert.Single(result.Switches);
        Assert.Equal("Hallway switch - Button 2", switchResource.Name);
    }

    [Fact]
    public async Task GetMotionSensorsCorrelatesAmbientLightAndUsesDeviceMetadataName()
    {
        using var client = new HttpClient(new QueueHandler(
            request =>
            {
                Assert.Equal("/clip/v2/resource/motion", request.RequestUri!.AbsolutePath);
                return JsonResponse(
                    """{"data":[{"id":"motion-1","owner":{"rid":"device-1","rtype":"device"},"motion":{"motion":true,"motion_valid":true}}]}""");
            },
            request =>
            {
                Assert.Equal("/clip/v2/resource/light_level", request.RequestUri!.AbsolutePath);
                return JsonResponse(
                    """{"data":[{"id":"level-1","owner":{"rid":"device-1","rtype":"device"},"light_level":{"light_level":5000,"light_level_valid":true}}]}""");
            },
            request =>
            {
                Assert.Equal("/clip/v2/resource/device", request.RequestUri!.AbsolutePath);
                return JsonResponse(
                    """{"data":[{"id":"device-1","metadata":{"name":"Kitchen motion sensor"},"services":[{"rid":"motion-1","rtype":"motion"},{"rid":"level-1","rtype":"light_level"}]}]}""");
            }));
        var hue = new HueBridgeClient(client);

        var result = await hue.GetMotionSensorsAsync(
            new Uri("https://bridge.local/"),
            "application-key");

        Assert.True(result.Succeeded);
        var sensor = Assert.Single(result.MotionSensors);
        Assert.Equal("Kitchen motion sensor", sensor.Name);
        Assert.Equal("motion-1", sensor.MotionResourceId);
        Assert.Equal("level-1", sensor.LightLevelResourceId);
        Assert.Equal("motion:motion-1;lightLevel:level-1", sensor.ResourceId);
        Assert.Equal("true", sensor.State.Attributes!["motion"]);
        Assert.Equal("5000", sensor.State.Attributes["lightLevel"]);
        Assert.Equal("true", sensor.State.Attributes["lightLevelAvailable"]);
        Assert.NotNull(sensor.EventId);
    }

    [Fact]
    public void EventStreamParserNormalizesMotionAndLightLevelPartialUpdates()
    {
        var resources = HueEventStreamParser.ParseResources(
            """
            [{"creationtime":"2026-01-01T10:00:01Z","data":[{"id":"motion-1","type":"motion","motion":{"motion":true,"motion_valid":true}},{"id":"level-1","type":"light_level","light_level":{"light_level":5000,"light_level_valid":true}}],"type":"update"}]
            """);

        Assert.Collection(
            resources,
            motion =>
            {
                Assert.Equal("motion", motion.ResourceType);
                Assert.Equal("true", motion.State.Attributes!["motion"]);
                Assert.NotNull(motion.EventId);
            },
            lightLevel =>
            {
                Assert.Equal("light_level", lightLevel.ResourceType);
                Assert.Equal("5000", lightLevel.State.Attributes!["lightLevel"]);
                Assert.NotEqual(resources[0].EventId, lightLevel.EventId);
            });
    }

    [Fact]
    public async Task GetMotionSensorsAllowsSensorsWithoutALightLevelResource()
    {
        using var client = new HttpClient(new QueueHandler(
            _ => JsonResponse(
                """{"data":[{"id":"motion-1","motion":{"motion":false,"motion_valid":true}}]}"""),
            _ => JsonResponse("""{"data":[]}"""),
            _ => JsonResponse("""{"data":[]}""")));
        var hue = new HueBridgeClient(client);

        var result = await hue.GetMotionSensorsAsync(
            new Uri("https://bridge.local/"),
            "application-key");

        var sensor = Assert.Single(result.MotionSensors);
        Assert.Null(sensor.LightLevelResourceId);
        Assert.Equal("motion:motion-1", sensor.ResourceId);
        Assert.Equal("false", sensor.State.Attributes!["motion"]);
        Assert.Equal("false", sensor.State.Attributes["lightLevelAvailable"]);
        Assert.StartsWith("Hue motion sensor ", sensor.Name, StringComparison.Ordinal);
    }

    [Fact]
    public async Task EventStreamUsesClipV2EndpointApplicationKeyAndParsesButtonBatch()
    {
        using var client = new HttpClient(new QueueHandler(request =>
        {
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Equal("/eventstream/clip/v2", request.RequestUri!.AbsolutePath);
            Assert.Equal("application-key", request.Headers.GetValues("hue-application-key").Single());
            Assert.Contains(
                request.Headers.Accept,
                value => string.Equals(value.MediaType, "text/event-stream", StringComparison.Ordinal));
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """
                    event: update
                    data: [{"creationtime":"2026-01-01T10:00:01Z","data":[{"id":"button-1","type":"button","metadata":{"control_id":1},"button":{"last_event":"short_release","last_event_date":"2026-01-01T10:00:01Z"}},{"id":"light-1","type":"light","on":{"on":true}}]}]

                    """,
                    Encoding.UTF8,
                    "text/event-stream"),
            };
        }));
        var hue = new HueBridgeClient(client);

        await using var events = hue.ReadEventStreamAsync(
            new Uri("https://bridge.local/"),
            "application-key").GetAsyncEnumerator();

        Assert.True(await events.MoveNextAsync());
        var button = events.Current;
        Assert.Equal("button-1", button.ResourceId);
        Assert.Equal("button-1:2026-01-01T10:00:01Z", button.EventId);
        Assert.Equal("short_release", button.State.Attributes!["event"]);
        Assert.Equal("1", button.State.Attributes["button"]);
    }

    [Fact]
    public void EventStreamParserAcceptsRealisticMultiEnvelopeUpdates()
    {
        var buttons = HueEventStreamParser.Parse(
            """
            [{"creationtime":"2026-01-01T10:00:01Z","data":[{"id":"button-1","type":"button","metadata":{"control_id":1},"button":{"last_event":"initial_press","last_event_date":"2026-01-01T10:00:01Z"}}],"type":"update"},{"creationtime":"2026-01-01T10:00:02Z","data":[{"id":"button-2","type":"button","metadata":{"control_id":2},"button":{"last_event":"repeat","last_event_date":"2026-01-01T10:00:02Z"}}],"type":"update"}]
            """);

        Assert.Collection(
            buttons,
            first => Assert.Equal("button-1:2026-01-01T10:00:01Z", first.EventId),
            second =>
            {
                Assert.Equal("button-2:2026-01-01T10:00:02Z", second.EventId);
                Assert.Equal("repeat", second.State.Attributes!["event"]);
            });
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
