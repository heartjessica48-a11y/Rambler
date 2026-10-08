#!/usr/bin/env bash
# Cross-builds Rambler from Linux/macOS (the app itself runs on Windows only).
# Usage: ./build.sh [--skip-tests] [--live-tests]
set -euo pipefail
cd "$(dirname "$0")"
CONFIG=Release
SKIP_TESTS=0
LIVE_TESTS=0
for arg in "$@"; do
  case "$arg" in
    --skip-tests) SKIP_TESTS=1 ;;
    --live-tests) LIVE_TESTS=1 ;;
    *) echo "Unknown option: $arg" >&2; exit 2 ;;
  esac
done

dotnet build Rambler.sln -c "$CONFIG"
[ "$SKIP_TESTS" = 1 ] || dotnet test tests/Rambler.Core.Tests -c "$CONFIG" --no-build
[ "$LIVE_TESTS" = 0 ] || dotnet test tests/Rambler.IntegrationTests -c "$CONFIG" --no-build --logger "console;verbosity=detailed"
dotnet publish src/Rambler/Rambler.csproj -c "$CONFIG" -r win-x64 --self-contained true -o artifacts/publish/win-x64
echo "Published: artifacts/publish/win-x64/Rambler.exe"
