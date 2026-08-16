# Captioner.Core

Owns immutable timeline, cue, endpoint-capability, and job-state vocabulary plus pure validation.

- Allowed dependencies: .NET BCL only.
- Forbidden dependencies: Engine, Infrastructure, CLI, UI, filesystem, HTTP, process execution.
- Public APIs are records/enums and pure services. Do not place orchestration here.
- All timestamps are integer milliseconds; no type may silently clamp invalid timing.
