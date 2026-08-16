# Captioner.Cli

Owns command parsing, console rendering, exit codes, cancellation wiring, and the dependency-composition root.

- Allowed dependencies: Core, Engine, Infrastructure, Generic Host, System.CommandLine.
- Forbidden responsibilities: pipeline decisions, HTTP payload parsing, FFmpeg commands, manifest mutation, subtitle serialization.
- Commands call Engine application APIs or read through workspace/configuration adapters.
- `--json` output is stable machine-readable output; diagnostics go to stderr.
- Ctrl+C must propagate cancellation and must not convert cancellation into a successful exit.
