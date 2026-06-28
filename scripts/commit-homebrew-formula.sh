#!/usr/bin/env bash
# Render Formula/seiton.rb from checksums, commit, and push on the current branch (this repo).
#
# Run from Seiton repository root (must have .git). CI: checkout default branch, then run on ubuntu only.
#
# Required env:
#   GITHUB_REPOSITORY — owner/name (e.g. guitarrapc/seiton)
#   SEITON_TAG        — v1.2.1
#   SEITON_VERSION    — 1.2.1
# One of:
#   CHECKSUMS_URL  — URL to checksums-sha256.txt for this release
#   CHECKSUMS_FILE — local path
#
# Optional:
#   SKIP_PUSH=1 — commit only (local debug; not recommended in CI)
set -euo pipefail

: "${GITHUB_REPOSITORY:?GITHUB_REPOSITORY is required}"
: "${SEITON_TAG:?SEITON_TAG is required}"
: "${SEITON_VERSION:?SEITON_VERSION is required}"

ROOT_DIR="$(cd "$(dirname "$0")/.." && pwd)"
cd "$ROOT_DIR"

if [ ! -d .git ]; then
  echo "commit-homebrew-formula.sh: run from Seiton repository root (.git missing)" >&2
  exit 1
fi

RENDER="${ROOT_DIR}/scripts/render-homebrew-seiton-formula.sh"
TMP="$(mktemp -d "${TMPDIR:-/tmp}/seiton.XXXXXX")"
trap 'rm -rf "$TMP"' EXIT

if [ -n "${CHECKSUMS_URL:-}" ]; then
  curl -fsSL "$CHECKSUMS_URL" -o "$TMP/checksums-sha256.txt"
elif [ -n "${CHECKSUMS_FILE:-}" ]; then
  cp "$CHECKSUMS_FILE" "$TMP/checksums-sha256.txt"
else
  echo "commit-homebrew-formula.sh: set CHECKSUMS_URL or CHECKSUMS_FILE" >&2
  exit 1
fi

mkdir -p Formula
bash "$RENDER" "$SEITON_VERSION" "$SEITON_TAG" "$GITHUB_REPOSITORY" \
  "$TMP/checksums-sha256.txt" > Formula/seiton.rb

git add Formula/seiton.rb

if git diff --cached --quiet; then
  echo "commit-homebrew-formula.sh: Formula/seiton.rb unchanged; nothing to commit."
  exit 0
fi

git commit -m "chore(homebrew): bump seiton to ${SEITON_VERSION}

Automated bump for ${GITHUB_REPOSITORY} ${SEITON_TAG}"

if [ "${SKIP_PUSH:-0}" = "1" ]; then
  echo "commit-homebrew-formula.sh: SKIP_PUSH=1; not pushing."
  exit 0
fi

BRANCH="$(git rev-parse --abbrev-ref HEAD)"
git push origin "HEAD:refs/heads/${BRANCH}"
