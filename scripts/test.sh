#!/bin/bash
set -euo pipefail
cd "$(dirname "$0")/.."
make restore
dotnet test Scan.slnx --no-restore "$@"
