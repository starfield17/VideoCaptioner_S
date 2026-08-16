# Captioner.Packaging

Owns verified FFmpeg acquisition and cross-platform release staging.

- Allowed dependencies: BCL and SharpCompress. Do not reference Core, Engine, Infrastructure, CLI, or Desktop.
- Forbidden responsibilities: caption pipeline decisions, HTTP inference, configuration, SRT writing.
- FFmpeg/FFprobe come from the pinned `packaging/ffmpeg/manifest.json` checksums only.
- Stage both frontends self-contained, copy notices, and emit ZIP/MSI/DMG artifacts. Do not sign unless credentials exist.
- `prepare-ffmpeg` and `package` must stay deterministic for a given target and version.
