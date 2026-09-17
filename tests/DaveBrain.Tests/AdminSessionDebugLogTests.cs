using DaveBrain;
using DaveHome;
using DaveIdentity;

namespace DaveBrain.Tests;

public sealed class AdminSessionDebugLogTests
{
    [Fact]
    public void LogIsSessionScopedAndRedactsSecrets()
    {
        var log = new AdminSessionDebugLog();
        using (RuntimeAuditContext.Begin(log.CreateSink("browser-session")))
        {
            RuntimeAuditContext.Write(
                "External HTTP",
                """{"access_token":"secret","email":"admin@example.test","serial":"camera-serial"}""",
                """{"thingName":"private-device","pm25":12}""");
        }

        var entry = Assert.Single(log.Read("browser-session"));
        Assert.Contains("\"access_token\":\"[redacted]\"", entry.Request, StringComparison.Ordinal);
        Assert.Contains("\"email\":\"[redacted]\"", entry.Request, StringComparison.Ordinal);
        Assert.Contains("\"serial\":\"[redacted]\"", entry.Request, StringComparison.Ordinal);
        Assert.Contains("\"thingName\":\"[redacted]\"", entry.Response, StringComparison.Ordinal);
        Assert.Empty(log.Read("another-browser-session"));
    }

    [Fact]
    public void LogRedactsFormEncodedSecrets()
    {
        var log = new AdminSessionDebugLog();
        using (RuntimeAuditContext.Begin(log.CreateSink("browser-session")))
        {
            RuntimeAuditContext.Write(
                "External HTTP",
                "POST https://example.test/oauth/token\nrefresh_token=private&hardware_id=device",
                string.Empty);
        }

        var entry = Assert.Single(log.Read("browser-session"));
        Assert.Contains("refresh_token=[redacted]", entry.Request, StringComparison.Ordinal);
        Assert.DoesNotContain("private", entry.Request, StringComparison.Ordinal);
    }

    [Fact]
    public async Task BinaryImageResponsesAreAuditedWithoutConsumingTheirContent()
    {
        var log = new AdminSessionDebugLog();
        using var handler = new AdminSessionDebugHttpMessageHandler
        {
            InnerHandler = new ImageResponseHandler(),
        };
        using var invoker = new HttpMessageInvoker(handler);
        using (RuntimeAuditContext.Begin(log.CreateSink("browser-session")))
        using (var response = await invoker.SendAsync(
                   new HttpRequestMessage(HttpMethod.Get, "https://example.test/snapshot"),
                   CancellationToken.None))
        {
            Assert.Equal([1, 2, 3], await response.Content.ReadAsByteArrayAsync());
        }

        var entry = Assert.Single(log.Read("browser-session"));
        Assert.Contains("[binary image; 3 bytes]", entry.Response, StringComparison.Ordinal);
        Assert.DoesNotContain("\u0001", entry.Response, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SuppressedExternalRequestsAreNotWrittenToTheDebugLog()
    {
        var log = new AdminSessionDebugLog();
        using var handler = new AdminSessionDebugHttpMessageHandler
        {
            InnerHandler = new ImageResponseHandler(),
        };
        using var invoker = new HttpMessageInvoker(handler);
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://live.example.test/liveview")
        {
            Content = new StringContent("""{"liveview_token":"must-not-log"}"""),
        };
        request.Options.Set(new HttpRequestOptionsKey<bool>("DaveBot.SuppressDebugLogging"), true);

        using (RuntimeAuditContext.Begin(log.CreateSink("browser-session")))
        using (var response = await invoker.SendAsync(request, CancellationToken.None))
        {
            Assert.True(response.IsSuccessStatusCode);
        }

        Assert.Empty(log.Read("browser-session"));
    }

    [Fact]
    public void EarActivityRecordsLifecycleAndCommands()
    {
        var log = new DaveEarActivityLog();
        var ear = new EarRecord(
            Guid.NewGuid(),
            "Kitchen Ear",
            Guid.NewGuid(),
            "https://brain.example.test",
            "administrator-subject",
            "DaveEar",
            "certificate-thumbprint",
            DateTimeOffset.UtcNow,
            null,
            Enabled: true);

        log.RecordRegistered(ear);
        log.RecordConnected(ear);
        log.RecordConnected(ear);
        log.RecordCommandReceived(ear, "turn on the kitchen lights");
        log.RecordDisconnectedEars(DateTimeOffset.UtcNow.AddMinutes(3));

        var entries = log.Read();
        Assert.Equal(["Registered", "Connected", "Command received", "Disconnected"],
            entries.Select(entry => entry.Event));
        Assert.Equal("Speech command transcribed.", entries[2].Details);
        Assert.Equal("turn on the kitchen lights", entries[2].Transcript);
    }

    private sealed class ImageResponseHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var content = new ByteArrayContent([1, 2, 3]);
            content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("image/jpeg");
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = content,
            });
        }
    }
}
