# Third-party notices

Captioner is MIT licensed. Its distributed binaries include the following third-party components:

- **sherpa-onnx** (`org.k2fsa.sherpa.onnx`), Apache-2.0, copyright the k2-fsa contributors. Source: <https://github.com/k2-fsa/sherpa-onnx>.
- **SharpCompress**, MIT, copyright Adam Hathcock and contributors. Source: <https://github.com/adamhathcock/sharpcompress>.
- **Avalonia**, MIT, copyright the Avalonia contributors. Source: <https://github.com/AvaloniaUI/Avalonia>.
- **FFmpeg**, distributed as separate GPLv3 command-line programs inside release packages. Exact source revision, build recipe, license text, and binary provider are included under `third-party/ffmpeg/` in each package. Source: <https://ffmpeg.org/>.

Speech models are not bundled. `captioner models pull` downloads explicitly selected artifacts:

- Whisper ONNX exports and token files from the pinned `csukuangfj/sherpa-onnx-whisper-*` revisions on Hugging Face. The catalog records the license, revision, size, and SHA-256 for every file.
- Qwen3-ASR 0.6B int8 from the pinned sherpa-onnx model release. Qwen3-ASR is Apache-2.0.
- Silero VAD from the pinned sherpa-onnx model release. See <https://github.com/snakers4/silero-vad> for its license and source.

Full license texts for NuGet dependencies are available from their linked source distributions. This notice does not alter the terms of any third-party component.
