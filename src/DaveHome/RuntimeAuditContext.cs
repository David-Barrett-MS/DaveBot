namespace DaveHome;

public interface IRuntimeAuditSink
{
    void Write(string category, string request, string response);
}

public static class RuntimeAuditContext
{
    private static readonly AsyncLocal<IRuntimeAuditSink?> Current = new();

    public static IDisposable Begin(IRuntimeAuditSink sink)
    {
        var previous = Current.Value;
        Current.Value = sink;
        return new Scope(previous);
    }

    public static void Write(string category, string request, string response) =>
        Current.Value?.Write(category, request, response);

    private sealed class Scope(IRuntimeAuditSink? previous) : IDisposable
    {
        public void Dispose() => Current.Value = previous;
    }
}
