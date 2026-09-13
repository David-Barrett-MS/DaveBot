# DaveBot Implementation Plan

## 1. Purpose and first release

DaveBot is a lightweight, local-first home-automation system designed to run
continuously on a Pentium J4205 machine with 8 GB RAM. It accepts speech from
registered DaveEar clients, turns it into text using Microsoft Foundry Local
with Whisper Tiny, resolves the command against the caller's location, and
controls registered home devices. Philips Hue is the first device integration.

The first release should support this complete path:

```text
DaveEar microphone
  -> voice activity detection and WAV segment
  -> DaveBrain /speech-commands
  -> Foundry Local Whisper Tiny
  -> DaveBrain command parser and location resolver
  -> DaveHome Hue adapter
  -> Philips Hue Bridge and lights
```

Whisper Tiny is the only model DaveBot runs: it performs local speech
transcription only. Command parsing, target resolution, automation, and every
response are custom deterministic code; DaveBot does not run a chat,
natural-language, or general-purpose model.

Examples:

- An Ear registered in `Kitchen` sends “lights on”; the Kitchen's registered
  lights turn on.
- An Ear sends “turn lounge lights on”; DaveBot resolves `Lounge` explicitly,
  rather than using the Ear's location.
- A Hue switch event changes state; configured triggers execute the linked
  actions.

The initial release deliberately excludes cloud services, user accounts,
multi-user authorization, wake-word recognition, a natural-language model, and
general-purpose action scripting. These can be added after the local,
deterministic path is reliable.

## 2. Solution structure

Create a .NET LTS solution using nullable reference types, implicit usings, and
central package version management. DaveBrain targets Windows Server x64,
where it runs as a Windows Service; DaveEar targets both Windows and Linux.
Publish self-contained, trimmed release builds only after verifying that the
selected audio and Foundry client dependencies support trimming.

```text
DaveBot.sln
src/
  DaveBrain/                 ASP.NET Core Web API and hosted services
  DaveHome/                  Domain model, orchestration, persistence, adapters
  DaveIdentity/              Internal OIDC/OAuth authority and local identity
  DaveEar/                   Cross-platform microphone capture client
tests/
  DaveHome.Tests/            Unit tests for domain and command behavior
  DaveBrain.Tests/           API and persistence integration tests
  DaveIdentity.Tests/        Identity, consent, OAuth/OIDC, and grant tests
  DaveEar.Tests/             Segmentation, buffering, and upload tests
Docs/
  Philips Hue/               Existing downloaded vendor reference
  architecture.md            Diagrams, local setup, ports, and configuration
  operations.md              Deployment, backup, update, and diagnostics guide
```

### Project responsibilities

| Project | Type | Responsibilities | Must not contain |
|---|---|---|---|
| `DaveHome` | Class library | Domain records, command parsing and resolution, action/trigger engine, storage ports, in-memory state store, default snapshot provider, device abstractions, Hue integration, configuration validation | HTTP controllers, microphone capture, provider-specific SQL types outside a storage adapter |
| `DaveIdentity` | Class library | Internal OIDC/OAuth authority, local accounts/roles, consent, client/grant stores, protocol endpoint mapping, mTLS certificate-bound token validation, signing-key integration, and identity persistence ports/providers | DaveBrain UI/pages, home-automation domain rules, Hue code, or microphone capture |
| `DaveBrain` | ASP.NET Core Web API | Hosts DaveIdentity, authenticated Ear-facing API, administration UI/API, audio-upload validation, Foundry transcription client, bounded command queue, dependency injection, health checks, and Windows Service host | OAuth/OIDC protocol implementation details, Hue protocol details, or command parsing rules |
| `DaveEar` | .NET worker/console executable | Registration, local identity/configuration, microphone capture, voice activity detection (VAD), WAV encoding, upload/retry, Windows service and Linux systemd operation | Home topology, Hue credentials, command interpretation |

`DaveBrain` references both `DaveHome` and `DaveIdentity`, and invokes only
their public registration/endpoint-mapping extension methods during startup.
`DaveIdentity` references neither `DaveBrain` nor `DaveHome`. `DaveEar`
interoperates with DaveIdentity only through standard OIDC discovery and OAuth
endpoints; it has no project reference to it. DaveEar shares only a
deliberately small HTTP-contract project or duplicated, versioned DTOs; it
must not reference `DaveHome`, because it should remain deployable and
updatable independently. If a fifth project is created, name it
`DaveContracts` and restrict it to request/response records and protocol
constants used by Brain and Ear.

### Dependency policy

Prefer .NET BCL and Microsoft-supported ASP.NET Core components before adding
third-party packages. In particular, use `System.Text.Json`,
`System.Security.Cryptography`, `Microsoft.Extensions.*`,
`Microsoft.AspNetCore.Authentication`, `Microsoft.AspNetCore.Authorization`,
`Microsoft.AspNetCore.DataProtection`, and ASP.NET Core Identity where they
meet the requirement. Keep any non-Microsoft OAuth/OIDC protocol library
isolated to `DaveIdentity`, because ASP.NET Core Identity manages local users
but is not itself a complete OAuth/OIDC authorization server. Evaluate any
such dependency for active maintenance, supported license, security-update
history, .NET LTS compatibility, deployment footprint, and support for the
required OAuth features before adoption.

## 3. Architecture and operating constraints

### 3.1 Local-first operation

- Bind DaveBrain only to the private LAN interface by default. Make the listen
  address, HTTPS certificate, and port explicit configuration rather than
  exposing the service publicly.
- Require HTTPS for Ear-to-Brain communication outside an explicitly marked
  development profile. Use the configured self-signed certificate and install
  its trust chain explicitly on every browser and DaveEar host.
- Run Foundry Local and Whisper Tiny on the same host as DaveBrain. DaveBrain
  invokes its configured local transcription endpoint; it does not load a
  second speech model in-process.
- Use an application-owned in-memory state store as the primary runtime store.
  All command routing, configuration reads, trigger evaluation, and current
  device state operate on memory-resident immutable snapshots.
- Place durable configuration and audit storage behind typed storage ports.
  The default provider loads and atomically checkpoints a small versioned JSON
  state snapshot, retaining the lightweight in-memory-first deployment. A
  future SQL or other provider must implement the same ports and be selected
  only in the DaveBrain composition root; domain, API, command, and Hue code
  must not know which provider is active.
- Retain command/action audit data in a bounded in-memory ring buffer and
  structured Windows Service logs by default. It is diagnostic data rather
  than configuration and may be lost when the service restarts.
- Keep stateful processing bounded: a single transcription worker is the
  starting default for this CPU class, with a small configurable queue.
  Reject excess work with a clear `429`/`503` response rather than accumulating
  unbounded audio in memory.
- Store only short-lived request audio by default. Delete it after
  transcription, retaining metadata and an optional redacted command audit.
  A diagnostic audio-retention mode must be explicitly enabled, have a quota,
  and be off by default.

### 3.2 Runtime configuration

Use `appsettings.json` for non-secret defaults, an environment-specific
override for machine settings, and environment variables or an OS-protected
secret store for secrets. Configuration must include:

```json
{
  "Storage": {
    "Provider": "JsonSnapshot",
    "StateSnapshotPath": "C:\\ProgramData\\DaveBot\\state.json",
    "BackupDirectory": "C:\\ProgramData\\DaveBot\\backups",
    "AuditRetentionCount": 1000,
    "ConnectionStringName": "DaveBotStorage"
  },
  "DaveBrain": {
    "ListenUrls": ["https://0.0.0.0:443"],
    "MaxAudioSeconds": 30,
    "MaxAudioBytes": 4000000,
    "CommandQueueCapacity": 4
  },
  "Transcription": {
    "BaseUrl": "http://127.0.0.1:<configured-port>",
    "Model": "whisper-tiny",
    "RequestTimeoutSeconds": 45
  },
  "Hue": {
    "DiscoveryEnabled": true,
    "PollIntervalSeconds": 30
  },
  "Identity": {
    "Issuer": "https://brain.daves.tips",
    "Audience": "api://davebrain",
    "WebClientId": "DaveBrain.Web",
    "EarClientId": "DaveEar",
    "AccessTokenLifetimeMinutes": 10,
    "RefreshTokenLifetimeDays": 30,
    "SigningCertificateThumbprint": "<certificate-thumbprint>",
    "EarClientCertificateAuthorityThumbprint": "<certificate-thumbprint>"
  }
}
```

Do not place Hue application keys, OAuth client secrets, refresh tokens, or
certificate private keys in source control or ordinary JSON configuration.

### 3.3 Observability and failure behavior

- Emit structured logs with a correlation ID spanning Ear upload,
  transcription, command resolution, and device action.
- Record command source, Ear ID, source location, normalized text, resolution
  outcome, elapsed time, and safe error code. Do not log OAuth tokens,
  authorization codes, or audio content.
- Supply `/health/live` for process liveness and `/health/ready` for the
  successfully loaded in-memory state, transcription endpoint, and configured
  Hue hub reachability. Readiness must identify unavailable dependencies
  without exposing credentials.
- Keep a compact action audit with result, target IDs, and failure reason so
  device operations can be traced after the fact.
- Treat external operations as fallible: time out Hue and transcription
  requests, return actionable errors to the Ear, and never report success
  before an action has succeeded or has been accepted for execution.

### 3.4 Authentication and authorization from the outset

DaveBrain is the internal OpenID Connect (OIDC) provider and OAuth 2.1
authorization server for DaveBot, hosted through the `DaveIdentity` class
library. `DaveBrain` calls `AddDaveIdentity(...)` and
`MapDaveIdentityEndpoints(...)`; the library owns local user authentication,
consent, client registration, authorization grants, token issuance, and
protocol endpoint behavior. DaveIdentity uses ASP.NET Core Identity and
Microsoft/ASP.NET Core primitives wherever possible, with OpenIddict
(Apache-2.0) for the OAuth/OIDC authorization-server features they do not
provide. Do not hand-roll OAuth endpoints, JWT construction,
authorization-code handling, or cryptographic verification.

Support these flows from the first release:

- OIDC Authorization Code flow with PKCE for the server-rendered
  `DaveBrain.Web` client;
- OAuth Device Authorization Grant (RFC 8628) for the public `DaveEar`
  client;
- signed JWT access tokens with DaveBrain issuer, audience, expiry, client ID,
  scope, subject, and role claims, discoverable through
  `/.well-known/openid-configuration` and a JWKS endpoint;
- mutual-TLS certificate-bound access tokens (RFC 8705) with a
  `cnf.x5t#S256` claim for the DaveEar client;
- short-lived access tokens, rotating refresh tokens, explicit consent
  revocation, account disablement, and administrator-managed role assignment.

Implement only two local DaveBrain roles:

| Role | Browser administration UI | Brain API |
|---|---|---|
| `Admin` | Full dashboard and configuration access: manage locations, Ears, hubs, devices, actions, action sets, triggers, and diagnostics; approve/revoke DaveEar enrolment. | All administration endpoints plus read-only operational endpoints. |
| `User` | Read-only dashboard access for locations, devices, hub/Ear health, and command/action history. | Read-only endpoints only; no configuration mutation, Hue linking, or Ear enrolment. |

Manage roles in DaveBrain's internal user store, not in a browser claim or a
separate identity provider. When no active administrator exists, DaveIdentity
creates the first `Admin` as `Dave`. The initial password is `Dave` and is
accepted only once: the first successful sign-in is restricted to setting a
replacement password before the account can use any other route. There is no
anonymous web bootstrap route. Thereafter, `Admin` users manage
accounts, role assignment, disablement, and password reset from a protected
`/identity/users` page.

Use ASP.NET Core Identity's supported password hasher rather than custom
password cryptography. Persist only the resulting versioned password hash,
normalized username, role membership, lockout state, security stamp, and
timestamps. Enforce unique case-insensitive usernames, minimum password
policy, rate limits, progressive temporary lockout after failed logins, secure
password-change/reset flows, and session invalidation when a password, role,
or account state changes. Never store or log plaintext passwords.

Register separate OAuth clients and scopes for the browser and DaveEar:

| Client | Flow | Allowed scopes | DaveBrain policy |
|---|---|---|---|
| `DaveBrain.Web` | OIDC Authorization Code + PKCE | `openid`, `profile`, `davebrain.read`, `davebrain.admin` | First-party server-rendered web client. Require the expected client ID plus `davebrain.read`; require the `Admin` role and `davebrain.admin` for mutations. |
| `DaveEar` | OAuth Device Authorization Grant | `davebrain.ear.enroll`, `davebrain.ear.command` | Require the expected public-client ID and relevant Ear scope. Never accept this client for browser administration routes. |

Publish and protect these internal OAuth/OIDC routes:

| Route | Purpose |
|---|---|
| `/.well-known/openid-configuration` and JWKS endpoint | Publish issuer metadata and active public signing keys. |
| `/connect/authorize` and `/connect/token` | Authorization Code with PKCE and refresh-token issuance/rotation. |
| `/connect/device` and `/connect/verify` | Device-code initiation and signed-in user consent/approval. |
| `/connect/revocation` | Revoke refresh-token grants and invalidate a revoked Ear's session. |
| `/connect/userinfo` | Optional standard identity claims for approved first-party clients. |
| `/identity/login`, `/identity/logout`, `/identity/users` | Local account sign-in/out and `Admin`-only account/role management. |

Store signing keys as an asymmetric certificate/private key in the Windows
Certificate Store, ACLed to the DaveBrain service account; never generate a
new signing key on each restart. Publish its public portion through JWKS and
retain prior public keys during a planned key-rotation overlap. Protect
ASP.NET Core Data Protection keys for login/anti-forgery cookies with DPAPI
under the same service account.

Use a dedicated internal Ear client-certificate authority, separate from the
server TLS and token-signing certificates. DaveIdentity issues one client
certificate per enrolled Ear after the administrator-approved device flow and
stores its SHA-256 certificate thumbprint in the Ear record. Configure Kestrel
to request client certificates and require a valid, non-revoked Ear
certificate for Ear token and resource endpoints. Browser routes must not
require a client certificate.

Validate issuer, audience, signature, expiry, not-before, authorized-party or
client ID, scopes, roles, and the `cnf.x5t#S256` certificate binding where
required on every protected request. Browser sessions use secure, HTTP-only,
same-site cookies after the local OIDC callback, with anti-forgery protection
on all state-changing form/API requests. Browser/API requests use bearer
tokens; Ear requests use certificate-bound access tokens over mutual TLS.
Never accept access tokens or authorization codes in URLs, forms, or
application logs.

## 4. Domain and persistence design

Use GUIDs as canonical identifiers. Persist IDs as lowercase, hyphenated UUID
text, accept only valid UUIDs at API boundaries, and use immutable IDs after
creation. Human-readable names are display values, not identifiers.

### 4.1 Core entities

| Entity | Required fields | Rules |
|---|---|---|
| `Location` | `Id`, `Name`, `IncludedLocationIds` | Names are unique case-insensitively. A location may include child locations; reject missing references, self-reference, and cycles. Commands targeting a parent apply to devices in its transitive child locations. |
| `Device` | `Id`, `Name`, `Type`, `LocationId`, `ConfigJson` | `Type` is a registered provider type such as `hue.light` or `hue.switch`. Device configuration is provider-owned JSON validated by its provider. |
| `DaveEar` | `Id`, `Name`, `LocationId`, `OAuthIssuer`, `OAuthSubject`, `OAuthClientId`, `MtlsCertificateThumbprint`, `CreatedUtc`, `LastSeenUtc`, `Enabled` | The Ear is bound to the consenting user's issuer/subject, the registered `DaveEar` OAuth client, and its unique client-certificate thumbprint. It stores no raw access token, refresh token, or client secret. |
| `HueHub` | `Id`, `Name`, `BaseUri`, `ApplicationKeyProtected`, `Enabled`, `LastSyncedUtc` | A hub owns discovered Hue resource mappings and its protected application key. |
| `Trigger` | `Id`, `Name`, `TriggerDeviceIds`, `StateCriteriaJson`, `ActionSetIds`, `Enabled`, `CooldownSeconds` | Valid only when its referenced devices and action sets exist. Criteria are evaluated against normalized device-state events. |
| `Action` | `Id`, `Kind`, `ParametersJson`, `Name` | A strongly defined, allow-listed operation. Initial kind: `device.setState`. No executable text, shell command, or arbitrary URL action type. |
| `ActionSet` | `Id`, `Name`, `ActionIds` | Executes actions in declared order, with each result recorded. |
| `CommandAudit` | `Id`, `ReceivedUtc`, `SourceEarId`, `Text`, `ResolvedIntentJson`, `Outcome`, `ErrorCode` | Stores operational trace data; make text retention configurable. |
| `IdentityUser` | `Id`, `UsernameNormalized`, `PasswordHash`, `Roles`, `SecurityStamp`, `Enabled`, `LockoutUntilUtc` | Internal local account. Roles are exactly `Admin` or `User`; hashes use the supported ASP.NET Core Identity hasher and are never exposed. |
| `OAuthGrant` | `Id`, `UserId`, `ClientId`, `Scopes`, `MtlsCertificateThumbprint`, `GrantType`, `TokenHash`, `ExpiresUtc`, `RevokedUtc` | Represents consent, authorization/device codes, and refresh-token families. Store opaque codes/tokens only as one-way hashes, enforce one-time code use, and bind Ear grants to their client certificate. |

The original draft's `actions` entity maps to `ActionSet` in code and
documentation to avoid confusion with a single `Action`.

### 4.2 In-memory runtime store and pluggable durable stores

Implement `IHomeStateStore` as the in-memory runtime projection for every
deployment. Its `InMemoryHomeStateStore` holds one immutable `HomeState`
aggregate containing dictionaries keyed by GUID and precomputed
normalized-name indexes. Readers obtain the current aggregate atomically and
perform no I/O or locking. This keeps the normal command path lightweight even
when a future SQL provider is chosen for durable storage.

Keep durable storage behind explicit ports owned by DaveHome:

```csharp
public interface IHomeStatePersistence
{
    Task<PersistedHomeState> LoadAsync(CancellationToken cancellationToken);
    Task<StoreRevision> SaveAsync(
        HomeStateDocument state,
        StoreRevision expectedRevision,
        CancellationToken cancellationToken);
}

public interface ICommandAuditStore
{
    void Append(CommandAudit audit);
    IReadOnlyList<CommandAudit> GetRecent(int maximumCount);
}
```

`HomeStateDocument` is the versioned canonical storage contract containing
core entities and ID lists. It is not an EF/database entity and must not
contain runtime caches or secrets. `HomeState` is its validated, indexed
in-memory projection. Provider-specific DTOs, SQL records, connection
strings, and query APIs are confined to the relevant persistence adapter.
Application services depend only on `IHomeStateStore`,
`IHomeStatePersistence`, and `ICommandAuditStore`; they must not access files,
SQL connections, or an ORM directly.

Keep internal identity/authorization persistence behind separate typed ports in
`DaveIdentity`: `IIdentityUserStore`, `IOAuthClientStore`, and
`IOAuthGrantStore`. The selected OAuth/OIDC library and ASP.NET Core Identity
integration adapt those ports to their required stores; they must not make
DaveBrain controllers/Razor Pages or DaveIdentity protocol endpoints depend
directly on JSON files or a SQL ORM.
`IdentityStateDocument` is distinct from `HomeStateDocument` and holds users,
roles, registered clients, consent, and hashed/expired/revoked grant records.
It never stores plaintext passwords, raw authorization/device/refresh tokens,
access tokens, or private signing keys.

At startup, DaveBrain loads `HomeStateDocument` through
`IHomeStatePersistence`, upgrades it if needed, validates it, and creates the
in-memory projection before readiness succeeds. Configuration writers
serialize through a single write gate: create and validate a complete
candidate aggregate, save its document using the expected `StoreRevision`,
then atomically publish the new in-memory projection. If durable storage
rejects or fails the write, retain the existing projection and return a
visible configuration-save failure.

The default first-release configuration is:

| Port | Default provider | Behavior |
|---|---|---|
| `IHomeStatePersistence` | `JsonSnapshotHomeStatePersistence` | Loads `C:\ProgramData\DaveBot\state.json`; atomically writes the validated replacement and retains `state.json.bak`. |
| `ICommandAuditStore` | `BoundedInMemoryCommandAuditStore` | Keeps the most recent configured count only; structured Windows Service logs retain operational diagnostics separately. |
| Identity/authorization stores | `JsonSnapshotIdentityPersistence` | Atomically persists `C:\ProgramData\DaveBot\identity.json`, its backup, local users, OAuth clients, consent, and hashed grants. |
| `ISecretStore` | `WindowsDpapiSecretStore` | Encrypts Hue app keys and OAuth client secrets under the dedicated DaveBrain service account. |

Each JSON document includes a `schemaVersion`; pure document upconverters run
before domain or identity validation. The snapshot providers serialize to a
temporary file in the same directory, flush it, preserve the prior file as
`.bak`, then use an atomic replace/rename operation. Invalid, unsupported, or
partially written home or identity data must fail readiness with a clear error
and never silently start with empty configuration.

Adding a SQL provider later must require only a new adapter, registration
change, tests, and a controlled data migration:

1. Implement `SqlHomeStatePersistence`, `SqlIdentityPersistence`, and,
   if durable history is needed, `SqlCommandAuditStore`, using the canonical
   home and identity document contracts.
2. Map document entities and ID lists to normalized relational tables with
   foreign keys, unique normalized-name indexes, and store-revision rows.
   Map identity users, roles, OAuth clients, consent, and grants to separate
   protected tables. Execute each home or identity save and revision check in
   one transaction.
3. Register the provider with `Storage:Provider` and resolve its connection
   string from protected configuration. DaveHome command, domain, API, Hue,
   Razor Pages, and OAuth/OIDC protocol code must remain unchanged.
4. Export validated home and identity documents from the current provider,
   import them to the new provider while configuration and identity writes are
   paused, verify the resulting projections/revisions, then preserve a
   rollback backup before switching providers.

Validate every reference during candidate construction: location containment,
device/Ear locations, trigger device IDs, and action/action-set IDs. Build
case-insensitive unique-name indexes for locations, hubs, devices within their
scope, and Ears. Configuration edits are all-or-nothing at the aggregate
level, replacing the full immutable snapshot only after validation succeeds.

Keep current Hue state, queued speech work, rate-limit counters, cooldown
keys, certificate revocation entries, and command/action audit entries in
memory with explicit bounded capacities. Rehydrate observed Hue state from the
bridge after startup rather than treating a persisted cache as authoritative.

Protect actual secrets separately from the storage documents with Windows
DPAPI under the dedicated DaveBrain service account. Persist only opaque
secret references in `HueHub`; home documents store OAuth issuer/client IDs
and Ear subject bindings, while identity documents store only password/token
hashes and client metadata. Backups of the default JSON documents are portable
for non-secret configuration, but restoring to a different Windows
host/service identity requires re-enrolling the Hue application key,
reconfiguring protected OAuth client secrets, and installing the configured
OAuth signing certificate.

`IHomeStateStore` should expose domain reads such as
`GetDevicesInLocationTreeAsync` and `GetEnabledTriggersForDeviceAsync`, rather
than exposing a generic key-value store. The persistence ports remain limited
to loading/saving the canonical document and audit records.

### 4.3 Device abstractions

Define these DaveHome interfaces:

```csharp
public interface IDeviceProvider
{
    string DeviceType { get; }
    Task<DeviceState> GetStateAsync(Device device, CancellationToken cancellationToken);
    Task<DeviceOperationResult> SetStateAsync(
        Device device,
        JsonElement requestedState,
        CancellationToken cancellationToken);
}

public interface IDeviceStateSource
{
    IAsyncEnumerable<DeviceStateChanged> ReadStateChangesAsync(
        CancellationToken cancellationToken);
}
```

`HueDevice` is the shared Hue implementation detail; `HueLight` and
`HueSwitch` expose their supported state and operation validation through the
provider. Use normalized state names such as `on`, `brightness`, `button`,
and `battery` at the DaveHome boundary, while keeping the raw Hue resource
payload only in the adapter/audit diagnostic data.

Do not deserialize provider configuration into loosely typed `dynamic`.
Deserialize `ConfigJson` into a provider-specific record, validate it at
creation/update time, and preserve unknown fields only where forward
compatibility requires it.

## 5. API contracts

Version all endpoints under `/api/v1`. Return RFC 9457-style problem details
for errors, including a stable machine-readable error code and correlation ID.
All IDs in request and response DTOs are UUID strings.

### 5.1 Ear lifecycle

| Endpoint | Authentication | Request | Result |
|---|---|---|---|
| `POST /ears/enrolments` | `DaveEar` OAuth client, `davebrain.ear.enroll` scope, consenting `Admin` | Proposed name, target location, client/version details | Creates a registered Ear bound to the token's issuer, subject, and authorized-party/client ID. |
| `POST /ears/{earId}/heartbeat` | Registered `DaveEar` OAuth client and `davebrain.ear.command` scope | Client version and diagnostics summary | Updates `LastSeenUtc`; returns enabled state and future server directives. |
| `POST /speech-commands` | Registered `DaveEar` OAuth client and `davebrain.ear.command` scope | `multipart/form-data`: WAV audio, `earId`, optional client timestamp | Queues transcription and returns a command result synchronously within the configured timeout, or a correlated accepted/retry response when capacity policy requires it. |
| `POST /text-commands` | Registered `DaveEar` OAuth client and Ear scope, or browser `Admin` | `text`, `earId` | Runs the same command pipeline without transcription. Primarily supports diagnostics and integration tests. |

DaveEar registration uses the OAuth Device Authorization Grant:

1. The local `daveear --register` command reads DaveBrain's discovery document
   and calls DaveBrain's `/connect/device` endpoint for a device code, user
   code, verification URI, and the `davebrain.ear.enroll` scope.
2. It displays the URI and short user code. The approving person opens the
   DaveBrain URI on any browser, signs in with a local DaveBrain account,
   reviews the request, and gives consent.
3. DaveEar polls DaveBrain's `/connect/token` endpoint as required by RFC
   8628. After approval it receives a short-lived access token and
   refresh-token grant from DaveBrain.
4. Before polling for a token, DaveEar generates and securely retains a local
   private key and certificate-signing request (CSR). After the
   administrator-approved enrolment token is issued, it sends the CSR to
   `POST /ears/enrolments`. DaveBrain verifies the OAuth client, scope, and
   `Admin` role, validates the chosen location, issues a unique Ear client
   certificate from its internal Ear certificate authority, and creates the
   Ear/certificate binding.
5. DaveEar securely stores its issued client certificate and uses mutual TLS
   for refresh-token and resource requests. DaveBrain issues
   `davebrain.ear.command` access tokens with `cnf.x5t#S256` bound to that
   certificate and accepts an Ear request only when its issuer, subject,
   client ID, certificate thumbprint, and Ear ID match the stored binding.

Require fresh interactive consent for re-enrolment after a device is revoked.
An `Admin` can disable or revoke an Ear from the web interface; DaveBrain
rejects its access token immediately based on the Ear record, and its internal
grant revocation prevents subsequent token refresh. Rate-limit login,
device-code polling/enrolment, token issuance, and command upload per Ear. Do
not use a shared
device password, static API key, or a bearer-only user-token-accepting endpoint
that lets an Ear impersonate an unrelated Ear ID. Validate the TLS client
certificate chain, validity, revocation state, and SHA-256 thumbprint against
the `cnf.x5t#S256` access-token binding before accepting any Ear request.

### 5.2 Configuration and administration

Implement an ASP.NET Core server-rendered administration web interface using
Razor Pages. It must be part of DaveBrain from the first releasable increment,
not a later replacement for an API or CLI. Keep the UI lightweight: render
HTML on the server, use ordinary forms and minimal progressive enhancement,
and avoid a separate SPA frontend, Node runtime, or browser token storage.

All UI routes initiate an OIDC login challenge for unauthenticated visitors.
`User` pages are read-only; every edit, enrolment, revocation, Hue-linking
step, and diagnostic command form requires the `Admin` role and anti-forgery
validation. Hide unauthorized actions in the UI, but rely on identical
server-side authorization policies for the underlying APIs.

The administration interface must provide these workflows:

1. Create a location hierarchy and inspect its effective descendants.
2. Discover a Hue bridge, instruct the administrator to press its link button,
   complete app-key registration, and confirm the bridge connection.
3. Import/select Hue lights and switches, map each to a DaveBot location, and
   rename the local devices.
4. Create a `device.setState` action and action set, then attach it to a
   trigger.
5. Issue a text command from a selected Ear context and inspect its result.
6. View device state, Ear connectivity/last-seen information, command/action
   outcomes, and service dependency health.
7. Review, disable, revoke, or re-enrol a DaveEar. The UI must not display
   access tokens, refresh tokens, or Ear private-key material.
8. Create, disable, reset, and assign the `Admin` or `User` role for local
   DaveBrain accounts.

Configuration deletion must reject removal of referenced entities, unless an
explicit replacement/removal workflow updates all dependencies atomically.

### 5.3 Web UI page and API layout

Use a page/view-model pair for each area and keep all state-changing actions
as explicit `POST`, `PUT`, or `DELETE` operations. The first page set is:

| Route | Access | Purpose |
|---|---|---|
| `/` | `User`, `Admin` | Dashboard with service health and recent in-memory activity. |
| `/locations` | `User`, `Admin` | View location tree and effective devices; `Admin` can create, edit, and delete. |
| `/devices` | `User`, `Admin` | View device state and mappings; `Admin` can import, map, rename, and remove. |
| `/hubs` | `User`, `Admin` | View Hue connection health; `Admin` can discover, link, and sync hubs. |
| `/ears` | `User`, `Admin` | View Ear state; `Admin` can approve, disable, revoke, and re-enrol. |
| `/automation` | `User`, `Admin` | View actions, action sets, and triggers; `Admin` can manage them. |
| `/commands` | `User`, `Admin` | View bounded command/action history; `Admin` can submit a diagnostic text command. |
| `/identity/users` | `Admin` | Manage local accounts, password resets, disablement, and `Admin`/`User` role assignment. |

Expose matching JSON APIs beneath `/api/v1` for DaveEar and later automation
clients. Apply the same named authorization policies at the endpoint level:
`ReadDashboard`, `ManageConfiguration`, `ManageHubs`, `ManageEars`, and
`ExecuteDiagnosticCommand`. Keep all authorization decisions in DaveBrain;
never trust a hidden page control or a caller-provided role field.

### 5.4 Speech-command upload contract

Accept only PCM WAV in the first release. DaveEar must produce mono,
16 kHz, 16-bit PCM; the server validates the RIFF/WAVE header, duration,
channel count, sample rate, and size before queuing work. This avoids codec
libraries and makes the Foundry request predictable on the constrained host.

The command response should contain:

```json
{
  "correlationId": "uuid",
  "transcript": "turn lounge lights on",
  "intent": "set-device-state",
  "targetLocationId": "uuid",
  "targetDeviceIds": ["uuid"],
  "outcome": "succeeded",
  "message": "Turned on 3 Lounge lights."
}
```

For recognition failure, unknown location/device, ambiguous target, disabled
Ear, full queue, or failed device operation, return a non-success outcome and
a user-safe message. Ambiguous commands must not cause a best-guess device
change.

## 6. Command processing

### 6.1 Shared command pipeline

Both speech and text commands must call one `CommandProcessor`:

1. Authenticate and load the Ear's immutable source location.
2. Normalize transcript whitespace, casing, punctuation, and known
   number/brightness forms while retaining the original text for audit.
3. Parse against an explicit grammar and command vocabulary.
4. Resolve target names against registered locations and devices.
5. Use the Ear location only when the parsed command has no explicit target
   location.
6. Validate supported device capability and construct an allow-listed action.
7. Execute through the device provider, record the outcome, and return an
   understandable result.

For the first increment, implement deterministic grammar rather than an LLM.
Support:

| Intent | Example forms | Result |
|---|---|---|
| Turn lights on/off | `lights on`, `turn lounge lights off` | Sets `on` for matching Hue lights in the resolved location tree. |
| Set brightness | `set kitchen lights to 50 percent`, `dim lights to 25` | Sets an integer brightness after capability/range validation. |
| Device state query | `are the lounge lights on` | Reads current provider state and reports a summary; performs no mutation. |

Normalize names case-insensitively and support a curated alias table. Names
that match more than one location or device must produce an ambiguity result
with candidate names, not execute every possible match. Add command grammar,
aliases, color, scenes, and multi-clause commands only after the core behavior
has tests and real household usage feedback.

### 6.2 Transcription integration

Build `ITranscriptionService` with a Foundry Local implementation and a fake
test implementation. The implementation sends the validated WAV stream to the
configured local Whisper Tiny transcription endpoint, explicitly selects the
configured model, and uses cancellation/timeout handling.

Before implementation, perform a short compatibility spike on the actual
Foundry Local version installed on the server:

- confirm model-install command, endpoint address, request format, and
  response schema for Whisper Tiny;
- measure cold and warm transcription latency for 5-, 15-, and 30-second
  clips on the Pentium J4205;
- choose the smallest acceptable queue capacity and command timeout from those
  measurements;
- document the exact installation and model provisioning commands in
  `Docs/operations.md`.

The production application must fail readiness clearly when the configured
endpoint/model is unavailable; it must not silently substitute a cloud model.

## 7. Philips Hue integration

The local references in `Docs/Philips Hue` cover bridge discovery, initial
registration/link-button flow, lights, switches, groups/scenes, and CLIP API
concepts. Use them for the initial implementation, but validate their API
version and authentication requirements against the physical bridge firmware
before coding the adapter. Isolate that decision behind `IHueBridgeClient` so
the domain and command layers do not depend on a particular CLIP version.

### 7.1 Hub enrollment and discovery

1. Discover bridges using the vendor-supported local discovery mechanism.
2. Show discovered IP address, bridge identity, and TLS/certificate
   information to the local administrator.
3. When the administrator presses the bridge link button, request an
   application key and persist it protected at rest.
4. Fetch the supported resources/capabilities and show import candidates.
5. Associate each imported resource with one `HueHub` and one DaveBot device.
6. Verify a read-only state request, then a user-confirmed test action.

Never scan arbitrary public addresses, trust an unverified remote bridge, or
store bridge credentials in log output. Preserve a bridge-resource identifier
in provider configuration rather than using mutable resource names.

### 7.2 Hue device behavior

- `HueLight` supports state reads plus on/off and brightness in the first
  release. Convert DaveHome's normalized 0-100 brightness to Hue's expected
  range only inside the adapter.
- `HueSwitch` consumes button and sensor events. Its initial role is trigger
  input; it has no unsupported “set switch” behavior.
- Cache last observed device state in memory for trigger comparison, then
  reconcile it from the bridge after restart or connectivity recovery.
- Prefer the bridge's supported event stream for prompt updates. If the bridge
  or selected API does not offer a suitable reliable stream, use bounded
  polling and document the interval and expected trigger latency.
- Reconnect with bounded exponential backoff. On reconnect, re-read relevant
  state before evaluating new triggers, preventing stale-state actions.
- Map vendor errors to typed failures such as unavailable bridge, unavailable
  resource, unauthorized app key, invalid state, and timeout.

### 7.3 Triggers and actions

Implement event handling as:

```text
Hue event -> normalized DeviceStateChanged
          -> matching enabled Trigger(s)
          -> state criteria evaluation
          -> cooldown/deduplication check
          -> ordered ActionSet execution
          -> ActionAudit records
```

Define `StateCriteriaJson` as an explicitly documented JSON predicate schema,
not an arbitrary expression language. Initial predicates should support
equality and membership on normalized state fields, for example:

```json
{
  "all": [
    { "field": "button", "operator": "equals", "value": "initial_press" }
  ]
}
```

Define the initial `device.setState` action parameters as:

```json
{
  "deviceIds": ["uuid"],
  "state": { "on": true, "brightness": 75 }
}
```

Validate action capability, referenced devices, criteria fields, and action-set
membership during configuration changes. Store a cooldown/deduplication key
per trigger and source state transition so an unchanged or replayed Hue event
cannot repeatedly activate a rule.

## 8. DaveEar implementation

### 8.1 Client configuration and registration

DaveEar is a cross-platform .NET Worker Service executable. It runs
interactively for installation/diagnostics and as a Windows Service or Linux
systemd service in normal operation.

Its local configuration contains:

```json
{
  "ServerUrl": "https://brain.daves.tips",
  "EarId": "uuid",
  "EarName": "Kitchen Ear",
  "LocationId": "uuid",
  "OAuthIssuer": "https://brain.daves.tips",
  "OAuthClientId": "DaveEar",
  "MicrophoneDeviceId": "platform-specific-id",
  "SilenceGapMilliseconds": 1000,
  "MaxSegmentSeconds": 30
}
```

Keep the Ear private key, issued client certificate, and OAuth refresh token in
an OS-appropriate protected secret location, separate from the readable
configuration file. At initial setup, `--register` completes the Device
Authorization Grant, submits a CSR with the requested name/location, saves the
returned Ear identity and certificate chain, validates server TLS, and sends a
heartbeat. DaveEar treats its server-assigned location as read-only; only a
DaveBrain `Admin` changes it. If consent is revoked, refresh fails, or the
client certificate/private key is unavailable, stop uploads, report an
authentication failure, and require re-enrolment rather than falling back to
unauthenticated operation.

### 8.2 Audio capture and segmentation

Introduce interfaces for `IAudioCapture`, `IVoiceActivityDetector`,
`IAudioSegmentEncoder`, and `ICommandUploader`. Keep the platform audio
backend separate from the common segmentation/uploader pipeline.

The minimum pipeline is:

1. Capture mono PCM frames from the selected default or configured microphone.
2. Use a lightweight VAD/noise-gate implementation to identify a sustained
   sound onset. Start with calibrated RMS/energy thresholds and a rolling noise
   floor; evaluate a native/WebRTC VAD dependency only if it materially reduces
   false activations on the target hardware.
3. Retain a small pre-roll buffer (approximately 250 ms) so first spoken
   syllables are not lost.
4. Continue collecting audio until at least 1,000 ms of trailing silence is
   observed.
5. Stop at `MaxSegmentSeconds`, mark the segment truncated, and send it rather
   than retaining unbounded input.
6. Resample/encode to the server's 16 kHz, 16-bit, mono PCM WAV contract and
   upload over HTTPS.

Implement a bounded on-disk retry spool for transient network/server errors.
The spool must have an item count/size limit, delete successfully uploaded
segments, delete expired segments, and surface when it begins discarding
audio. Never let microphone capture or a failed network request cause
unbounded RAM, disk, or worker-thread consumption.

Add initial platform support in two replaceable implementations:

- Windows: a tested WASAPI-compatible capture backend suitable for a service.
- Linux: an ALSA/PulseAudio/PipeWire-compatible backend selected after testing
  on the intended distribution and hardware.

The implementation spike must verify that the selected package has supported
licenses, compatible native deployment requirements, service-mode support,
and low idle CPU use on both target platforms.

### 8.3 Service and update operations

- Provide `--register`, `--diagnose`, and `--run` commands. `--diagnose`
  validates configuration, microphone availability, server certificate,
  OAuth/mTLS authentication, and a short test upload only after user consent.
- Document Windows Service installation/recovery and a `systemd` unit with a
  restricted service account, restart policy, configuration path, and required
  audio-device permissions.
- Make service status expose capture state, last heartbeat, last upload
  outcome, queue depth, and microphone error without revealing OAuth tokens or
  Ear private-key material.
- On configuration/permission/microphone failure, keep a supervised process
  alive with health/error reporting and bounded retry instead of spinning in a
  tight restart loop.

## 9. Delivery phases

Windows Server 2019 x64 or later is the DaveBrain deployment baseline. Test
each increment as it is implemented; do not schedule separate up-front
hardware profiling or metric-collection work. The detailed preparation
documents below are the execution checklist for their respective phases.

| Phase | Detailed preparation document |
|---|---|
| Phase 0 | [Phase 0 - Architecture and Dependencies](Docs/Implementation%20Phases/Phase%200%20-%20Architecture%20and%20Dependencies.md) |
| Phase 1 | [Phase 1 - Foundation and Identity](Docs/Implementation%20Phases/Phase%201%20-%20Foundation%20and%20Identity.md) |
| Phase 2 | [Phase 2 - Hue Vertical Slice](Docs/Implementation%20Phases/Phase%202%20-%20Hue%20Vertical%20Slice.md) |
| Phase 3 | [Phase 3 - Text Commands](Docs/Implementation%20Phases/Phase%203%20-%20Text%20Commands.md) |
| Phase 4 | [Phase 4 - Speech Transcription](Docs/Implementation%20Phases/Phase%204%20-%20Speech%20Transcription.md) |
| Phase 5 | [Phase 5 - DaveEar](Docs/Implementation%20Phases/Phase%205%20-%20DaveEar.md) |
| Phase 6 | [Phase 6 - Automation and Operations](Docs/Implementation%20Phases/Phase%206%20-%20Automation%20and%20Operations.md) |

### Phase 0 - Validate environment and technical choices

1. Use Windows Server 2019 x64 or later as the fixed DaveBrain deployment
   baseline. Record the local DNS name, HTTPS certificate strategy, issuer,
   storage paths, signing-key rotation approach, local backup-package scope,
   and one-time first-administrator password-change procedure.
2. Select the Hue CLIP v2 API, default `JsonSnapshotHomeStatePersistence`
   storage layout, and the OAuth/OIDC server component for DaveIdentity.
   Prefer Microsoft/BCL components and use OpenIddict (Apache-2.0) only for
   the missing authorization-server features. Use its native mutual TLS
   certificate-bound token support (RFC 8705). Confine it to DaveIdentity and
   record its license-free dependency decision. Record audio
   backend requirements for the supported DaveEar platforms; select the
   concrete package while implementing Phase 5. Configure the issuer, exact
   HTTPS redirect/verification URIs, audience, scopes, `Admin`/`User` roles,
   token lifetimes, token-signing certificate, and internal Ear client
   certificate authority. See
   [ADR-001](Docs/Decisions/ADR-001-daveidentity-oauth-server.md).
3. Record the selected dependency, configuration, and deployment decisions in
   architecture/operations documentation. Functional behavior is tested
   within the phase that implements it, beginning in Phase 1.

**Exit criteria:** the Windows Server 2019+ deployment baseline, dependency
choices, issuer/certificate configuration, storage layout, and operational
decisions are documented and ready for Phase 1 implementation.

### Phase 1 - Establish solution and durable configuration

1. Create the four projects and their test projects, including the
   `DaveIdentity` class library referenced by DaveBrain, Razor Pages
   administration shell, build configuration, central package versions,
   analyzers already supplied by the .NET SDK, and CI build.
2. Implement DaveHome domain records, ID validation, storage ports, the
   immutable in-memory state store, the default atomic JSON snapshot provider,
   a reusable persistence-provider contract test suite, and location hierarchy
   validation.
3. Implement DaveIdentity local account bootstrap/management, ASP.NET Core
   Identity password/lockout/session handling, OpenIddict OIDC/OAuth endpoints
   and discovery/JWKS publication, `Admin`/`User` role mapping, scope/client
   policies, mTLS certificate issuance/binding/revocation, protected
   signing/secret storage, structured logging, and health checks.
4. Implement authenticated read-only dashboard/location pages, `Admin`
   location management, Ear Device Authorization enrolment, and the
   heartbeat contract. Apply the same authorization policies to each UI and
   API endpoint from its first implementation.

**Exit criteria:** an administrator can create a nested location hierarchy,
register/disable an Ear through the consent flow, restart DaveBrain with its
configuration and identity/grant state restored from their snapshots, and an
unauthenticated, incorrect-role, revoked-grant, or unregistered Ear cannot
call protected endpoints.

### Phase 2 - Build the Hue vertical slice

1. Implement `IHueBridgeClient`, hub enrollment, protected app-key storage,
   discovery, resource import, and periodic reconciliation.
2. Implement Hue light state reads and on/off/brightness commands.
3. Implement device/location mapping and the `device.setState` action.
4. Add the authenticated Hue and device-management pages to the administration
   interface, including the manual diagnostic text-command form for an
   `Admin`.

**Exit criteria:** a mapped light can be turned on, off, and dimmed through
DaveBrain; a failed bridge/device operation produces a visible typed failure;
the bridge state is reconciled correctly after service restart.

### Phase 3 - Complete text commands

1. Implement deterministic parsing, aliases, location-tree target resolution,
   ambiguity detection, command audit, and safe result messaging.
2. Expose `POST /text-commands` and route it through the shared command
   processor.
3. Add capability-aware execution across all selected lights in the resolved
   location.

**Exit criteria:** the three first-release intents work for both default Ear
location and explicit locations; ambiguous or unknown targets change no
devices; results identify affected devices and failures.

### Phase 4 - Integrate Foundry Local speech transcription

1. Implement validated WAV upload, bounded queue, local transcription service,
   temporary audio lifecycle, and speech-command response DTOs.
2. Route transcription text into the already-tested text command processor.
3. Add endpoint readiness/error behavior for unavailable model, timeout,
   malformed audio, and saturated queue.

**Exit criteria:** a valid WAV from a registered Ear can turn a mapped light on
and produces a correlated audit record; malformed/oversized audio and an
offline Foundry endpoint are safely rejected without memory growth.

### Phase 5 - Deliver DaveEar

1. Implement configuration, secure local key storage, registration flow,
   heartbeat, and command uploader.
2. Implement target-platform capture, VAD segmentation, WAV encoding,
   retry spool, diagnostic mode, and service integrations.
3. Test real microphone behavior for quiet rooms, normal conversation,
   background audio, short speech, long speech, unplugged devices, loss of
   network, and Brain restart.

**Exit criteria:** an installed Ear on Windows and Linux can continuously
capture speech, use the 1-second silence boundary, recover from an interrupted
network, and control the correct location's lights without user interaction.

### Phase 6 - Add switch-trigger automation and harden operations

1. Implement Hue switch event source, normalized device-state events,
   trigger criteria, cooldown/deduplication, action sets, and action audit.
2. Complete local disk backup-package/restore, service recovery, structured
   diagnostics, log retention, update/rollback, and security documentation.
3. Run sustained soak testing with Ears and Hue events; tune queue sizes,
   timeouts, VAD thresholds, polling/event reconnection, and resource limits
   from measured behavior.

**Exit criteria:** a configured switch action fires once per intended press,
does not repeat on event replay/reconnect, and the system remains within the
measured CPU, memory, disk, and latency budget over the soak period.

## 10. Test strategy

### Unit tests

- Location inclusion traversal, missing references, duplicate normalized names,
  and every cycle case.
- Configuration validation for all entity references and provider JSON.
- Command normalization, grammar parsing, explicit/default location resolution,
  aliases, ambiguity, unsupported capability, and no-action error paths.
- Action parameter validation, action-set ordering, trigger criteria, cooldown,
  and deduplication.
- WAV header/duration validation and DaveEar VAD state transitions using
  deterministic PCM fixtures.
- OIDC claim-to-role mapping, scope/client authorization policies, mTLS client
  certificate-chain/thumbprint/revocation validation, and Ear
  issuer/subject/client/certificate binding without logging tokens or secrets.
- Password-hash verification/rehash, normalized-user uniqueness, role changes,
  account disablement, lockout, security-stamp session invalidation, consent,
  authorization/device-code single use and expiry, refresh-token rotation, and
  revocation.

### Integration tests

- Run DaveBrain against a temporary state-snapshot directory and fake
  transcription/Hue clients.
- Run the persistence-provider contract suite against the JSON snapshot
  provider. The suite covers load, version upgrade, validation failure,
  expected-revision conflict, atomic save failure, backup recovery, and
  round-trip equivalence; every future SQL or other provider must pass it.
- Run an equivalent identity-store contract suite against the JSON identity
  provider. It covers account/role persistence, password-hash round trips,
  client registration, consent, one-time-code consumption, refresh-token
  rotation, revocation, expiry cleanup, and atomic/revision failure behavior;
  every future SQL or other identity provider must pass it.
- Exercise registration, heartbeat, text command, speech command, error
  problem details, queue capacity, and disabled/revoked Ear authorization.
- Run the Razor Pages UI against DaveBrain's internal test identity store and
  test signing certificate to verify unauthenticated challenge, `User`
  read-only access, `Admin` mutations, anti-forgery failure, and API/UI policy
  parity.
- Exercise DaveBrain's own Device Authorization Grant endpoints: pending
  consent, denial, expiry, successful mTLS certificate enrolment, rotated
  refresh token, invalid audience/client/scope, certificate mismatch, and
  grant revocation.
- Verify snapshot upconversion from every prior schema version, atomic
  checkpoint failure handling, backup-snapshot recovery, restart persistence,
  and an export/import round trip through the canonical storage document.
- Use a stub Hue HTTP server to test link/enrollment flows, timeouts,
  unauthorized credentials, resource mapping, reconnect reconciliation, and
  payload conversion.

### Hardware and acceptance tests

- Execute end-to-end tests on the Pentium J4205 with the production Foundry
  Local model, bridge, and at least one physical light and switch.
- Measure cold/warm command latency, idle CPU/RAM, peak RAM during
  transcription, number of concurrent uploads, disk spool behavior, and
  recovery after power/service interruption.
- Establish release thresholds from the measurements before setting them as
  CI/operational gates. A practical initial goal is that the system remains
  responsive at idle and processes its configured single speech command
  reliably without swap pressure.
- Manually test that “lights on” from each Ear affects only its configured
  location tree and that “turn lounge lights on” overrides that context.

## 11. Risks, decisions, and mitigations

| Risk or open decision | Mitigation |
|---|---|
| Foundry Local API/model behavior differs by installed version | Complete Phase 0 compatibility spike, encapsulate it behind `ITranscriptionService`, and document the verified version/configuration. |
| Whisper Tiny accuracy is insufficient for room/device names | Start with unique short names and aliases, record failed/ambiguous text outcomes, and evaluate vocabulary prompting or a larger local model only after measuring hardware impact. |
| Continuous listening produces false positives or clips speech | Tune VAD with recordings from real rooms, retain pre-roll, enforce a silence gap and max length, and make thresholds diagnosable/configurable per Ear. |
| Hue vendor API differences or firmware upgrades break integration | Isolate protocol code, store stable resource IDs, use the supplied docs plus a physical bridge verification test, and provide typed adapter errors. |
| Internal identity configuration, signing key, or roles are unavailable or incorrect | Include OIDC/OAuth setup in Phase 0, require successful local account, discovery/JWKS, signing-certificate, and token checks for readiness, fail closed, document administrator provisioning/key rotation, and test `Admin`/`User` policies before exposing the service. |
| OAuth device credentials are exposed or replayed | HTTPS, Device Authorization Grant user consent, short-lived mTLS certificate-bound tokens, protected refresh-token/private-key storage, certificate validation/revocation, credential redaction, and grant revocation through Ear administration. |
| A slow dependency exhausts limited hardware | Bounded queue, one transcription worker, upload/audio limits, timeouts, retry backoff, and an in-memory state store/direct HTTP rather than additional services. |
| A future storage provider changes behavior or loses configuration/identity state | Keep home and identity documents plus storage ports provider-neutral, require optimistic revision handling and atomic/transactional saves, run shared home/identity provider contract suites, and use validated export/import with rollback backup during provider migration. |
| Location hierarchy causes unintended broad actions | Validate acyclic containment, display effective device targets during administration, require exact/unique command resolution, and audit every action. |

## 12. Definition of done for the first releasable system

The first release is complete when a freshly deployed Windows Server DaveBrain
instance can be configured with a local Foundry Local Whisper Tiny endpoint
and Hue bridge, two named locations and mapped lights; registered Windows and
Linux DaveEar clients can continuously capture and send valid speech segments;
commands resolve according to their authenticated Ear location or an explicit
location; the supported light commands execute safely; Hue switch triggers
execute configured action sets once; the authenticated Razor Pages interface
enforces read-only `User` access and mutating `Admin` access; every DaveEar is
enrolled through OAuth device consent and uses mTLS certificate-bound tokens; in-memory
configuration is restored after a controlled restart through the default
snapshot provider; storage ports and their contract suite support adding a SQL
provider without changing domain or API behavior; and deployment, backup,
recovery, troubleshooting, and known limitations are documented.
