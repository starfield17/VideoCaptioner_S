# Captioner product specification

Intent: deliver a maintainable open-source C# batch captioning product with one shared pipeline, a dependable CLI, and an approachable desktop interface.

Done means:

1. Files and directory trees become valid sidecar SRT files without modifying input media.
2. Local sherpa-onnx Whisper/Qwen3-ASR and optional OpenAI-compatible remote ASR are supported.
3. An OpenAI-compatible LLM performs strict-contract segmentation, correction, and optional translation.
4. Interrupted batches resume from verified artifacts and one failed file does not stop unrelated files.
5. English desktop and CLI workflows can configure, diagnose, download models, run, stop, and resume jobs.
6. Six self-contained platform packages include verified FFmpeg/FFprobe binaries and licensing notices.

## Do not build

- Do not burn, mux, render, or synthesize subtitles into video.
- Do not add TTS, media downloading, or online asset management.
- Do not accept existing subtitle files as input in v1; media goes in and SRT comes out.
- Do not add a database, distributed workers, multi-process leases, or a plugin framework.
- Do not implement automatic background model downloads or GPU lifecycle management.
- Do not copy code, prompts, tests, or assets from the GPL reference repository.
- Do not log or persist API keys in manifests, stage artifacts, reports, or subtitles.

## Found · Not doing

- Hardware acceleration is deferred; local inference is CPU-first.
- Package signing and notarization are extension points pending owner credentials.
- Provider-native LLM protocols are out of scope; providers use OpenAI-compatible chat completions.
