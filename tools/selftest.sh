#!/usr/bin/env bash
# Builds MaxSnapr and runs its self-test in an isolated home, so real settings, library and shortcuts stay untouched.
# Usage: tools/selftest.sh <name> [exe]   (Git Bash on Windows; needs the .NET 8 SDK on PATH or in DOTNET_ROOT)
set -u
export DOTNET_NOLOGO=1 DOTNET_CLI_TELEMETRY_OPTOUT=1
[ -n "${DOTNET_ROOT:-}" ] && export PATH="$DOTNET_ROOT:$PATH"
NAME=${1:-run}
# Resolve a relative exe argument before changing directory.
if [ -n "${2:-}" ]; then GIVEN=$(cd "$(dirname "$2")" && pwd)/$(basename "$2"); fi
ROOT=$(cd "$(dirname "$0")/.." && pwd)
cd "$ROOT/src/MaxSnapr" || exit 99
if [ -z "${2:-}" ]; then
  OUT=$(dotnet build -c Debug 2>&1)
  ERR=$(echo "$OUT" | grep -E " error |warning CS" | sed 's/\[[A-Z]:.*//' | sort -u)
  if [ -n "$ERR" ]; then echo "$ERR"; fi
  if ! echo "$OUT" | grep -qE "Build succeeded|Compilación correcta"; then echo "BUILD FAILED"; exit 98; fi
  EXE=./bin/Debug/net8.0-windows10.0.19041.0/win-x64/MaxSnapr.exe
else
  EXE=$GIVEN
fi
T="${TEMP:-/tmp}/maxsnapr-tests/$NAME"
WT=$(cygpath -w "$T")
rm -rf "$T"; mkdir -p "$T"
MAXSNAPR_HOME="$WT\\home" MAXSNAPR_NO_HOTKEYS=1 timeout 600 "$EXE" --selftest "$WT\\out"
echo "exit=$?"
grep -v "^PASS" "$T/out/report.txt"
echo "PASS: $(grep -c '^PASS' "$T/out/report.txt")"
[ -f "$T/home/maxsnapr.log" ] && tail -5 "$T/home/maxsnapr.log"
