#!/usr/bin/env bash
# ---------------------------------------------------------------------------
# Afrowave DiskMigrator - build script
#
# Produces a single self-contained Windows EXE (no installation, no runtime
# prerequisites) and can run the automated test suite.
#
#   ./build.sh            build the Windows EXE into ./dist
#   ./build.sh test       run the automated test suite (works on Linux too)
#   ./build.sh all        tests + build
#   ./build.sh clean      remove build output
# ---------------------------------------------------------------------------
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
cd "$ROOT"

# Keep every SDK and NuGet artefact inside the repository so the build works
# on any machine (including sandboxed CI) without touching the user profile.
export DOTNET_CLI_HOME="$ROOT/.dotnet-home"
export DOTNET_NOLOGO=1
export DOTNET_CLI_TELEMETRY_OPTOUT=1
export DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1
export DOTNET_CLI_UI_LANGUAGE=en
mkdir -p "$DOTNET_CLI_HOME"

DIST="$ROOT/dist"
TESTS="$ROOT/tests/Afrowave.DiskMigrator.Tests/Afrowave.DiskMigrator.Tests.csproj"
CLI="$ROOT/src/Afrowave.DiskMigrator.Cli/Afrowave.DiskMigrator.Cli.csproj"

run_tests() {
  echo "==> Running automated tests"
  dotnet run --project "$TESTS" -c Release --no-launch-profile -- "$@"
}

build_exe() {
  echo "==> Publishing Afrowave DiskMigrator for Windows x64"
  rm -rf "$DIST"
  dotnet publish "$CLI" \
    -c Release \
    -r win-x64 \
    --self-contained true \
    -o "$DIST" \
    --nologo

  # Only the single EXE belongs in the portable output.
  rm -f "$DIST"/*.pdb

  echo
  echo "==> Build finished"
  ls -all "$DIST"/AfrowaveDiskMigrator.exe 2>/dev/null || true
  if command -v sha256sum >/dev/null 2>&1; then
    sha256sum "$DIST"/AfrowaveDiskMigrator.exe 2>/dev/null || true
  fi
}

clean() {
  echo "==> Cleaning"
  rm -rf "$DIST"
  find "$ROOT/src" "$ROOT/tests" -type d \( -name bin -o -name obj \) -prune -exec rm -rf {} + 2>/dev/null || true
}

case "${1:-build}" in
  test)   run_tests "${@:2}" ;;
  build)  build_exe ;;
  all)    run_tests; build_exe ;;
  clean)  clean ;;
  *)      echo "Použití: $0 {test|build|all|clean}" >&2; exit 2 ;;
esac
