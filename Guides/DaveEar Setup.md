# DaveEar setup and operation

DaveEar captures microphone audio, detects speech locally, and sends speech segments to DaveBrain for transcription and command processing. It does not run a chat/transcription model itself or play spoken responses. This guide describes the current `src/DaveEar` implementation.

## Supported platforms

| Platform | Capture | Credential storage | Supported execution context |
| --- | --- | --- | --- |
| Windows | WASAPI through NAudio | Machine-bound DPAPI and restricted file ACLs | Signed-in user's interactive session; optionally an at-logon scheduled task |
| Linux | ALSA through `arecord`; PipeWire via ALSA compatibility | `secret-tool` and an unlocked Secret Service provider | User session with audio and credential-service access |

macOS is **not supported**: the capture factory rejects platforms other than Windows and Linux. There are no implemented Android, iOS, or browser DaveEar clients. Windows Service Session 0 microphone capture is explicitly unsupported.

Examples below target Windows x64 and Linux x64. Linux ARM64 devices, such as a 64-bit Raspberry Pi OS installation, can use `linux-arm64` instead, provided the OS supports .NET 10 and satisfies the audio/keyring requirements. The project does not declare a tested architecture/OS-version matrix; publishing successfully alone does not establish hardware compatibility.

## 1. Prepare DaveBrain

Before enrolling, have these ready:

1. A running DaveBrain server. The current DaveBrain application requires Windows Server, even when DaveEar runs on Linux.
2. The server's **HTTPS issuer**, matching `DaveIdentity:Issuer`. The configured default is `https://brain.daves.tips`; replace this with your actual deployment address in the examples. Do not use `localhost` from a different computer.
3. A trusted server certificate valid for that hostname. DaveEar uses normal TLS validation and has no insecure/skip-validation switch. For an internal CA, install the verified CA certificate into the Ear computer's trusted certificate store. Trusting a development certificate on the server does not trust it on another computer.
4. Network access to the issuer's HTTPS port (usually **443**) and the separate Ear **mTLS port 8443** on the same host. Configure the DaveBrain listener, certificates, and firewall first. Registration performs a certificate-presented token refresh on 8443 before saving state. A proxy that drops client certificates is not a substitute for the mTLS listener.
5. An active DaveBrain **Admin** account to approve device enrollment in a browser.
6. A configured DaveBrain Location and its GUID. Create it on the **Locations** page (`/Locations`). Obtain its ID from the administrator; in the current UI an administrator can inspect the location's Rename/Delete form hidden `id` field. Use this GUID, not a Hue room name or an Ear ID.
7. A working DaveBrain transcription endpoint and configured device/command processing for end-to-end voice commands. `DaveBot:SpeechTranscription` controls the server-side transcription endpoint/model. Foundry Local is not required on the Ear computer.

The default DaveBrain launch profile uses HTTP on localhost and is **not** a complete remote Ear deployment. See [DaveBrain startup](../src/DaveBrain/Program.cs) and [server settings](../src/DaveBrain/appsettings.json).

> Browser login on 443 is not enough: enrollment also needs 8443. The Ear CLI currently has no registration override for the mTLS port. It creates an endpoint using the issuer hostname and port 8443. After enrollment, `mtlsEndpoint` in the Ear configuration can be changed to another HTTPS port on that same host, distinct from the issuer port.

## 2. Build or publish

Install the **.NET 10 SDK** on the build computer. Run the examples from the repository root. You can use `dotnet run` from source or publish a self-contained app and copy its entire output directory to the target computer.

Self-contained publishing removes the target's separate .NET runtime requirement, but not OS audio, certificate trust, or Linux Secret Service dependencies. Framework-dependent deployment requires the matching .NET 10 runtime instead.

Enroll every Ear installation independently. Do not copy another Ear's credentials or enrollment state.

## 3. Windows

### Microphone prerequisites

- Connect the microphone and test recording in Windows Sound settings.
- Enable microphone access, including access for desktop applications, in Windows privacy settings.
- Register and run using the **same signed-in user**.
- Start with the default recording device, or use its **exact, case-sensitive friendly name** with `--microphone`. This selects a capture endpoint, not a speaker/loopback device.

### Publish and register (PowerShell)

```powershell
 dotnet publish .\src\DaveEar\DaveEar.csproj -c Release -r win-x64 --self-contained true -o .\artifacts\DaveEar\win-x64
 $ear = (Resolve-Path .\artifacts\DaveEar\win-x64\DaveEar.exe).Path
 $state = Join-Path $env:LOCALAPPDATA "DaveBot\DaveEar"
 $issuer = "https://brain.daves.tips"
 $location = "REPLACE-WITH-YOUR-LOCATION-GUID"
 & $ear --register --issuer $issuer --name "Lounge Windows Ear" --location $location --state-dir $state
```

Replace the issuer and Location GUID before running registration. For a particular input device, add `--microphone "EXACT MICROPHONE FRIENDLY NAME"` to the registration command.

The console prints a verification URL and user code. Open the printed URL, sign in as a DaveBrain administrator, and approve the device. The browser may be on another trusted computer. Keep registration running while approving. Success ends with `DaveEar registered with Ear ID ... Run with --run.`

### Diagnose, run, and stop

```powershell
 & $ear --diagnose --state-dir $state
 & $ear --run --state-dir $state
```

Stop with **Ctrl+C**. In another PowerShell window, define the same `$ear` and `$state` variables and inspect status:

```powershell
 & $ear --status --state-dir $state
```

Source-run alternative:

```powershell
 dotnet run --project .\src\DaveEar\DaveEar.csproj -- --run --state-dir $state
```

Substitute `--register` and its required arguments, `--diagnose`, or `--status` for `--run` when needed. The `--` separator passes arguments to DaveEar rather than to `dotnet`.

### Optional at-logon startup

Once foreground operation works, create a Task Scheduler task for the enrolled user:

- Trigger: **At log on**.
- Select **Run only when user is logged on**.
- Program: the absolute path to the published `DaveEar.exe`.
- Arguments: `--run --state-dir "C:\Users\YOUR-USER\AppData\Local\DaveBot\DaveEar"`, using your real path.
- Do not start another instance if the task is already running.

Do not install the executable as a Windows Service or run it as SYSTEM for microphone access. Session 0 capture is explicitly rejected. Foreground Ctrl+C is the implemented graceful shutdown mechanism; forcibly ending a task is not equivalent.

## 4. Linux (ALSA or PipeWire)

### Dependencies

On Debian/Ubuntu-family distributions:

```bash
sudo apt update
sudo apt install alsa-utils libsecret-tools gnome-keyring ca-certificates
```

Use equivalent packages on other distributions. Install the .NET 10 SDK separately if building/running from source on this computer.

- `alsa-utils` provides **`arecord`**.
- `libsecret-tools` provides **`secret-tool`**.
- A Secret Service provider such as GNOME Keyring must be **running and unlocked**, reachable through the user's D-Bus session. Installing packages alone is insufficient; a normal desktop login typically supplies this environment.
- PipeWire needs its ALSA compatibility plugin configured so a capture device is exposed to `arecord`. DaveEar does not invoke `pw-record` directly.

Use the same ordinary user for registration and capture. Do not run DaveEar with `sudo`: it changes the account, credential-service access, and often state/audio access.

### Select and test a microphone

```bash
arecord -l
arecord -L
arecord -D default -f S16_LE -c 1 -r 16000 -d 3 /tmp/daveear-mic-test.wav
aplay /tmp/daveear-mic-test.wav
rm /tmp/daveear-mic-test.wav
```

This records real microphone audio; delete it after testing. Replace `default` with a working ALSA name listed on your computer, such as a configured PipeWire device or `plughw:CARD=YourCard,DEV=0`. `plughw` can provide format conversion for hardware that cannot directly record 16 kHz mono. Supply the tested name through `--microphone` at registration.

DaveEar launches `arecord` with raw **S16_LE, mono, 16,000 Hz** capture. Check permissions and exclusive device use if capture fails. The Linux built-in diagnostic only runs `arecord -l`; it does not verify the selected `-D` device can capture the required format, so perform the recording test too.

### Publish and register (Bash)

```bash
dotnet publish ./src/DaveEar/DaveEar.csproj -c Release -r linux-x64 --self-contained true -o ./artifacts/DaveEar/linux-x64
ear="$PWD/artifacts/DaveEar/linux-x64/DaveEar"
state="${XDG_STATE_HOME:-$HOME/.local/state}/daveear"
issuer="https://brain.daves.tips"
location="REPLACE-WITH-YOUR-LOCATION-GUID"
"$ear" --register --issuer "$issuer" --name "Lounge Linux Ear" --location "$location" --microphone "default" --state-dir "$state"
```

Replace the issuer, GUID, and microphone as appropriate. For a supported Linux ARM64 installation, substitute `linux-arm64` in the publish command and executable path. Confirm that the device uses a 64-bit OS. When copying published files from another machine, preserve executable permissions or use `chmod +x /path/to/DaveEar`.

Follow the printed verification URL/code and approve as an administrator. The browser can be on another computer, but the Ear computer must still have its own unlocked Secret Service for saving credentials.

### Diagnose and run

```bash
"$ear" --diagnose --state-dir "$state"
"$ear" --run --state-dir "$state"
```

Stop with **Ctrl+C**. From another terminal, define the same variables and run:

```bash
"$ear" --status --state-dir "$state"
```

Source-run alternative:

```bash
dotnet run --project ./src/DaveEar/DaveEar.csproj -- --run --state-dir "$state"
```

### Background/headless operation

Establish foreground operation first. Desktop autostart or a user-level service must run as the enrolled user, with access to both audio devices/session and the unlocked Secret Service. Use an absolute executable path, explicit `--state-dir`, and one instance per state directory.

Headless Linux is **conditional**, not a turnkey deployment: an SSH shell/system service may lack both a D-Bus user session and an unlocked keyring. Arrange these securely before registering/running. Do not bypass keyring errors by putting refresh tokens or private keys in plaintext configuration. DaveEar has no built-in service installation command.

## 5. Verify end-to-end operation

1. Run `--diagnose` after enrollment and microphone/configuration changes.
2. Look for `configuration=valid`, `microphone=wasapi-ready` or `alsa-ready`, `certificate=valid`, `heartbeat=enabled`, and inspect `test-upload=...`.
3. The diagnostic sends a **silent one-second WAV** through the protected upload endpoint. A no-speech rejection is expected and is not proof of spoken-command recognition. Inspect the codes even if the process exits successfully.
4. Start `--run`, speak a short command, and pause for at least one second. Choose a harmless test or be prepared for the requested device action to execute.
5. Check DaveBrain's audit/command output and **Ears** page (`/Ears`) for last-seen/status. `--status` reads a local saved snapshot, not a live health probe; its information may outlast the process.

DaveEar continuously listens while running. Its energy-based detector starts a speech segment after sustained sound and ends it at a silence boundary. It sends bounded WAV segments, not an uninterrupted live audio stream. Heartbeats are scheduled approximately once a minute, though upload work can delay them. An accepted upload alone does not prove a device action succeeded.

## 6. Configuration and protected state

Always use the **same `--state-dir`** for every operation. If omitted, defaults are:

| Platform | Default directory |
| --- | --- |
| Windows | `%ProgramData%\DaveBot\DaveEar` |
| Linux | `$XDG_STATE_HOME/daveear`, or `~/.local/state/daveear` if unset |

The Windows examples deliberately use per-user LocalAppData. DaveEar restricts directory/file permissions to the current Windows identity or private Unix permissions. Do not use a general-purpose shared directory as its state directory.

Enrollment writes `config.json` with camelCase properties. **Stop DaveEar before editing**; configuration is loaded at startup. `--microphone` applies at registration, not as a `--run` override. Change `microphoneDevice` in saved configuration to select another input afterwards.

| Setting | Default | Purpose |
| --- | --- | --- |
| `issuer` | Registration argument | Trusted OIDC issuer |
| `mtlsEndpoint` | Same host, HTTPS port 8443 | Certificate-authenticated token refresh, heartbeat, upload |
| `clientId` | `DaveEar` | OAuth client; normally leave unchanged |
| `earId`, `locationId`, `name` | Enrollment values | Device identity/location; do not fabricate IDs |
| `microphoneDevice` | `null` if omitted | Windows friendly name or Linux ALSA device |
| `energyThreshold` | `650` | Higher thresholds reject more quiet input |
| `onsetMilliseconds` | `100` | Sound duration to start a segment |
| `preRollMilliseconds` | `300` | Audio retained before speech onset |
| `silenceBoundaryMilliseconds` | `1000` | Silence ending a segment; minimum 1000 |
| `maximumSegmentMilliseconds` | `30000` | Segment limit; keep within DaveBrain's limits |
| `queueCapacity` | `4` | Pending memory segments; newest dropped when full |
| `maximumSpoolBytes` | `20971520` | Retry/held audio quota (20 MiB) |
| `maximumSpoolAgeHours` | `24` | Maximum retained spool age |

Windows credentials live in DPAPI-protected `credentials.bin`. Linux credentials live in Secret Service under an identifier derived from the absolute state-directory path: changing the path changes which credential item is used. Do not commit credentials, state, or spool audio to source control. Spool files hold sensitive recordings protected by file permissions, not a plaintext credential fallback.

Server 408/429/5xx responses can be retried within spool limits. Network failures or upload timeouts can leave the outcome unknown; those segments are held and **not automatically resent**, avoiding duplicate commands. Authentication failure stops uploads. Microphone failures trigger a retry after five seconds without retaining audio during the outage.

## 7. Troubleshooting and re-enrollment

| Symptom | Action |
| --- | --- |
| Not registered | Register once; check the account and state directory on subsequent invocations. |
| Already registered | Do not enroll over the active identity; revoke the old Ear first when replacing it. |
| HTTPS/issuer mismatch | Fix hostname, DNS, issuer, trust chain, and system time; do not disable TLS validation. |
| Registration fails after browser approval | Check the 8443 mTLS listener/firewall/certificates. Registration is incomplete until local credentials and configuration are saved. |
| `access_denied` / `expired_token` | Start registration again and approve the new code promptly with an active Admin account. |
| `arecord-not-installed` | Install `alsa-utils`; make `arecord` available on PATH. |
| `microphone-not-found` / `microphone-recovering` | Check privacy/device permissions, exact device name, capture format, exclusive access, and audio session. |
| `windows-service-session-audio-unavailable` | Use an interactive session or at-logon task, not a Windows Service. |
| Secret Service unavailable / credentials missing | Check `secret-tool`, provider unlock, user D-Bus session, account, and state path; do not use sudo as a workaround. |
| `server-400`, `server-413`, rejected speech | Inspect server validation/transcription logs and WAV limits. Silent diagnostic audio may legitimately be rejected. |
| `upload-timeout` / `network-outcome-unknown` | Check DaveBrain audit results before manually repeating a command that might already have run. |
| `authentication-rejected`, `re-enrollment-required`, expired certificate | Check revocation/account/server identity state and re-enroll as necessary. |

Ear certificates currently last **90 days**. DaveEar has no certificate-renewal CLI command. To replace/re-enroll an Ear: stop it, revoke its record on DaveBrain's **Ears** page, and register using a new private state directory. Securely remove old protected state/keyring entries and audio when no longer required. Do not delete credentials from an active Ear as a routine repair.

## CLI reference

| Option | Meaning |
| --- | --- |
| `--register` | Enroll; requires issuer, name, location |
| `--issuer <https-url>` | Issuer used at registration |
| `--name <name>` | Display name used at registration |
| `--location <guid>` | DaveBrain Location ID used at registration |
| `--microphone <device>` | Optional input device saved at registration |
| `--run` | Capture/upload using saved settings; also the default operation |
| `--diagnose` | Check configuration, credentials, device enumeration, heartbeat, silent upload |
| `--status` | Print enrollment identifiers and saved status |
| `--state-dir <directory>` | Override protected state directory for any operation |

Specify only one operation per invocation. There is no implemented `--help`, `--unregister`, or `--install-service` operation.

## Implementation references

- [CLI and enrollment](../src/DaveEar/Program.cs)
- [Configuration defaults/validation](../src/DaveEar/Configuration.cs)
- [Capture backends](../src/DaveEar/AudioCapture.cs)
- [Protected credential storage](../src/DaveEar/SecureStorage.cs)
- [Capture/upload lifecycle](../src/DaveEar/DaveEarService.cs)
- [Transport and retry spool](../src/DaveEar/Upload.cs)
