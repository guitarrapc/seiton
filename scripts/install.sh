#!/usr/bin/env bash
set -euo pipefail

# install.sh — Download and install the seiton binary from GitHub Releases.
# Usage:
#   curl -fsSL https://raw.githubusercontent.com/guitarrapc/seiton/main/scripts/install.sh | bash
#   curl -fsSL https://raw.githubusercontent.com/guitarrapc/seiton/main/scripts/install.sh | bash -s -- --version 1.0.0
#   curl -fsSL https://raw.githubusercontent.com/guitarrapc/seiton/main/scripts/install.sh | bash -s -- --dir ~/.local/bin

REPO="guitarrapc/seiton"
BINARY_NAME="seiton"

# Globals for cleanup trap
tmpdir=""

cleanup() {
  if [ -n "$tmpdir" ] && [ -d "$tmpdir" ]; then
    rm -rf "$tmpdir"
  fi
}
trap cleanup EXIT

# --- Platform detection ---

detect_os() {
  local os
  os="$(uname -s)"
  case "$os" in
    Linux*)  echo "linux" ;;
    Darwin*) echo "osx" ;;
    MINGW*|MSYS*|CYGWIN*) echo "win" ;;
    *) echo "Error: Unsupported OS: $os" >&2; exit 1 ;;
  esac
}

detect_arch() {
  local arch
  arch="$(uname -m)"
  case "$arch" in
    x86_64|amd64)   echo "amd64" ;;
    aarch64|arm64)   echo "arm64" ;;
    *) echo "Error: Unsupported architecture: $arch" >&2; exit 1 ;;
  esac
}

# --- Helpers ---

need_cmd() {
  if ! command -v "$1" >/dev/null 2>&1; then
    echo "Error: required command '$1' not found." >&2
    exit 1
  fi
}

install_binary() {
  local src="$1" dest="$2" install_path
  install_path="$(dirname "$dest")"

  mkdir -p "$install_path"

  if [ -w "$install_path" ]; then
    cp "$src" "$dest"
    chmod +x "$dest"
    return
  fi

  if [ "$(id -u)" -eq 0 ]; then
    cp "$src" "$dest"
    chmod +x "$dest"
    return
  fi

  if ! command -v sudo >/dev/null 2>&1; then
    echo "Error: cannot install to ${install_path} because it is not writable and 'sudo' is not available. Re-run as root or choose a writable directory with --dir." >&2
    exit 1
  fi

  echo "Elevated permissions required to install to ${install_path}."
  sudo cp "$src" "$dest"
  sudo chmod +x "$dest"
}

# Fetch the latest release tag from the GitHub API.
# Uses /releases/latest which returns a single object (avoids fragile grep on arrays).
fetch_latest_tag() {
  local tag
  tag="$(curl --proto '=https' --tlsv1.2 -fsSL \
    "https://api.github.com/repos/${REPO}/releases/latest" \
    | grep '"tag_name"' | head -1 | sed 's/.*"tag_name": *"\([^"]*\)".*/\1/')"
  if [ -z "$tag" ]; then
    echo "Error: could not determine latest release tag." >&2
    exit 1
  fi
  echo "$tag"
}

# Verify SHA-256 checksum of a file against a checksums file.
verify_checksum() {
  local file="$1" checksums_file="$2" filename
  filename="$(basename "$file")"

  local expected matches match_count
  matches="$(awk -v filename="$filename" '
    {
      file = $2
      sub(/^\*/, "", file)
      if (file == filename) {
        print $1
      }
    }
  ' "$checksums_file")"
  match_count="$(printf '%s\n' "$matches" | awk 'NF { count++ } END { print count + 0 }')"
  if [ "$match_count" -eq 0 ]; then
    echo "Error: checksum entry for '${filename}' not found in checksums file." >&2
    exit 1
  fi
  if [ "$match_count" -ne 1 ]; then
    echo "Error: multiple checksum entries found for '${filename}' in checksums file." >&2
    exit 1
  fi
  expected="$matches"

  local actual
  if command -v sha256sum >/dev/null 2>&1; then
    actual="$(sha256sum "$file" | awk '{print $1}')"
  elif command -v shasum >/dev/null 2>&1; then
    actual="$(shasum -a 256 "$file" | awk '{print $1}')"
  else
    echo "Error: no compatible checksum utility found (sha256sum or shasum)." >&2
    exit 1
  fi

  if [ "$expected" != "$actual" ]; then
    echo "Error: checksum mismatch for '${filename}'." >&2
    echo "  expected: ${expected}" >&2
    echo "  actual:   ${actual}" >&2
    exit 1
  fi
}

# --- Argument parsing ---

usage() {
  cat <<EOF
Usage: install.sh [OPTIONS]

Options:
  -v, --version VERSION   Install a specific version (e.g. 1.0.0 or v1.0.0).
                          Default: latest release.
  -d, --dir DIRECTORY     Install to this directory. Default: /usr/local/bin.
  -h, --help              Show this help message.

Examples:
  # Install latest to /usr/local/bin
  curl -fsSL https://raw.githubusercontent.com/${REPO}/main/scripts/install.sh | bash

  # Install version 1.2.0
  curl -fsSL https://raw.githubusercontent.com/${REPO}/main/scripts/install.sh | bash -s -- -v 1.2.0

  # Install to custom directory
  curl -fsSL https://raw.githubusercontent.com/${REPO}/main/scripts/install.sh | bash -s -- -d ~/.local/bin
EOF
}

# --- Main ---

main() {
  local version="" install_dir=""

  # Parse arguments
  while [ $# -gt 0 ]; do
    case "$1" in
      -v|--version)
        if [ $# -lt 2 ]; then
          echo "Error: --version requires a value." >&2
          exit 1
        fi
        version="$2"
        shift 2
        ;;
      -d|--dir)
        if [ $# -lt 2 ]; then
          echo "Error: --dir requires a value." >&2
          exit 1
        fi
        install_dir="$2"
        shift 2
        ;;
      -h|--help)
        usage
        exit 0
        ;;
      *)
        echo "Error: unknown option '$1'." >&2
        usage >&2
        exit 1
        ;;
    esac
  done

  # Check required commands
  need_cmd curl
  need_cmd tar
  need_cmd uname

  local os arch
  os="$(detect_os)"
  arch="$(detect_arch)"
  echo "Detected platform: ${os}/${arch}"

  # Resolve version / tag
  local tag
  if [ -n "$version" ]; then
    # Normalize: strip leading 'v' then re-add for tag
    version="${version#v}"
    tag="v${version}"
  else
    echo "Fetching latest release..."
    tag="$(fetch_latest_tag)"
    version="${tag#v}"
  fi
  echo "Version: ${version} (${tag})"

  # Determine asset name
  local asset_name extension
  if [ "$os" = "win" ]; then
    extension="zip"
  else
    extension="tar.gz"
  fi
  asset_name="${BINARY_NAME}-${os}-${arch}.${extension}"

  # Determine install directory
  if [ -z "$install_dir" ]; then
    install_dir="/usr/local/bin"
  fi

  # Create temp directory
  tmpdir="$(mktemp -d)"

  # Download asset
  echo "Downloading ${asset_name}..."
  if ! curl --proto '=https' --tlsv1.2 -fSL -o "${tmpdir}/${asset_name}" \
    "https://github.com/${REPO}/releases/download/${tag}/${asset_name}"; then
    echo "Error: failed to download '${asset_name}' from release '${tag}'." >&2
    echo "Check available assets at: https://github.com/${REPO}/releases/tag/${tag}" >&2
    exit 1
  fi

  # Download checksums
  echo "Downloading checksums..."
  if ! curl --proto '=https' --tlsv1.2 -fSL -o "${tmpdir}/checksums-sha256.txt" \
    "https://github.com/${REPO}/releases/download/${tag}/checksums-sha256.txt"; then
    echo "Error: failed to download checksums file." >&2
    exit 1
  fi

  # Verify checksum
  echo "Verifying checksum..."
  verify_checksum "${tmpdir}/${asset_name}" "${tmpdir}/checksums-sha256.txt"
  echo "Checksum verified."

  # Verify SLSA provenance (best-effort: only when gh CLI is available)
  if command -v gh >/dev/null 2>&1; then
    echo "Verifying SLSA build provenance..."
    if gh attestation verify "${tmpdir}/${asset_name}" -R "${REPO}" 2>/dev/null; then
      echo "SLSA provenance verified."
    else
      echo "Warning: SLSA provenance verification failed. Continuing with checksum-only verification." >&2
    fi
  fi

  # Extract
  echo "Extracting..."
  if [ "$extension" = "tar.gz" ]; then
    tar xzf "${tmpdir}/${asset_name}" -C "${tmpdir}"
  else
    # zip (Windows/MSYS)
    need_cmd unzip
    unzip -q "${tmpdir}/${asset_name}" -d "${tmpdir}"
  fi

  # Install
  local executable_name="$BINARY_NAME"
  if [ "$os" = "win" ]; then
    executable_name="${executable_name}.exe"
  fi

  local src="${tmpdir}/${executable_name}"
  local dest="${install_dir}/${executable_name}"
  install_binary "$src" "$dest"

  echo ""
  echo "Installed ${BINARY_NAME} ${version} to ${dest}"

  # PATH hint if the directory is not already in PATH
  case ":${PATH}:" in
    *":${install_dir}:"*) ;;
    *)
      echo ""
      echo "Note: '${install_dir}' is not in your PATH."
      echo "Add it with:"
      echo "  export PATH=\"${install_dir}:\$PATH\""
      echo ""
      echo "To make it permanent, add that line to your shell profile (~/.bashrc, ~/.zshrc, etc.)."
      ;;
  esac
}

main "$@"
