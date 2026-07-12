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
    public void ResolveKillMethodName_UnknownMethod_LogsTheFallbackWarning()
    {
        // Arrange
        var closer = CreateCloser();

        // Act
        closer.ResolveKillMethodName("NO-SUCH-METHOD");

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
    public void SendKeyPressIfForeground_ActivationFails_SendsNoKeystrokeAndLogsTheFailure()
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

        // Assert
        Assert.Equal(0, keystrokes);
        Assert.Contains("Failed to set foreground window", File.ReadAllText(_tempLogger.LogPath));
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
