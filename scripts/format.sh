#!/bin/bash
set -euo pipefail
cd "$(dirname "$0")/.."
dotnet format Scan.slnx --no-restore
