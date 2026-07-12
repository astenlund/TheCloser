using System.Drawing;
using TheCloser.Shared;

using static TheCloser.NativeMethods;
using static TheCloser.TitleBarClickPosition;

namespace TheCloser.Tests;

public sealed class ForegroundActivatorTests : IDisposable
{
    private static readonly IntPtr OwnerWindow = new(100);
    private static readonly IntPtr TargetWindow = new(200);

    private readonly TempLogger _tempLogger = new();
    private readonly SharedState _sharedState = new(TestNames.UniqueMapName());

    public void Dispose()
    {
        _sharedState.Dispose();
        _tempLogger.Dispose();
    }

    [Fact]
    public void TryActivate_TargetAlreadyForeground_SucceedsWithoutTouchingInputState()
    {
        // Arrange
        var native = new FakeNativeApi { ForegroundWindow = TargetWindow };
        var activator = CreateActivator(native);

        // Act
        var activated = activator.TryActivate(TargetWindow, Left);

        // Assert
        Assert.True(activated);
        Assert.Empty(native.Calls);
    }

    [Fact]
    public void TryActivate_DistinctForegroundOwner_AttachesOwnerThenTargetBeforeSetForegroundAndDetachesInReverse()
    {
        // Arrange
        var native = new FakeNativeApi { ForegroundWindow = OwnerWindow };
        var activator = CreateActivator(native);

        // Act
        var activated = activator.TryActivate(TargetWindow, Left);

        // Assert: the owner attach borrows foreground rights, so it must precede SetForegroundWindow (the Chrome activation fix, 6fbbbc3).
        Assert.True(activated);
        Assert.Equal(new[] { "attach:100", "attach:200", "setForeground:200", "detach:200", "detach:100" }, native.Calls);
    }

    [Fact]
    public void TryActivate_OwnerSharesTargetThread_SkipsTheOwnerAttach()
    {
        // Arrange
        var native = new FakeNativeApi { ForegroundWindow = OwnerWindow, ThreadIdOf = _ => 7u };
        var activator = CreateActivator(native);

        // Act
        activator.TryActivate(TargetWindow, Left);

        // Assert
        Assert.Equal(new[] { "attach:200", "setForeground:200", "detach:200" }, native.Calls);
    }

    [Fact]
    public void TryActivate_NoForegroundWindow_SkipsTheOwnerAttach()
    {
        // Arrange
        var native = new FakeNativeApi();
        var activator = CreateActivator(native);

        // Act
        activator.TryActivate(TargetWindow, Left);

        // Assert
        Assert.Equal(new[] { "attach:200", "setForeground:200", "detach:200" }, native.Calls);
    }

    [Fact]
    public void TryActivate_SetForegroundWindowFails_StillDetachesBothAttachedThreads()
    {
        // Arrange
        var native = new FakeNativeApi { ForegroundWindow = OwnerWindow, SetForegroundSucceeds = false, GetWindowRectSucceeds = false };
        var activator = CreateActivator(native);

        // Act
        var activated = activator.TryActivate(TargetWindow, Left);

        // Assert: the detaches live in a finally, so a failed activation must not leak an attach.
        Assert.False(activated);
        Assert.Equal(new[] { "attach:100", "attach:200", "setForeground:200", "detach:200", "detach:100" }, native.Calls);
    }

    [Fact]
    public void TryActivate_NativeAttempt_WrapsSetForegroundWindowInTheSuppressionScope()
    {
        // Arrange
        var native = new FakeNativeApi { ForegroundWindow = OwnerWindow };
        var activator = new ForegroundActivator(_sharedState, _tempLogger.Logger, native, _ => { }, () =>
        {
            native.Calls.Add("suppression:create");

            return new RecordingDisposable(native.Calls);
        });

        // Act
        activator.TryActivate(TargetWindow, Left);

        // Assert
        Assert.True(native.Calls.IndexOf("suppression:create") < native.Calls.IndexOf("setForeground:200"));
        Assert.True(native.Calls.IndexOf("setForeground:200") < native.Calls.IndexOf("suppression:dispose"));
    }

    [Fact]
    public void PerformedInputAttach_AnyAttachSucceeded_ReadsTrue()
    {
        // Arrange
        var native = new FakeNativeApi { ForegroundWindow = OwnerWindow };
        var activator = CreateActivator(native);

        // Act
        activator.TryActivate(TargetWindow, Left);

        // Assert
        Assert.True(activator.PerformedInputAttach);
    }

    [Fact]
    public void PerformedInputAttach_EveryAttachFailed_ReadsFalse()
    {
        // Arrange
        var native = new FakeNativeApi { ForegroundWindow = OwnerWindow, AttachSucceeds = false };
        var activator = CreateActivator(native);

        // Act
        activator.TryActivate(TargetWindow, Left);

        // Assert
        Assert.False(activator.PerformedInputAttach);
    }

    [Fact]
    public void TryActivate_RootResolvesToZero_ActivatesTheTargetItself()
    {
        // Arrange
        var native = new FakeNativeApi { ForegroundWindow = OwnerWindow, RootOf = _ => IntPtr.Zero };
        var activator = CreateActivator(native);

        // Act
        var activated = activator.TryActivate(TargetWindow, Left);

        // Assert
        Assert.True(activated);
        Assert.Contains("setForeground:200", native.Calls);
    }

    [Fact]
    public void TryActivate_NativeFails_ClicksTheLeftTitleBarOffsetAndRestoresTheCursor()
    {
        // Arrange
        var native = new FakeNativeApi
        {
            ForegroundWindow = OwnerWindow,
            SetForegroundSucceeds = false,
            WindowRect = new RECT { Left = 100, Top = 200, Right = 300, Bottom = 400 },
            CursorPosition = new Point(5, 6)
        };
        var activator = CreateActivator(native);

        // Act
        var activated = activator.TryActivate(TargetWindow, Left);

        // Assert: left click point is (rect.Left + 10, rect.Top + 20); the cursor returns home afterwards.
        Assert.False(activated);
        Assert.Contains("moveCursor:110,220", native.Calls);
        Assert.Contains("sendInput:2", native.Calls);
        Assert.Equal("moveCursor:5,6", native.Calls.Last());
    }

    [Fact]
    public void TryActivate_CenterClickPosition_ClicksTheHorizontalMiddleOfTheTitleBar()
    {
        // Arrange
        var native = new FakeNativeApi
        {
            ForegroundWindow = OwnerWindow,
            SetForegroundSucceeds = false,
            WindowRect = new RECT { Left = 100, Top = 200, Right = 300, Bottom = 400 },
            CursorPosition = new Point(5, 6)
        };
        var activator = CreateActivator(native);

        // Act
        activator.TryActivate(TargetWindow, Center);

        // Assert
        Assert.Contains("moveCursor:200,220", native.Calls);
    }

    [Fact]
    public void TryActivate_CursorPositionUnreadable_SkipsTheClickFallback()
    {
        // Arrange
        var native = new FakeNativeApi
        {
            ForegroundWindow = OwnerWindow,
            SetForegroundSucceeds = false,
            WindowRect = new RECT { Left = 100, Top = 200, Right = 300, Bottom = 400 },
            CursorPositionAvailable = false
        };
        var activator = CreateActivator(native);

        // Act
        var activated = activator.TryActivate(TargetWindow, Left);

        // Assert: without a saved home position the fallback must not move the cursor at all.
        Assert.False(activated);
        Assert.DoesNotContain(native.Calls, call => call.StartsWith("moveCursor:"));
        Assert.Contains("Could not save the cursor position", File.ReadAllText(_tempLogger.LogPath));
    }

    [Fact]
    public void TryActivate_SendInputInjectsFewerEvents_LogsAndStillRestoresTheCursor()
    {
        // Arrange
        var native = new FakeNativeApi
        {
            ForegroundWindow = OwnerWindow,
            SetForegroundSucceeds = false,
            WindowRect = new RECT { Left = 100, Top = 200, Right = 300, Bottom = 400 },
            CursorPosition = new Point(5, 6),
            SendInputSucceeds = false
        };
        var activator = CreateActivator(native);

        // Act
        var activated = activator.TryActivate(TargetWindow, Left);

        // Assert
        Assert.False(activated);
        Assert.Contains("SendInput injected fewer events than requested", File.ReadAllText(_tempLogger.LogPath));
        Assert.Equal("moveCursor:5,6", native.Calls.Last());
    }

    // The default suppression records into a throwaway list so the strict call-sequence
    // assertions stay scoped to input-state calls; the suppression-scope test injects its own
    // factory recording into native.Calls instead.
    private ForegroundActivator CreateActivator(FakeNativeApi native) =>
        new(_sharedState, _tempLogger.Logger, native, _ => { }, () => new RecordingDisposable([]));

    private sealed class RecordingDisposable(List<string> calls) : IDisposable
    {
        public void Dispose() => calls.Add("suppression:dispose");
    }

    // Records ordering-relevant calls (attach/detach/setForeground/moveCursor/sendInput); pure reads
    // are not recorded. SetForegroundWindow moves the fake foreground on success, so the ladder's
    // post-activation IsForeground checks behave like the real desktop.
    private sealed class FakeNativeApi : INativeWindowApi
    {
        public List<string> Calls { get; } = [];

        public IntPtr ForegroundWindow { get; set; }

        public bool AttachSucceeds { get; set; } = true;

        public bool SetForegroundSucceeds { get; set; } = true;

        public bool GetWindowRectSucceeds { get; set; } = true;

        public bool CursorPositionAvailable { get; set; } = true;

        public bool SendInputSucceeds { get; set; } = true;

        public RECT WindowRect { get; set; }

        public Point CursorPosition { get; set; }

        public Func<IntPtr, uint> ThreadIdOf { get; set; } = handle => (uint)handle;

        public Func<IntPtr, IntPtr> RootOf { get; set; } = handle => handle;

        public IntPtr GetRootWindow(IntPtr hWnd) => RootOf(hWnd);

        public IntPtr GetForegroundWindow() => ForegroundWindow;

        public uint GetWindowThreadId(IntPtr hWnd) => ThreadIdOf(hWnd);

        public bool AttachThreadInput(IntPtr hWnd)
        {
            Calls.Add($"attach:{hWnd}");

            return AttachSucceeds;
        }

        public bool DetachThreadInput(IntPtr hWnd)
        {
            Calls.Add($"detach:{hWnd}");

            return true;
        }

        public bool SetForegroundWindow(IntPtr hWnd)
        {
            Calls.Add($"setForeground:{hWnd}");

            if (SetForegroundSucceeds)
            {
                ForegroundWindow = hWnd;
            }

            return SetForegroundSucceeds;
        }

        public bool TryGetWindowRect(IntPtr hWnd, out RECT rect)
        {
            rect = WindowRect;

            return GetWindowRectSucceeds;
        }

        public bool TryGetCursorPosition(out Point position)
        {
            position = CursorPosition;

            return CursorPositionAvailable;
        }

        public bool SetCursorPosition(int x, int y)
        {
            Calls.Add($"moveCursor:{x},{y}");
            CursorPosition = new Point(x, y);

            return true;
        }

        public uint SendInput(INPUT[] inputs)
        {
            Calls.Add($"sendInput:{inputs.Length}");

            return SendInputSucceeds ? (uint)inputs.Length : 0u;
        }
    }
}
