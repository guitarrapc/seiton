#!/usr/bin/env bash
# Regression test: formula renders and is valid Ruby (no network).
# Run on ubuntu only.
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
FIX="${ROOT}/scripts/fixtures/homebrew-seiton-checksums.txt"
RENDER="${ROOT}/scripts/render-homebrew-seiton-formula.sh"
OUT="$(mktemp)"
trap 'rm -f "$OUT"' EXIT

bash "$RENDER" "0.9.30" "v0.9.30" "acme/seiton" "$FIX" > "$OUT"

grep -q 'aaa1111111111111111111111111111111111111111111111111111111111111' "$OUT"
grep -q 'seiton-osx-arm64.tar.gz' "$OUT"
grep -q 'https://github.com/acme/seiton/releases/download/v0.9.30/' "$OUT"

if command -v ruby >/dev/null 2>&1; then
  ruby -c "$OUT" >/dev/null
else
  echo "test-homebrew-formula-render: ruby not found; skipping ruby -c" >&2
fi

echo "test-homebrew-formula-render: OK"
