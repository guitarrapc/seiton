# Installation

Seiton is distributed as a single-file NativeAOT binary, so in most cases you only need to place the executable in your `PATH`.

There are several ways to install or download Seiton.

1. [Homebrew](#homebrew)
1. [Scoop](#scoop)
1. [Prebuilt Binaries](#prebuilt-binaries)
1. [GitHub Actions](#github-actions)
1. [Docker](#docker)
1. [Build from Source](#build-from-source)
1. [Verify the Installation](#verify-the-installation)

---

## Homebrew

Seiton provides a Homebrew formula from the repository tap for macOS and Linux:

```sh
brew tap guitarrapc/seiton https://github.com/guitarrapc/seiton
brew install seiton
```

---

## Scoop

Seiton is available from [`guitarrapc/scoop-bucket`](https://github.com/guitarrapc/scoop-bucket) for Windows.

```powershell
scoop bucket add guitarrapc https://github.com/guitarrapc/scoop-bucket
scoop install seiton
```

Upgrade later with `scoop update seiton`.

---

## Prebuilt Binaries

Download a release archive from the [GitHub Releases page](https://github.com/guitarrapc/seiton/releases), extract it, and place the resulting executable in a directory on your `PATH`.

The prebuilt binary is a NativeAOT single-file executable. No .NET runtime is required.

| Platform | Architecture | Archive |
|---|---|---|
| Linux | x64 | `seiton-linux-amd64.tar.gz` |
| Linux | arm64 | `seiton-linux-arm64.tar.gz` |
| macOS | x64 (Intel) | `seiton-osx-amd64.tar.gz` |
| macOS | arm64 (Apple Silicon) | `seiton-osx-arm64.tar.gz` |
| Windows | x64 | `seiton-win-amd64.zip` |
| Windows | arm64 | `seiton-win-arm64.zip` |

### Download with GitHub CLI and verify attestation

If you already use [GitHub CLI](https://cli.github.com/), you can download and verify a release artifact directly.

Example for Linux x64:

```sh
version=v0.9.20
asset=seiton-linux-amd64.tar.gz
gh release download -R guitarrapc/seiton "$version" -p "$asset"
tar xzf "$asset"
sudo mv seiton /usr/local/bin/
```

For other platforms, change `asset` to one of the archive names listed above.

Optionally, you can verify the attestation of the downloaded artifact. This is highly recommended in terms of security.

```sh
gh attestation verify -R guitarrapc/seiton seiton-linux-amd64.tar.gz
```

`gh attestation verify` checks the build provenance attached to the release artifact, which provides stronger supply-chain guarantees than checksum verification alone.

---

## GitHub Actions

Use the [`guitarrapc/setup-seiton`](https://github.com/guitarrapc/setup-seiton) action to install the native binary in a workflow. The action adds `seiton` to `PATH`, so later steps can invoke it directly.

```yaml
- uses: actions/checkout@de0fac2e4500dabe0009e67214ff5f5447ce83dd # v6.0.2
- uses: guitarrapc/setup-seiton@v1
- run: seiton
```

Install a specific version:

```yaml
- uses: guitarrapc/setup-seiton@v1
  with:
    seiton-version: 0.9.20
```

See [usage](usage.md#github-actions) for full CI examples, including Docker and SARIF upload.

---

## Docker

Official multi-architecture container images are published to GHCR for `linux/amd64` and `linux/arm64`.

```sh
docker pull ghcr.io/guitarrapc/seiton:latest
docker pull ghcr.io/guitarrapc/seiton:0.9.20
docker pull ghcr.io/guitarrapc/seiton:v0.9.20
```

Lint the workflow files in the current directory:

```sh
docker run --rm -v "$PWD:/repo:ro" ghcr.io/guitarrapc/seiton:latest
```

The `:ro` mount is for lint-only runs. To apply fixes with `--fix`, omit `:ro`:

```sh
docker run --rm -v "$PWD:/repo" ghcr.io/guitarrapc/seiton:latest --fix
```

> `--dry-run` and `--check` do not write files, so `:ro` works for those.

---

## Build from Source

Building from source requires the [.NET SDK](https://dotnet.microsoft.com/download) version 10.0 or later.

```sh
git clone https://github.com/guitarrapc/seiton.git
cd seiton
dotnet build -c Release src/Seiton/Seiton.csproj
```

The build output is written under `src/Seiton/bin/Release/net10.0/`.

To produce a self-contained NativeAOT binary, `dotnet publish` also requires the native toolchain for your platform:

- Windows: Visual Studio 2022 or later with the Desktop development with C++ workload.
- Ubuntu/Linux: `clang` and `zlib1g-dev` at minimum on Ubuntu-based distributions. Other distributions need the equivalent compiler and zlib development packages.
- macOS: the latest Xcode Command Line Tools.

NativeAOT publish is OS-specific. You can cross-compile between architectures on the same OS with the required native toolchain installed, but not across operating systems.

Run only the command that matches the OS you are currently using:

```sh
# Windows machine
dotnet publish -c Release -r win-x64 src/Seiton/Seiton.csproj -o publish/win-x64

# Linux machine
dotnet publish -c Release -r linux-x64 src/Seiton/Seiton.csproj -o publish/linux-x64

# macOS machine
dotnet publish -c Release -r osx-arm64 src/Seiton/Seiton.csproj -o publish/osx-arm64
```

---

## Verify the Installation

Run `seiton version` to confirm the installation succeeded. If you downloaded the binary into the current directory, run `./seiton version` instead:

```sh
seiton version
```

Example output:

```text
seiton 0.9.20
built with .NET 10.0.8, win-x64
```

---

## Next Steps

- [Usage](usage.md) — Learn how to run Seiton and integrate it into your workflow.
- [Configuration](configuration.md) — Configure rule behavior and exclusions.
- **Agent integration** — Run `seiton install --skills` to install skill files for coding agents (Claude Code, GitHub Copilot, Cursor).
