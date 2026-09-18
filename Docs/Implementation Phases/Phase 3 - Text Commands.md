# Phase 3 - Text Commands

## Objective

Turn authenticated text into deterministic, location-aware Hue light actions.
Both future speech text and the Admin diagnostic form use the same command
processor; there is no separate parsing path.

This phase must not invoke a model. Command parsing, responses, and automation
are custom deterministic code; the only planned model use is Whisper Base
speech transcription in Phase 4.

## Prerequisites

- Phase 2 can map and control Hue lights.
- Phase 1 can authenticate a DaveEar and identify its assigned location.

## Implementation work

### 1. Command model and parsing

1. Define normalized command, parse result, resolved intent, target, execution
   result, and safe user-message types in DaveHome.
2. Normalize whitespace, casing, punctuation, brightness percentages, and
   configured aliases while retaining original text for bounded audit.
3. Implement explicit grammar only. Do not add an LLM, chat or
   natural-language model, arbitrary scripting, or best-effort fuzzy
   execution.
4. Support these initial intents:
   - `lights on` and `lights off`
   - `turn lights on/off`
   - `turn <location> lights on/off`
   - `set <location> lights to <percent>`
   - `set <location> lights to <colour>` for colour-capable lights
   - `are <location> lights on`

### 2. Target resolution

1. Resolve an explicit location before falling back to the authenticated Ear's
   stored location.
2. Traverse a location's validated inclusion tree and select devices with the
   matching capability.
3. Use normalized names and an Admin-managed alias table.
4. Return a no-action ambiguity result with candidates whenever a location or
   device name has more than one match.
5. Return a no-action unknown/unsupported result when no target has the
   requested capability.

### 3. Execution and APIs

1. Implement one `CommandProcessor` that loads the authenticated source Ear,
   parses, resolves, validates, invokes the device provider, and records an
   audit entry.
2. Add the protected `/api/v1/text-commands` endpoint for registered DaveEars
   and the Admin diagnostic surface.
3. Bind Ear calls to issuer, subject, client ID, mTLS certificate thumbprint,
   scope, and Ear ID before creating command context.
4. Return correlation ID, normalized transcript, intent, target summary,
   outcome, and safe message without exposing provider credentials.

## Validation while implementing

- Unit-test grammar, aliases, normalisation, explicit/default locations,
  parent-location traversal, ambiguity, no capability, and malformed input.
- Integration-test one shared command path for Admin diagnostic requests and
  OAuth/mTLS-authenticated Ear requests.
- Test that a command from each registered Ear affects only its configured
  location unless an explicit unique location is supplied.
- Test all error paths produce an audit entry where appropriate and never
  trigger an unintended device action.

## Exit checklist

- [x] The initial intents work through one deterministic processor.
- [x] Explicit locations override Ear context.
- [x] Unknown and ambiguous commands make no device change.
- [x] Device failures and partial multi-device results are reported clearly.
- [x] The UI exposes recent bounded command history to authorized users.

## Implemented command grammar

The parser accepts only normalized forms of `lights on`, `lights off`, `turn
<location> lights on|off`, `set <location> lights to <0-100>%` (or `percent`),
`set <location> lights to <red|green|blue|yellow|orange|purple|pink|white>`,
and `are <location> lights on`. Punctuation, casing, and repeated whitespace
are normalized; location aliases are exact normalized matches configured by an
administrator on the **Text commands** page. Unsupported, unknown, and
ambiguous requests are rejected without actions.

Aliases are additive home-state data (and are preserved by home data
export/import), so snapshots created before text commands remain valid. Command
history retains a bounded, safe audit record; no model is involved in parsing,
targeting, executing, or responding.
