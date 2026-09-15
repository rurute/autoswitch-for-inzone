Option Explicit

' Launch INZONE Auto Switch without ever showing a PowerShell console.
' Invoked by Task Scheduler as: wscript.exe //B //Nologo launch-hidden.vbs
'
' Comments are deliberately ASCII-only. This file has no BOM, so wscript.exe
' reads it as ANSI; non-ASCII bytes here would be misinterpreted.

Dim shell, scriptDir, command
Set shell = CreateObject("WScript.Shell")

scriptDir = Left(WScript.ScriptFullName, InStrRev(WScript.ScriptFullName, "\"))
command = "powershell.exe -NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File """ & scriptDir & "INZONE-AutoSwitch.ps1"""

' Run(command, 0, False): window style 0 = hidden, wait = False
shell.Run command, 0, False
