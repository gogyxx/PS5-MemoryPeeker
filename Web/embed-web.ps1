param(
    [string]$InputPath = (Join-Path $PSScriptRoot "web\index.html"),
    [string]$OutputPath = (Join-Path $PSScriptRoot "source\web_ui.h")
)

$ErrorActionPreference = "Stop"

if (!(Test-Path $InputPath)) {
    throw "Web UI not found: $InputPath"
}

$ProgressImage = Join-Path (Resolve-Path (Join-Path $PSScriptRoot "..\PS5MemoryPeeker\Assets\Progress")).Path "gold-stroke-progress.png"
if (!(Test-Path $ProgressImage)) {
    throw "Progress image not found: $ProgressImage"
}

$Html = [IO.File]::ReadAllText((Resolve-Path $InputPath), [Text.Encoding]::UTF8)
$ProgressData = [Convert]::ToBase64String([IO.File]::ReadAllBytes($ProgressImage))
$Html = $Html.Replace("{{PROGRESS_DATA_URI}}", "data:image/png;base64,$ProgressData")
$Bytes = [Text.Encoding]::UTF8.GetBytes($Html)
$Builder = [Text.StringBuilder]::new()
[void]$Builder.AppendLine("#ifndef PS5_MEMORY_PEEKER_WEB_UI_H")
[void]$Builder.AppendLine("#define PS5_MEMORY_PEEKER_WEB_UI_H")
[void]$Builder.AppendLine("static const unsigned char WEB_UI[] = {")
for ($Offset = 0; $Offset -lt $Bytes.Length; $Offset += 20) {
    $End = [Math]::Min($Offset + 20, $Bytes.Length)
    $Line = for ($Index = $Offset; $Index -lt $End; $Index++) { "0x{0:X2}" -f $Bytes[$Index] }
    [void]$Builder.Append("    ")
    [void]$Builder.Append(($Line -join ", "))
    [void]$Builder.AppendLine(",")
}
[void]$Builder.AppendLine("    0x00")
[void]$Builder.AppendLine("};")
[void]$Builder.AppendLine("#define WEB_UI_LENGTH (sizeof(WEB_UI) - 1)")
[void]$Builder.AppendLine("#endif")

[IO.File]::WriteAllText($OutputPath, $Builder.ToString(), [Text.UTF8Encoding]::new($false))
Write-Host "Embedded web UI: $OutputPath ($($Bytes.Length) bytes)"
