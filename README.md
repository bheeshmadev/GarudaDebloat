# GarudaDebloat

GarudaDebloat is a portable Windows debloater utility built with C# WinForms on .NET 8.

Created by **Garuda Vault Security Research**.

- Author/GitHub: bheeshmadev
- Website: bheeshma.me
- Version: 1.0.0

## Overview

GarudaDebloat scans and manages removable software in one interface:

1. Win32 apps (registry uninstall entries)
2. UWP / Microsoft Store apps (AppX)

The tool provides bulk selection, uninstall/removal workflow with confirmation, and a live operation log.

On first launch, the tool shows a mandatory safety acknowledgment about irreversible removal risk.

## Removal Behavior

### Win32 apps

- Reads `UninstallString` and `QuietUninstallString` from registry entries
- Handles MSI uninstall normalization (`msiexec /x /qn /norestart`)
- Falls back to silent switches (`/S`, `/quiet`) when needed
- Attempts best-effort residual cleanup for known install/data paths after uninstall

### UWP / Store apps

- Executes `Remove-AppxPackage -AllUsers`
- Removes provisioned package records via `Remove-AppxProvisionedPackage`
- Attempts cleanup of local package data folders when safely resolvable

## Safety Controls

- Manifest requires elevation (`requireAdministrator`)
- Runtime admin verification at launch
- Explicit confirmation dialog listing selected targets
- Full operation logging with error reporting

## Build Requirements

- Windows 10/11
- .NET SDK 8.0+
- Administrator privileges to run the executable

## Download Releases

- Open the repository Releases page on GitHub
- Download the latest `GarudaDebloat-<version>-win-x64-selfcontained.zip`
- Extract and run `GarudaDebloat.exe` as Administrator
- No separate .NET runtime installation is required for self-contained releases

## Build and Run

```powershell
dotnet restore
dotnet build -c Release
dotnet run -c Release
```

## Publish Single File (Portable)

```powershell
dotnet publish -c Release -r win-x64 --self-contained false /p:PublishSingleFile=true
```

Published output:

- `bin\Release\net8.0-windows\win-x64\publish\GarudaDebloat.exe`

## Release Workflow

- A GitHub Actions pipeline is included at `.github/workflows/release.yml`
- Pushing a tag like `v1.0.0` automatically builds a self-contained win-x64 package
- The workflow uploads `.zip` and `.sha256` files to the GitHub Release

Example tag flow:

```powershell
git tag v1.0.0
git push origin v1.0.0
```

## Notes

This project intentionally performs system-level software and feature removal. Validate selections carefully before uninstall operations in production environments.
