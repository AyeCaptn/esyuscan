#!/bin/bash
set -euo pipefail
cd "$(dirname "$0")/.."
make restore
dotnet format Scan.slnx --no-restore --include src tests
