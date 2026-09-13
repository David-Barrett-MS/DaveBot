using DaveHome;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace DaveBrain.Pages;

[Authorize(Policy = DaveIdentity.DaveIdentityPolicies.DashboardRead)]
public sealed class IndexModel(IHomeStateStore homeStateStore) : PageModel
{
    public int LocationCount { get; private set; }

    public int DeviceCount { get; private set; }

    public void OnGet()
    {
        LocationCount = homeStateStore.Current.LocationsById.Count;
        DeviceCount = homeStateStore.Current.DevicesById.Count;
    }
}
