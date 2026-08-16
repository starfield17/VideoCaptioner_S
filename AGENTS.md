# Repository map

- `src/Captioner.Core`: immutable caption/timeline/job vocabulary and pure invariants. BCL only.
- `src/Captioner.Engine`: application pipeline, stage fingerprints, batch/recovery semantics, and external ports.
- `src/Captioner.Infrastructure`: sherpa-onnx/model storage, OpenAI-compatible HTTP, FFmpeg, filesystem workspace, configuration, and SRT adapters.
- `src/Captioner.Cli`: command surface and dependency composition only.
- `src/Captioner.Desktop`: Avalonia presentation and dependency composition only.
- `tests/Captioner.Tests`: unit, contract, architecture, and acceptance tests.
- `tests/Captioner.Desktop.Tests`: headless Avalonia startup and presentation regression tests.
- `tools/Captioner.Packaging`: verified FFmpeg acquisition and cross-platform release staging.
- `packaging`: pinned third-party manifests and installer definitions.
- `reffer/VideoCaptioner/`: read-only behavioral reference; never reference it from the C# build or copy its implementation/assets/prompts.

## Global invariants

- Input media is never modified.
- Final output is SRT only and is atomically published after validation.
- Time uses integer milliseconds and must pass timeline validation at every timing-producing boundary.
- Resume reuses an artifact only when its checksum and full stage fingerprint match.
- Core and Engine never depend on Infrastructure, CLI, or Desktop.
- Do not create `Common`, `Utils`, `Helpers`, or generic Manager/Provider dumping grounds.

## Canonical commands

```bash
dotnet build Captioner.slnx
dotnet test Captioner.slnx
dotnet format Captioner.slnx --verify-no-changes
```

Local module rules live in the nearest `AGENTS.md`.
