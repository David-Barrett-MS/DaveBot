# DaveBot Operations

## Phase 0 operating baseline

| Item | Standard |
|---|---|
| Host | Windows Server 2019 x64 or later |
| Service name | `DaveBrain` |
| Service identity | `NT SERVICE\DaveBrain` |
| Server URL and OIDC issuer | `https://brain.daves.tips` |
| HTTPS listener | TCP 443 on the private LAN |
| TLS | Self-signed `brain.daves.tips` certificate |
| State path | `C:\ProgramData\DaveBot` |
| Backup-package path | `C:\ProgramData\DaveBot\backups` |

No DaveBrain endpoint is intended for public internet access. Configure private
LAN DNS so `brain.daves.tips` resolves to the server's private address. Limit
the Windows Firewall inbound rule for TCP 443 to the relevant LAN subnets.

## Service account and directories

Install DaveBrain as a Windows Service running under `NT SERVICE\DaveBrain`.
Grant this identity:

- read access to the selected certificate private key;
- modify access to `C:\ProgramData\DaveBot` and its child directories;
- network access only where required for the Hue bridge and private LAN clients.

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
alternative name. Install its private key in the Windows Local Machine
Certificate Store and configure DaveBrain by certificate thumbprint. The
certificate should be non-exportable unless an encrypted recovery process has
been explicitly established.

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

Phase 6 implements an on-disk backup package in
`C:\ProgramData\DaveBot\backups`. Its initial contents are:

- `state.json` and `state.json.bak`;
- `identity.json` and `identity.json.bak`;
- a manifest containing creation time, document schema versions, checksums,
  software version, issuer URL, and restore instructions;
- a record of certificate thumbprint and DPAPI recovery requirements, without
  embedding private key material or protected secret plaintext.

  ## Home data import and export

  An authenticated `Admin` can use the **Import / export** page to download a
  versioned JSON file containing locations and devices. The file deliberately
  does not contain Hue application keys, protected-secret files, OAuth material,
  or private keys.

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
   identity signing-key, Foundry Local, and Hue readiness.
2. Check Windows Service status and recent correlation IDs in the structured
   logs.
3. For browser/Ear TLS failures, verify DNS resolution and installed
   self-signed certificate trust before changing application settings.
4. For enrollment failures, verify the user's `Admin` role, active account,
   device-code expiry, client certificate, and Ear revocation state.
5. For automation failures, verify the Hue hub connectivity and mapped device
   state before changing trigger/action configuration.
