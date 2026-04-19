#!/usr/bin/env bash
set -euo pipefail

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REFERENCES_DIR="${ROOT_DIR}/.references"

mkdir -p "${REFERENCES_DIR}"

repos=(
  "rhysd/actionlint"
  "suzuki-shunsuke/ghalint"
  "hadashiA/VYaml"
  "zizmorcore/zizmor"
  "suzuki-shunsuke/pinact"
  "azu/dockerfile-pin"
  "stacklok/frizbee"
  "praetorian-inc/trajan"
  "AdnaneKhan/Gato-X"
  "Cysharp/ConsoleAppFramework"
)

for repo in "${repos[@]}"; do
  name="${repo#*/}"
  target="${REFERENCES_DIR}/${name}"
  url="https://github.com/${repo}.git"

  if [[ -d "${target}/.git" ]]; then
    echo "[pull] ${repo}"
    git -C "${target}" pull --ff-only
  elif [[ -d "${target}" ]]; then
    echo "[skip] ${target} exists but is not a git repository"
  else
    echo "[clone] ${repo}"
    git clone "${url}" "${target}"
  fi
done
