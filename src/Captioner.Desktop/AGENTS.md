# Desktop boundaries

- Keep pipeline stages, retries, fingerprints, and subtitle rules in Engine.
- Keep FFmpeg, HTTP, configuration, workspace, and SRT behavior in Infrastructure.
- Code here may coordinate UI state, file pickers, cancellation, and progress polling only.
- Every long-running action must remain cancellable and must not block the UI thread.
