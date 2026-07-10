# PS5-MemoryPeeker Web

PS5-MemoryPeeker Web is a PS5-hosted memory scanner and editor. Send the ELF to the PS5, then open the interface from a browser on Windows, Android, iPhone, macOS, or another device on the same network.

## Features

- Automatically detects the running `eboot.bin` process.
- Loads and filters readable EBOOT memory sections.
- Exact, fuzzy, range, changed, unchanged, increased, and decreased scans.
- Supports 1/2/4/8-byte integers, Pointer, Float, Double, Hex, and String values.
- Runs First Scan and Next Scan in a cancellable background thread.
- Reports real scan progress without blocking the web server.
- Reads and writes memory addresses.
- Supports saved addresses, locked values, local cheat-table save/load, and JSON/SHN/MC4 export.
- Includes Pause, Resume, Kill, auto-pause, section selection, and light/dark themes.
- Responsive WPF-style interface for desktop and mobile browsers.
- Uses one editable table for scan results and enabled cheats.
- Embeds startup audio and looping background music directly in the ELF.

## Run

Send the ELF to the payload-loader port:

```powershell
.\send.ps1 -HostName 192.168.178.41 -Port 9021
```

Open:

```text
http://192.168.178.41:1999/
```

The web port is fixed to `1999`. The payload-loader port is supplied to `send.ps1`.

## Build

The build uses `ps5-payload-sdk`, CMake, Ninja, and LLVM from the workspace:

```powershell
.\build.ps1
```

`build.ps1` embeds `web/index.html`, the progress image, and both audio files into the ELF, builds the payload, and copies it to:

```text
bin\PS5MemoryPeekerWeb.elf
```

## Main API

- `GET /api/status`
- `GET /api/eboot`
- `GET /api/maps?refresh=1`
- `GET /api/sections/select?index=0&selected=1`
- `GET /api/sections/select-all?selected=1`
- `GET /api/read?addr=0x2110000&len=4&type=4`
- `GET /api/write?addr=0x2110000&type=4&value=99`
- `GET /api/scan/start?value=99&type=4&compare=exact&aligned=1`
- `GET /api/scan/next/start?value=98&type=4&compare=exact`
- `GET /api/scan/progress`
- `GET /api/scan/stop`
- `GET /api/results`
- `GET /api/process/pause`
- `GET /api/process/resume`
- `GET /api/process/kill`

## Structure

- `source/main.c`: PS5 HTTP, EBOOT, memory, scan, and process backend.
- `web/index.html`: browser interface.
- `embed-web.ps1`: embeds the browser interface and progress asset.
- `embed-audio.ps1`: embeds the bundled audio assets.
- `build.ps1`: builds the PS5 ELF.
- `send.ps1`: sends the ELF to the PS5 payload loader.
