#SingleInstance Force

; ==== Hand-synchronized copies of TheCloser.Shared/Constants.cs values (AutoHotkey cannot
; ==== consume C#): kernel object names, payload MMF offsets (SharedState.cs), button code.
global DaemonMutexName := "TheCloserDaemonMutex"
global ActivationEventName := "TheCloserActivationEvent"
global DaemonExitEventName := "TheCloserDaemonExitEvent"
global SharedStateName := "TheCloserSharedState"
global ActivationQpcOffset := 16
global ActivationButtonOffset := 24
global TriggerButtonXButton2 := 1

; ==== Auto-execute: replace any running daemon with the freshly deployed binary (upgrade
; transition, and any unelevated daemon left by manual testing). The exit event is signaled
; directly so #SingleInstance Force cannot orphan a child stopper that later kills the fresh
; daemon. Both mutex polls are bounded at 5000 ms (above the ~2 s drain and the measured
; 0.7-2.5 s process-creation window). Every branch ends with no handle held; a launch failure
; is silent (headless at logon) and leaves the degraded mode, whose per-press fallback attempts
; best-effort recovery.
; ==== This replacement is also the whole answer to downward mixed elevation (an elevated
; script signalling an unelevated daemon, which OpenEvent cannot distinguish and which would
; silently fail to close elevated windows). Detecting that per press is an explicit spec
; anti-goal: it arises only from manually starting a daemon in a deployed chain, and the
; stop-then-start above replaces such a daemon at the next script start. Add no per-press
; elevation probe. Upward (unelevated script, elevated daemon) needs nothing either: OpenEvent
; fails with access denied and the press takes the fallback.
Loop, 2 {
    if (!SignalDaemonExit())
        Break
    if (DaemonMutexGone(5000)) {
        Run, "%A_ScriptDir%\TheCloser.Daemon.exe" --start, %A_ScriptDir%, UseErrorLevel
        if (ErrorLevel != "ERROR")
            DaemonMutexAppeared(5000)  ; Non-diagnostic: expiry leaves degraded mode; a late daemon may still publish.
        Break
    }
}
Return

; Signals synchronously in this script process. A missing event means no published daemon needs
; a stop; the mutex poll below still detects any contradictory or mixed-version state.
SignalDaemonExit() {
    global DaemonExitEventName
    handle := DllCall("OpenEvent", "UInt", 0x0002, "Int", 0, "Str", DaemonExitEventName, "Ptr") ; EVENT_MODIFY_STATE
    if (!handle)
        Return 1
    signaled := DllCall("SetEvent", "Ptr", handle)
    DllCall("CloseHandle", "Ptr", handle)
    Return signaled
}

; Polls until the daemon mutex name no longer exists. Opens the mutex only to test existence
; and closes the handle before sleeping (a held handle would pin the name past daemon exit).
DaemonMutexGone(timeoutMs) {
    Return WaitForDaemonMutex(0, timeoutMs)
}

DaemonMutexAppeared(timeoutMs) {
    Return WaitForDaemonMutex(1, timeoutMs)
}

WaitForDaemonMutex(expectedPresent, timeoutMs) {
    global DaemonMutexName
    start := DllCall("GetTickCount64", "Int64")
    Loop {
        handle := DllCall("OpenMutex", "UInt", 0x00100000, "Int", 0, "Str", DaemonMutexName, "Ptr") ; SYNCHRONIZE
        present := handle != 0
        if (handle)
            DllCall("CloseHandle", "Ptr", handle)
        if (present = expectedPresent)
            Return 1
        if (DllCall("GetTickCount64", "Int64") - start >= timeoutMs)
            Return 0
        Sleep, 100
    }
}

XButton2:: ; Mouse5 is XButton2 in AHK
DllCall("QueryPerformanceCounter", "Int64*", LaunchQpc)
; EVENT_MODIFY_STATE = 0x0002. Open per press, never cached: the name existing is the exact
; daemon-alive signal (see the fix design's daemon-down detection).
hEvent := DllCall("OpenEvent", "UInt", 0x0002, "Int", 0, "Str", ActivationEventName, "Ptr")
if (!hEvent) {
    ; Fallback: today's slow-but-working path; may start a daemon for a later press.
    ; No UseErrorLevel here, deliberately: the auto-execute suppresses its launch-error dialog
    ; because a modal at logon would block a headless sequence, and the spec states that
    ; rationale does not transfer to a press an interactive user is present for. Keeping today's
    ; modal surface means the fallback's failure exposure is never worse than shipped.
    Run, "%A_ScriptDir%\TheCloser.exe", %A_ScriptDir%
    Return
}
; FILE_MAP_WRITE = 0x0002. Payload before signal (value-before-flag); offsets mirror
; SharedState.cs. On mapping failure, still signal: latency is unavailable only when no earlier
; payload remains pending.
hMap := DllCall("OpenFileMapping", "UInt", 0x0002, "Int", 0, "Str", SharedStateName, "Ptr")
if (hMap) {
    pView := DllCall("MapViewOfFile", "Ptr", hMap, "UInt", 0x0002, "UInt", 0, "UInt", 0, "UPtr", 0, "Ptr")
    if (pView) {
        ; The payload carries a timestamp and a button code and nothing else. Press-time cursor
        ; position transmission is an explicit spec non-goal: the daemon samples the cursor when
        ; it handles the press, which is the user's latest intent, where a press-time position
        ; would aim a deferred close at a spot the user has already left.
        NumPut(LaunchQpc, pView + ActivationQpcOffset, 0, "Int64")
        NumPut(TriggerButtonXButton2, pView + ActivationButtonOffset, 0, "Int")
        DllCall("UnmapViewOfFile", "Ptr", pView)
    }
    DllCall("CloseHandle", "Ptr", hMap)
}
DllCall("SetEvent", "Ptr", hEvent)
DllCall("CloseHandle", "Ptr", hEvent)
Return
