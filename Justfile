# bronze-data-loader — Justfile
# See https://just.systems/man/en/

# ----- Project metadata -----
project := "bronze-data-loader"
version := "0.3.0"

# ----- Auto-detect RID for the current OS -----
rid := `dotnet --info 2>/dev/null | sed -n 's/^ *RID: *//p'`

# ============================================================================
# Recipes
# ============================================================================

# List available recipes (default — shown when you run `just` with no recipe)
list:
    @just --list

# Run the console app (passes all remaining arguments through)
run *args:
    dotnet run --project src/BronzeDataLoader.Console -- {{args}}

# Run all tests (restores if needed)
test:
    dotnet test

# Run the console load command using the configuration in example/
example *args:
    dotnet run --project src/BronzeDataLoader.Console -- load example/config.yaml {{args}}

# Build in Release configuration
release:
    dotnet build -c Release

# Publish a single-file self-contained executable for the current OS
publish:
    dotnet publish src/BronzeDataLoader.Console \
        -c Release \
        -r {{rid}} \
        --self-contained true \
        -p:PublishSingleFile=true \
        -o publish/{{rid}}

# Publish a single-file self-contained executable for Windows (win-x64)
publish-windows:
    dotnet publish src/BronzeDataLoader.Console \
        -c Release \
        -r win-x64 \
        --self-contained true \
        -p:PublishSingleFile=true \
        -o publish/win-x64

# Publish a single-file self-contained executable for Linux (linux-x64)
publish-linux:
    dotnet publish src/BronzeDataLoader.Console \
        -c Release \
        -r linux-x64 \
        --self-contained true \
        -p:PublishSingleFile=true \
        -o publish/linux-x64

# Publish a single-file self-contained executable for macOS (osx-x64)
publish-osx:
    dotnet publish src/BronzeDataLoader.Console \
        -c Release \
        -r osx-x64 \
        --self-contained true \
        -p:PublishSingleFile=true \
        -o publish/osx-x64


