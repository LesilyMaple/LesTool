' Hidden launcher for LesTool on machines where .NET 10 apphosts fail the CET check
' (unpatched Win10). Routes through the muxer: dotnet exec <app>.dll
' The "0" window style keeps the dotnet console invisible. ASCII-only on purpose:
' Windows PowerShell/cscript read BOM-less files as ANSI on zh-CN systems.
Set fso = CreateObject("Scripting.FileSystemObject")
root = fso.GetParentFolderName(fso.GetParentFolderName(WScript.ScriptFullName))
dll = root & "\src\LesTool\bin\Release\net10.0-windows\win-x64\LesTool.dll"

If Not fso.FileExists(dll) Then
  MsgBox "LesTool.dll not found (build first: dotnet build -c Release):" & vbCrLf & dll, 48, "LesTool"
  WScript.Quit 1
End If

CreateObject("WScript.Shell").Run "dotnet exec """ & dll & """", 0, False
