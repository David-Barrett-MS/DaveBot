# DaveBot Operations

## Phase 0 operating baseline

| Item | Standard |
|---|---|
| Host | Windows Server 2019 x64 or later |
| Service name | `DaveBrain` |
| Service identity | `NT SERVICE\DaveBrain` |
| Server URL and OIDC issuer | `https://brain.daves.tips` |
| Browser/OIDC HTTPS listener | TCP 443 on the private LAN |
| DaveEar mTLS HTTPS listener | TCP 8443 on the private LAN |
| TLS | Self-signed `brain.daves.tips` certificate |
| State path | `C:\ProgramData\DaveBot` |
| Backup-package path | `C:\ProgramData\DaveBot\backups` |

No DaveBrain endpoint is intended for public internet access. Configure private
LAN DNS so `brain.daves.tips` resolves to the server's private address. Limit
the Windows Firewall inbound rules for TCP 443 and TCP 8443 to the relevant
LAN subnets.

Kestrel serves the browser administration UI, OIDC discovery, device
authorization, and enrollment on `https://brain.daves.tips:443` without
requesting a client certificate. DaveEar uses
`https://brain.daves.tips:8443` for mTLS token refreshes, heartbeats, and
command uploads. Both listeners use the same `brain.daves.tips` server TLS
certificate. The `DaveEarMtls` endpoint must set
`ClientCertificateMode` to `RequireCertificate`; the application rejects Ear
requests arriving on the UI/OIDC listener.

## Service account and directories

Install DaveBrain as a Windows Service running under `NT SERVICE\DaveBrain`.
Grant this identity:

- read access to the selected certificate private key;
- modify access to `C:\ProgramData\DaveBot` and its child directories;
- network access only where required for the Hue bridge and private LAN clients,
  plus outbound HTTPS/WSS on TCP 443 if Philips Air+ is enabled.

Do not run DaveBrain as LocalSystem, a personal administrator account, or a
shared interactive account. Keep Foundry Local bound to loopback and grant the
service only the local access it needs to call it.

## Local speech transcription

DaveBrain sends `POST /api/v1/speech-commands` audio only to the configured
Foundry Local OpenAI-compatible transcription endpoint. The default endpoint is
`http://127.0.0.1:5273/v1/audio/transcriptions`; startup rejects any endpoint
that is not `localhost` or an IP loopback address. It also rejects every model
name except `whisper-tiny`. There is no cloud, alternate-model, chat, or
natural-language fallback.

Configure `DaveBot:SpeechTranscription` with the loopback endpoint and bounded
timeout, upload/duration, and queue values. The defaults allow a 1 MB, mono
16 kHz, 16-bit PCM WAV of at most 30 seconds and one worker with two waiting
items. `/health` returns HTTP 503 and a safe Foundry/Whisper dependency code
when the local endpoint or model is unavailable; `/health/live` only reports
whether DaveBrain is running. Audio is held only in the bounded in-memory queue
for the request. Multipart buffering is explicitly memory-only within the
configured body limit, so raw audio is never written to temporary storage or
otherwise persisted.

## Self-signed certificate

Create one self-signed certificate with `brain.daves.tips` in its DNS subject
alternative name and the server's listener IP address in its IP subject
alternative name. Install its private key in the Windows Local Machine `My`
Certificate Store. The CrankyServer configuration selects the certificate by
the `brain.daves.tips` subject. The certificate should be non-exportable unless
an encrypted recovery process has been explicitly established.

For the configured `192.168.0.113` listener, run
`Install-DaveBrainHttpsCertificate.ps1` from an elevated PowerShell session on
CrankyServer. The script reuses or creates separate non-exportable five-year
certificates in `Cert:\LocalMachine\My` for:

- the RSA 2048-bit `brain.daves.tips` server-authentication certificate;
- the RSA 3072-bit DaveIdentity signing/encryption certificate; and
- the RSA 3072-bit DaveBot Ear client-certificate authority.

It writes the identity certificate thumbprints and Kestrel listener settings to
`appsettings.json`, configures the same TLS certificate for the port-443 UI
and port-8443 DaveEar listeners, and installs the TLS certificate's public
portion in `Cert:\LocalMachine\Root` so Kestrel can validate the self-signed
certificate with `AllowInvalid` disabled. It never exports a private key.

Distribute the public certificate or self-signed trust anchor before enrolling
clients:

| Client | Required trust installation |
|---|---|
| Windows browser/DaveEar | Import into the Local Machine Trusted Root Certification Authorities store. |
| Ubuntu, Raspberry Pi OS, Debian | Install the public PEM/CRT file in the system CA directory, then run the distribution's CA update command. |

Validate the certificate hostname and chain from each client. Never configure
DaveEar, the browser, or HTTP handlers to accept arbitrary certificates.

Replace the certificate before expiry. During replacement, distribute and trust
the replacement public certificate before restarting DaveBrain. Retain the old
certificate only as long as it is needed to validate existing signatures or
sessions; plan signing-key rollover so JWKS publishes both public keys during
the overlap.

Maintain three separate certificate roles:

| Certificate | Purpose | Private-key location |
|---|---|---|
| Server TLS certificate | Authenticates `brain.daves.tips` to browsers and DaveEars | Windows Certificate Store on DaveBrain |
| OAuth signing certificate | Signs DaveIdentity access tokens and publishes its public key through JWKS | Windows Certificate Store on DaveBrain |
| Ear client-certificate authority | Issues and validates per-Ear mTLS client certificates | Windows Certificate Store on DaveBrain |

Never reuse the server TLS or OAuth signing certificate as the Ear
client-certificate authority.

## Hue bridge TLS trust

Hue discovery reports a bridge's LAN IP address, but the bridge certificate is
issued for the bridge ID rather than that IP address. For a bridge selected
from discovery, DaveBrain uses the bridge ID as the HTTPS hostname and routes
the TCP connection to its discovered IP address; it does not disable TLS
hostname validation.

During the physical link-button flow, a currently valid bridge-ID certificate
whose only chain failure is `PartialChain` is trusted once and its SHA-256
fingerprint is pinned in the hub record. All later Hue requests require the
same hostname and fingerprint. Certificate hostname mismatches, expired
certificates, all other chain failures, and a changed pinned certificate are
rejected. Installing the Philips Hue Bridge CA in the Windows Local Machine
**Trusted Root Certification Authorities** store is optional and provides
normal chain validation, but is not required for this pinned pairing flow.

The Hue bridge link page shows the returned Hue API error description or the
safe TLS/network error detail.

## Philips Air+ account integration

Philips Air+ is a cloud integration; it is not discovered on the LAN. Enable
it only when the DaveBrain host is permitted to make outbound certificate-
validated HTTPS and WSS connections to Philips/Versuni on TCP 443. The
application uses the same public client identity and registered
`com.philips.air://loginredirect` redirect URI as the referenced Air+ email
integration; the administrator does not configure protocol credentials.

After restarting DaveBrain, an Admin opens **Philips Air+**, enters the account
email address, requests a verification code, and enters that code in the same
Admin UI. The short-lived email verification data remains only in server
memory; the completed access and refresh registration is stored as the hub's
DPAPI-protected secret, exactly like the Hue application-key registration.
The import screen lists devices even when a particular device has no current
shadow readings; import it, then use **Read sensors** after it is online. The
application only permits fan-speed values 1-10, 81, and 82, and does not allow
arbitrary cloud device-state writes.

## Blink live view

Blink live view requires DaveBrain to make outbound certificate-validated HTTPS
and TLS/TCP connections to Blink on port 443. Blink's `immis://` stream endpoint
currently presents a certificate with a chain and endpoint-name mismatch. For
this client only, DaveBrain accepts those two validation errors when a server
certificate is present, currently valid, and permits TLS server authentication.
It still rejects an unavailable, expired, or otherwise invalid certificate.
This exception applies neither to Blink HTTPS APIs nor to any other integration.
The **Live view** action is available only to the same authenticated
administrators permitted to operate devices. It establishes a short-lived,
user-bound WebRTC H.264 session directly from DaveBrain to a current Chromium
or Edge browser; no Blink address, token, or stream URL is sent to the browser.

The current private-LAN deployment uses direct ICE candidates and does not
provide a TURN relay. Permit the relevant LAN browser subnets to reach the
DaveBrain host's dynamically selected UDP ports, and do not publish the service
to the public internet. If that cannot be guaranteed, disable access to the
page rather than exposing it through NAT. The SIPSorcery managed package
packetises the Blink H.264 stream for WebRTC and has no native media dependency.

Live-view state, MPEG-TS packets, and H.264 access units exist only in memory.
Closing the overlay, leaving the page, connection failure, expiry, or service
shutdown closes the socket and tells Blink that the command is done. The
application never writes video segments, stream metadata, Blink live-view
tokens, or upstream stream addresses to disk or the administration debug log.

## Hue motion and switch event-stream recovery

DaveBrain tracks mapped Hue switch presses plus Hue motion and ambient
light-level sensor updates through each bridge's authenticated CLIP v2
Server-Sent Events endpoint, `/eventstream/clip/v2`. A motion resource and its
associated `light_level` resource are one logical `hue.motion-sensor`; a sensor
without a light-level resource remains valid. The application key is sent only
as the `hue-application-key` request header. The stream uses the same
bridge-ID TLS hostname mapping and certificate pinning as all other Hue
requests; operators must not configure a certificate-validation bypass.

The service reconciles each mapped source's current state before it accepts a
stream and after every disconnect. Reconciliation is a baseline, not a
trigger, so service restarts, stream snapshots, reconnects, and newly mapped
buttons or sensors cannot replay historic automation. Streams reconnect
independently per hub with exponential delays capped at 30 seconds. The legacy
one-second poll is not normal operation: it is enabled only after three
consecutive stream failures, only until the next stream retry window, and is
itself baseline-suppressed. A bridge with stream failures therefore does not
block event ingestion from other linked hubs. Investigate sustained
stream-fallback warnings, bridge connectivity, Hue authorization, and the
pinned certificate; the service keeps retrying the stream automatically.

## Identity administration

When no active administrator exists, DaveIdentity creates the local `Admin`
account `Dave`. Its bootstrap password is `Dave`; it is accepted only for the
first sign-in, which redirects exclusively to the password-change page. The
replacement password must be at least 8 characters. The bootstrap password
is stored only as an ASP.NET Core Identity password hash, is never logged, and
cannot be used after a replacement password is saved. There is no anonymous
web bootstrap endpoint.

After first setup, only an authenticated `Admin` may create users, assign
`Admin` or `User`, reset a password, disable an account, approve DaveEar
device-code consent, or revoke an Ear. Disabling an account, revoking a grant,
or disabling an Ear invalidates subsequent access and refresh-token use.

## DaveEar trust and enrollment

Before running `daveear --register`, install the DaveBrain self-signed
certificate trust anchor on the Ear host. Registration performs Device
Authorization Grant against DaveBrain:

1. DaveEar retrieves `https://brain.daves.tips/.well-known/openid-configuration`.
2. It requests a device code, displays the verification URI and user code, and
   creates/stores a private key and certificate-signing request securely.
3. An Admin signs in to DaveBrain on a browser and approves consent.
4. DaveIdentity issues an Ear client certificate from its internal
   client-certificate authority. DaveEar stores the certificate chain and
   private key securely.
5. DaveEar exchanges/refreshes tokens over mutual TLS and uses only
   certificate-bound access tokens for Ear resource calls.

If the Ear is revoked, its refresh token fails, or its client certificate or
private key is lost, it must be re-enrolled. Do not copy a refresh token,
client certificate, or private key between devices.

## DaveEar deployment and operation

DaveEar is an audio capture and upload agent. It does not contain a chat,
natural-language, or general-purpose model. DaveBrain is the only component
that transcribes submitted PCM WAV audio, using its loopback-only
`whisper-tiny` Foundry Local configuration.

Register each machine after installing the DaveBrain TLS trust anchor:

```text
daveear --register --issuer https://brain.daves.tips --name Kitchen --location <location-guid>
daveear --diagnose
daveear --run
```

`--register` displays the OIDC device verification URI and code. An
administrator approves it in a browser. The Ear generates its RSA key and CSR
locally; only the CSR is submitted. The protected local credential contains
the private key, issued certificate, and rotating refresh token. The nonsecret
configuration has its own owner-only ACL. `--status` displays Ear, issuer,
location, and the owner-only local safe runtime counters/state. It never
displays tokens, certificates, private keys, audio, or device codes.

On Windows, credentials are DPAPI LocalMachine-protected and the state
directory is ACLed to the service identity. WASAPI capture is supported in an
interactive user audio session. Before configuring a Windows Service, test
`daveear --diagnose` under that exact service identity: ordinary Session 0
services commonly cannot see an interactive microphone, and DaveEar reports
that condition rather than pretending it can capture. Create a dedicated
least-privileged account, grant it microphone permission and modify access
only to the DaveEar state directory, then configure service recovery, for
example:

```text
sc.exe create DaveEar binPath= "\"C:\Program Files\DaveBot\DaveEar.exe\" --run" obj= ".\DaveEarSvc" password= "<managed password>"
sc.exe failure DaveEar reset= 86400 actions= restart/5000/restart/5000/""/0
```

On Ubuntu 24.04, Raspberry Pi OS, and Debian 12, DaveEar uses `arecord` with
the ALSA PCM interface (`S16_LE`, mono, 16 kHz). PipeWire is supported when
its ALSA compatibility plugin exposes the configured/default device. Install
`alsa-utils`, give the dedicated `daveear` user only the required `audio`
group/device permissions, and verify with `--diagnose`. Linux credentials use
the OS Secret Service via `secret-tool`; installation intentionally fails
closed if `secret-tool` or an unlocked service provider is unavailable rather
than writing refresh tokens or private keys to disk.

An example least-privileged systemd unit (adjust `User`, `StateDirectory`, and
the Secret Service environment for the chosen provider) is:

```ini
[Unit]
Description=DaveEar audio upload agent
After=network-online.target sound.target
Wants=network-online.target

[Service]
Type=exec
User=daveear
Group=daveear
SupplementaryGroups=audio
ExecStart=/usr/local/bin/daveear --run --state-dir /var/lib/daveear
Restart=on-failure
RestartSec=5
StateDirectory=daveear
UMask=0077
NoNewPrivileges=yes
PrivateTmp=yes
ProtectSystem=strict
ProtectHome=yes
ReadWritePaths=/var/lib/daveear

[Install]
WantedBy=multi-user.target
```

The segmenter holds at most its configured pre-roll, current segment, bounded
upload channel, and bounded protected retry spool. It starts after sustained
energy, ends after 1,000 ms silence, and forces the configured maximum length.
Only exact 16 kHz mono 16-bit PCM WAV is sent. Definite 429/5xx responses are
retried with capped exponential/server-directed delay. A timeout or connection
failure is deliberately held and never automatically resent because DaveBrain
may already have accepted it. Successful, rejected, expired, and quota-excess
items are deleted/discarded without logging audio. A failed certificate-bound
refresh or rejected heartbeat stops uploads and requires re-enrollment.

## State, logs, and backup packages

`state.json` contains home configuration. `identity.json` contains local
identity records, client metadata, consent, and hashed/expired/revoked grant
records. Both use atomic write with `.bak` recovery. They must not contain
plaintext passwords, raw access/refresh/device/authorization tokens, Hue
application keys, private signing keys, or certificates.

Create an encrypted, access-controlled backup package under
`C:\ProgramData\DaveBot\backups` while DaveBrain is stopped. Its contents
must include:

- `state.json` and `state.json.bak`;
- `identity.json` and `identity.json.bak`;
- a manifest containing creation time, document schema versions, checksums,
  software version, issuer URL, and restore instructions;
- a record of certificate thumbprint and DPAPI recovery requirements, without
  embedding private key material or protected secret plaintext.

### Backup and restore procedure

1. Record the running DaveBrain version, service identity, state-directory
   path, issuer, and certificate thumbprints. Stop `DaveBrain` and confirm it
   is stopped before copying files.
2. Copy `state.json`, `state.json.bak`, `identity.json`, and
   `identity.json.bak` together. Include the `secrets` directory and the
   Data Protection `keys` directory only in an encrypted backup whose access
   is restricted to the recovery operators.
3. Back up the server TLS, OAuth signing, and Ear CA certificate private keys
   only through the approved certificate recovery process. They are normally
   non-exportable; do not weaken that setting merely to make a backup.
4. Store a manifest with SHA-256 hashes, schema/revision values, software
   version, issuer, service identity, and certificate thumbprints. Do not put
   protected-secret plaintext, passwords, tokens, audio, or private keys in
   the manifest.
5. To restore on the same host and service identity, stop the service, retain
   the failed directory as a rollback copy, restore the matching documents,
   `.bak` files, DPAPI keys, and protected secrets, verify ACLs, then start
   the service and check `/health/ready`.

DPAPI material and protected Hue secrets normally cannot be moved to a
different host or a different service identity. On such a restore, install
approved replacement certificates and re-enroll Hue bridges and DaveEars as
needed. Recreating the signing key invalidates prior sessions/tokens; retain
the prior signing certificate for any planned validation overlap.

## Windows Service recovery and log retention

Configure the DaveBrain service to restart on its first and second failures,
then require operator review on a third failure, for example:

```text
sc.exe failure DaveBrain reset= 86400 actions= restart/5000/restart/30000/""/0
```

Use the exact `NT SERVICE\DaveBrain` identity for recovery testing. Retain
structured service logs for the approved operational period, archive them with
access controls, and remove expired archives securely. Logs and automation
audit entries contain only IDs, outcome codes, and typed statuses; they must
never contain Hue keys, tokens, credentials, raw audio, or arbitrary bridge
payloads.

## Hue switch automation

Hue CLIP v2 `button` resources are mapped as `hue.switch` devices. DaveBrain
uses the authenticated CLIP v2 Server-Sent Events listener at
`/eventstream/clip/v2` as the normal source of switch changes. Each mapped,
enabled Hue hub has an independent long-lived listener using the same
bridge-ID hostname routing and pinned-certificate validation as other Hue
requests. At startup and after every stream reconnect, DaveBrain reconciles
the last observed button event and deliberately suppresses it before accepting
new events.

An ended or failed stream retries with exponential delays of 1, 2, 4, 8, 16,
then a maximum of 30 seconds. Only after three consecutive stream failures
does DaveBrain use bounded one-second polling, and only until the next stream
retry. The fallback also reconciles before processing changes and never runs
while the event stream is available. This recovery rule can miss a press while
the bridge is disconnected, but prevents a stale/replayed press from turning
devices on after reconnection.

Automation configuration is Admin-only. Actions are persisted as the sole
allow-listed `device.setState` operation, targeting a mapped device and its
advertised capabilities. Shell commands, scripts, URLs, and expressions are
not configuration options. An action set contains ordered action IDs; a
trigger contains ordered action-set IDs and optional cooldown seconds.
Completed actions are not retried when a later action fails. The bounded
in-memory audit shows trigger decisions and individual action outcomes.

State criteria is strict JSON:

```json
{
  "all": [
    { "field": "event", "equals": "initial_press" },
    { "field": "button", "equals": "1" }
  ]
}
```

Each `all` item has one supported `field` and scalar `equals` value. Supported
fields are `event`, `button`, `isOn`, `brightness`, `colour.x`, and
`colour.y`. Button events are deduplicated by mapped source and Hue event ID;
trigger cooldown is evaluated after criteria matching. The Automation page
previews the source, criteria fields, references, and effective targets before
operators rely on a switch press.

## Home data import and export

An authenticated `Admin` can use the **Import / export** page to download a
versioned JSON file containing locations, devices, command aliases, and
automation actions/action sets/triggers. The file deliberately does not
contain Hue application keys, protected-secret files, OAuth material, or
private keys.

Import is an ID-based merge: an imported location or device replaces the local
object with the same GUID, while local objects absent from the file remain.
The complete merged state is validated before it is saved, so imports with
duplicate IDs, invalid location references, duplicate names, or unsupported
schema versions are rejected without changing state. Hue device mappings
require their linked hub to already exist on the destination host.

Remote copies, schedules, off-host retention, and automated backup jobs are
explicitly deferred. A package restores configuration and identity data on the
same protected host/service identity. Restoring it elsewhere requires manual
certificate installation plus Hue and protected-secret recovery/re-enrollment.

Write structured Windows Service logs under `C:\ProgramData\DaveBot\logs`.
Use correlation IDs. Never log passwords, OAuth tokens, authorization codes,
device codes, Hue application keys, private key material, or raw audio.

## Future SQL migration

When a SQL provider is added, do not copy files directly into database tables.
Use the application migration procedure:

1. Put DaveBrain into a maintenance state that rejects configuration and
   identity writes.
2. Export and validate provider-neutral `HomeStateDocument` and
   `IdentityStateDocument`.
3. Import both documents through the SQL provider, including revision values.
4. Validate the resulting in-memory projections and identity client/grant
   records using the shared provider contract suites.
5. Preserve the JSON snapshots as rollback material, select the SQL provider
   in protected configuration, and restart the service.

Do not discard the source snapshots until login, device enrollment, home
configuration, and Hue mappings have been checked after migration.

## Support triage

1. Check `/health/live` for process status and `/health/ready` for state,
   identity, Foundry Local, and configured Hue-switch readiness. A
   transcription dependency failure or unavailable configured Hue switch
   returns HTTP 503; notify the operator with the dependency code/status, not
   any credential or payload.
2. Check Windows Service status and recent correlation IDs in the structured
   logs.
3. For browser/Ear TLS failures, verify DNS resolution and installed
   self-signed certificate trust before changing application settings.
4. For enrollment failures, verify the user's `Admin` role, active account,
   device-code expiry, client certificate, and Ear revocation state.
5. For automation failures, verify the Hue hub connectivity and mapped device
   state before changing trigger/action configuration.
