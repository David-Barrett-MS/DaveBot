using DaveIdentity;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace DaveBrain.Pages.System;

[Authorize(Policy = DaveIdentityPolicies.ConfigurationManagement)]
public sealed class BackupModel : PageModel
{
    public void OnGet()
    {
    }
}
