# Phase 5 - DaveEar

## Objective

Deliver a small cross-platform DaveEar service that enrolls through DaveBrain,
continuously captures microphone audio, creates bounded speech segments using
the one-second silence boundary, and securely uploads them.

## Prerequisites

- Phase 4 speech-command endpoint is available.
- Phase 0 selected Windows and Linux audio-capture dependencies.

## Implementation work

### 1. Client identity and lifecycle

1. Implement `--register`, `--diagnose`, and `--run`.
2. During `--register`, discover DaveBrain's OIDC configuration, generate a
   private key and CSR, run the Device Authorization Grant, store returned Ear
   ID, issuer, client ID, location metadata, certificate chain, and approved
   grant.
3. Store the Ear private key, issued client certificate, and refresh token in
   an OS-protected location, never in readable JSON. Store non-secret client
   configuration separately.
4. Refresh access tokens over mutual TLS before expiry, present the client
   certificate for resource calls, and surface re-enrollment requirements after
   revocation, refresh failure, certificate/private-key loss, or Ear
   disablement.
5. Send authenticated heartbeats and display server-directed enabled state.

### 2. Audio pipeline

1. Define `IAudioCapture`, `IVoiceActivityDetector`,
   `IAudioSegmentEncoder`, and `ICommandUploader`.
2. Capture mono frames from a configured microphone backend.
3. Use a lightweight noise-floor/energy VAD with a small pre-roll buffer.
4. Begin a segment on sustained sound; finish after at least 1,000 ms of
   silence; force a configured maximum segment length.
5. Resample/encode only valid segments to the Phase 4 16 kHz, 16-bit, mono
   PCM WAV contract.
6. Use bounded channels/buffers and a bounded disk retry spool. Remove
   successful or expired segments and make discard events diagnosable.

### 3. Platform and service integration

1. Implement the selected WASAPI-compatible Windows capture backend and test
   it in the Windows Service account context.
2. Implement the selected ALSA/PulseAudio/PipeWire-compatible Linux backend.
3. Provide Windows Service installation/recovery guidance and a systemd unit
   with least privilege, restart behavior, configuration path, and audio-device
   permissions.
4. Make `--diagnose` validate configuration, microphone availability, HTTPS
   certificate, OAuth/mTLS auth, heartbeat, and a consented short test upload.
5. Expose local service status without audio content, tokens, private keys, or
   user credentials.

## Validation while implementing

- Unit-test the VAD state machine with deterministic PCM frames: silence,
  onset, pre-roll, one-second trailing gap, continuous sound, and max length.
- Test enrollment success, denial, expiry, client-certificate issuance, refresh
  rotation, certificate mismatch, certificate revocation, and grant revocation
  using DaveIdentity's test endpoints.
- Test retries, retry-spool quota, Brain restart, network loss, unplugged
  microphone, and malformed server responses.
- Run each backend on its target operating system as it is implemented and
  confirm the intended location is used for resulting commands.

## Exit checklist

- [ ] Windows and Linux Ears can enroll without manually copying a credential.
- [ ] The service continuously segments voice/sound with a one-second silence
      boundary and produces valid WAV uploads.
- [ ] Authentication failure stops uploads safely and requires re-enrollment.
- [ ] Queue, memory, and retry-spool growth are bounded.
- [ ] Service installation and diagnostics are documented for both platforms.
