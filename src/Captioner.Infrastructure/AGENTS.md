# Captioner.Infrastructure

Owns adapters at the Engine ports: sherpa-onnx, OpenAI-compatible HTTP, pinned model storage, FFmpeg/FFprobe, the filesystem workspace, JSON configuration, and SRT import/export.

- Allowed dependencies: `Captioner.Core`, `Captioner.Engine`, BCL, sherpa-onnx, SharpCompress.
- Forbidden dependencies: CLI, Desktop, UI toolkits, copying anything from `reffer/`.
- Input media is opened read-only. Final SRT is parsed, timeline-validated, flushed, and atomically moved.
- API keys are resolved only for HTTP calls. They are never written to manifests, stage artifacts, logs, or SRT.
- Local models download only on an explicit pull. Runtime inference is CPU-first.
- FFmpeg arguments must keep `--` or explicit `-i` paths; never interpolate unsanitized strings into a shell.
- SRT syntax lives here. Engine consumes `SubtitleDocument` only.
