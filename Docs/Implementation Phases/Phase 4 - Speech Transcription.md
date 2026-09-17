# Phase 4 - Speech Transcription

## Objective

Add a bounded, local speech-command path that accepts valid WAV audio from an
authenticated DaveEar, transcribes it with Foundry Local Nemotron Speech, and
hands the result to the Phase 3 command processor.

Nemotron Speech is the sole model use in DaveBot and is limited to transcription;
the command processor and its responses remain custom deterministic code.

## Prerequisites

- Phase 3 text commands are fully functional and tested.
- DaveBrain is published for the Windows Server architecture, so its
  `Microsoft.AI.Foundry.Local` package runtime is included in the deployment.

## Implementation work

### 1. Transcription adapter

1. Define `ITranscriptionService` in DaveBrain with a packaged Foundry Local
   native SDK implementation and deterministic fake test implementation.
2. Initialize Foundry Local in process, download/load only
   `nemotron-speech-streaming-en-0.6b`, and
   pass the validated WAV's raw PCM payload to the SDK's live-transcription
   session in 100 ms chunks, accepting only final results from its in-memory
   stream.
3. Explicitly select Nemotron Speech; never fall back to a cloud service or a
   different local model without an explicit configuration change.
4. Make readiness fail with a clear dependency result when the packaged SDK or
   Nemotron Speech model is unavailable.

### 2. Audio intake and queue

1. Implement `POST /api/v1/speech-commands` with the existing OAuth/mTLS Ear
   authorization policy.
2. Accept only mono, 16 kHz, 16-bit PCM WAV in the first release. Validate
   RIFF/WAVE structure, sample format, duration, channel count, sample rate,
   and upload-size limit before queuing.
3. Stream or buffer only within the configured maximum. Reject malformed,
   oversized, or overlong audio before it reaches Foundry Local.
4. Use one bounded transcription worker and a small bounded queue. Return a
   correlated retry/overload response rather than retaining unbounded requests.
5. Keep audio memory-only through transcription. Do not write uploads to
   temporary or persistent storage; diagnostic retention remains disabled.

### 3. Command result

1. Pass successful transcript text directly to `CommandProcessor`.
2. Return one correlated response covering transcription, parsed command,
   resolved targets, and action result.
3. Record a completed transcript in the administrator-only DaveEar Debug UI
   activity log, with safe metadata and outcome but without raw audio or OAuth
   material.
4. Distinguish no speech/transcription failure from a successfully transcribed
   command that cannot be resolved.

## Validation while implementing

- Unit-test WAV header and limit checks against valid, truncated, malformed,
  wrong-channel, wrong-rate, and overlong fixtures.
- Integration-test the endpoint with a fake transcription service for success,
  no text, timeout, unavailable dependency, cancellation, and saturated queue.
- Test the Foundry adapter with short representative WAVs as it is completed,
  then execute the resulting text through the same Phase 3 command tests.
- Confirm unauthenticated, missing/invalid-certificate, wrong-Ear, and revoked
  requests never enqueue audio.

## Exit checklist

- [ ] A valid Ear WAV can invoke a real local transcription and device action.
- [x] Invalid/oversized requests do not consume transcription capacity.
- [x] An unavailable Foundry SDK/model is visible through readiness and a safe
      command result.
- [x] Temporary audio is never persisted; bounded in-memory audio becomes
      collectible when its request completes or is cancelled.

## Implemented configuration and API

`DaveBot:SpeechTranscription` configures the model, timeout, WAV
upload/duration limits, and queue bounds. The only accepted model value is
`nemotron-speech-streaming-en-0.6b`; invalid values fail startup. DaveBrain bundles the Foundry
Local SDK through NuGet, so it does not require a Foundry CLI, loopback HTTP
endpoint, or separate daemon. On its first start, the SDK downloads the model
to the DaveBrain service account's model cache. Defaults are a 1 MB upload,
30-second WAV, 60-second Foundry timeout, and one worker with two queued
requests.

`POST /api/v1/speech-commands` accepts `multipart/form-data` with `audio`
(mono 16 kHz 16-bit PCM RIFF/WAVE) and `earId`. It applies the same issuer,
subject, client ID, scope, Ear ID, and mTLS certificate binding as the text
command route. The response has a correlation ID and distinguishes malformed
audio, queue overload (HTTP 429 with `Retry-After`), no speech, dependency
unavailability, timeout, transcription failure, and deterministic command
rejection. Raw audio and OAuth material are neither persisted nor returned.
