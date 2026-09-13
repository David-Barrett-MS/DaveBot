# Phase 2 - Hue Vertical Slice

## Objective

Deliver the smallest real home-automation path: an authenticated Admin links a
Philips Hue bridge, maps a light to a DaveBot location, and performs a
capability-validated on/off or brightness action from DaveBrain.

## Prerequisites

- Phase 1 authentication, Admin UI, storage, and protected-secret handling.
- The Philips Hue references in `Docs/Philips Hue`.

## Implementation work

### 1. Hue adapter boundary

1. Define `IHueBridgeClient` and keep Hue HTTP payloads, authentication, and
   conversion inside its adapter.
2. Define DaveHome `IDeviceProvider`, `DeviceState`, and
   `DeviceOperationResult` contracts. Register `hue.light` as the first
   device type.
3. Use stable bridge resource identifiers in provider configuration, never
   mutable light names as the external key.
4. Map vendor failures to typed unavailable-bridge, unauthorized, missing
   resource, invalid-state, and timeout results.

### 2. Bridge management

1. Add an Admin-only Hue hub page that discovers supported local bridges and
   displays identity, address, and connection state without exposing secrets.
2. Implement the bridge link-button flow. Persist the resulting application
   key through `ISecretStore`/Windows DPAPI, with only an opaque reference in
   home state.
3. On successful linking, fetch all available Hue light resources and import
   every resource not already mapped by its stable bridge resource ID. Assign
   newly imported lights no location so an Admin can safely map them without
   guessing a room.
4. Allow an Admin to review imported lights, assign each to zero or one
   DaveBot location, set its local display name, and disable/remove the
   mapping.
5. Refresh cached resource capabilities when the hub is linked or explicitly
   synchronized.

### 3. Light behavior and action execution

1. Implement read state, on/off, and 0-100 brightness operations.
2. Convert normalized brightness to/from the bridge format only in the Hue
   adapter.
3. Implement `device.setState` parameter validation and capability checks
   before contacting the bridge.
4. Add an Admin-only diagnostic command form that executes a selected
   action against a selected device and shows the typed result.
5. Reconcile mapped resource state after DaveBrain startup; do not treat an
   old in-memory value as authoritative.

## Validation while implementing

- Use a stub Hue HTTP server for discovery, linking, resource reads, writes,
  timeout, unauthorized, and missing-resource responses.
- Test all Admin-only Hue pages and APIs as anonymous, User, and Admin.
- Test location/device reference validation, secret-reference persistence, and
  restart reconciliation.
- Test the physical bridge/light after each completed adapter capability rather
  than scheduling separate performance measurement.

## Exit checklist

- [ ] An Admin can link a bridge through the web interface.
- [ ] A newly linked bridge automatically imports every unrecognized Hue light;
      each light can then be named, mapped, read, turned on/off, and dimmed.
- [ ] User and anonymous callers cannot change Hue configuration or state.
- [ ] Hue credentials never appear in pages, snapshots, responses, or logs.
- [ ] Failure results are visible and do not claim a successful action.
