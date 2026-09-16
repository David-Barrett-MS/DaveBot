using DaveIdentity;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace DaveBrain.Pages.System;

[Authorize(Policy = DaveIdentityPolicies.ConfigurationManagement)]
public sealed class DebugModel(
    AdminSessionDebugLog debugLog,
    DaveEarActivityLog earActivityLog) : PageModel
{
    public IReadOnlyList<AdminSessionDebugEntry> Entries { get; private set; } =
        Array.Empty<AdminSessionDebugEntry>();
    public IReadOnlyList<DaveEarActivityEntry> EarActivities { get; private set; } =
        Array.Empty<DaveEarActivityEntry>();

    public void OnGet() => Load();

    public void OnPostClear()
    {
        debugLog.Clear(debugLog.GetOrCreateSessionId(HttpContext));
        Load();
    }

    private void Load() =>
        (Entries, EarActivities) = (
            debugLog.Read(debugLog.GetOrCreateSessionId(HttpContext)),
            earActivityLog.Read());
}
