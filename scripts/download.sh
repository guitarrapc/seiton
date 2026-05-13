#!/usr/bin/env bash
set -euo pipefail

# download.sh — Download the seiton binary from GitHub Releases.
# Usage:
#   curl -fsSL https://raw.githubusercontent.com/guitarrapc/seiton/main/scripts/download.sh | bash
#   curl -fsSL https://raw.githubusercontent.com/guitarrapc/seiton/main/scripts/download.sh | bash -s -- --version 1.0.0
#   curl -fsSL https://raw.githubusercontent.com/guitarrapc/seiton/main/scripts/download.sh | bash -s -- --dir ./bin

REPO="guitarrapc/seiton"
BINARY_NAME="seiton"

tmpdir=""

cleanup() {
  if [ -n "$tmpdir" ] && [ -d "$tmpdir" ]; then
    rm -rf "$tmpdir"
  fi
}
trap cleanup EXIT

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
    x86_64|amd64) echo "amd64" ;;
    aarch64|arm64) echo "arm64" ;;
    *) echo "Error: Unsupported architecture: $arch" >&2; exit 1 ;;
  esac
}

need_cmd() {
  if ! command -v "$1" >/dev/null 2>&1; then
    echo "Error: required command '$1' not found." >&2
    exit 1
  fi
}

publish_github_actions_metadata() {
  local executable_path="$1" target_dir="$2"

  if [ -n "${GITHUB_ACTIONS:-}" ]; then
    if [ -n "${GITHUB_PATH:-}" ]; then
      printf '%s\n' "$target_dir" >> "$GITHUB_PATH"
    fi

    if [ -n "${GITHUB_OUTPUT:-}" ]; then
      printf 'executable=%s\n' "$executable_path" >> "$GITHUB_OUTPUT"
      printf 'directory=%s\n' "$target_dir" >> "$GITHUB_OUTPUT"
    else
      # GitHub Enterprise instances may still rely on the legacy workflow command.
      echo "::set-output name=executable::${executable_path}"
      echo "::set-output name=directory::${target_dir}"
    fi
  fi
}

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

usage() {
  cat <<EOF
Usage: download.sh [OPTIONS]

Options:
  -v, --version VERSION   Download a specific version (e.g. 1.0.0 or v1.0.0).
                          Default: latest release.
  -d, --dir DIRECTORY     Directory to place the extracted binary in.
                          Default: current directory.
  -h, --help              Show this help message.

GitHub Actions:
  When GITHUB_ACTIONS is set, the script appends the target directory to
  GITHUB_PATH and writes executable/directory outputs when available.

Examples:
  # Download the latest binary to the current directory
  curl -fsSL https://raw.githubusercontent.com/${REPO}/main/scripts/download.sh | bash

  # Download version 1.2.0 to the current directory
  curl -fsSL https://raw.githubusercontent.com/${REPO}/main/scripts/download.sh | bash -s -- -v 1.2.0

  # Download to an existing directory
  curl -fsSL https://raw.githubusercontent.com/${REPO}/main/scripts/download.sh | bash -s -- -d ./bin
EOF
}

main() {
  local version="" target_dir=""

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
        target_dir="$2"
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

  need_cmd curl
  need_cmd uname

  local os arch
  os="$(detect_os)"
  arch="$(detect_arch)"
  echo "Detected platform: ${os}/${arch}"

  local tag
  if [ -n "$version" ]; then
    version="${version#v}"
    tag="v${version}"
  else
    echo "Fetching latest release..."
    tag="$(fetch_latest_tag)"
    version="${tag#v}"
  fi
  echo "Version: ${version} (${tag})"

  local extension
  if [ "$os" = "win" ]; then
    extension="zip"
  else
    extension="tar.gz"
  fi

  local asset_name="${BINARY_NAME}-${os}-${arch}.${extension}"
  local executable_name="$BINARY_NAME"
  if [ "$os" = "win" ]; then
    executable_name="${executable_name}.exe"
  fi

  if [ -z "$target_dir" ]; then
    target_dir="$(pwd)"
  else
    if [ ! -d "$target_dir" ]; then
      echo "Error: directory '$target_dir' does not exist." >&2
      exit 1
    fi
    target_dir="$(cd "$target_dir" && pwd)"
  fi
  if [ ! -w "$target_dir" ]; then
    echo "Error: directory '$target_dir' is not writable." >&2
    exit 1
  fi

  tmpdir="$(mktemp -d)"

  echo "Downloading ${asset_name}..."
  if ! curl --proto '=https' --tlsv1.2 -fSL -o "${tmpdir}/${asset_name}" \
    "https://github.com/${REPO}/releases/download/${tag}/${asset_name}"; then
    echo "Error: failed to download '${asset_name}' from release '${tag}'." >&2
    echo "Check available assets at: https://github.com/${REPO}/releases/tag/${tag}" >&2
    exit 1
  fi

  echo "Downloading checksums..."
  if ! curl --proto '=https' --tlsv1.2 -fSL -o "${tmpdir}/checksums-sha256.txt" \
    "https://github.com/${REPO}/releases/download/${tag}/checksums-sha256.txt"; then
    echo "Error: failed to download checksums file." >&2
    exit 1
  fi

  echo "Verifying checksum..."
  verify_checksum "${tmpdir}/${asset_name}" "${tmpdir}/checksums-sha256.txt"
  echo "Checksum verified."

  if command -v gh >/dev/null 2>&1; then
    echo "Verifying SLSA build provenance..."
    if gh attestation verify "${tmpdir}/${asset_name}" -R "${REPO}" 2>/dev/null; then
      echo "SLSA provenance verified."
    else
      echo "Warning: SLSA provenance verification failed. Continuing with checksum-only verification." >&2
    fi
  fi

  echo "Extracting..."
  if [ "$extension" = "tar.gz" ]; then
    need_cmd tar
    tar xzf "${tmpdir}/${asset_name}" -C "${tmpdir}" "$executable_name"
  else
    need_cmd unzip
    unzip -q "${tmpdir}/${asset_name}" "$executable_name" -d "${tmpdir}"
  fi

  cp "${tmpdir}/${executable_name}" "${target_dir}/${executable_name}"
  chmod +x "${target_dir}/${executable_name}"

  publish_github_actions_metadata "${target_dir}/${executable_name}" "$target_dir"

  echo ""
  echo "Downloaded ${BINARY_NAME} ${version} to ${target_dir}/${executable_name}"
  echo "Run it with: ${target_dir}/${executable_name} version"
}

main "$@"
