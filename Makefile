.PHONY: build run check check-all test format package install
build:
	dotnet build Scan.slnx --no-restore
run:
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
