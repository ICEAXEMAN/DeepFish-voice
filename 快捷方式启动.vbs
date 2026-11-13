' Desktop shortcut launcher for the FishPet live2d pet (da fei yu).
' 1. If she is running: ask her to come out again (POST /call/show with the token from api.json).
'    If she is not running: start bin\FishPet.exe.
' 2. If the tray console (FishTray.exe) is not running, start it too, so the console icon is back.
' No window is shown: run this with wscript.exe from the shortcut.
Option Explicit

Dim fso, shell, root, apiFile, exePath, trayPath, text, url, token, ok

Set fso = CreateObject("Scripting.FileSystemObject")
Set shell = CreateObject("WScript.Shell")

root = fso.GetParentFolderName(WScript.ScriptFullName)
apiFile = fso.BuildPath(root, "api.json")
exePath = fso.BuildPath(fso.BuildPath(root, "bin"), "FishPet.exe")
trayPath = fso.BuildPath(fso.BuildPath(fso.BuildPath(root, "tray"), "app"), "FishTray.exe")

ok = False
If fso.FileExists(apiFile) Then
    On Error Resume Next
    text = fso.OpenTextFile(apiFile, 1).ReadAll
    On Error GoTo 0
    url = JsonValue(text, "url")
    token = JsonValue(text, "token")
    If Len(url) > 0 And Len(token) > 0 Then ok = AskShow(url, token)
End If

If Not ok And fso.FileExists(exePath) Then
    shell.CurrentDirectory = fso.GetParentFolderName(exePath)
    shell.Run """" & exePath & """", 0, False
End If

If fso.FileExists(trayPath) And Not ProcessRunning("FishTray.exe") Then
    shell.CurrentDirectory = fso.GetParentFolderName(trayPath)
    shell.Run """" & trayPath & """", 0, False
End If

Function JsonValue(blob, key)
    Dim re
    Set re = New RegExp
    re.Pattern = """" & key & """\s*:\s*""([^""]*)"""
    If re.Test(blob) Then
        JsonValue = re.Execute(blob)(0).SubMatches(0)
    Else
        JsonValue = ""
    End If
End Function

Function AskShow(baseUrl, authToken)
    Dim http, done
    done = False
    On Error Resume Next
    Set http = CreateObject("MSXML2.XMLHTTP.6.0")
    If Err.Number <> 0 Then
        Err.Clear
        Set http = CreateObject("Microsoft.XMLHTTP")
    End If
    If Err.Number = 0 Then
        Err.Clear
        ' Optional: some MSXML builds lack setTimeouts (error 438). Clear it so the
        ' successful request below is not judged as a failure.
        http.setTimeouts 1500, 1500, 3000, 4000
        Err.Clear
        http.Open "POST", baseUrl & "/call/show", False
        If Err.Number = 0 Then
            http.setRequestHeader "Authorization", "Bearer " & authToken
            http.setRequestHeader "Content-Type", "application/json"
            http.Send "{}"
            If Err.Number = 0 Then
                If http.Status = 200 Then done = True
            End If
        End If
    End If
    On Error GoTo 0
    AskShow = done
End Function

Function ProcessRunning(name)
    Dim wmi, list, running
    running = False
    On Error Resume Next
    Set wmi = GetObject("winmgmts:\\.\root\cimv2")
    If Err.Number = 0 Then
        Set list = wmi.ExecQuery("SELECT ProcessId FROM Win32_Process WHERE Name='" & name & "'")
        If Err.Number = 0 Then running = (list.Count > 0)
    End If
    On Error GoTo 0
    ProcessRunning = running
End Function
