# PS5-MemoryPeeker

![ZIP downloads](https://img.shields.io/github/downloads/POWER-CHANGES-U/PS5-MemoryPeeker/v1.2Beta/PS5MemoryPeeker-v1.2Beta.zip?label=ZIP%20downloads)

PS5-MemoryPeeker is a lightweight Windows WPF tool for reading, scanning, and editing memory from the running PS5 `eboot.bin` process through PS5Debug/libdebug.

The release also includes `PS5MemoryPeekerWeb.elf`, which hosts the scanner directly on the PS5 at port `1999` for use from desktop and mobile browsers.

## What It Does

- Connects to PS5Debug/libdebug over the network.
- Targets the running `eboot.bin` process.
- Loads readable process memory sections.
- Scans values with first scan and next scan narrowing.
- Reads and writes memory addresses.
- Supports locked/frozen values.
- Saves and loads cheat tables.
- Exports active entries as JSON, SHN, or MC4.
- Uses one editable table for scan results and active cheats.
- Includes looping background music with a mute/resume control.

## Requirements

- Windows 10/11
- [.NET 8 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/8.0)
- Jailbroken PS5 with PS5Debug support

## Included Runtime Files

The release package includes:

- `libdebug.dll`
- `PS5Debug.bin`

## Setup

1. Download the latest release ZIP.
2. Extract the ZIP to a normal writable folder.
3. Start `PS5MemoryPeeker.exe`.
4. Enter the PS5 IP address.
5. Enter the payload-loader port.
6. Click `Connect`.
7. Start your game on the PS5.
8. Wait until the app shows `EBOOT HOOKED`.
9. Click `Refresh` to load process memory.
10. Select memory sections and scan values.

## Connection Flow

PS5-MemoryPeeker connects to PS5Debug through libdebug port `744`.

If PS5Debug is already running, the app connects directly. If PS5Debug is not running, the app can send `PS5Debug.bin` through the payload-loader port entered by the user, then connects once libdebug becomes available.

When no game is running yet, the app stays on `EBOOT WAITING`. Once a game starts and `eboot.bin` appears, it switches to `EBOOT HOOKED`.

## Build

```powershell
dotnet build .\PS5MemoryPeeker.csproj -c Release
dotnet publish .\PS5MemoryPeeker.csproj -c Release -o .\publish
```

Target framework: `net8.0-windows`

The PS5-hosted Web/ELF source and build instructions are in [`Web`](Web/README.md).
