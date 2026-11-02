#!/usr/bin/env bash
# Classify a complete changed-file list; errors retain all checks.
set -uo pipefail

if ! changed="$(cat)" || [[ -z "$changed" ]]; then
  printf 'code=true\ncs=true\njs=true\n'
  exit 0
fi

matches() {
  local status
  grep "$1" "$2" <<< "$changed" >/dev/null
  status=$?
  case "$status" in
    1) printf 'false' ;;
    *) printf 'true' ;;
  esac
}

printf 'code=%s\n' "$(matches -vE '(\.md$|^docs/images/)')"
printf 'cs=%s\n' "$(matches -E '(\.cs$|\.csproj$|\.props$|\.targets$|\.slnx$|^global\.json$|^dotnet-tools\.json$)')"
printf 'js=%s\n' "$(matches -E '(\.(ts|tsx|js|jsx|mjs|cjs)$|package-lock\.json$)')"
