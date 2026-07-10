param(
    [string]$HostName = "192.168.178.41",
    [int]$Port = 9021,
    [string]$Payload = ".\bin\PS5MemoryPeekerWeb.elf"
)

$ErrorActionPreference = "Stop"

$Root = Split-Path -Parent $MyInvocation.MyCommand.Path
$PayloadPath = Join-Path $Root $Payload

if (!(Test-Path $PayloadPath)) {
    throw "Payload not found: $PayloadPath"
}

$Bytes = [System.IO.File]::ReadAllBytes((Resolve-Path $PayloadPath).Path)
$Client = [System.Net.Sockets.TcpClient]::new()
$Connect = $Client.BeginConnect($HostName, $Port, $null, $null)

if (!$Connect.AsyncWaitHandle.WaitOne(5000)) {
    $Client.Close()
    throw "Connection to $HostName`:$Port timed out."
}

$Client.EndConnect($Connect)
$Stream = $Client.GetStream()
$Stream.WriteTimeout = 10000
$Stream.Write($Bytes, 0, $Bytes.Length)
$Stream.Flush()
$Stream.Close()
$Client.Close()

Write-Host "Sent $($Bytes.Length) bytes to $HostName`:$Port"
Write-Host "Open: http://$HostName`:1999/"
