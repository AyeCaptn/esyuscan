#!/bin/bash
set -euo pipefail
cd "$(dirname "$0")/.."
dotnet test tests/Scan.Tests --no-restore "$@"
