# DaveBot Architecture

## Scope

DaveBot is a local-first home-automation system. DaveBrain runs on Windows
Server 2019 x64 or later; DaveEar runs on Windows 11, Ubuntu 24.04 LTS with
PipeWire, Raspberry Pi OS, and Debian 12.

DaveBrain is the local HTTPS web application, home-automation API, and
internal OpenID Connect/OAuth 2.1 authority. It is not exposed to the public
internet.

## Deployment baseline

| Setting | Value |
|---|---|
| Public name and OIDC issuer | `https://brain.daves.tips` |
| Browser/OIDC HTTPS port | `443` |
| DaveEar mTLS HTTPS port | `8443` |
| DaveBrain host | Windows Server 2019 x64 or later |
| Windows Service identity | `NT SERVICE\DaveBrain` |
| TLS | Self-signed certificate for `brain.daves.tips` |
| Home-state provider | `JsonSnapshotHomeStatePersistence` |
| Identity-state provider | `JsonSnapshotIdentityPersistence` |
| Hue API | Current Hue CLIP v2 |

Private LAN DNS must resolve `brain.daves.tips` to the DaveBrain host's private
address. The hostname is the immutable OIDC issuer and must not change after
users or Ears are enrolled.

The self-signed certificate must be explicitly trusted by every browser and
DaveEar host. Clients must never bypass certificate validation. The certificate
private key remains in the Windows Local Machine Certificate Store and is
readable only by the DaveBrain service identity.

## Component topology

```text
Browsers
  | HTTPS: Razor Pages and OIDC sign-in/consent
  v
DaveBrain (Windows Service)
  |-- Browser/OIDC HTTPS, port 443
  |-- DaveEar mTLS HTTPS, port 8443
  |-- DaveIdentity: local accounts and OAuth/OIDC authority
  |-- DaveHome: commands, locations, devices, actions, triggers, Hue adapter
  |-- Foundry Local: Whisper Tiny transcription over loopback
  |-- Philips Hue Bridge: CLIP v2 over the private LAN
  `-- Philips Air+: account-authorized HTTPS/MQTT over TLS

DaveEar (Windows/Linux)
  | HTTPS + OAuth mTLS certificate-bound access token
  `----------------------------------------------> DaveBrain
```

Foundry Local must listen only on loopback. DaveBrain binds its public
interface to the private LAN and rejects unencrypted traffic outside an
explicit development profile.

Whisper Tiny is the only model in the DaveBot runtime and is used solely to
transcribe speech. Command parsing, command responses, and automation are
custom deterministic code; no chat, natural-language, or other general-purpose
model is run.

## Hue event automation

Hue CLIP v2 button resources are imported as `hue.switch` devices. Motion and
associated ambient-light resources are imported together as one
`hue.motion-sensor` device, using the Hue device metadata name where available.
Its deterministic attributes are `motion` and `lightLevel` (plus availability
and validity attributes when Hue reports them). A bounded CLIP v2 Server-Sent
Events stream at `/eventstream/clip/v2` is the primary source for every mapped,
enabled Hue hub. Each hub has an independent
long-lived stream, authenticated with its Hue application key and using the
same bridge-ID hostname routing and pinned-certificate validation as normal
CLIP requests. Event batches are bounded, button/motion/light-level updates are
normalized and merged into stable `DeviceStateChanged` IDs, and a
reconciliation establishes the current baseline before a stream is accepted
and after every reconnect. This prevents an initial stream snapshot, a
reconnect, or a newly mapped source from triggering historic automation.

An ended or failed stream reconnects with a bounded exponential backoff. Only
after three consecutive stream failures is the one-second polling reader used,
and then only for the bounded wait until the next stream attempt; it first
reconciles and suppresses the current event. Polling never runs while a stream
is available, and the service continues trying to restore the stream. The
automation engine selects enabled triggers for the source, evaluates strict
state criteria, deduplicates the source/event ID, applies a bounded cooldown
cache, and runs declared action sets/actions in order. Its only action type is
deterministic `device.setState`; it resolves a registered device provider and
validates the target capability. The engine holds only bounded deduplication,
cooldown, and audit history and never stores bridge credentials or payloads in
that history.

## Philips Air+ integration

Philips Air+ devices are an explicit exception to DaveBot's local-first device
topology. The supported Air+ protocol is account-authorized cloud access, not a
LAN integration: DaveBrain calls the Philips/Versuni HTTPS device API and uses
MQTT 3.1.1 over certificate-validated WSS for device-shadow reads and writes.
It opens a bounded request/response MQTT connection only when an administrator
checks devices, reads sensors, or sets a fan speed; it does not create a
long-lived cloud listener.

An administrator starts a PKCE authorization flow from the Philips Air+ page
and supplies the returned redirect address or authorization code to complete
the link. The authorization verifier/state is random, tied to the
administrator, single-use, and expires after ten minutes. Access and refresh
tokens are stored only through `ISecretStore` (Windows DPAPI by default), never
in `state.json`, logs, or the UI. The persisted device mapping contains the
Air+ API device ID, not the cloud MQTT thing name.

Sensor attributes are populated only where the shadow reports them, including
PM2.5, allergen/air-quality/gas indicators, temperature, humidity, filter
status, product state/errors, and fan speed. The sole mutable Air+ operation
is fan speed, allow-listed to levels `1` through `10`, `81` (level 11), and
`82` (level 12). This prevents arbitrary cloud-shadow writes.

## Solution and dependency graph

```text
DaveBrain  -> DaveHome
           -> DaveIdentity

DaveIdentity -> no DaveBrain or DaveHome project
DaveEar      -> no DaveBrain, DaveHome, or DaveIdentity project
```

`DaveBrain` hosts the web UI and API, then composes its dependencies through
`AddDaveHome`, `AddDaveIdentity`, and endpoint-mapping extension methods.
`DaveIdentity` owns all identity protocol routes, local user/role services,
consent, grants, key integration, and identity persistence abstractions.
`DaveHome` owns home-automation domain logic and home-state persistence
abstractions. DaveEar learns the identity endpoints through OIDC discovery and
never has a project reference to DaveIdentity.

## Authentication and authorization

DaveIdentity issues tokens with issuer `https://brain.daves.tips` for two
registered first-party clients:

| Client | Flow | Scopes |
|---|---|---|
| `DaveBrain.Web` | Authorization Code with PKCE | `openid`, `profile`, `davebrain.read`, `davebrain.admin` |
| `DaveEar` | Device Authorization Grant | `davebrain.ear.enroll`, `davebrain.ear.command` |

The only initial local roles are:

- `Admin`: manages local users, locations, Ears, Hue hubs/devices, automation,
  diagnostics, and consent/revocation.
- `User`: views the dashboard, locations, devices, hub/Ear status, and bounded
  history without making changes.

DaveIdentity publishes discovery/JWKS, authorization, token, device,
verification, revocation, user-info, login, logout, and account-management
routes. Browser administration sessions use secure, HTTP-only, same-site
cookies with anti-forgery protection. DaveEar uses mTLS certificate-bound
access tokens: each Ear generates a private key and CSR, then DaveIdentity
issues an Ear-specific client certificate after administrator consent.
DaveIdentity binds the issued token's `cnf.x5t#S256` claim to that
certificate. DaveBrain also binds every Ear request to the enrolled Ear ID,
token subject, client ID, and certificate thumbprint.

Token signing uses an asymmetric certificate stored in the Windows Certificate
Store. ASP.NET Core Data Protection keys, Hue application keys, and any
confidential client secret are protected with DPAPI for
`NT SERVICE\DaveBrain`.

The Ear client-certificate authority is separate from the self-signed
server-TLS and OAuth token-signing certificates. Kestrel does not request a
client certificate on the browser/OIDC listener at port 443. A separate
port-8443 DaveEar listener uses the same server TLS certificate and requires
a valid, non-revoked Ear certificate for Ear token/resource endpoints.

## Persistence architecture

The active command path reads immutable in-memory projections. Durable storage
is behind typed ports so a future SQL provider changes only registration and
adapter code:

| Area | Runtime component | Default durable provider |
|---|---|---|
| Home configuration | `IHomeStateStore` | `JsonSnapshotHomeStatePersistence` |
| Command/action history | `ICommandAuditStore` | Bounded in-memory history plus Windows Service logs |
| Users, OAuth clients, consent, grants | DaveIdentity stores | `JsonSnapshotIdentityPersistence` |
| Protected secrets | `ISecretStore` | Windows DPAPI |

`HomeStateDocument` and `IdentityStateDocument` are versioned, provider-neutral
documents. They contain only durable data; caches, audio, raw OAuth tokens,
plaintext passwords, and private keys are excluded. A write validates a full
candidate state, performs an expected-revision save to the durable provider,
then atomically replaces the in-memory projection.

The default JSON providers write a temporary file in the target directory,
flush it, retain a `.bak` copy, then atomically replace the active snapshot.
`state.json` and `identity.json` live under `C:\ProgramData\DaveBot`.

A future SQL provider must implement the same home and identity storage ports,
use optimistic revision checks and transactions, pass the shared provider
contract suites, and migrate through validated document export/import while
writes are paused.

## Filesystem layout

```text
C:\ProgramData\DaveBot\
  state.json
  state.json.bak
  identity.json
  identity.json.bak
  backups\
  dataprotection\
  audio\                 transient; diagnostic retention is opt-in
  logs\
```

The service account gets the minimum access needed for these directories. Do
not put passwords, tokens, PFX files, private keys, or Hue application keys in
the application directory, source repository, ordinary JSON configuration, or
logs.

## Dependency policy

Use .NET BCL and Microsoft-supported ASP.NET Core components before adding
third-party packages:

- `System.Text.Json` for documents and wire data;
- `System.Security.Cryptography` for cryptographic primitives;
- `Microsoft.Extensions.*` for hosting, configuration, logging, health, and
  dependency injection;
- ASP.NET Core authentication, authorization, and Data Protection;
- ASP.NET Core Identity for local account, password, lockout, and role support.

ASP.NET Core Identity is not a complete OAuth/OIDC authorization server.
`DaveIdentity` therefore selects OpenIddict (Apache-2.0) for the required
standards-compliant authorization-server features. It is the only
non-Microsoft identity dependency and must remain confined to DaveIdentity.
RFC 8705 mTLS certificate-bound access tokens protect DaveEar requests. Pin
its version centrally in Phase 1 after confirming current support for Device
Authorization Grant, mTLS, discovery/JWKS, PKCE, revocation, and the required
custom-store integration.

The rationale and licensing condition are recorded in
[ADR-001](Decisions/ADR-001-daveidentity-oauth-server.md).
