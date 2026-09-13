using System.ComponentModel.DataAnnotations;
using DaveHome;
using DaveIdentity;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace DaveBrain.Pages.Commands;

[Authorize(Policy = DaveIdentityPolicies.DiagnosticCommands)]
public sealed class IndexModel(
    ICommandProcessor commandProcessor,
    IHomeStateStore homeStateStore,
    ICommandAuditStore commandAuditStore) : PageModel
{
    [BindProperty]
    [StringLength(TextCommandNormalizer.MaximumTranscriptLength)]
    public string Transcript { get; set; } = string.Empty;

    [BindProperty]
    public Guid? SourceLocationId { get; set; }

    [BindProperty]
    [StringLength(200)]
    public string NewAlias { get; set; } = string.Empty;

    [BindProperty]
    public Guid NewAliasLocationId { get; set; }

    public CommandExecutionResult? Result { get; private set; }

    public IReadOnlyList<Location> Locations { get; private set; } = Array.Empty<Location>();

    public IReadOnlyList<CommandAlias> Aliases { get; private set; } = Array.Empty<CommandAlias>();

    public IReadOnlyList<CommandAuditEntry> RecentCommands { get; private set; } = Array.Empty<CommandAuditEntry>();

    public void OnGet() => Load();

    public async Task<IActionResult> OnPostRunAsync(CancellationToken cancellationToken)
    {
        if (Transcript.Length > TextCommandNormalizer.MaximumTranscriptLength)
        {
            ModelState.AddModelError(
                nameof(Transcript),
                $"Commands cannot exceed {TextCommandNormalizer.MaximumTranscriptLength} characters.");
            Load();
            return Page();
        }

        Result = await commandProcessor.ProcessAsync(
            Transcript,
            new CommandSource("admin", SourceLocationId),
            cancellationToken);
        Load();
        return Page();
    }

    public async Task<IActionResult> OnPostAddAliasAsync(CancellationToken cancellationToken)
    {
        if (!ModelState.IsValid)
        {
            Load();
            return Page();
        }

        if (!await UpdateAliasesAsync(
            aliases => aliases.Append(new CommandAlias(
                Guid.NewGuid(),
                NewAlias.Trim(),
                NewAliasLocationId)).ToArray(),
            cancellationToken))
        {
            return Page();
        }

        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostUpdateAliasAsync(
        Guid id,
        string alias,
        Guid locationId,
        CancellationToken cancellationToken)
    {
        if (!await UpdateAliasesAsync(
            aliases => aliases.Select(candidate => candidate.Id == id
                ? candidate with { Alias = alias.Trim(), LocationId = locationId }
                : candidate).ToArray(),
            cancellationToken))
        {
            return Page();
        }

        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostDeleteAliasAsync(Guid id, CancellationToken cancellationToken)
    {
        if (!await UpdateAliasesAsync(
            aliases => aliases.Where(candidate => candidate.Id != id).ToArray(),
            cancellationToken))
        {
            return Page();
        }

        return RedirectToPage();
    }

    private async Task<bool> UpdateAliasesAsync(
        Func<IReadOnlyList<CommandAlias>, IReadOnlyList<CommandAlias>> update,
        CancellationToken cancellationToken)
    {
        try
        {
            await homeStateStore.UpdateAsync(state => state.Document with
            {
                CommandAliases = update(state.Document.CommandAliases),
            }, homeStateStore.Current.Revision, cancellationToken);
            return true;
        }
        catch (InvalidOperationException exception)
        {
            ModelState.AddModelError(string.Empty, exception.Message);
            Load();
            return false;
        }
    }

    private void Load()
    {
        Locations = homeStateStore.Current.Document.Locations
            .OrderBy(location => location.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        Aliases = homeStateStore.Current.Document.CommandAliases
            .OrderBy(alias => alias.Alias, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        RecentCommands = commandAuditStore.ReadRecent(50);
    }
}
