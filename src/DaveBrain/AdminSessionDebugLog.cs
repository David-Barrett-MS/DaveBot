using System.Collections.Concurrent;
using DaveHome;
using Microsoft.AspNetCore.Http;

namespace DaveBrain;

public sealed record AdminSessionDebugEntry(
    long Sequence,
    DateTimeOffset OccurredUtc,
    string Category,
    string Request,
    string Response);

public sealed class AdminSessionDebugLog
{
    public const string CookieName = "DaveBot.AdminDebugSession";
    private const int MaximumEntries = 250;
    private readonly ConcurrentDictionary<string, SessionEntries> sessions = new(StringComparer.Ordinal);

    public string GetOrCreateSessionId(HttpContext context)
    {
        if (context.Request.Cookies.TryGetValue(CookieName, out var sessionId) &&
            Guid.TryParseExact(sessionId, "N", out _))
        {
            return sessionId;
        }

        sessionId = Guid.NewGuid().ToString("N");
        context.Response.Cookies.Append(CookieName, sessionId, new CookieOptions
        {
            HttpOnly = true,
            IsEssential = true,
            SameSite = SameSiteMode.Lax,
            Secure = context.Request.IsHttps,
        });
        return sessionId;
    }

    public IRuntimeAuditSink CreateSink(string sessionId) => new SessionAuditSink(this, sessionId);

    public IReadOnlyList<AdminSessionDebugEntry> Read(string sessionId) =>
        sessions.TryGetValue(sessionId, out var entries) ? entries.Read() : Array.Empty<AdminSessionDebugEntry>();

    public void Clear(string sessionId) => sessions.TryRemove(sessionId, out _);

    private void Write(string sessionId, string category, string request, string response)
    {
        var entries = sessions.GetOrAdd(sessionId, _ => new SessionEntries());
        entries.Write(category, Redact(request), Redact(response));
    }

    private static string Redact(string value)
    {
        foreach (var name in new[]
                 {
                     "authorization", "token", "signature", "thingname", "password", "code", "email",
                     "serial",
                 })
        {
            value = System.Text.RegularExpressions.Regex.Replace(
                value,
                $"(\"[^\"]*{name}[^\"]*\"\\s*:\\s*)\"[^\"]*\"",
                "$1\"[redacted]\"",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        }

        value = System.Text.RegularExpressions.Regex.Replace(
            value,
            """(^|[?&\r\n])([^=&\s]*(?:authorization|token|signature|thingname|password|code|email)[^=&\s]*)=[^&\s]*""",
            "$1$2=[redacted]",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        return value.Length <= 65_536 ? value : value[..65_536] + "\n[truncated]";
    }

    private sealed class SessionAuditSink(AdminSessionDebugLog owner, string sessionId) : IRuntimeAuditSink
    {
        public void Write(string category, string request, string response) =>
            owner.Write(sessionId, category, request, response);
    }

    private sealed class SessionEntries
    {
        private readonly Queue<AdminSessionDebugEntry> entries = new();
        private long sequence;

        public void Write(string category, string request, string response)
        {
            lock (entries)
            {
                entries.Enqueue(new AdminSessionDebugEntry(
                    ++sequence, DateTimeOffset.UtcNow, category, request, response));
                while (entries.Count > MaximumEntries)
                {
                    entries.Dequeue();
                }
            }
        }

        public IReadOnlyList<AdminSessionDebugEntry> Read()
        {
            lock (entries)
            {
                return entries.ToArray();
            }
        }
    }
}
