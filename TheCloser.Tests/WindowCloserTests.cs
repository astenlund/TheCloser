using System.Diagnostics;
using GregsStack.InputSimulatorStandard.Native;
using Microsoft.Extensions.Configuration;
using TheCloser.Shared;

namespace TheCloser.Tests;

public sealed class WindowCloserTests : IDisposable
{
    private readonly TempLogger _tempLogger = new();
    private readonly SharedState _sharedState = new(TestNames.UniqueMapName());

    public void Dispose()
    {
        _sharedState.Dispose();
        _tempLogger.Dispose();
    }

    [Theory]
    [InlineData(null, "CTRL-W")]
    [InlineData("NO-SUCH-METHOD", "CTRL-W")]
    [InlineData("ctrl-shift-w", "ctrl-shift-w")]
    [InlineData("WM_CLOSE", "WM_CLOSE")]
    public void ResolveKillMethodName_ResolvesKnownMethodsCaseInsensitivelyAndFallsBackOtherwise(string? configured, string expected)
    {
        // Arrange
        var closer = CreateCloser();

        // Act
        var resolved = closer.ResolveKillMethodName(configured);

        // Assert
        Assert.Equal(expected, resolved);
    }

    [Fact]
    public void ResolveKillMethodName_EmptyString_FallsBackToTheDefault()
    {
        // Arrange
        var closer = CreateCloser();

        // Act
        var resolved = closer.ResolveKillMethodName("");

        // Assert
        Assert.Equal("CTRL-W", resolved);
    }

    [Theory]
    [InlineData("WM_DESTROY")]
    [InlineData("WM_CLOSE")]
    [InlineData("WM_QUIT")]
    [InlineData("SC_CLOSE")]
    [InlineData("ESCAPE")]
    [InlineData("ALT-F4")]
    [InlineData("CTRL-F4")]
    [InlineData("CTRL-W")]
    [InlineData("CTRL-SHIFT-W")]
    public void ResolveKillMethodName_EveryDocumentedMethod_ResolvesVerbatim(string documented)
    {
        // Arrange
        var closer = CreateCloser();

        // Act
        var resolved = closer.ResolveKillMethodName(documented);

        // Assert
        Assert.Equal(documented, resolved);
    }

    [Fact]
    public async Task ResolveKillMethodName_UnknownMethod_LogsTheFallbackWarning()
    {
        // Arrange
        var closer = CreateCloser();

        // Act
        closer.ResolveKillMethodName("NO-SUCH-METHOD");
        await _tempLogger.DrainAsync();

        // Assert
        Assert.Contains("No kill action configured for method 'NO-SUCH-METHOD'", File.ReadAllText(_tempLogger.LogPath));
    }

    [Fact]
    public void SendKeyPressIfForeground_ActivationSucceeds_SleepsTheSettleDelayThenSendsTheKeystroke()
    {
        // Arrange
        var calls = new List<string>();
        var activator = new FakeActivator();
        VirtualKeyCode[]? sentModifiers = null;
        VirtualKeyCode? sentKey = null;
        var closer = new WindowCloser(
            new ConfigurationBuilder().Build(),
            _sharedState,
            _tempLogger.Logger,
            activator,
            (modifiers, key) =>
            {
                calls.Add("keystroke");
                sentModifiers = modifiers;
                sentKey = key;
            },
            delay => calls.Add($"sleep:{delay.TotalMilliseconds}"));

        // Act
        closer.SendKeyPressIfForeground(new IntPtr(42), TitleBarClickPosition.Left, VirtualKeyCode.VK_W, VirtualKeyCode.CONTROL);

        // Assert
        Assert.Equal(new[] { "sleep:50", "keystroke" }, calls);
        Assert.Equal(VirtualKeyCode.VK_W, sentKey);
        Assert.Equal(new[] { VirtualKeyCode.CONTROL }, sentModifiers);
        var activation = Assert.Single(activator.Activations);
        Assert.Equal(new IntPtr(42), activation.Window);
        Assert.Equal(TitleBarClickPosition.Left, activation.ClickPosition);
    }

    [Fact]
    public async Task SendKeyPressIfForeground_ActivationFails_SendsNoKeystrokeAndLogsTheFailure()
    {
        // Arrange
        var keystrokes = 0;
        var closer = new WindowCloser(
            new ConfigurationBuilder().Build(),
            _sharedState,
            _tempLogger.Logger,
            new FakeActivator { ActivateResult = false },
            (_, _) => keystrokes++,
            _ => { });

        // Act
        closer.SendKeyPressIfForeground(new IntPtr(0xAB), TitleBarClickPosition.Left, VirtualKeyCode.VK_W, VirtualKeyCode.CONTROL);
        await _tempLogger.DrainAsync();

        // Assert
        Assert.Equal(0, keystrokes);
        Assert.Contains("Failed to set foreground window", File.ReadAllText(_tempLogger.LogPath));
    }

    [Fact]
    public async Task SendKeyPressIfForeground_SlowKeystrokeInjection_LogsTheDuration()
    {
        // Arrange: SendInput blocks 2 s (observed with a slow low-level keyboard hook); the
        // duration must be attributable from the log.
        var now = 0L;
        var closer = new WindowCloser(
            new ConfigurationBuilder().Build(),
            _sharedState,
            _tempLogger.Logger,
            new FakeActivator(),
            (_, _) => now += Stopwatch.Frequency * 2,
            _ => { },
            () => now);

        // Act
        closer.SendKeyPressIfForeground(new IntPtr(42), TitleBarClickPosition.Left, VirtualKeyCode.VK_W, VirtualKeyCode.CONTROL);
        await _tempLogger.DrainAsync();

        // Assert
        Assert.Contains("Keystroke injection took 2000 ms", File.ReadAllText(_tempLogger.LogPath));
    }

    [Fact]
    public async Task SendKeyPressIfForeground_FastKeystrokeInjection_LogsNothingAboutInjection()
    {
        // Arrange: a normal injection stays under the stall threshold and must not grow the log.
        var closer = new WindowCloser(
            new ConfigurationBuilder().Build(),
            _sharedState,
            _tempLogger.Logger,
            new FakeActivator(),
            (_, _) => { },
            _ => { },
            () => 0L);

        // Act
        closer.SendKeyPressIfForeground(new IntPtr(42), TitleBarClickPosition.Left, VirtualKeyCode.VK_W, VirtualKeyCode.CONTROL);
        await _tempLogger.DrainAsync();

        // Assert: nothing at all was logged, so the file may not even exist.
        var log = File.Exists(_tempLogger.LogPath) ? File.ReadAllText(_tempLogger.LogPath) : string.Empty;
        Assert.DoesNotContain("Keystroke injection", log);
    }

    [Fact]
    public void TryGetProcessName_LiveProcess_ReturnsItsName()
    {
        // Arrange
        using var current = Process.GetCurrentProcess();

        // Act
        var name = WindowCloser.TryGetProcessName(current.Id);

        // Assert
        Assert.Equal(current.ProcessName, name);
    }

    [Fact]
    public void TryGetProcessName_ExitedProcess_ReturnsNull()
    {
        // Arrange: a child that has already exited leaves an id no live process owns.
        using var child = Process.Start(new ProcessStartInfo("cmd.exe", "/c exit") { CreateNoWindow = true, UseShellExecute = false })!;
        child.WaitForExit();

        // Act
        var name = WindowCloser.TryGetProcessName(child.Id);

        // Assert
        Assert.Null(name);
    }

    [Fact]
    public void PerformedInputAttach_ReflectsTheInjectedActivator()
    {
        // Arrange
        var closer = new WindowCloser(
            new ConfigurationBuilder().Build(),
            _sharedState,
            _tempLogger.Logger,
            new FakeActivator { PerformedInputAttach = true },
            (_, _) => { },
            _ => { });

        // Act
        var performed = closer.PerformedInputAttach;

        // Assert
        Assert.True(performed);
    }

    private WindowCloser CreateCloser() => new(new ConfigurationBuilder().Build(), _sharedState, _tempLogger.Logger);

    private sealed class FakeActivator : IForegroundActivator
    {
        public bool ActivateResult { get; init; } = true;

        public bool PerformedInputAttach { get; init; }

        public List<(IntPtr Window, TitleBarClickPosition ClickPosition)> Activations { get; } = [];

        public bool TryActivate(IntPtr targetWindow, TitleBarClickPosition clickPosition)
        {
            Activations.Add((targetWindow, clickPosition));

            return ActivateResult;
        }
    }
}
