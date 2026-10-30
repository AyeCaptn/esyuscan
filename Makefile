.PHONY: restore build run check check-all test format package install
.DEFAULT_GOAL := build
restore:
	bash scripts/prepare-wayland.sh
	dotnet restore Scan.slnx --locked-mode --verbosity quiet
build: restore
	dotnet build Scan.slnx --no-restore
run: restore
	dotnet run --project src/Scan --no-restore -- open
check:
	bash scripts/check.sh
check-all:
	bash scripts/check-all.sh
test:
	bash scripts/test.sh $(ARGS)
format:
	bash scripts/format.sh
package:
	bash scripts/package.sh
install:
	bash scripts/install.sh
