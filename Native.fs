module Native

    open System
    open System.Drawing
    open System.Runtime.InteropServices

    type WindowNotification =
        | WM_DESTROY=0x0002u
        | WM_CLOSE=0x0010u
        | WM_QUIT=0x0012u

    [<StructLayout(LayoutKind.Sequential)>]
    type POINT =
        struct
            val X : int
            val Y : int
            new(x : int, y : int) = { X = x; Y = y }
        end

    /// <summary>
    ///     Brings the thread that created the specified window into the foreground and activates the window. Keyboard input is
    ///     directed to the window, and various visual cues are changed for the user. The system assigns a slightly higher
    ///     priority to the thread that created the foreground window than it does to other threads.
    ///     <para>See for https://msdn.microsoft.com/en-us/library/windows/desktop/ms633539%28v=vs.85%29.aspx more information.</para>
    /// </summary>
    /// <param name="hWnd">
    ///     C++ ( hWnd [in]. Type: HWND )<br />A handle to the window that should be activated and brought to the foreground.
    /// </param>
    /// <returns>
    ///     <c>true</c> or nonzero if the window was brought to the foreground, <c>false</c> or zero If the window was not
    ///     brought to the foreground.
    /// </returns>
    /// <remarks>
    ///     The system restricts which processes can set the foreground window. A process can set the foreground window only if
    ///     one of the following conditions is true:
    ///     <list type="bullet">
    ///     <listheader>
    ///         <term>Conditions</term><description></description>
    ///     </listheader>
    ///     <item>The process is the foreground process.</item>
    ///     <item>The process was started by the foreground process.</item>
    ///     <item>The process received the last input event.</item>
    ///     <item>There is no foreground process.</item>
    ///     <item>The process is being debugged.</item>
    ///     <item>The foreground process is not a Modern Application or the Start Screen.</item>
    ///     <item>The foreground is not locked (see LockSetForegroundWindow).</item>
    ///     <item>The foreground lock time-out has expired (see SPI_GETFOREGROUNDLOCKTIMEOUT in SystemParametersInfo).</item>
    ///     <item>No menus are active.</item>
    ///     </list>
    ///     <para>
    ///     An application cannot force a window to the foreground while the user is working with another window.
    ///     Instead, Windows flashes the taskbar button of the window to notify the user.
    ///     </para>
    ///     <para>
    ///     A process that can set the foreground window can enable another process to set the foreground window by
    ///     calling the AllowSetForegroundWindow function. The process specified by dwProcessId loses the ability to set
    ///     the foreground window the next time the user generates input, unless the input is directed at that process, or
    ///     the next time a process calls AllowSetForegroundWindow, unless that process is specified.
    ///     </para>
    ///     <para>
    ///     The foreground process can disable calls to SetForegroundWindow by calling the LockSetForegroundWindow
    ///     function.
    ///     </para>
    /// </remarks>
    // For Windows Mobile, replace user32.dll with coredll.dll 
    [<DllImport("user32.dll", SetLastError = true)>]
    extern [<MarshalAs(UnmanagedType.Bool)>] bool SetForegroundWindow(IntPtr hWnd);

    /// <summary>
    ///     Retrieves a handle to the foreground window (the window with which the user is currently working). The system
    ///     assigns a slightly higher priority to the thread that creates the foreground window than it does to other threads.
    ///     <para>See https://msdn.microsoft.com/en-us/library/windows/desktop/ms633505%28v=vs.85%29.aspx for more information.</para>
    /// </summary>
    /// <returns>
    ///     C++ ( Type: Type: HWND )<br /> The return value is a handle to the foreground window. The foreground window
    ///     can be NULL in certain circumstances, such as when a window is losing activation.
    /// </returns>
    [<DllImport("user32.dll")>]
    extern IntPtr GetForegroundWindow()

    [<DllImport("user32.dll", SetLastError = true)>]
    extern uint32 GetWindowThreadProcessId(IntPtr hWnd, uint32& lpdwProcessId)

    [<DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)>]
    extern [<MarshalAs(UnmanagedType.Bool)>] bool PostMessage(IntPtr hWnd, uint32 msg, IntPtr wParam, IntPtr lParam)

    [<DllImport("user32.dll", SetLastError = true)>]
    extern [<MarshalAs(UnmanagedType.Bool)>] bool GetCursorPos(POINT& lpPoint)

    [<DllImport("user32.dll", SetLastError = true)>]
    extern IntPtr WindowFromPoint(Point p)

    let getProcessIdFromWindowHandle (hWnd : IntPtr) =
        let mutable lpdwProcessId = uint32 0
        GetWindowThreadProcessId(hWnd, &lpdwProcessId) |> ignore
        int lpdwProcessId

    let postMessage (hWnd : nativeint, message : WindowNotification) =
        PostMessage(hWnd, uint32 message, IntPtr.Zero, IntPtr.Zero) |> ignore

    let getMouseCursorPosition =
        let mutable lpPoint = POINT(-1, -1)
        GetCursorPos(&lpPoint) |> ignore
        Point(lpPoint.X, lpPoint.Y)
