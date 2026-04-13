# Changelog

All notable changes to this project will be documented in this file.

This format is based on Keep a Changelog and follows Semantic Versioning principles.

## [Unreleased]

### Added
- Copilot instructions file with coding, changelog, and release standards.
- `CHANGELOG.md` with policy and baseline entry.

### Changed
- CI workflow: restricted push/PR triggers to code-relevant paths to avoid unnecessary runs.
- Publish workflow: replaced third-party `ncipollo/release-action` with native `gh release create --generate-notes`. Added `fetch-depth: 0` and NBGV setup so version is computed correctly from git history. Removed manual `Version` override.
- `version.json`: extended `publicReleaseRefSpec` to include tags (`refs/tags/v*`) so NBGV produces stable versions when building from a release tag.

### Fixed

### Deprecated

### Removed

### Security

## [0.5.2] - 2026-04-13

### Added
- Initial changelog baseline.
- Defined release-note categories for future entries.

## Changelog Policy

- Add entries under [Unreleased] as part of each pull request.
- Keep entries user-facing and concise.
- Group entries by Added, Changed, Fixed, Deprecated, Removed, and Security.
- Move [Unreleased] entries to a version section when creating a release tag.
- Use release tags in `vX.Y.Z` format to match publishing workflow conventions.
