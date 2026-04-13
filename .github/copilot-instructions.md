# Copilot Instructions for ReverseTunnel.Yarp

## Scope
These instructions apply to the entire repository.

## Architecture and Code Style
- Prefer small, focused changes over broad refactors.
- Keep public APIs stable unless explicitly requested.
- Follow existing naming conventions and folder structure.
- Keep nullable reference types and async best practices in mind.
- Avoid adding dependencies unless there is a clear benefit.

## Versioning and Release
- Versioning is managed with Nerdbank.GitVersioning via `version.json`.
- Use `nbgv prepare-release` to cut a release branch and move `main` to the next development version.
- Stable publishing is tag-driven via tags matching `v*`.

## CI and Workflows
- CI is intentionally scoped to code-relevant paths.
- Keep workflow changes minimal and explain why trigger logic changes.
- Preserve `fetch-depth: 0` in CI unless there is a strong reason to change it because NBGV relies on git history.

## Testing and Validation
- For code changes, run restore, build, and tests when feasible.
- If tests are not run, explicitly state that in the final summary.
- Do not silently ignore failing tests introduced by your changes.

## Changelog
- Every code change that affects behavior, APIs, or configuration must include an entry under `[Unreleased]` in `CHANGELOG.md`.
- Use the categories: Added, Changed, Fixed, Deprecated, Removed, Security.
- Entries must be user-facing and concise — not internal refactor notes.
- When creating a release tag, move `[Unreleased]` entries to a versioned section matching the tag (e.g., `[0.5.2] - 2026-04-13`).
- Do not leave `[Unreleased]` empty after a code change.

## Documentation
- Update docs when behavior, release flow, or configuration changes.
- Keep release and publishing guidance aligned with workflow files.

## Security
- Never commit secrets or API keys.
- Assume all credentials are provided through GitHub secrets.
