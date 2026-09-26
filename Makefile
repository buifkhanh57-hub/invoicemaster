# invoicemaster — build helpers for environments with the .NET SDK.
# The tool itself only uses System.* / System.Text.Json (zero NuGet packages).

DOTNET  ?= dotnet
CONFIG  ?= Release
ARGS    ?=
RID     ?= linux-x64

.PHONY: build run debug test publish clean lines

## build: compile the main executable
build:
        $(DOTNET) build invoicemaster.csproj -c $(CONFIG)

## run: run the CLI (pass ARGS="customer list" etc.)
run:
        $(DOTNET) run --project invoicemaster.csproj -c $(CONFIG) -- $(ARGS)

## debug: run in Debug configuration
debug:
        $(DOTNET) run --project invoicemaster.csproj -c Debug -- $(ARGS)

## test: build and run the self-contained test runner (exit code 0 = all pass)
test:
        $(DOTNET) run --project tests/invoicemaster.Tests.csproj -c $(CONFIG)

## publish: self-contained single-file binary for the current OS/arch
publish:
        $(DOTNET) publish invoicemaster.csproj -c $(CONFIG) -r $(RID) --self-contained true \
                /p:PublishSingleFile=true /p:PublishTrimmed=false -o ./publish

## clean: remove build artifacts
clean:
        $(DOTNET) clean invoicemaster.csproj
        $(DOTNET) clean tests/invoicemaster.Tests.csproj
        rm -rf ./publish

## lines: total C# line count of the project
lines:
        @find . -type f -name '*.cs' -print0 | xargs -0 wc -l | tail -1
