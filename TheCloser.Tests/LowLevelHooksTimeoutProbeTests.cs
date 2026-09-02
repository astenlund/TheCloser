using TheCloser.Shared;
using Xunit;

namespace TheCloser.Tests;

public class LowLevelHooksTimeoutProbeTests
{
    [Fact]
    public void Describe_ValueAbsent_ReportsTheWindowsDefault()
    {
        // Arrange
        static object? ReadValue() => null;

        // Act
        var description = LowLevelHooksTimeoutProbe.Describe(ReadValue);

        // Assert
        Assert.Equal("LowLevelHooksTimeout: not set (Windows default applies).", description);
    }

    [Fact]
    public void Describe_ValuePresent_ReportsItInMilliseconds()
    {
        // Arrange
        static object? ReadValue() => 2000;

        // Act
        var description = LowLevelHooksTimeoutProbe.Describe(ReadValue);

        // Assert
        Assert.Equal("LowLevelHooksTimeout: 2000 ms.", description);
    }

    [Fact]
    public void Describe_ReadThrows_ReportsUnreadableInsteadOfThrowing()
    {
        // Arrange: a startup diagnostic must never take the daemon down.
        static object? ReadValue() => throw new UnauthorizedAccessException();

        // Act
        var description = LowLevelHooksTimeoutProbe.Describe(ReadValue);

        // Assert
        Assert.Equal("LowLevelHooksTimeout: unreadable (UnauthorizedAccessException).", description);
    }

    [Fact]
    public void Describe_HighDword_PrintsUnsigned()
    {
        // Arrange: REG_DWORD arrives as a signed int.
        static object? ReadValue() => unchecked((int)0x80000000);

        // Act
        var description = LowLevelHooksTimeoutProbe.Describe(ReadValue);

        // Assert
        Assert.Equal("LowLevelHooksTimeout: 2147483648 ms.", description);
    }

    [Fact]
    public void Describe_RealRegistry_ReadsTheUsersDesktopKey()
    {
        // Act: HKCU\Control Panel\Desktop is always readable by its own user, so the real path
        // must never land in the unreadable branch.
        var description = LowLevelHooksTimeoutProbe.Describe();

        // Assert
        Assert.StartsWith("LowLevelHooksTimeout: ", description);
        Assert.DoesNotContain("unreadable", description);
    }
}
