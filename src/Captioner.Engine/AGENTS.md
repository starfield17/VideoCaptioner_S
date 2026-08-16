# Captioner.Engine

Owns the application pipeline, stage dependency fingerprints, batch concurrency, cancellation, and crash recovery.

- Allowed dependencies: `Captioner.Core` and the .NET BCL.
- Forbidden dependencies: Infrastructure, CLI, provider SDKs, FFmpeg details, filesystem implementation, UI.
- Ports describe real external boundaries only: media tooling, ASR, LLM, workspace, and final publication.
- Stage order and invalidation fingerprints are compatibility contracts. Change their versions deliberately.
- A ready stage is reusable only after artifact checksum verification.
- Per-file stages are sequential; files may run concurrently through shared ASR/LLM gates.
