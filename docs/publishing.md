# Publishing to NuGet.org

This document describes how to publish packages to NuGet.org.

## Automated Publishing

The repository uses GitHub Actions to automatically publish NuGet packages to nuget.org when a new version tag is pushed.

### Prerequisites

The repository must have the `NUGET_APIKEY` secret configured in GitHub repository settings.

#### Setting up the NUGET_APIKEY Secret

1. Generate an API key from [nuget.org](https://www.nuget.org/account/apikeys)
   - Go to your NuGet.org account settings
   - Navigate to API Keys
   - Create a new API key with "Push" permissions
   - Copy the generated API key

2. Add the secret to GitHub repository settings:
   - Go to repository Settings → Secrets and variables → Actions
   - Click "New repository secret"
   - Name: `NUGET_APIKEY`
   - Value: Paste your NuGet.org API key
   - Click "Add secret"

### Publishing Process

Once the secret is configured, publishing is automatic:

1. Create and push a version tag:
   ```bash
   git tag v1.0.0
   git push origin v1.0.0
   ```

2. The [publish workflow](.github/workflows/publish.yml) will automatically:
   - Build the project
   - Pack all projects as NuGet packages
   - Push packages to nuget.org
   - Create a GitHub release with the packages attached

### Workflow Configuration

The publish workflow (`.github/workflows/publish.yml`) is triggered on version tags matching `v*` pattern (e.g., v1.0.0, v2.1.3).

The workflow uses:
- **Secret**: `NUGET_APIKEY` for authentication
- **Target**: `https://api.nuget.org/v3/index.json`
- **Packages**: All `.nupkg` files from Release configuration
