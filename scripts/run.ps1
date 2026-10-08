# Dev launcher: builds Release and runs LesTool via "dotnet exec".
# Why not run LesTool.exe directly? On this dev box (Win10 19045 without
# 2024+ Windows updates) every .NET 10 apphost fail-fasts at startup with
# "Your Windows doesn't fully support CET". The muxer path works fine.
# After installing Windows Updates, LesTool.exe will launch normally and
# this script becomes unnecessary.
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$proj = Join-Path $root "src\LesTool\LesTool.csproj"
$outDir = Join-Path $root "src\LesTool\bin\Release\net10.0-windows\win-x64"

dotnet build $proj -c Release | Out-Host
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

Push-Location $outDir
try {
    dotnet exec (Join-Path $outDir "LesTool.dll")
} finally {
    Pop-Location
}
