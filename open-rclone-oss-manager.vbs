Option Explicit

Dim shell, fso, scriptDir, command

Set shell = CreateObject("WScript.Shell")
Set fso = CreateObject("Scripting.FileSystemObject")
scriptDir = fso.GetParentFolderName(WScript.ScriptFullName)
command = Chr(34) & scriptDir & "\RcloneOssManager.exe" & Chr(34)
shell.Run command, 1, False
