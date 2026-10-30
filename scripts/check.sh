#!/bin/bash
set -euo pipefail
cd "$(dirname "$0")/.."
make restore
dotnet format Scan.slnx --verify-no-changes --no-restore --verbosity quiet --include src tests
dotnet build Scan.slnx --no-restore --verbosity quiet
dotnet test Scan.slnx --no-build --no-restore --filter 'TestCategory!=Integration' --verbosity quiet
