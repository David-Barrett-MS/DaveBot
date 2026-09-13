using System.Globalization;
using DaveHome;
using DaveIdentity;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace DaveBrain.Pages.Automation;

[Authorize(Policy = DaveIdentityPolicies.ConfigurationManagement)]
public sealed class IndexModel(
    AutomationConfigurationService configuration,
    IHomeStateStore homeStateStore,
    IAutomationAuditStore auditStore) : PageModel
{
    public IReadOnlyList<Device> Devices { get; private set; } = Array.Empty<Device>();

    public IReadOnlyList<AutomationAction> Actions { get; private set; } =
        Array.Empty<AutomationAction>();

    public IReadOnlyList<AutomationActionSet> ActionSets { get; private set; } =
        Array.Empty<AutomationActionSet>();

    public IReadOnlyList<AutomationTrigger> Triggers { get; private set; } =
        Array.Empty<AutomationTrigger>();

    public AutomationValidationPreview Preview { get; private set; } =
        new(Array.Empty<AutomationTriggerPreview>(), Array.Empty<string>());

    public IReadOnlyList<AutomationAuditEntry> RecentAudit { get; private set; } =
        Array.Empty<AutomationAuditEntry>();

    public void OnGet() => Load();

    public async Task<IActionResult> OnPostSaveActionAsync(
        Guid? id,
        string? name,
        Guid targetDeviceId,
        string? isOn,
        int? brightness,
        double? colourX,
        double? colourY,
        bool enabled,
        CancellationToken cancellationToken)
    {
        if (!RequireValue(name, "Action name") ||
            !TryCreateStateChange(isOn, brightness, colourX, colourY, out var change))
        {
            Load();
            return Page();
        }

        try
        {
            await configuration.SaveActionAsync(
                new AutomationAction(
                    id.GetValueOrDefault(Guid.NewGuid()),
                    name!.Trim(),
                    AutomationActionTypes.DeviceSetState,
                    targetDeviceId,
                    change!,
                    enabled),
                cancellationToken);
            return RedirectToPage();
        }
        catch (InvalidOperationException exception)
        {
            ModelState.AddModelError(string.Empty, exception.Message);
            Load();
            return Page();
        }
    }

    public async Task<IActionResult> OnPostSaveActionSetAsync(
        Guid? id,
        string? name,
        string? actionIds,
        bool enabled,
        CancellationToken cancellationToken)
    {
        if (!RequireValue(name, "Action-set name") ||
            !TryParseIds(actionIds, "action IDs", out var parsedActionIds))
        {
            Load();
            return Page();
        }

        try
        {
            await configuration.SaveActionSetAsync(
                new AutomationActionSet(
                    id.GetValueOrDefault(Guid.NewGuid()),
                    name!.Trim(),
                    parsedActionIds,
                    enabled),
                cancellationToken);
            return RedirectToPage();
        }
        catch (InvalidOperationException exception)
        {
            ModelState.AddModelError(string.Empty, exception.Message);
            Load();
            return Page();
        }
    }

    public async Task<IActionResult> OnPostSaveTriggerAsync(
        Guid? id,
        string? name,
        Guid sourceDeviceId,
        string? stateCriteriaJson,
        string? actionSetIds,
        int cooldownSeconds,
        bool enabled,
        CancellationToken cancellationToken)
    {
        if (!RequireValue(name, "Trigger name") ||
            !RequireValue(stateCriteriaJson, "State criteria") ||
            !TryParseIds(actionSetIds, "action set IDs", out var parsedActionSetIds))
        {
            Load();
            return Page();
        }

        try
        {
            await configuration.SaveTriggerAsync(
                new AutomationTrigger(
                    id.GetValueOrDefault(Guid.NewGuid()),
                    name!.Trim(),
                    sourceDeviceId,
                    stateCriteriaJson!.Trim(),
                    parsedActionSetIds,
                    cooldownSeconds,
                    enabled),
                cancellationToken);
            return RedirectToPage();
        }
        catch (InvalidOperationException exception)
        {
            ModelState.AddModelError(string.Empty, exception.Message);
            Load();
            return Page();
        }
    }

    public Task<IActionResult> OnPostDeleteActionAsync(
        Guid id,
        CancellationToken cancellationToken) =>
        DeleteAsync(() => configuration.DeleteActionAsync(id, cancellationToken), cancellationToken);

    public Task<IActionResult> OnPostDeleteActionSetAsync(
        Guid id,
        CancellationToken cancellationToken) =>
        DeleteAsync(() => configuration.DeleteActionSetAsync(id, cancellationToken), cancellationToken);

    public Task<IActionResult> OnPostDeleteTriggerAsync(
        Guid id,
        CancellationToken cancellationToken) =>
        DeleteAsync(() => configuration.DeleteTriggerAsync(id, cancellationToken), cancellationToken);

    private async Task<IActionResult> DeleteAsync(
        Func<Task> delete,
        CancellationToken cancellationToken)
    {
        try
        {
            await delete();
            return RedirectToPage();
        }
        catch (InvalidOperationException exception)
        {
            ModelState.AddModelError(string.Empty, exception.Message);
            Load();
            return Page();
        }
    }

    private bool TryCreateStateChange(
        string? isOn,
        int? brightness,
        double? colourX,
        double? colourY,
        out DeviceStateChange? change)
    {
        bool? parsedIsOn = isOn switch
        {
            "" or null => null,
            "true" => true,
            "false" => false,
            _ => null,
        };
        if (isOn is not ("" or null or "true" or "false"))
        {
            ModelState.AddModelError(nameof(isOn), "On/off must be unchanged, on, or off.");
        }

        DeviceColour? colour = null;
        if (colourX is not null || colourY is not null)
        {
            if (colourX is null || colourY is null ||
                colourX is < 0 or > 1 || colourY is < 0 or > 1)
            {
                ModelState.AddModelError(nameof(colourX), "Colour coordinates must both be between 0 and 1.");
            }
            else
            {
                colour = new DeviceColour(colourX.Value, colourY.Value);
            }
        }

        if (brightness is < 0 or > 100)
        {
            ModelState.AddModelError(nameof(brightness), "Brightness must be between 0 and 100.");
        }

        change = new DeviceStateChange(parsedIsOn, brightness, colour);
        if (change is { IsOn: null, Brightness: null, Colour: null })
        {
            ModelState.AddModelError(string.Empty, "Choose at least one deterministic state change.");
        }

        return ModelState.IsValid;
    }

    private bool RequireValue(string? value, string displayName)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            return true;
        }

        ModelState.AddModelError(string.Empty, $"{displayName} is required.");
        return false;
    }

    private bool TryParseIds(string? input, string displayName, out IReadOnlyList<Guid> ids)
    {
        var values = (input ?? string.Empty)
            .Split([',', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (values.Length is < 1 or > 100 ||
            values.Any(value => !Guid.TryParse(value, out _)))
        {
            ModelState.AddModelError(
                string.Empty,
                $"Provide between 1 and 100 comma-separated {displayName}.");
            ids = Array.Empty<Guid>();
            return false;
        }

        ids = values.Select(Guid.Parse).ToArray();
        if (ids.Distinct().Count() != ids.Count)
        {
            ModelState.AddModelError(string.Empty, $"{displayName} cannot be repeated.");
            return false;
        }

        return true;
    }

    private void Load()
    {
        var document = homeStateStore.Current.Document;
        Devices = document.Devices
            .OrderBy(device => device.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        Actions = document.AutomationActions
            .OrderBy(action => action.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        ActionSets = document.AutomationActionSets
            .OrderBy(actionSet => actionSet.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        Triggers = document.AutomationTriggers
            .OrderBy(trigger => trigger.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        Preview = configuration.GetPreview();
        RecentAudit = auditStore.ReadRecent(50);
    }
}
