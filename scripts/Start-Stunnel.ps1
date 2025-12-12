# ===============================
# Start Secure FIX Tunnel
# ===============================
param(
    [string]$ConfigPath = "C:\My Apps\RJF.TradeAllocBridge\cfg\stunnel.conf"
)

$stunnelPath = "C:\Program Files (x86)\stunnel\bin\stunnel.exe"

if (-not (Test-Path $stunnelPath)) {
    Write-Host "Stunnel not found at $stunnelPath"
    exit 1
}

if (-not (Test-Path $ConfigPath)) {
    Write-Host "Configuration file missing at $ConfigPath"
    exit 1
}

Write-Host "Starting FIX TLS tunnel..."
Start-Process -FilePath $stunnelPath -ArgumentList "`"$ConfigPath`"" -WindowStyle Hidden

Start-Sleep -Seconds 2

if (Get-Process stunnel -ErrorAction SilentlyContinue) {
    Write-Host "stunnel running. Local port 9876 → fix.broadridge.com:9880"
} else {
    Write-Host "stunnel failed to start. Check stunnel.log for details."
}
