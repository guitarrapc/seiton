#!/usr/bin/env bash
set -euo pipefail

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REFERENCES_DIR="${ROOT_DIR}/.references"

mkdir -p "${REFERENCES_DIR}"

repo_specs=(
  "guitarrapc/setup-seiton"
  "rhysd/actionlint"
  "rhysd/actionlint|actionlint-gh-pages|gh-pages"
  "suzuki-shunsuke/ghalint"
  "hadashiA/VYaml"
  "zizmorcore/zizmor"
  "suzuki-shunsuke/pinact"
  "azu/dockerfile-pin"
  "stacklok/frizbee"
  "praetorian-inc/trajan"
  "AdnaneKhan/Gato-X"
  "Cysharp/ConsoleAppFramework"
  "guitarrapc/githubactions-lab"
  "Cysharp/Actions"
)

for spec in "${repo_specs[@]}"; do
  IFS='|' read -r repo name branch <<< "${spec}"
  name="${name:-${repo#*/}}"
  target="${REFERENCES_DIR}/${name}"
  url="https://github.com/${repo}.git"

  if [[ -d "${target}/.git" ]]; then
    echo "[pull] ${repo}${branch:+ @ ${branch}}"
    if [[ -n "${branch:-}" ]]; then
      git -C "${target}" pull --ff-only origin "${branch}"
    else
      git -C "${target}" pull --ff-only
    fi
  elif [[ -d "${target}" ]]; then
    echo "[skip] ${target} exists but is not a git repository"
  else
    echo "[clone] ${repo}${branch:+ @ ${branch}} -> ${name}"
    if [[ -n "${branch:-}" ]]; then
      git clone --branch "${branch}" --single-branch "${url}" "${target}"
    else
      git clone "${url}" "${target}"
    fi
  fi
done
