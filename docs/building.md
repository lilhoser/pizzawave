# Building Guide

This guide covers building pizzawave from source on all supported platforms.

## Prerequisites

### All Platforms

* [.NET 9.0 SDK](https://dotnet.microsoft.com/download/dotnet/9.0)
* Git for version control
* A code editor (Visual Studio, VS Code, or Rider)

### Windows

* **Visual Studio 2022** (Community Edition is free) with:
  * .NET desktop development workload
  * ASP.NET and web development (optional)

### Linux

```bash
# Ubuntu/Debian
sudo apt-get install -y dotnet-sdk-9.0 git

# Fedora/RHEL
sudo dnf install -y dotnet-sdk-9.0 git
```

### macOS

```bash
# Using Homebrew
brew install --cask dotnet-sdk git
```

## Quick Start

```bash
# Clone the repository
git clone https://github.com/lilhoser/pizzawave.git
cd pizzawave

# Restore dependencies
dotnet restore pizzawave.sln

# Build all projects (Debug)
dotnet build pizzawave.sln

# Build all projects (Release)
dotnet build pizzawave.sln -c Release
```

## Build Output Structure

Build output is organized in the `artifacts/` folder:

```
artifacts/
├── pizzalib/
│   ├── bin/Debug/net9.0/
│   └── obj/
├── pizzacmd/
│   ├── bin/Debug/net9.0/
│   └── obj/
├── pizzaui/
│   ├── bin/Debug/net9.0-windows7.0/
│   └── obj/
└── pizzapi/
    ├── bin/Debug/net9.0/
    └── obj/
```

## Platform-Specific Builds

### Runtime Identifiers (RID)

When publishing for a specific platform, use the appropriate RID:

| Platform | RID |
|----------|-----|
| Windows x64 | `win-x64` |
| Linux x64 | `linux-x64` |
| Linux ARM64 | `linux-arm64` |
| macOS x64 | `osx-x64` |
| macOS ARM64 | `osx-arm64` |

Find more RIDs in the [.NET RID catalog](https://learn.microsoft.com/en-us/dotnet/core/rid-catalog).

### Publish for Specific Platform

```bash
# Publish pizzapi for Raspberry Pi (ARM64)
dotnet publish pizzapi/pizzapi.csproj \
  -c Release \
  -r linux-arm64 \
  --self-contained true \
  -p:PublishSingleFile=true \
  -o ./publish/pizzapi-arm64

# Publish pizzacmd for Linux x64
dotnet publish pizzacmd/pizzacmd.csproj \
  -c Release \
  -r linux-x64 \
  --self-contained true \
  -o ./publish/pizzacmd-linux

# Publish pizzaui for Windows (framework-dependent, smaller)
dotnet publish pizzaui/pizzaui.csproj \
  -c Release \
  -r win-x64 \
  --self-contained false \
  -o ./publish/pizzaui-win
```

### Publish Options Explained

| Option | Description |
|--------|-------------|
| `-c Release` | Release configuration (optimized) |
| `-r <RID>` | Target runtime identifier |
| `--self-contained true` | Include .NET runtime (larger, no dependencies) |
| `--self-contained false` | Require .NET runtime installed (smaller) |
| `-p:PublishSingleFile=true` | Bundle into single executable |
| `-o <path>` | Output directory |

## Building Individual Projects

```bash
# Build specific project
dotnet build pizzalib/pizzalib.csproj
dotnet build pizzacmd/pizzacmd.csproj
dotnet build pizzaui/pizzaui.csproj
dotnet build pizzapi/pizzapi.csproj

# Build with specific configuration
dotnet build pizzacmd/pizzacmd.csproj -c Release

# Build for specific platform
dotnet build pizzacmd/pizzacmd.csproj -r linux-x64
```

## Development Workflow

### Visual Studio (Windows)

1. Open `pizzawave.sln` in Visual Studio
2. Set startup project (right-click solution → Set Startup Projects)
3. Press F5 to debug, Ctrl+F5 to run without debugging

### VS Code (All Platforms)

1. Open the `pizzawave` folder in VS Code
2. Install C# extension
3. Press F5 to debug (select project when prompted)

**Sample launch.json for pizzacmd:**

```json
{
    "version": "0.2.0",
    "configurations": [
        {
            "type": "coreclr",
            "request": "launch",
            "name": "Debug pizzacmd",
            "program": "${workspaceFolder}/artifacts/pizzacmd/bin/Debug/net9.0/pizzacmd",
            "args": ["--talkgroups=/path/to/talkgroups.csv"],
            "cwd": "${workspaceFolder}",
            "console": "internalConsole"
        }
    ]
}
```

### Rider (All Platforms)

1. Open `pizzawave.sln` in Rider
2. Select run configuration from dropdown
3. Press Shift+F10 to run, Shift+F9 to debug

## Clean Build

```bash
# Clean all projects
dotnet clean pizzawave.sln

# Clean and rebuild
dotnet clean pizzawave.sln && dotnet build pizzawave.sln

# Clean artifacts folder (manual)
rm -rf artifacts/*  # Linux/macOS
rmdir /s /q artifacts  # Windows
```

## Running Tests

Currently, pizzawave does not include automated tests. This is a planned enhancement.

## Creating Release Packages

### For GitHub Releases

```bash
# Get version from git tag
VERSION=$(git describe --tags --abbrev=0)

# Build for multiple platforms
dotnet publish pizzapi/pizzapi.csproj -c Release -r linux-arm64 --self-contained true -o ./publish/arm64
dotnet publish pizzapi/pizzapi.csproj -c Release -r linux-x64 --self-contained true -o ./publish/x64

# Create archives
cd publish
zip -r pizzapi-${VERSION}-linux-arm64.zip arm64/*
zip -r pizzapi-${VERSION}-linux-x64.zip x64/*
```

### For .deb Package (Linux)

See [Deployment Guide](deployment.md) for creating .deb packages.

## Troubleshooting

### Build Errors

**Error: "The SDK 'Microsoft.NET.Sdk' was not found"**
```bash
# Verify .NET SDK installation
dotnet --version
dotnet --list-sdks

# Reinstall if needed
# Windows: Download from dotnet.microsoft.com
# Linux: sudo apt-get install --reinstall dotnet-sdk-9.0
# macOS: brew reinstall --cask dotnet-sdk
```

**Error: "NuGet package restore failed"**
```bash
# Clear NuGet cache
dotnet nuget locals all --clear

# Restore again
dotnet restore
```

### Runtime Errors

**Error: "Unable to load shared library"**

Install system dependencies:

```bash
# Debian/Ubuntu
sudo apt-get install -y libicu-dev libssl3 zlib1g \
  libfontconfig1 libx11-6 libx11-xcb1 libxcb1 \
  libxext6 libxfixes3 libxi6 libxrender1 libxtst6
```

**Error: "Whisper native library not found"**

Ensure correct Whisper.net runtime is referenced. For Linux:

```xml
<!-- In .csproj file -->
<PackageReference Include="Whisper.net.Runtime" Version="1.9.0-preview2" />
<!-- Or for NVIDIA GPU -->
<PackageReference Include="Whisper.net.Runtime.Cuda" Version="1.9.0-preview2" />
```

## Performance Tips

### Release vs Debug

* **Debug**: Includes symbols, no optimizations. Use for development.
* **Release**: Optimized, smaller, faster. Use for deployment.

### Self-Contained vs Framework-Dependent

| Type | Size | Startup | Dependencies |
|------|------|---------|--------------|
| Self-contained | ~200MB | Faster | None |
| Framework-dependent | ~5MB | Slower | .NET 9.0 runtime |

### Single-File Publishing

Single-file publishing bundles everything into one executable:

```bash
dotnet publish -p:PublishSingleFile=true
```

Benefits:
* Simpler deployment
* No dependency management

Drawbacks:
* Larger file size
* Slightly slower startup (extracts to temp folder)

## See Also

* [Main README](README.md) - Project overview
* [Quick Start](quickstart.md) - 5-minute setup guide
* [Deployment Guide](deployment.md) - Deployment instructions
* [pizzalib README](../pizzalib/README.md) - Library documentation
