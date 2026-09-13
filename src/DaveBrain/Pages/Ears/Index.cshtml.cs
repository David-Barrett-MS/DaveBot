using DaveIdentity;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace DaveBrain.Pages.Ears;

[Authorize(Policy = DaveIdentityPolicies.EarManagement)]
public sealed class IndexModel(EarEnrollmentService enrollmentService) : PageModel
{
    public IReadOnlyList<EarRecord> Ears { get; private set; } = Array.Empty<EarRecord>();

    public void OnGet() => LoadEars();

    public async Task<IActionResult> OnPostAsync(Guid id, CancellationToken cancellationToken)
    {
        try
        {
            await enrollmentService.RevokeAsync(id, cancellationToken);
        }
        catch (InvalidOperationException exception)
        {
            ModelState.AddModelError(string.Empty, exception.Message);
            LoadEars();
            return Page();
        }

        return RedirectToPage();
    }

    private void LoadEars() => Ears = enrollmentService.GetEars();
}
