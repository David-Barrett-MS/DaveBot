using DaveBrain;
using DaveHome;

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
                """{"token":"secret","email":"admin@example.test"}""",
                """{"thingName":"private-device","pm25":12}""");
        }

        var entry = Assert.Single(log.Read("browser-session"));
        Assert.Contains("\"token\":\"[redacted]\"", entry.Request, StringComparison.Ordinal);
        Assert.Contains("\"email\":\"[redacted]\"", entry.Request, StringComparison.Ordinal);
        Assert.Contains("\"thingName\":\"[redacted]\"", entry.Response, StringComparison.Ordinal);
        Assert.Empty(log.Read("another-browser-session"));
    }
}
