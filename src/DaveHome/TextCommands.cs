using System.Text;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace DaveHome;

public sealed record NormalizedTextCommand(string OriginalText, string Transcript);

public enum TextCommandIntent
{
    Unknown,
    LightsOn,
    LightsOff,
    SetBrightness,
    SetColour,
    GetLightStatus,
    GetCameraSnapshot,
}

public enum CommandTargetStatus
{
    Resolved,
    Malformed,
    UnknownLocation,
    AmbiguousLocation,
    MissingSourceLocation,
    Unsupported,
    UnknownCamera,
    AmbiguousCamera,
}

public enum CommandOutcome
{
    Rejected,
    Completed,
    PartiallyCompleted,
    Failed,
}

public sealed record CommandParseResult(
    NormalizedTextCommand Command,
    TextCommandIntent Intent,
    string? LocationName = null,
    int? Brightness = null,
    DeviceColour? Colour = null,
    string? DeviceName = null)
{
    public bool Succeeded => Intent != TextCommandIntent.Unknown;
}

public sealed record ResolvedCommandIntent(
    CommandParseResult Parse,
    Guid? LocationId,
    bool UsedSourceLocation);

public sealed record CommandTarget(
    CommandTargetStatus Status,
    string Summary,
    IReadOnlyList<Device> Devices,
    IReadOnlyList<string>? Candidates = null);

public sealed record SafeUserMessage(string Code, string Text);

public sealed record CommandExecutionResult(
    Guid CorrelationId,
    string Transcript,
    TextCommandIntent Intent,
    string TargetSummary,
    CommandOutcome Outcome,
    SafeUserMessage Message,
    [property: JsonIgnore] DeviceSnapshot? Snapshot = null);

public sealed record CommandSource(
    string Source,
    Guid? LocationId = null,
    bool AllowsBrowserSnapshot = false);

public interface ICommandProcessor
{
    Task<CommandExecutionResult> ProcessAsync(
        string? transcript,
        CommandSource source,
        CancellationToken cancellationToken = default);
}

public static class TextCommandNormalizer
{
    public const int MaximumTranscriptLength = 500;

    public static NormalizedTextCommand Create(string? text)
    {
        var original = (text ?? string.Empty).Trim();
        if (original.Length > MaximumTranscriptLength)
        {
            original = original[..MaximumTranscriptLength];
        }

        return new NormalizedTextCommand(original, Normalize(original));
    }

    public static string Normalize(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        var builder = new StringBuilder(text.Length);
        foreach (var character in text.Trim().ToLowerInvariant())
        {
            builder.Append(char.IsLetterOrDigit(character) || char.IsWhiteSpace(character) || character == '%'
                ? character
                : ' ');
        }

        return string.Join(' ', builder.ToString().Split(
            (char[]?)null,
            StringSplitOptions.RemoveEmptyEntries));
    }
}

public static class TextCommandParser
{
    private static readonly Regex LightsPattern = new(
        "^lights (?<action>on|off)$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex DefaultTurnPattern = new(
        "^turn lights (?<action>on|off)$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex TurnPattern = new(
        "^turn (?<location>.+) lights (?<action>on|off)$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex BrightnessPattern = new(
        "^set (?<location>.+) lights to (?<brightness>\\d{1,3})(?:\\s*%| percent)$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex ColourPattern = new(
        "^set (?<location>.+) lights to (?<colour>red|green|blue|yellow|orange|purple|pink|white)$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex StatusPattern = new(
        "^are (?<location>.+) lights on$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex SnapshotFromPattern = new(
        "^(?:show|get) snapshot from (?<camera>.+)$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex CameraSnapshotPattern = new(
        "^(?:show|get) (?<camera>.+) camera snapshot$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static CommandParseResult Parse(string? transcript)
    {
        var command = TextCommandNormalizer.Create(transcript);
        if (command.OriginalText.Length == TextCommandNormalizer.MaximumTranscriptLength &&
            (transcript?.Trim().Length ?? 0) > command.OriginalText.Length)
        {
            return new CommandParseResult(command, TextCommandIntent.Unknown);
        }

        var lights = LightsPattern.Match(command.Transcript);
        if (lights.Success)
        {
            return new CommandParseResult(
                command,
                ToOnOffIntent(lights.Groups["action"].Value));
        }

        var defaultTurn = DefaultTurnPattern.Match(command.Transcript);
        if (defaultTurn.Success)
        {
            return new CommandParseResult(
                command,
                ToOnOffIntent(defaultTurn.Groups["action"].Value));
        }

        var turn = TurnPattern.Match(command.Transcript);
        if (turn.Success)
        {
            return new CommandParseResult(
                command,
                ToOnOffIntent(turn.Groups["action"].Value),
                turn.Groups["location"].Value);
        }

        var brightness = BrightnessPattern.Match(command.Transcript);
        if (brightness.Success &&
            int.TryParse(brightness.Groups["brightness"].Value, out var percentage) &&
            percentage is >= 0 and <= 100)
        {
            return new CommandParseResult(
                command,
                TextCommandIntent.SetBrightness,
                brightness.Groups["location"].Value,
                percentage);
        }

        var colour = ColourPattern.Match(command.Transcript);
        if (colour.Success && DeviceColourPalette.TryGet(colour.Groups["colour"].Value, out var value))
        {
            return new CommandParseResult(
                command,
                TextCommandIntent.SetColour,
                colour.Groups["location"].Value,
                Colour: value);
        }

        var snapshot = SnapshotFromPattern.Match(command.Transcript);
        if (!snapshot.Success)
        {
            snapshot = CameraSnapshotPattern.Match(command.Transcript);
        }

        if (snapshot.Success)
        {
            return new CommandParseResult(
                command,
                TextCommandIntent.GetCameraSnapshot,
                DeviceName: snapshot.Groups["camera"].Value);
        }

        var status = StatusPattern.Match(command.Transcript);
        return status.Success
            ? new CommandParseResult(
                command,
                TextCommandIntent.GetLightStatus,
                status.Groups["location"].Value)
            : new CommandParseResult(command, TextCommandIntent.Unknown);
    }

    private static TextCommandIntent ToOnOffIntent(string action) =>
        action == "on" ? TextCommandIntent.LightsOn : TextCommandIntent.LightsOff;
}

public static class DeviceColourPalette
{
    private static readonly IReadOnlyDictionary<string, DeviceColour> Colours =
        new Dictionary<string, DeviceColour>(StringComparer.Ordinal)
        {
            ["red"] = new(0.675, 0.322),
            ["green"] = new(0.409, 0.518),
            ["blue"] = new(0.168, 0.041),
            ["yellow"] = new(0.444, 0.517),
            ["orange"] = new(0.556, 0.409),
            ["purple"] = new(0.272, 0.109),
            ["pink"] = new(0.385, 0.155),
            ["white"] = new(0.313, 0.329),
        };

    public static bool TryGet(string name, out DeviceColour colour) =>
        Colours.TryGetValue(name, out colour!);
}

public sealed class CommandProcessor(
    IHomeStateStore homeStateStore,
    IEnumerable<IDeviceProvider> deviceProviders,
    ICommandAuditStore auditStore) : ICommandProcessor
{
    private const int MaximumAuditFieldLength = 500;

    private readonly IReadOnlyDictionary<string, IDeviceProvider> _providers = deviceProviders
        .GroupBy(provider => provider.DeviceType, StringComparer.Ordinal)
        .ToDictionary(group => group.Key, group => group.Single(), StringComparer.Ordinal);
    private readonly IReadOnlyDictionary<string, IDeviceSnapshotProvider> _snapshotProviders = deviceProviders
        .OfType<IDeviceSnapshotProvider>()
        .GroupBy(provider => provider.DeviceType, StringComparer.Ordinal)
        .ToDictionary(group => group.Key, group => group.Single(), StringComparer.Ordinal);

    public async Task<CommandExecutionResult> ProcessAsync(
        string? transcript,
        CommandSource source,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        var correlationId = Guid.NewGuid();
        var parse = TextCommandParser.Parse(transcript);
        CommandExecutionResult result;
        try
        {
            result = await ProcessParsedAsync(correlationId, parse, source, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            result = CreateResult(
                correlationId,
                parse,
                "No lights were changed.",
                CommandOutcome.Failed,
                new SafeUserMessage("processing-failed", "The command could not be completed."));
        }
        catch (HttpRequestException)
        {
            result = CreateResult(
                correlationId,
                parse,
                "No lights were changed.",
                CommandOutcome.Failed,
                new SafeUserMessage("processing-failed", "The command could not be completed."));
        }
        catch (IOException)
        {
            result = CreateResult(
                correlationId,
                parse,
                "No lights were changed.",
                CommandOutcome.Failed,
                new SafeUserMessage("processing-failed", "The command could not be completed."));
        }
        catch (InvalidOperationException)
        {
            result = CreateResult(
                correlationId,
                parse,
                "No lights were changed.",
                CommandOutcome.Failed,
                new SafeUserMessage("processing-failed", "The command could not be completed."));
        }

        await auditStore.WriteAsync(
            new CommandAuditEntry(
                correlationId,
                DateTimeOffset.UtcNow,
                BoundAuditField(source.Source),
                BoundAuditField($"{result.Intent}: {result.Outcome}; {result.TargetSummary}"),
                result.Outcome == CommandOutcome.Completed,
                parse.Command.OriginalText),
            cancellationToken);
        return result;
    }

    private async Task<CommandExecutionResult> ProcessParsedAsync(
        Guid correlationId,
        CommandParseResult parse,
        CommandSource source,
        CancellationToken cancellationToken)
    {
        if (!parse.Succeeded)
        {
            return CreateResult(
                correlationId,
                parse,
                "No target",
                CommandOutcome.Rejected,
                new SafeUserMessage("malformed", "I did not understand that command."));
        }

        if (parse.Intent == TextCommandIntent.GetCameraSnapshot && !source.AllowsBrowserSnapshot)
        {
            return CreateResult(
                correlationId,
                parse,
                "Browser display required",
                CommandOutcome.Rejected,
                new SafeUserMessage(
                    "snapshot-browser-only",
                    "Camera snapshots can only be displayed from the browser command page."));
        }

        var target = ResolveTarget(parse, source);
        if (target.Status != CommandTargetStatus.Resolved)
        {
            return CreateResult(
                correlationId,
                parse,
                target.Summary,
                CommandOutcome.Rejected,
                TargetMessage(target));
        }

        return parse.Intent switch
        {
            TextCommandIntent.GetLightStatus =>
                await ReadStatusAsync(correlationId, parse, target, cancellationToken),
            TextCommandIntent.GetCameraSnapshot =>
                await ReadSnapshotAsync(correlationId, parse, target, cancellationToken),
            _ => await SetStateAsync(correlationId, parse, target, cancellationToken),
        };
    }

    private CommandTarget ResolveTarget(CommandParseResult parse, CommandSource source)
    {
        if (parse.Intent == TextCommandIntent.GetCameraSnapshot)
        {
            return ResolveSnapshotTarget(parse);
        }

        var state = homeStateStore.Current;
        Location? location = null;
        DeviceGroup? deviceGroup = null;
        var usedSourceLocation = false;
        if (parse.LocationName is { } requestedName)
        {
            var requested = TextCommandNormalizer.Normalize(requestedName);
            var locationMatches = state.Document.Locations
                .Where(candidate => TextCommandNormalizer.Normalize(candidate.Name) == requested)
                .Concat(state.Document.CommandAliases
                    .Where(alias => TextCommandNormalizer.Normalize(alias.Alias) == requested)
                    .Select(alias => state.LocationsById[alias.LocationId]))
                .DistinctBy(candidate => candidate.Id)
                .ToArray();
            var groupMatches = state.Document.Groups
                .Where(candidate => TextCommandNormalizer.Normalize(candidate.Name) == requested)
                .ToArray();
            if (locationMatches.Length == 0 && groupMatches.Length == 0)
            {
                return new CommandTarget(CommandTargetStatus.UnknownLocation, "Unknown location", Array.Empty<Device>());
            }

            if (locationMatches.Length + groupMatches.Length != 1)
            {
                return new CommandTarget(
                    CommandTargetStatus.AmbiguousLocation,
                    "Ambiguous group or location",
                    Array.Empty<Device>(),
                    locationMatches.Select(match => $"Location: {match.Name}")
                        .Concat(groupMatches.Select(match => $"Group: {match.Name}"))
                        .Order(StringComparer.OrdinalIgnoreCase)
                        .ToArray());
            }

            location = locationMatches.SingleOrDefault();
            deviceGroup = groupMatches.SingleOrDefault();
        }
        else if (source.LocationId is { } sourceLocationId &&
                 state.LocationsById.TryGetValue(sourceLocationId, out var sourceLocation))
        {
            location = sourceLocation;
            usedSourceLocation = true;
        }
        else
        {
            return new CommandTarget(
                CommandTargetStatus.MissingSourceLocation,
                "No source location",
                Array.Empty<Device>());
        }

        var requiredCapability = RequiredCapability(parse.Intent);
        var includedLocationIds = deviceGroup is null
            ? GetDescendantLocationIds(location!.Id, state.Document.Locations)
            : null;
        var devices = (deviceGroup is not null
                ? state.Document.Devices.Where(device =>
                    device.GroupIds?.Contains(deviceGroup.Id) == true)
                : state.Document.Devices.Where(device =>
                    device.LocationId is { } deviceLocationId &&
                    includedLocationIds!.Contains(deviceLocationId)))
            .Where(device => device.Enabled && device.Capabilities.HasFlag(requiredCapability))
            .OrderBy(device => device.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(device => device.Id)
            .ToArray();
        if (devices.Length == 0)
        {
            return new CommandTarget(
                CommandTargetStatus.Unsupported,
                $"{(deviceGroup?.Name ?? location!.Name)}: no supported lights",
                Array.Empty<Device>());
        }

        if (devices.Any(device => !_providers.ContainsKey(device.DeviceType)))
        {
            return new CommandTarget(
                CommandTargetStatus.Unsupported,
                $"{(deviceGroup?.Name ?? location!.Name)}: unavailable light provider",
                Array.Empty<Device>());
        }

        var locationDescription = deviceGroup is not null
            ? $"Group {deviceGroup.Name}"
            : usedSourceLocation
                ? $"{location!.Name} (source location)"
                : location!.Name;
        return new CommandTarget(
            CommandTargetStatus.Resolved,
            $"{locationDescription}: {devices.Length} light{(devices.Length == 1 ? string.Empty : "s")}",
            devices);
    }

    private CommandTarget ResolveSnapshotTarget(CommandParseResult parse)
    {
        var cameraName = TextCommandNormalizer.Normalize(parse.DeviceName);
        var cameras = homeStateStore.Current.Document.Devices
            .Where(device => device.Enabled &&
                device.DeviceType == "blink.camera" &&
                TextCommandNormalizer.Normalize(device.Name) == cameraName)
            .OrderBy(device => device.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(device => device.Id)
            .ToArray();
        if (cameras.Length == 0)
        {
            return new CommandTarget(CommandTargetStatus.UnknownCamera, "Unknown camera", Array.Empty<Device>());
        }

        if (cameras.Length != 1)
        {
            return new CommandTarget(
                CommandTargetStatus.AmbiguousCamera,
                "Ambiguous camera",
                Array.Empty<Device>(),
                cameras.Select(camera => camera.Name).ToArray());
        }

        var camera = cameras[0];
        if (!_snapshotProviders.ContainsKey(camera.DeviceType))
        {
            return new CommandTarget(
                CommandTargetStatus.Unsupported,
                $"{camera.Name}: unavailable camera provider",
                Array.Empty<Device>());
        }

        return new CommandTarget(
            CommandTargetStatus.Resolved,
            $"{camera.Name}: Blink camera",
            [camera]);
    }

    private async Task<CommandExecutionResult> SetStateAsync(
        Guid correlationId,
        CommandParseResult parse,
        CommandTarget target,
        CancellationToken cancellationToken)
    {
        var change = parse.Intent switch
        {
            TextCommandIntent.LightsOn => new DeviceStateChange(IsOn: true),
            TextCommandIntent.LightsOff => new DeviceStateChange(IsOn: false),
            TextCommandIntent.SetBrightness => new DeviceStateChange(Brightness: parse.Brightness),
            TextCommandIntent.SetColour => new DeviceStateChange(Colour: parse.Colour),
            _ => throw new InvalidOperationException("Unsupported state change intent."),
        };
        var successes = 0;
        foreach (var device in target.Devices)
        {
            try
            {
                var operation = await _providers[device.DeviceType]
                    .SetStateAsync(device, change, cancellationToken);
                if (operation.Succeeded)
                {
                    successes++;
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (OperationCanceledException)
            {
            }
            catch (HttpRequestException)
            {
            }
            catch (IOException)
            {
            }
            catch (InvalidOperationException)
            {
            }
        }

        return CompletionResult(correlationId, parse, target, successes);
    }

    private async Task<CommandExecutionResult> ReadStatusAsync(
        Guid correlationId,
        CommandParseResult parse,
        CommandTarget target,
        CancellationToken cancellationToken)
    {
        var states = new List<DeviceState>();
        foreach (var device in target.Devices)
        {
            try
            {
                var operation = await _providers[device.DeviceType]
                    .GetStateAsync(device, cancellationToken);
                if (operation.Succeeded && operation.State is not null)
                {
                    states.Add(operation.State);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (OperationCanceledException)
            {
            }
            catch (HttpRequestException)
            {
            }
            catch (IOException)
            {
            }
            catch (InvalidOperationException)
            {
            }
        }

        if (states.Count != target.Devices.Count)
        {
            return CreateResult(
                correlationId,
                parse,
                target.Summary,
                states.Count == 0 ? CommandOutcome.Failed : CommandOutcome.PartiallyCompleted,
                new SafeUserMessage(
                    "status-incomplete",
                    $"I could read {states.Count} of {target.Devices.Count} lights."));
        }

        var stateDescription = states.All(state => state.IsOn == true)
            ? "are on"
            : states.All(state => state.IsOn == false)
                ? "are off"
                : "have mixed states";
        return CreateResult(
            correlationId,
            parse,
            target.Summary,
            CommandOutcome.Completed,
            new SafeUserMessage("status", $"The {target.Devices.Count} lights {stateDescription}."));
    }

    private async Task<CommandExecutionResult> ReadSnapshotAsync(
        Guid correlationId,
        CommandParseResult parse,
        CommandTarget target,
        CancellationToken cancellationToken)
    {
        var camera = target.Devices.Single();
        var result = await _snapshotProviders[camera.DeviceType]
            .GetSnapshotAsync(camera, cancellationToken);
        if (!result.Succeeded || result.Snapshot is null)
        {
            return CreateResult(
                correlationId,
                parse,
                target.Summary,
                CommandOutcome.Failed,
                new SafeUserMessage(
                    "snapshot-failed",
                    result.Detail ?? "I could not retrieve that camera snapshot."));
        }

        return CreateResult(
            correlationId,
            parse,
            target.Summary,
            CommandOutcome.Completed,
            new SafeUserMessage("snapshot", $"Retrieved the snapshot from {camera.Name}."),
            result.Snapshot);
    }

    private static CommandExecutionResult CompletionResult(
        Guid correlationId,
        CommandParseResult parse,
        CommandTarget target,
        int successes)
    {
        var outcome = successes == target.Devices.Count
            ? CommandOutcome.Completed
            : successes == 0 ? CommandOutcome.Failed : CommandOutcome.PartiallyCompleted;
        return CreateResult(
            correlationId,
            parse,
            target.Summary,
            outcome,
            new SafeUserMessage(
                outcome switch
                {
                    CommandOutcome.Completed => "completed",
                    CommandOutcome.PartiallyCompleted => "partial-failure",
                    _ => "failed",
                },
                outcome == CommandOutcome.Completed
                    ? $"Updated {successes} light{(successes == 1 ? string.Empty : "s")}."
                    : $"Updated {successes} of {target.Devices.Count} lights."));
    }

    private static CommandExecutionResult CreateResult(
        Guid correlationId,
        CommandParseResult parse,
        string targetSummary,
        CommandOutcome outcome,
        SafeUserMessage message,
        DeviceSnapshot? snapshot = null) =>
        new(
            correlationId,
            parse.Command.Transcript,
            parse.Intent,
            BoundAuditField(targetSummary),
            outcome,
            message,
            snapshot);

    private static string BoundAuditField(string? value)
    {
        var text = value ?? string.Empty;
        return text.Length <= MaximumAuditFieldLength
            ? text
            : text[..MaximumAuditFieldLength];
    }

    private static SafeUserMessage TargetMessage(CommandTarget target) => target.Status switch
    {
        CommandTargetStatus.UnknownCamera =>
            new("unknown-camera", "I could not find that Blink camera."),
        CommandTargetStatus.AmbiguousCamera =>
            new("ambiguous-camera", "That Blink camera name is ambiguous."),
        CommandTargetStatus.UnknownLocation =>
            new("unknown-location", "I could not find that location."),
        CommandTargetStatus.AmbiguousLocation =>
            new(
                "ambiguous-location",
                BoundAuditField(
                    $"That location is ambiguous: {string.Join(", ", target.Candidates ?? Array.Empty<string>())}.")),
        CommandTargetStatus.MissingSourceLocation =>
            new("missing-source-location", "No source location is configured for this command."),
        _ => new("unsupported", "There are no supported lights in that location."),
    };

    private static DeviceCapabilities RequiredCapability(TextCommandIntent intent) => intent switch
    {
        TextCommandIntent.SetBrightness => DeviceCapabilities.Brightness,
        TextCommandIntent.SetColour => DeviceCapabilities.Colour,
        _ => DeviceCapabilities.OnOff,
    };

    private static HashSet<Guid> GetDescendantLocationIds(
        Guid rootId,
        IReadOnlyList<Location> locations)
    {
        var included = new HashSet<Guid> { rootId };
        var pending = new Queue<Guid>();
        pending.Enqueue(rootId);
        while (pending.TryDequeue(out var parentId))
        {
            foreach (var child in locations.Where(location => location.ParentLocationId == parentId))
            {
                if (included.Add(child.Id))
                {
                    pending.Enqueue(child.Id);
                }
            }
        }

        return included;
    }
}
