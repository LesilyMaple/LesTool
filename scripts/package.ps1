param(
    [ValidateSet("Release", "Debug")][string]$Configuration = "Release",
    [string]$Output = "",
    [switch]$SkipTests
)
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$arguments = @((Join-Path $root "tools/package.py"), "--configuration", $Configuration)
if ($Output) { $arguments += @("--output", $Output) }
if ($SkipTests) { $arguments += "--skip-tests" }
& python @arguments
if ($LASTEXITCODE -ne 0) { throw "Package verification failed ($LASTEXITCODE)" }
