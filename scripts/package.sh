#!/bin/bash
set -euo pipefail
cd "$(dirname "$0")/../pkgbuild"
makepkg --force
