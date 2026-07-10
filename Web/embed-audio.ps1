param(
    [string]$SoundRoot = "",
    [string]$OutputPath = (Join-Path $PSScriptRoot "source\audio_assets.h")
)

$ErrorActionPreference = "Stop"

if ([string]::IsNullOrWhiteSpace($SoundRoot)) {
    $SoundRoot = @(
        (Join-Path $PSScriptRoot "..\Assets\Sounds"),
        (Join-Path $PSScriptRoot "..\PS5MemoryPeeker\Assets\Sounds")
    ) | Where-Object { Test-Path $_ } | Select-Object -First 1
}
if (!$SoundRoot) { throw "Audio asset folder not found." }

function Add-ByteArray([Text.StringBuilder]$Builder, [string]$Name, [string]$Path) {
    if (!(Test-Path $Path)) { throw "Audio asset not found: $Path" }
    $bytes = [IO.File]::ReadAllBytes((Resolve-Path $Path))
    [void]$Builder.AppendLine("static const unsigned char $Name[] = {")
    for ($offset = 0; $offset -lt $bytes.Length; $offset += 20) {
        $end = [Math]::Min($offset + 20, $bytes.Length)
        $line = for ($index = $offset; $index -lt $end; $index++) { "0x{0:X2}" -f $bytes[$index] }
        [void]$Builder.AppendLine("    $($line -join ', '),")
    }
    [void]$Builder.AppendLine("};")
    [void]$Builder.AppendLine("#define ${Name}_LENGTH (sizeof($Name))")
}

$builder = [Text.StringBuilder]::new()
[void]$builder.AppendLine("#ifndef PS5_MEMORY_PEEKER_AUDIO_ASSETS_H")
[void]$builder.AppendLine("#define PS5_MEMORY_PEEKER_AUDIO_ASSETS_H")
Add-ByteArray $builder "FOLD_SOUND_MP3" (Join-Path $SoundRoot "Animation_Sound.mp3")
Add-ByteArray $builder "BACKGROUND_MUSIC_MP3" (Join-Path $SoundRoot "Warabe Uta.mp3")
[void]$builder.AppendLine("#endif")
[IO.File]::WriteAllText($OutputPath, $builder.ToString(), [Text.UTF8Encoding]::new($false))
Write-Host "Embedded audio assets: $OutputPath"
