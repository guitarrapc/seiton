# Installation

Seiton is distributed as a single-file NativeAOT binary, so in most cases you only need to place the executable in your `PATH`.

There are several ways to install Seiton.

1. [Homebrew](#homebrew)
1. [Scoop](#scoop)
1. [Prebuilt Binaries](#prebuilt-binaries)
1. [Install Script](#install-script)
1. [Docker](#docker)
1. [Build from Source](#build-from-source)
1. [Verify the Installation](#verify-the-installation)

---

## Homebrew

Seiton provides a Homebrew formula from the repository tap for macOS and Linux:

```sh
brew tap guitarrapc/seiton
brew install seiton
```

If you prefer the explicit repository URL:

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
version=v0.9.6
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

## Install Script

The install script is the fastest way to install Seiton on macOS or Linux.

```sh
curl -fsSL https://raw.githubusercontent.com/guitarrapc/seiton/main/scripts/install.sh | bash
```

The script auto-detects your OS and architecture, downloads the latest release, verifies the SHA-256 checksum, and installs the binary to `/usr/local/bin` by default. If `gh` is available, it also attempts SLSA attestation verification.

Install a specific version:

```sh
curl -fsSL https://raw.githubusercontent.com/guitarrapc/seiton/main/scripts/install.sh | bash -s -- --version 0.9.6
```

Install to a custom directory:

```sh
curl -fsSL https://raw.githubusercontent.com/guitarrapc/seiton/main/scripts/install.sh | bash -s -- --dir ~/.local/bin
```

Combine both options:

```sh
curl -fsSL https://raw.githubusercontent.com/guitarrapc/seiton/main/scripts/install.sh | bash -s -- --version 0.9.6 --dir ~/.local/bin
```

If the destination directory is not in your `PATH`, the script prints a hint explaining what to add.

---

## Docker

Official multi-architecture container images are published to GHCR for `linux/amd64` and `linux/arm64`.

```sh
docker pull ghcr.io/guitarrapc/seiton:latest
docker pull ghcr.io/guitarrapc/seiton:0.9.6
docker pull ghcr.io/guitarrapc/seiton:v0.9.6
```

Lint the workflow files in the current directory:

```sh
docker run --rm -v "$PWD:/repo:ro" ghcr.io/guitarrapc/seiton:latest /repo
```

The `:ro` mount keeps the repository read-only inside the container.

---

## Build from Source

Building from source requires the [.NET SDK](https://dotnet.microsoft.com/download) version 10.0 or later.

```sh
git clone https://github.com/guitarrapc/seiton.git
cd seiton
dotnet build -c Release src/Seiton/Seiton.csproj
```

The build output is written under `src/Seiton/bin/Release/net10.0/`.

To produce a self-contained NativeAOT binary:

```sh
dotnet publish -c Release src/Seiton/Seiton.csproj
```

---

## Verify the Installation

Run `seiton version` to confirm the installation succeeded:

```sh
seiton version
```

Example output:

```text
seiton 0.9.6
built with .NET 10.0.0 (NativeAOT), linux/x64
```

---

## Next Steps

- [Usage](usage.md) — Learn how to run Seiton and integrate it into your workflow.
- [Configuration](configuration.md) — Configure rule behavior and exclusions.
