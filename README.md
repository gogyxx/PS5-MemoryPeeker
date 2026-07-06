# PS5-MemoryPeeker

PS5-MemoryPeeker is a lightweight Windows WPF memory scanner and editor for PS5 jailbreak environments where PS5Debug/libdebug access is available.

The tool is focused on the running `eboot.bin` game process. It is not an ELF launcher, payload collection, exploit host, or game-title database.

## Features

- Connects to a PS5 running PS5Debug/libdebug.
- Can send `PS5Debug.bin` through a user-provided payload-loader port when available.
- Automatically targets the running `eboot.bin` process.
- Loads readable process memory sections.
- Scans selected memory sections for exact values and next-scan narrowing.
- Reads and writes selected memory addresses.
- Supports locked cheat values.
- Saves and loads local cheat tables.
- Exports active cheats as JSON, SHN, or MC4.
- Includes light/dark theme support and a lightweight startup animation.

## Requirements

- Windows 10/11
- [.NET 8 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/8.0)
- A PS5 jailbreak environment with PS5Debug running or a payload loader that can receive `PS5Debug.bin`
- Network access from the PC to the PS5

## Quick Start

1. Download the latest release ZIP.
2. Extract it to a normal writable folder.
3. Start `PS5MemoryPeeker.exe`.
4. Enter your PS5 IP address.
5. Enter your payload-loader port.
6. Click `Connect`.
7. Start a game on the PS5 so the app can hook `EBOOT`.
8. Click `Refresh` to load process memory sections.
9. Select memory sections and scan values.

## Connection Notes

- PS5-MemoryPeeker talks to PS5Debug on libdebug port `744`.
- The payload-loader port is entered by the user because different setups use different ports.
- If PS5Debug is already running and port `744` answers, the app connects directly.
- If PS5Debug is not running, the app can try sending `PS5Debug.bin` when that file is placed next to the app.

## Building From Source

```powershell
dotnet build .\PS5MemoryPeeker.csproj -c Release
dotnet publish .\PS5MemoryPeeker.csproj -c Release -o .\publish
```

The project targets `net8.0-windows` and uses WPF.

## Responsible Use

Use this tool only on hardware and software you own or have permission to analyze. Do not use it for online cheating, harassment, commercial piracy, or activity that violates platform rules.

## Third-Party Files

Before redistributing binaries, verify that you are allowed to include or redistribute these files in your release package:

- `libdebug.dll`
- `PS5Debug.bin`

If in doubt, publish only the source and instruct users to place their own compatible `PS5Debug.bin` next to the app.
