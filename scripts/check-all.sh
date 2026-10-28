#!/bin/bash
set -euo pipefail
cd "$(dirname "$0")/.."
bash scripts/check.sh
dotnet test tests/Scan.Tests --no-build --no-restore --verbosity normal
dotnet list src/Scan package --vulnerable --include-transitive
desktop-file-validate pkgbuild/esyuscan.desktop
for script in scripts/*.sh pkgbuild/PKGBUILD pkgbuild/esyuscan; do
    bash -n "$script"
done
