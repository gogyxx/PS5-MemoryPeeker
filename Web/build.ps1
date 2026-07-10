$ErrorActionPreference = "Stop"

$Root = Split-Path -Parent $MyInvocation.MyCommand.Path
$Workspace = $Root
while ($Workspace -and !(Test-Path (Join-Path $Workspace "tools\ps5-payload-sdk\ps5-payload-sdk"))) {
    $Parent = Split-Path -Parent $Workspace
    if ($Parent -eq $Workspace) { break }
    $Workspace = $Parent
}
$Sdk = if ($env:PS5_PAYLOAD_SDK) { $env:PS5_PAYLOAD_SDK } else { Join-Path $Workspace "tools\ps5-payload-sdk\ps5-payload-sdk" }
$CMake = "C:\Program Files\Microsoft Visual Studio\18\Insiders\Common7\IDE\CommonExtensions\Microsoft\CMake\CMake\bin\cmake.exe"
$Ninja = "C:\Program Files\Microsoft Visual Studio\18\Insiders\Common7\IDE\CommonExtensions\Microsoft\CMake\Ninja\ninja.exe"

if (!(Test-Path $Sdk)) {
    throw "ps5-payload-sdk not found: $Sdk"
}

if (!(Test-Path $CMake)) {
    throw "CMake not found: $CMake"
}

if (!(Test-Path $Ninja)) {
    throw "Ninja not found: $Ninja"
}

if (!(Test-Path (Join-Path $Sdk "win\prospero-clang.cmd"))) {
    throw "prospero-clang.cmd not found in SDK: $Sdk"
}

$env:PS5_PAYLOAD_SDK = (Resolve-Path $Sdk).Path
$env:SCE_PROSPERO_SDK_DIR = $env:PS5_PAYLOAD_SDK

$LlvmBin = Join-Path $Workspace "tools\LLVM\bin"
if (!(Test-Path (Join-Path $LlvmBin "clang.exe"))) {
    throw "LLVM clang.exe not found: $LlvmBin"
}

$WinLld = Join-Path $env:PS5_PAYLOAD_SDK "win\prospero-lld.exe"
$BinLld = Join-Path $env:PS5_PAYLOAD_SDK "bin\prospero-lld.exe"
if ((Test-Path $WinLld) -and !(Test-Path $BinLld)) {
    Copy-Item -Force $WinLld $BinLld
}

$env:Path = "$env:PS5_PAYLOAD_SDK\win;$LlvmBin;$env:PS5_PAYLOAD_SDK\bin;$(Split-Path $Ninja);$env:Path"

& (Join-Path $Root "embed-web.ps1")
& (Join-Path $Root "embed-audio.ps1")

$BuildDir = Join-Path $Root "build"
New-Item -ItemType Directory -Force -Path $BuildDir | Out-Null

& $CMake -S $Root -B $BuildDir -G Ninja "-DCMAKE_MAKE_PROGRAM=$Ninja" "-DCMAKE_TOOLCHAIN_FILE=$env:PS5_PAYLOAD_SDK\toolchain\prospero.cmake" "-DCMAKE_TRY_COMPILE_TARGET_TYPE=STATIC_LIBRARY"
if ($LASTEXITCODE -ne 0) {
    throw "CMake configure failed with exit code $LASTEXITCODE"
}

& $CMake --build $BuildDir
if ($LASTEXITCODE -ne 0) {
    throw "CMake build failed with exit code $LASTEXITCODE"
}

$Elf = Join-Path $BuildDir "PS5MemoryPeekerWeb.elf"
if (!(Test-Path $Elf)) {
    throw "Build finished but ELF was not created: $Elf"
}

$BinDir = Join-Path $Root "bin"
New-Item -ItemType Directory -Force -Path $BinDir | Out-Null
Copy-Item -Force $Elf (Join-Path $BinDir "PS5MemoryPeekerWeb.elf")

Write-Host "Built: $Elf"
Write-Host "Copied: $(Join-Path $BinDir "PS5MemoryPeekerWeb.elf")"
