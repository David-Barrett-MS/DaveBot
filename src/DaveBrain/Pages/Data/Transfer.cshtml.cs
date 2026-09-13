using System.ComponentModel.DataAnnotations;
using System.Text;
using System.Text.Json;
using DaveHome;
using DaveIdentity;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace DaveBrain.Pages.Data;

[Authorize(Policy = DaveIdentityPolicies.ConfigurationManagement)]
public sealed class TransferModel(HomeDataTransferService transfer) : PageModel
{
    private const long MaximumImportLength = 2 * 1024 * 1024;

    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    [BindProperty]
    [Required]
    [Display(Name = "Home data JSON file")]
    public IFormFile? ImportFile { get; set; }

    public void OnGet()
    {
    }

    public IActionResult OnGetExport()
    {
        var document = transfer.Export();
        var content = JsonSerializer.Serialize(document, SerializerOptions);
        return File(
            Encoding.UTF8.GetBytes(content),
            "application/json",
            $"davebot-home-data-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}.json");
    }

    public async Task<IActionResult> OnPostAsync(CancellationToken cancellationToken)
    {
        if (!ModelState.IsValid || ImportFile is null)
        {
            return Page();
        }

        if (ImportFile.Length == 0 || ImportFile.Length > MaximumImportLength)
        {
            ModelState.AddModelError(
                nameof(ImportFile),
                "The import file must be between 1 byte and 2 MB.");
            return Page();
        }

        try
        {
            await using var stream = ImportFile.OpenReadStream();
            var document = await JsonSerializer.DeserializeAsync<HomeDataExportDocument>(
                stream,
                SerializerOptions,
                cancellationToken);
            if (document is null)
            {
                ModelState.AddModelError(nameof(ImportFile), "The import file is empty.");
                return Page();
            }

            var result = await transfer.ImportAsync(document, cancellationToken);
            TempData["StatusMessage"] =
                $"Imported {result.ImportedLocations} location(s) and {result.ImportedDevices} device(s).";
            return RedirectToPage();
        }
        catch (JsonException)
        {
            ModelState.AddModelError(nameof(ImportFile), "The import file is not valid home-data JSON.");
            return Page();
        }
        catch (InvalidOperationException exception)
        {
            ModelState.AddModelError(nameof(ImportFile), exception.Message);
            return Page();
        }
    }
}
