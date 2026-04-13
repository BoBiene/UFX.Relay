# Publishing and Release Process

This repository uses GitHub Actions and Nerdbank.GitVersioning (NBGV) for both preview and stable package publishing.

## Prerequisites

1. Repository secret `NUGET_APIKEY` is configured in GitHub Actions secrets.
2. Local tooling is available when doing manual release preparation:
   - `git`
   - `.NET SDK`
   - `nbgv` CLI (for example: `dotnet tool install -g nbgv`)
3. Your local branch is clean before running `nbgv prepare-release`.

## How Publishing Works in This Repository

- [ci.yml](../.github/workflows/ci.yml)
  - Runs on pushes to `main` and PRs targeting `main`.
  - On `main` pushes, publishes preview packages to GitHub Packages and NuGet.org.
- [publish.yml](../.github/workflows/publish.yml)
  - Runs on tags matching `v*`.
  - Packs with `-p:Version=${GITHUB_REF##*/v}` and publishes to NuGet.org.
  - Creates a GitHub Release and uploads `.nupkg` and `.snupkg` artifacts.

## Preview Releases (Automatic)

Every push to `main` creates preview packages.

- Version base comes from [version.json](../version.json), currently `0.5.2-beta`.
- NBGV adds commit height (for example `0.5.2-beta.123`).
- These builds are intentionally prerelease packages.

## Stable Release Runbook

1. Ensure `main` is up to date and clean:

```bash
git checkout main && git pull --ff-only
```

2. Cut release branch and start next dev version:

```bash
nbgv prepare-release
git push --all
```

`nbgv prepare-release` creates a release branch (e.g. `v0.5`), removes the prerelease suffix on that branch, and bumps `main` to the next version automatically.

3. Switch to the release branch, verify the version is clean (no `-beta`), and tag:

```bash
git checkout v0.5
nbgv get-version            # must show a stable version, e.g. 0.5.2
git tag v0.5.2
git push origin v0.5.2
```

4. The [publish workflow](../.github/workflows/publish.yml) runs automatically and:
   - Packs NuGet packages (version computed by NBGV from the tag).
   - Pushes to NuGet.org.
   - Creates a GitHub Release with auto-generated notes and packages attached.

## Notes on NBGV Configuration

- [version.json](../version.json) defines the base version and public release refs.
- Current `publicReleaseRefSpec` allows release branches matching `vX` or `vX.Y`.
- If your branch strategy changes (for example `release/vX.Y`), update `publicReleaseRefSpec` accordingly.

## NuGet and Security Best Practices

- Use scoped NuGet API keys with only required permissions.
- Rotate API keys regularly.
- Never store API keys in files; use GitHub Actions secrets only.
- For manual publish commands, always target `https://api.nuget.org/v3/index.json`.

## Troubleshooting

- `nbgv prepare-release` fails with dirty working tree:
  - Commit or stash changes first.
- Tag pushed but no publish workflow:
  - Ensure tag matches `v*` and was pushed to origin.
- NuGet push fails in workflow:
  - Check `NUGET_APIKEY` validity and package ownership on nuget.org.
