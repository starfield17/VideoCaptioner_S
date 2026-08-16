# Captioner

Captioner is a clean-room C# batch media-to-SRT application. It transcribes with local sherpa-onnx models or an OpenAI-compatible ASR endpoint, then uses an OpenAI-compatible LLM for semantic cue splitting, correction, and optional translation. It only writes validated sidecar SRT files; source media is never modified.

The same resumable pipeline powers both frontends:

- `captioner`: automation-friendly CLI.
- `captioner-desktop`: all-English Avalonia queue with seven-stage progress.

## Included capabilities

- Local Whisper tiny/base/small/medium/large-v3 and English-only variants.
- Local Qwen3-ASR 0.6B int8.
- Explicit, resumable model downloads with pinned revisions, sizes, and SHA-256 checks.
- OpenAI-compatible remote ASR as an alternative backend.
- OpenAI-compatible LLM segmentation, correction, and translation; DeepSeek is the default profile.
- Durable per-stage artifacts and batch resume.
- Self-contained Windows, macOS, and Linux packages with FFmpeg and FFprobe included.

Models are not silently downloaded. Install one explicitly before the first local run.

## Build and test

The source build requires the .NET 10 SDK and FFmpeg/FFprobe on `PATH`:

```bash
dotnet build Captioner.slnx
dotnet test Captioner.slnx
dotnet format Captioner.slnx --verify-no-changes --no-restore
```

Published packages are self-contained and include FFmpeg/FFprobe.

## Configure

Create and inspect the default configuration:

```bash
captioner config init
captioner config path
captioner config show
```

The default ASR is local `whisper-small`. Download it explicitly:

```bash
captioner models list
captioner models pull whisper-small
captioner models path
```

Configure the LLM from the CLI without placing the key in shell history:

```bash
printf '%s\n' "$DEEPSEEK_API_KEY" | captioner config set-llm \
  --base-url https://api.deepseek.com \
  --model deepseek-v4-flash \
  --api-key-stdin
```

The desktop Settings panel can save the same values. API keys are stored as plaintext JSON because this application is designed for local single-user configuration. The config file is created with owner-only permissions on Unix when supported. Treat it like a password: do not commit, share, or attach it to bug reports. `config show`, logs, job manifests, stage artifacts, and SRT files never expose the key.

For automation, a configured `apiKeyEnvironmentVariable` remains a supported fallback. A directly configured `apiKey` takes precedence. Named profiles under `profiles` may be selected with `--asr-profile` or `--llm-profile`.

Run diagnostics before processing media:

```bash
captioner doctor
captioner doctor --check-api
```

## CLI

Process one file, several files, or directory trees:

```bash
captioner run ./videos --output ./subtitles
captioner run a.mp4 b.mkv --output ./subtitles --target-language zh-CN
captioner run ./course --output ./subtitles --target-language en --layout target --jobs 3
```

Without a target language, output defaults to source-only. With a target language, it defaults to bilingual. Output directories mirror input subdirectories; names look like `lesson.captioned.srt` and `lesson.captioned.zh-CN.bilingual.srt`.

`run` persists a batch ID before inference begins. Resume keeps the original content settings:

```bash
captioner status
captioner status 20260816143000-1a2b3c4d
captioner resume 20260816143000-1a2b3c4d
captioner clean 20260816143000-1a2b3c4d
```

Useful switches:

- `--no-segment`: deterministic length/duration splitting without the LLM.
- `--no-correct`: skip LLM correction.
- `--max-cue-characters` and `--max-cue-duration-ms`: deterministic safety limits.
- `--json`: machine-readable result output.
- `--workspace`: override durable batch and artifact storage.
- `--config`: override the configuration file.

Exit codes are `0` success, `1` runtime error, `2` invalid usage/input, `3` missing dependency/configuration, `4` partial batch failure, and `130` cancellation.

## Desktop

```bash
dotnet run --project src/Captioner.Desktop
```

Choose media and an output directory, configure local or remote ASR and the LLM, then start the queue. Each job displays probe, chunk, transcribe, segment, correct, translate, and export state. Stop cancels current work without deleting committed artifacts; a saved batch ID can be resumed.

## Packages and releases

CI builds six self-contained targets: Windows x64/ARM64, macOS x64/ARM64, and Linux x64/ARM64. Windows additionally receives MSI files, macOS receives DMG files, and every platform receives a ZIP. MSI and DMG artifacts are intentionally unsigned until signing credentials are supplied.

Build one package locally after preparing its pinned FFmpeg distribution:

```bash
dotnet run --project tools/Captioner.Packaging -- prepare-ffmpeg linux-x86_64
dotnet run --project tools/Captioner.Packaging -- package linux-x64 1.0.0
dotnet run --project tools/Captioner.Packaging -- checksums artifacts/release
```

A tag matching `vMAJOR.MINOR.PATCH` publishes a GitHub release. Manual workflow runs only create and validate artifacts; they do not publish a release.

See [docs/architecture.md](docs/architecture.md) for module boundaries, recovery, model, timing, security, and publication rules.
