module Program

    open System.Diagnostics
    open System.IO
    open Common
    open GregsStack.InputSimulatorStandard
    open GregsStack.InputSimulatorStandard.Native
    open Keyboard
    open Microsoft.Extensions.Configuration
    open Native

    [<Literal>]
    let DefaultKillMethod = "CTRL-W"

    let config =
        ConfigurationBuilder()
            .SetBasePath(Path.GetDirectoryName(Process.GetCurrentProcess().MainModule.FileName))
            .AddJsonFile("appsettings.json", true)
            .Build();

    let logFile = Path.Combine(Path.GetTempPath(), "TheCloser.txt")
    
    let initLog =
        match File.Exists(logFile) with
        | true -> File.WriteAllText(logFile, "")
        | false -> File.Create(logFile).Close()
    
    let log msg =
        File.AppendAllText(logFile, msg)

    let logProcName (proc : Process) =
        log proc.ProcessName
        proc

    let logKillMethod (killMethod : string) =
        log (sprintf " -> %s" killMethod)
        killMethod

    let trySetForegroundWindow hWnd =
        match GetForegroundWindow() with
        | x when x = hWnd -> true
        | _ -> SetForegroundWindow(hWnd)

    let trySendKeyPress (hWnd : nativeint, keyCode : VirtualKeyCode, modifierKeyCode : VirtualKeyCode option) =
        match trySetForegroundWindow hWnd with
        | true -> sendKeyPress (keyCode, modifierKeyCode)
        | false -> log " -> NOOP (window activation failed)"

    let killFunctions =
        dict[
            "WM_DESTROY", fun hWnd -> postMessage (hWnd, WindowNotification.WM_DESTROY);
            "WM_CLOSE", fun hWnd -> postMessage (hWnd, WindowNotification.WM_CLOSE);
            "WM_QUIT", fun hWnd -> postMessage (hWnd, WindowNotification.WM_QUIT);
            "ESCAPE", fun hWnd -> trySendKeyPress (hWnd, VirtualKeyCode.ESCAPE, None);
            "ALT-F4", fun hWnd -> trySendKeyPress (hWnd, VirtualKeyCode.F4, Some VirtualKeyCode.LMENU);
            "CTRL-W", fun hWnd -> trySendKeyPress (hWnd, VirtualKeyCode.VK_W, Some VirtualKeyCode.CONTROL);
            "CTRL-F4", fun hWnd -> trySendKeyPress (hWnd, VirtualKeyCode.F4, Some VirtualKeyCode.CONTROL);
        ]

    let getKillMethod (proc : Process) =
        match config.Item proc.ProcessName with
        | null -> DefaultKillMethod
        | killMethod -> killMethod.ToUpperInvariant()

    let getKillFunction killMethod =
        killFunctions.TryGetValue(killMethod)
        |> mapOption

    [<EntryPoint>]
    let main _ =
        initLog
        
        let target =
            getMouseCursorPosition
            |> WindowFromPoint

        let killFunction =
            target
            |> getProcessIdFromWindowHandle
            |> Process.GetProcessById
            |> logProcName
            |> getKillMethod
            |> logKillMethod
            |> getKillFunction

        match killFunction with
        | Some kill -> kill target
        | None -> ()

        0
