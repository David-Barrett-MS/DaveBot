using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace DaveBrain.Pages.Identity;

[AllowAnonymous]
public sealed class AccessDeniedModel : PageModel
{
    public void OnGet()
    {
    }
}
