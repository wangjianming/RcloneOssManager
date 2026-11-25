Option Explicit

Const ForAppending = 8
Const TristateFalse = 0
Const adTypeText = 2

' Engine resolution mirrors RcloneOssManager RuntimePaths.Load precedence:
' 1) RcloneOssManager.settings.json next to this script  2) %LOCALAPPDATA%\RcloneOssManager\settings.json
' The embedded engine inside the EXE is not reachable from VBScript, so a machine that only
' has the embedded engine must restore through the manager itself. Keep this file ASCII:
' wscript reads non-UTF-16 scripts as ANSI.
Dim fso, shell, scriptDir, logDir, recordPath, rclonePath, configPath, logPath, operationLogPath, settingsPath, lines, line, parts, remoteName, drive, volumeName, command
Set fso = CreateObject("Scripting.FileSystemObject")
Set shell = CreateObject("WScript.Shell")
scriptDir = fso.GetParentFolderName(WScript.ScriptFullName)
recordPath = scriptDir & "\rclone-mounts.txt"
settingsPath = ""
logDir = shell.ExpandEnvironmentStrings("%LOCALAPPDATA%") & "\RcloneOssManager"
If Not fso.FolderExists(logDir) Then
    On Error Resume Next
    fso.CreateFolder logDir
    On Error GoTo 0
End If
logPath = logDir & "\rclone-oss-startup.log"

If Not fso.FileExists(recordPath) Then
    WriteLog "startup skipped: no mount records"
    WScript.Quit 0
End If
If Not ResolveEngine() Then
    WriteLog "startup failed: no usable rclone executable and config (checked sidecar settings and user settings)"
    WScript.Quit 1
End If

lines = Split(ReadUtf8(recordPath), vbLf)

For Each line In lines
    line = Replace(CStr(line), vbCr, "")
    parts = Split(Trim(line), "|")
    If UBound(parts) = 1 Or UBound(parts) = 2 Then
        remoteName = Trim(parts(0))
        drive = UCase(Trim(parts(1)))
        volumeName = remoteName
        If UBound(parts) = 2 Then volumeName = Trim(parts(2))
        If IsValidRemote(remoteName) And IsValidDrive(drive) And IsValidVolumeName(volumeName) Then
            If fso.DriveExists(drive) Then
                WriteLog "startup skipped: drive occupied " & remoteName & " " & drive
            Else
                operationLogPath = scriptDir & "\rclone-" & remoteName & ".log"
                command = Chr(34) & rclonePath & Chr(34) & " --config " & Chr(34) & configPath & Chr(34) & " mount " & remoteName & ": " & drive & " --vfs-cache-mode full --volname " & Chr(34) & volumeName & Chr(34) & " --log-file " & Chr(34) & operationLogPath & Chr(34) & " --log-level INFO"
                ' ServerModtimeRemotes is read from the same settings file as the engine path.
                If NeedsServerModtime(remoteName) Then command = command & " --use-server-modtime"
                shell.Run command, 0, False
                WriteLog "startup launched: " & remoteName & " " & drive
            End If
        ElseIf Len(Trim(line)) > 0 Then
            WriteLog "startup skipped: invalid mount record remote=" & remoteName & " drive=" & drive
        End If
    End If
Next

' Reads the manager path settings; when unset, falls back to rclone.exe/conf beside this script.
' :return: True when both the engine and its config file were located.
Function ResolveEngine()
    Dim candidates, candidate, settings, exe, conf, sidecar, userSettings
    ResolveEngine = False
    sidecar = scriptDir & "\RcloneOssManager.settings.json"
    userSettings = shell.ExpandEnvironmentStrings("%LOCALAPPDATA%") & "\RcloneOssManager\settings.json"
    candidates = Array(sidecar, userSettings)
    For Each candidate In candidates
        If fso.FileExists(candidate) Then
            settings = ReadUtf8(candidate)
            exe = Anchor(JsonValue(settings, "RcloneExecutable"))
            conf = Anchor(JsonValue(settings, "RcloneConfiguration"))
            If fso.FileExists(exe) And fso.FileExists(conf) Then
                rclonePath = fso.GetAbsolutePathName(exe)
                configPath = fso.GetAbsolutePathName(conf)
                settingsPath = candidate
                WriteLog "startup engine resolved from settings " & candidate
                ResolveEngine = True
                Exit Function
            End If
        End If
    Next
    exe = scriptDir & "\rclone.exe"
    conf = scriptDir & "\rclone.conf"
    If fso.FileExists(exe) And fso.FileExists(conf) Then
        rclonePath = fso.GetAbsolutePathName(exe)
        configPath = fso.GetAbsolutePathName(conf)
        WriteLog "startup engine resolved from script directory"
        ResolveEngine = True
    End If
End Function

' Reads ServerModtimeRemotes from the settings JSON; returns False when unset or unreadable.
' :param remoteName: connection identifier.
' :return: True when the connection should be mounted with --use-server-modtime.
Function NeedsServerModtime(remoteName)
    Dim text, matcher, match, items, item
    NeedsServerModtime = False
    If settingsPath = "" Then Exit Function
    On Error Resume Next
    text = ReadUtf8(settingsPath)
    On Error GoTo 0
    Set matcher = CreateObject("VBScript.RegExp")
    matcher.Pattern = """ServerModtimeRemotes""\s*:\s*\[([^\]]*)\]"
    matcher.Global = False
    Set match = matcher.Execute(text)
    If match.Count = 0 Then Exit Function
    items = Split(match(0).Submatches(0), ",")
    For Each item In items
        item = Replace(Trim(item), Chr(34), "")
        If LCase(item) = LCase(remoteName) Then NeedsServerModtime = True
    Next
End Function

' Relative paths belong to the script directory, not the process working directory.
Function Anchor(value)
    Dim first
    Anchor = ""
    If value = "" Then Exit Function
    If Len(value) >= 2 And Mid(value, 2, 1) = ":" Then
        first = UCase(Left(value, 1))
        If first >= "A" And first <= "Z" Then
            Anchor = value
            Exit Function
        End If
    End If
    If Left(value, 2) = "\\" Then
        Anchor = value
        Exit Function
    End If
    Anchor = scriptDir & "\" & value
End Function

' Extracts a quoted string member from the settings JSON and unescapes backslashes.
Function JsonValue(text, key)
    Dim matcher, match, collected, q
    JsonValue = ""
    q = Chr(34)
    Set matcher = CreateObject("VBScript.RegExp")
    matcher.Pattern = q & key & q & "\s*:\s*" & q & "((?:[^" & q & "\\]|\\.)*)" & q
    matcher.Global = False
    Set match = matcher.Execute(text)
    If match.Count > 0 Then
        collected = match(0).Submatches(0)
        collected = Replace(collected, "\\", Chr(1))
        collected = Replace(collected, Chr(92) & Chr(34), Chr(34))
        collected = Replace(collected, Chr(1), Chr(92))
        JsonValue = collected
    End If
End Function

' Reads UTF-8 text without a BOM dependency; callers split records on line feeds.
Function ReadUtf8(path)
    Dim stream
    Set stream = CreateObject("ADODB.Stream")
    stream.Type = adTypeText
    stream.Charset = "utf-8"
    stream.Open
    stream.LoadFromFile path
    ReadUtf8 = stream.ReadText
    stream.Close
End Function

Function IsValidRemote(value)
    Dim matcher
    Set matcher = CreateObject("VBScript.RegExp")
    matcher.Pattern = "^[A-Za-z][A-Za-z0-9_-]*$"
    IsValidRemote = matcher.Test(value)
End Function

Function IsValidDrive(value)
    Dim matcher
    Set matcher = CreateObject("VBScript.RegExp")
    matcher.Pattern = "^[A-Z]:$"
    matcher.IgnoreCase = True
    IsValidDrive = matcher.Test(value)
End Function

Function IsValidVolumeName(value)
    IsValidVolumeName = Len(value) > 0 And InStr(value, "|") = 0 And InStr(value, vbCr) = 0 And InStr(value, vbLf) = 0
End Function

Sub WriteLog(message)
    Dim logFile
    On Error Resume Next
    Set logFile = fso.OpenTextFile(logPath, ForAppending, True, TristateFalse)
    If Err.Number <> 0 Then Err.Clear: Exit Sub
    logFile.WriteLine Year(Now) & "-" & Right("0" & Month(Now), 2) & "-" & Right("0" & Day(Now), 2) & " " & Time & " | " & message
    logFile.Close
    On Error GoTo 0
End Sub
