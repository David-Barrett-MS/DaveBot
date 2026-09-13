# Phase 0 - Architecture and Dependencies

## Objective

Make the few decisions that must be stable before implementation starts. This
phase is documentation and dependency selection only. Windows Server 2019 x64
or later is assumed for DaveBrain; no hardware discovery, benchmark exercise,
or standalone proof-of-concept is required here. Each feature is tested as it
is built in later phases.

## Inputs

- [Master implementation plan](../../DaveBot%20Implementation%20Plan.md)
- Existing Philips Hue references in `Docs/Philips Hue`
- The fixed DaveBrain host baseline: Windows Server 2019 x64 or later

## Confirmed implementation baseline

| Area | Decision |
|---|---|
| DaveBrain issuer and public URL | `https://brain.daves.tips` on HTTPS port 443 |
| DaveBrain service identity | `NT SERVICE\DaveBrain` |
| TLS certificate | Self-signed certificate, explicitly trusted by every browser and DaveEar host |
| DaveEar platforms | Windows 11; Ubuntu 24.04 LTS with PipeWire; Raspberry Pi OS; Debian Linux 12 |
| Hue API | Current Hue CLIP v2 API; confirm physical bridge details while implementing Phase 2 |
| Backup scope | Package DaveBot state/recovery data in `C:\ProgramData\DaveBot\backups`; remote destination, scheduling, and broader backup automation are deferred |
| OAuth/OIDC dependency | OpenIddict (Apache-2.0) in DaveIdentity; Microsoft/BCL components elsewhere; RFC 8705 mTLS for Ear proof of possession |

## Decisions to record

1. **Deployment identity**
   - Set the OIDC issuer and browser/Ear server URL to
     `https://brain.daves.tips`. Private LAN DNS must resolve this name to the
     DaveBrain host; it must not require public internet exposure.
   - Run the service as `NT SERVICE\DaveBrain` and grant it only access to the
     certificate private key, `C:\ProgramData\DaveBot`, and required network
     resources.
   - Create a self-signed TLS certificate for `brain.daves.tips`, configure
     its Windows Certificate Store thumbprint, and document renewal/replacement
     ownership.
   - Install the self-signed certificate or its trust anchor in the Local
     Machine trusted-root store on Windows clients and in the system CA store
     on Ubuntu, Raspberry Pi OS, and Debian clients. Do not disable TLS
     certificate validation.

2. **Storage defaults**
   - Confirm `JsonSnapshotHomeStatePersistence` for home configuration and
     `JsonSnapshotIdentityPersistence` for users, clients, consent, and
     grants.
   - Reserve `C:\ProgramData\DaveBot` for `state.json`, `identity.json`,
     their `.bak` files, Data Protection keys, transient audio, and logs.
   - In Phase 6, package the state/identity snapshots, their backups, a
     recovery manifest, and documented certificate/DPAPI recovery requirements
     into a locally saved package in `C:\ProgramData\DaveBot\backups`. Defer
     remote destinations, schedules, and automated off-host retention.
   - Specify the protected configuration source for future connection strings
     and OAuth client secrets so a SQL provider can later be selected without
     changing application code.

3. **Identity dependency selection**
   - Prefer BCL and Microsoft ASP.NET Core components first: ASP.NET Core
     Identity, authentication/authorization, Data Protection,
     `System.Security.Cryptography`, `System.Text.Json`, and
     `Microsoft.Extensions.*`.
   - Identify whether an additional OAuth/OIDC server library is necessary for
     standards-compliant Authorization Code with PKCE, Device Authorization
     Grant, mTLS certificate-bound tokens, discovery/JWKS, revocation, and
     custom stores.
   - **Decision:** use OpenIddict (Apache-2.0) for the missing
     standards-compliant authorization-server features and native RFC 8705
     mutual TLS certificate-bound tokens. Constrain it to `DaveIdentity`; pin
     its version in Phase 1 and record its security-update process, .NET LTS
     support, required dependencies, and trimming implications. The complete
     rationale, rejected alternatives, and follow-up are in
     [ADR-001](../Decisions/ADR-001-daveidentity-oauth-server.md).

4. **Protocol constants**
   - Fix the two internal client registrations: `DaveBrain.Web` and `DaveEar`.
   - Fix scopes: `davebrain.read`, `davebrain.admin`,
     `davebrain.ear.enroll`, and `davebrain.ear.command`.
   - Fix the only initial roles: `Admin` and `User`.
   - Use 10-minute access tokens and 30-day rotating refresh tokens. Document
     key-rotation overlap and grant-revocation behavior.

5. **Integration boundaries**
   - Confirm project references: `DaveBrain -> DaveHome, DaveIdentity`;
     `DaveIdentity` references neither; `DaveEar` uses only HTTP/OIDC
     discovery contracts.
   - Confirm the provider ports and canonical documents remain the sole
     persistence boundary for a later SQL implementation.
   - Use the current Hue CLIP v2 API. Verify resource/authentication details
     against the physical bridge while implementing Phase 2.
   - Select audio capture packages that support Windows 11, Ubuntu 24.04 LTS
     with PipeWire, Raspberry Pi OS, and Debian 12. Record backend-specific
     deployment prerequisites with the Phase 5 work.

## Deliverables

- `Docs/architecture.md` describing host, process, project-reference, network,
  certificate, storage, and issuer boundaries.
- `Docs/operations.md` describing service account, paths, backup ownership,
  certificate/key rotation, self-signed certificate trust installation, local
  backup-package scope, and one-time `Dave` password-change procedure.
- `Docs/Decisions/ADR-001-daveidentity-oauth-server.md` documenting the
  selected OpenIddict OAuth/OIDC protocol component and its Apache-2.0
  licensing decision.
- Central package/version entries for only the chosen dependencies when Phase
  1 begins; do not add speculative packages.
- An implementation decision record for any non-Microsoft OAuth/OIDC package
  and its confinement to DaveIdentity.

## Exit checklist

- [x] The Windows Server 2019+ deployment baseline is documented.
- [x] The issuer URL, TLS strategy, signing certificate location, and service
      account are defined.
- [x] Storage paths, local backup-package scope, and future SQL configuration
      seam are defined.
- [x] Project references and DaveIdentity ownership are unambiguous.
- [x] Dependency choices follow the Microsoft/BCL-first policy.
- [x] Hue CLIP v2 and the target DaveEar operating systems are defined.
- [x] Phase 1 can start without revisiting an architectural decision.
