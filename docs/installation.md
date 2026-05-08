# Installation

This page describes how to install Seiton.

---

## Prebuilt Binaries

Download a prebuilt binary for your platform from the [releases page](https://github.com/guitarrapc/seiton/releases).

Extract the archive and place the `seiton` executable somewhere in your `$PATH`.

Prebuilt binaries are provided for:

| OS | Architecture |
|---|---|
| Linux | x64, arm64 |
| macOS | x64, arm64 |
| Windows | x64, arm64 |

The binary is a NativeAOT single-file executable. No .NET runtime is required.

---

## Windows

### Winget

```powershell
winget install guitarrapc.seiton
```

### Scoop

```powershell
scoop install seiton
```

### Manual

1. Download the latest `seiton-win-amd64.zip` (x64) or `seiton-win-arm64.zip` (ARM64) from the [releases page](https://github.com/guitarrapc/seiton/releases).
2. Extract and place `seiton.exe` in a directory that is in your `PATH`.

---

## macOS

### Homebrew

This repo provides [`Formula/seiton.rb`](https://github.com/guitarrapc/seiton/blob/main/Formula/seiton.rb) (updated automatically when a GitHub Release is published). Tap the **application repository**, then install:

```sh
brew tap guitarrapc/seiton https://github.com/guitarrapc/seiton
brew install seiton
```

If you use the default GitHub short form and your repo is `github.com/guitarrapc/seiton`:

```sh
brew tap guitarrapc/seiton
brew install seiton
```

### Manual

```sh
curl -L https://github.com/guitarrapc/seiton/releases/latest/download/seiton-osx-arm64.tar.gz | tar xz
sudo mv seiton /usr/local/bin/
```

For Intel Macs, use `seiton-osx-amd64.tar.gz` instead.

---

## Linux

### Homebrew (Linux)

Same as macOS: tap [`guitarrapc/seiton`](https://github.com/guitarrapc/seiton) (formula includes Linux amd64/arm64).

```sh
brew tap guitarrapc/seiton https://github.com/guitarrapc/seiton
brew install seiton
```

### Manual

```sh
curl -L https://github.com/guitarrapc/seiton/releases/latest/download/seiton-linux-amd64.tar.gz | tar xz
sudo mv seiton /usr/local/bin/
```

For Linux arm64, use `seiton-linux-arm64.tar.gz` instead.

---

## Docker

An official Docker image is available from the GitHub Container Registry:

```sh
docker pull ghcr.io/guitarrapc/seiton:latest
```

Lint the workflow files in the current repository:

```sh
docker run --rm -v "$PWD:/repo" ghcr.io/guitarrapc/seiton:latest /repo
```

---

## Build from Source

Building from source requires the [.NET SDK](https://dotnet.microsoft.com/download) (version 10.0 or later).

```sh
git clone https://github.com/guitarrapc/seiton.git
cd seiton
dotnet build -c Release src/Seiton/Seiton.csproj
```

The output binary will be in `src/Seiton/bin/Release/net10.0/`.

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

Expected output (example):

```
seiton 1.0.0
built with .NET 10.0.0 (NativeAOT), linux/x64
```

---

## Next Steps

- [Usage](usage.md) — Learn how to run Seiton and integrate it into your workflow.
- [Configuration](configuration.md) — Configure rule behavior and exclusions.
