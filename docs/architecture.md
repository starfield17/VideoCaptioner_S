# Architecture

## Dependency direction

```text
Captioner.Core  <──  Captioner.Engine  <──  Captioner.Infrastructure
       ▲                    ▲                        ▲
       └────────────────────┴──────── Captioner.Cli / Captioner.Desktop
```

- **Core** contains immutable timelines, endpoint/model vocabulary, durable execution options, and manifests.
- **Engine** owns orchestration, fingerprints, concurrency gates, recovery, timing fallback, and validation.
- **Infrastructure** owns sherpa-onnx, OpenAI-compatible HTTP, model storage, FFmpeg processes, files, JSON configuration, and SRT syntax.
- **CLI/Desktop** compose dependencies and translate interaction into Engine calls; neither implements caption stages.

Architecture tests prevent Core or Engine from depending on Infrastructure or either frontend.

## Staged execution and recovery

```text
probe → chunks → transcribe → segment → correct → translate → export
```

Each stage stores a JSON artifact, SHA-256 checksum, semantic version, upstream artifact hash, and every setting that affects output. Reuse requires both checksum and fingerprint to match. Export additionally parses and validates the final SRT before reuse.

Options are persisted with each batch. A process killed during a stage leaves it pending while earlier committed stages remain reusable. Files fail independently. File, ASR, and LLM concurrency have separate gates. Job directories are batch-owned so a new run or cleanup cannot corrupt another batch.

## ASR and model boundary

`RoutingAsrClient` dispatches by endpoint backend:

- `SherpaOnnxAsrClient` loads an explicitly installed Whisper or Qwen3-ASR model and uses Silero VAD to produce anchored segments on CPU.
- `OpenAiAsrClient` calls an OpenAI-compatible transcription endpoint.

The model catalog pins model revision, expected files or archive, byte size, SHA-256, and license. `AsrModelManager` downloads only on an explicit pull action, resumes partial transfers, verifies size and hashes, extracts into a staging directory, and atomically marks a complete installation. Runtime model objects are cached per model/language and decoding is serialized per recognizer.

Audio is normalized to mono 16 kHz PCM16 WAV. Chunk duration starts from endpoint duration/byte limits and is reduced until each chunk fits. Chunk offsets use integer milliseconds.

## Timing and LLM contracts

ASR word timestamps are authoritative when available. Segment timestamps are converted into token anchors while preserving exact outer bounds. The LLM may return only existing anchor or cue IDs; it never invents timestamps. Cue timing is calculated from anchors and validated after every timing-producing stage.

Transcript and caption text is serialized as untrusted JSON in a separate user message. Responses must be strict JSON with exact IDs: added, removed, duplicated, renamed, or empty corrected/translated cues are rejected and retried once. Long inputs are windowed with fixed boundaries that prevent separate windows from being rejoined.

## Configuration and secrets

Configuration may contain a plaintext API key or the name of an environment variable. A direct key takes precedence. Plaintext storage is an explicit local-product tradeoff and the config receives owner-only Unix permissions when supported.

Keys are resolved only for HTTP calls. They are excluded from serialized endpoint profiles and therefore never enter batch manifests, stage artifacts, logs, diagnostics, or SRT output. `config show` returns a redacted projection. No request headers or request bodies are logged.

## Output and publication

SRT output is written to a same-directory temporary file, parsed and timeline-validated, flushed, and atomically moved into place. Existing output is rejected unless overwrite was explicit. Media is opened read-only.

The packaging tool publishes both frontends self-contained, adds pinned FFmpeg/FFprobe binaries, project and third-party notices, and creates platform ZIPs. CI validates all six target families; release automation additionally builds unsigned per-user MSI packages on Windows and unsigned DMGs on macOS. A strict semantic-version tag is the only path that publishes a GitHub release.
