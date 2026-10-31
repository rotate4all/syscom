# Contributing

Thanks for helping. SYSCOM keeps a few rules that matter more than any single feature:

- **One hardware dependency.** Sensors come from LibreHardwareMonitorLib. Anything else should come from .NET
  itself or be written here.
- **Monitoring only.** Nothing that writes to hardware (fan curves, overclocking, RGB).
- **Light.** The dashboard stays framework-free with no build step. The server only polls hardware while
  someone is watching.

## Workflow

1. Open an issue for anything bigger than a small fix, so we can agree on the approach.
2. Branch from `main`, keep pull requests focused, and fill in the PR template.
3. CI must pass: Windows build with warnings as errors, JS syntax check, and the Linux smoke test.
4. Dashboard changes: include a screenshot (light and dark if styling changed). `--mock` gives you data on any OS.

See [docs/BUILDING.md](docs/BUILDING.md) for setup.

## Style

- C#: nullable enabled, file-scoped namespaces, small classes. Comments say *why*, not *what*.
- JS: ES modules, no dependencies, `const` by default, DOM built with `createElement` (escape anything that came
  from a server before using `innerHTML`).
- Follow `.editorconfig`.
