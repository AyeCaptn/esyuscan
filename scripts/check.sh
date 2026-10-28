#!/bin/bash
set -euo pipefail
cd "$(dirname "$0")/.."
dotnet format Scan.slnx --verify-no-changes --no-restore --verbosity quiet
dotnet build Scan.slnx --no-restore --verbosity quiet
dotnet test tests/Scan.Tests --no-build --no-restore --filter 'TestCategory!=Integration' --verbosity quiet
