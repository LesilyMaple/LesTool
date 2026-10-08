@echo off
rem CET shim for .NET 10 native csc.exe crash on Win10 without 2024+ updates.
rem This machine (Win10 19045, last patch 2022-09) fails the CET check built into
rem the native compiler hosts (csc.exe / VBCSCompiler.exe). Route through
rem "dotnet exec csc.dll" instead, which runs fine.
rem Real fix: install Windows Updates, then delete this shim + Directory.Build.props.
for /f "usebackq tokens=* delims=" %%v in (`dotnet --version`) do set "LESTOOL_SDK_VER=%%v"
dotnet "%ProgramFiles%\dotnet\sdk\%LESTOOL_SDK_VER%\Roslyn\bincore\csc.dll" %*
exit /b %ERRORLEVEL%
