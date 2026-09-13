# Phase 6 - Automation and Operations

## Objective

Add Philips Hue switch-triggered automation and complete the operational
features needed to run DaveBot reliably: backups, recovery, service operation,
security maintenance, and incremental end-to-end testing.

## Prerequisites

- Phase 5 provides enrolled, functioning DaveEar clients.
- Phase 2 Hue adapter can read resources and Phase 3 can execute actions.

## Implementation work

### 1. Hue event ingestion

1. Extend the Hue adapter with `IDeviceStateSource` and normalize bridge events
   into `DeviceStateChanged`.
2. Prefer a supported Hue event stream; if unavailable, use bounded polling
   with documented interval and recovery behavior.
3. Implement reconnect with bounded exponential backoff.
4. Reconcile relevant device state after reconnect before processing new
   triggers, preventing stale cache actions.
5. Import/map Hue switches as `hue.switch` devices and expose their current
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
