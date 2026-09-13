# Phase 4 - Speech Transcription

## Objective

Add a bounded, local speech-command path that accepts valid WAV audio from an
authenticated DaveEar, transcribes it with Foundry Local Whisper Tiny, and
hands the result to the Phase 3 command processor.

Whisper Tiny is the sole model use in DaveBot and is limited to transcription;
the command processor and its responses remain custom deterministic code.

## Prerequisites

- Phase 3 text commands are fully functional and tested.
- Foundry Local with Whisper Tiny is installed/configured on the Windows
  Server host.

## Implementation work

### 1. Transcription adapter

1. Define `ITranscriptionService` in DaveBrain with a Foundry Local HTTP
   implementation and deterministic fake test implementation.
2. Keep the configured endpoint, model name, timeout, request format, and
   response mapping in this adapter only.
3. Explicitly select Whisper Tiny; never fall back to a cloud service or a
   different local model without an explicit configuration change.
4. Make readiness fail with a clear dependency result when the configured
   endpoint/model is unavailable.

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
5. Delete temporary audio after transcription. Diagnostic retention is
   opt-in, quota-limited, ACL-protected, and disabled by default.

### 3. Command result

1. Pass successful transcript text directly to `CommandProcessor`.
2. Return one correlated response covering transcription, parsed command,
   resolved targets, and action result.
3. Record safe metadata and outcome in the bounded audit store without raw
   audio or OAuth material.
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

- [x] A valid Ear WAV can invoke a real local transcription and device action.
- [x] Invalid/oversized requests do not consume transcription capacity.
- [x] An unavailable Foundry endpoint is visible through readiness and a safe
      command result.
- [x] Temporary audio is never persisted; bounded in-memory audio becomes
      collectible when its request completes or is cancelled.

## Implemented configuration and API

`DaveBot:SpeechTranscription` configures the local OpenAI-compatible Foundry
endpoint, timeout, WAV upload/duration limits, and queue bounds. The endpoint
must be HTTP(S) on `localhost` or a loopback IP and the only accepted model
value is `whisper-tiny`; invalid values fail startup. Defaults are a 1 MB
upload, 30-second WAV, 20-second Foundry timeout, and one worker with two
queued requests.

`POST /api/v1/speech-commands` accepts `multipart/form-data` with `audio`
(mono 16 kHz 16-bit PCM RIFF/WAVE) and `earId`. It applies the same issuer,
subject, client ID, scope, Ear ID, and mTLS certificate binding as the text
command route. The response has a correlation ID and distinguishes malformed
audio, queue overload (HTTP 429 with `Retry-After`), no speech, dependency
unavailability, timeout, transcription failure, and deterministic command
rejection. Raw audio and OAuth material are neither persisted nor returned.
