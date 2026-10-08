# Started hidden by launch.vbs. Starts the app via the muxer path with
# CREATE_NO_WINDOW so the console is suppressed without polluting the WPF
# window show-state (taskbar icon bug). ASCII-only on purpose.
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$dll = Join-Path $root "src\LesTool\bin\Release\net10.0-windows\win-x64\LesTool.dll"

if (-not (Test-Path $dll)) {
    [System.Windows.Forms.MessageBox]::Show("LesTool.dll not found (build first: dotnet build -c Release)`n$dll", "LesTool") 
    exit 1
}

$psi = New-Object System.Diagnostics.ProcessStartInfo
$psi.FileName = "dotnet"
$psi.Arguments = "exec `"$dll`""
$psi.UseShellExecute = $false
$psi.CreateNoWindow = $true
[void][System.Diagnostics.Process]::Start($psi)
