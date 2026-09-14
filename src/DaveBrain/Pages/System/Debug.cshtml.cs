using DaveIdentity;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace DaveBrain.Pages.System;

[Authorize(Policy = DaveIdentityPolicies.ConfigurationManagement)]
public sealed class DebugModel(AdminSessionDebugLog debugLog) : PageModel
{
    public IReadOnlyList<AdminSessionDebugEntry> Entries { get; private set; } =
        Array.Empty<AdminSessionDebugEntry>();

    public void OnGet() => Load();

    public void OnPostClear()
    {
        debugLog.Clear(debugLog.GetOrCreateSessionId(HttpContext));
        Load();
    }

    private void Load() =>
        Entries = debugLog.Read(debugLog.GetOrCreateSessionId(HttpContext));
}
