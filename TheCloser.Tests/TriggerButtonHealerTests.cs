using TheCloser.Shared;

using static TheCloser.NativeMethods;

namespace TheCloser.Tests;

public sealed class TriggerButtonHealerTests : IDisposable
{
    private readonly string _loggerName = TestNames.UniqueLoggerName();
    private readonly Logger _logger;

    public TriggerButtonHealerTests()
    {
        _logger = new Logger(_loggerName);
    }

    public void Dispose()
    {
        File.Delete(Path.Combine(Path.GetTempPath(), $"{_loggerName}.log"));
        File.Delete(Path.Combine(Path.GetTempPath(), $"{_loggerName}.log.old"));
    }

    [Fact]
    public void HealStuckButtons_AllButtonsUp_ReturnsWithoutSleepingOrInjecting()
    {
        // Arrange
        var injected = new List<int>();
        var sleeps = 0;
        var healer = new TriggerButtonHealer(_logger, _ => false, injected.Add, _ => sleeps++);

        // Act
        healer.HealStuckButtons();

        // Assert
        Assert.Empty(injected);
        Assert.Equal(0, sleeps);
    }

    [Fact]
    public void HealStuckButtons_ButtonReleasedDuringMonitoring_DoesNotInject()
    {
        // Arrange
        var injected = new List<int>();
        var sleeps = 0;
        var healer = new TriggerButtonHealer(_logger, virtualKey => virtualKey == VK_XBUTTON2 && sleeps < 3, injected.Add, _ => sleeps++);

        // Act
        healer.HealStuckButtons();

        // Assert
        Assert.Empty(injected);
        Assert.Equal(3, sleeps);
    }

    [Fact]
    public void HealStuckButtons_ButtonStuckPastDeadline_InjectsReleaseForOnlyThatButton()
    {
        // Arrange
        var injected = new List<int>();
        var healer = new TriggerButtonHealer(_logger, virtualKey => virtualKey == VK_XBUTTON2, injected.Add, _ => { });

        // Act
        healer.HealStuckButtons();

        // Assert
        var virtualKey = Assert.Single(injected);
        Assert.Equal(VK_XBUTTON2, virtualKey);
    }

    [Fact]
    public void HealStuckButtons_ButtonStuckPastDeadline_LogsTheInjection()
    {
        // Arrange
        var healer = new TriggerButtonHealer(_logger, virtualKey => virtualKey == VK_XBUTTON2, _ => { }, _ => { });

        // Act
        healer.HealStuckButtons();

        // Assert
        var logContent = File.ReadAllText(Path.Combine(Path.GetTempPath(), $"{_loggerName}.log"));
        Assert.Contains("Trigger button 0x06", logContent);
        Assert.Contains("injected its release", logContent);
    }
}
