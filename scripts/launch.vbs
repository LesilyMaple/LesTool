' Hidden launcher for LesTool on machines where .NET 10 apphosts fail the CET check
' (unpatched Win10). Routes through the muxer: dotnet exec <app>.dll
' Chain: wscript (hidden) -> powershell (hidden) -> dotnet with CreateNoWindow.
' The extra hop matters: WScript.Run style 0 sets SW_HIDE in STARTUPINFO, which
' leaks into the WPF first window ("no taskbar icon until minimized" bug).
' PowerShell's ProcessStartInfo.CreateNoWindow uses CREATE_NO_WINDOW instead,
' which suppresses only the console and leaves the window show-state clean.
' ASCII-only on purpose (zh-CN ANSI/GBK decoding of BOM-less scripts).
Set fso = CreateObject("Scripting.FileSystemObject")
root = fso.GetParentFolderName(fso.GetParentFolderName(WScript.ScriptFullName))
ps1 = root & "\scripts\launch.ps1"

If Not fso.FileExists(ps1) Then
  MsgBox "launch.ps1 not found: " & ps1, 48, "LesTool"
  WScript.Quit 1
End If

CreateObject("WScript.Shell").Run _
  "powershell -NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File """ & ps1 & """", 0, False
