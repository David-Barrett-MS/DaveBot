# Phase 6 - Automation and Operations

## Objective

Add Philips Hue switch- and motion-sensor-triggered automation and complete the operational
features needed to run DaveBot reliably: backups, recovery, service operation,
security maintenance, and incremental end-to-end testing.

## Prerequisites

- Phase 5 provides enrolled, functioning DaveEar clients.
- Phase 2 Hue adapter can read resources and Phase 3 can execute actions.

## Implementation work

### 1. Hue event ingestion

1. Extend the Hue adapter with `IDeviceStateSource` and normalize CLIP v2 SSE
   button, motion, and light-level event batches from `/eventstream/clip/v2` into stable
   `DeviceStateChanged` IDs.
2. Run an independent, application-key-authenticated stream per mapped Hue
   hub, retaining bridge-ID TLS hostname routing and certificate pinning.
3. Reconnect failed streams with exponential backoff capped at 30 seconds and
   reconcile each hub's mapped switch and motion-sensor state before accepting stream events.
4. Treat reconciliation, initial stream snapshots, and new mappings as
   baselines so a historic `last_event` is never triggered. Only after three
   consecutive stream failures use the legacy one-second poll, only during the
   bounded wait for the next stream retry, while continuing stream recovery.
5. Import/map Hue switches as `hue.switch` devices and correlated motion plus
   ambient-light resources as `hue.motion-sensor` devices; expose their current
   status in the Admin UI.

### 2. Triggers and action sets

1. Implement persisted actions, action sets, triggers, and the documented
   JSON state-criteria schema.
2. Restrict first-release actions to allow-listed `device.setState`
   operations. Reject executable script, shell, arbitrary URL, or arbitrary
   expression action types.
3. On a normalized event, find enabled referenced triggers, evaluate criteria,
   check cooldown and deduplication keys, then execute each action set in
   declared order.
4. Record each trigger decision and action result in bounded audit/log output.
5. Add Admin-only management pages with validation previews for trigger
   devices, state fields, actions, references, and effective targets.

### 3. Operations and recovery

1. Document installation, Windows Service recovery, configuration paths,
   certificates, Data Protection keys, signing-key rotation, and log retention.
2. Define backup/restore procedures for `state.json`, `identity.json`, their
   backups, protected DPAPI material, and signing certificates. Make clear
   which secrets require re-enrollment on a different host/service identity.
3. Document storage-provider migration from JSON snapshots to a future SQL
   provider: pause writes, export validated home/identity documents, import,
   verify revisions/projections, retain rollback copies, and switch provider.
4. Document user administration, password recovery, grant/Ear revocation,
   certificate compromise, and lost Ear-private-key response procedures.
5. Complete health endpoints, concise support diagnostics, and notification
   guidance for unavailable Hue or transcription dependencies.

## Validation while implementing

- Unit-test state criteria, trigger selection, cooldown, duplicate events,
  action ordering, partial failures, and disabled references.
- Integration-test stream/poll event mappings, reconnect reconciliation,
  trigger-to-action execution, and Admin authorization for all automation
  configuration.
- Exercise real switch presses, Ear commands, Hue disconnect/reconnect,
  DaveBrain service restart, grant revocation, account disablement, and backup
  restore as each capability is completed.
- Run the storage provider contract suites against any newly added SQL adapter
  before migration is supported.

## Exit checklist

- [ ] A configured switch press executes its action set exactly once.
- [ ] Event replay, reconnect, disabled triggers, and partial action failures
      do not cause unintended repeated actions.
- [ ] Operators can back up, restore, revoke access, rotate keys, and recover
      the Windows Service using documented procedures.
- [ ] The JSON default and every future storage provider have passed their
      relevant persistence contract suites.
