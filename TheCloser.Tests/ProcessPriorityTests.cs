using System.Diagnostics;
using TheCloser.Shared;
using Xunit;

namespace TheCloser.Tests;

// These tests change the test host's own priority class and restore it; they never touch another process.
public class ProcessPriorityTests
{
    [Fact]
    public void EnsureAtLeastNormal_BelowNormal_RaisesToNormalAndReportsIt()
    {
        // Arrange
        using var current = Process.GetCurrentProcess();
        var original = current.PriorityClass;

        try
        {
            current.PriorityClass = ProcessPriorityClass.BelowNormal;

            // Act
            var raised = ProcessPriority.EnsureAtLeastNormal(current);

            // Assert
            Assert.True(raised);
            Assert.Equal(ProcessPriorityClass.Normal, current.PriorityClass);
        }
        finally
        {
            current.PriorityClass = original;
        }
    }

    [Fact]
    public void EnsureCurrentAtLeastNormal_BelowNormal_RaisesAndLogs()
    {
        // Arrange
        using var current = Process.GetCurrentProcess();
        var original = current.PriorityClass;
        var lines = new List<string>();

        try
        {
            current.PriorityClass = ProcessPriorityClass.BelowNormal;

            // Act
            ProcessPriority.EnsureCurrentAtLeastNormal(lines.Add);

            // Assert: read the class back through a fresh handle so the cached value cannot mask the OS state.
            using var fresh = Process.GetCurrentProcess();
            Assert.Equal(ProcessPriorityClass.Normal, fresh.PriorityClass);
            Assert.Single(lines, l => l.StartsWith("Raised the process priority class", StringComparison.Ordinal));
        }
        finally
        {
            current.PriorityClass = original;
        }
    }

    [Fact]
    public void EnsureCurrentAtLeastNormal_UnreadableClass_LogsAndDoesNotThrow()
    {
        // Arrange: a Process whose process has exited throws on PriorityClass; the startup form must
        // swallow that and say so, since the raise is optional and startup is not.
        using var child = Process.Start(new ProcessStartInfo("cmd.exe", "/c exit") { CreateNoWindow = true, UseShellExecute = false })!;
        child.WaitForExit();
        var lines = new List<string>();

        // Act
        ProcessPriority.EnsureCurrentAtLeastNormal(lines.Add, () => child);

        // Assert
        Assert.Single(lines, l => l.StartsWith("Could not adjust the process priority class", StringComparison.Ordinal));
    }

    [Fact]
    public void EnsureAtLeastNormal_AboveNormal_LeavesItAlone()
    {
        // Arrange: a deliberately higher class must never be lowered.
        using var current = Process.GetCurrentProcess();
        var original = current.PriorityClass;

        try
        {
            current.PriorityClass = ProcessPriorityClass.AboveNormal;

            // Act
            var raised = ProcessPriority.EnsureAtLeastNormal(current);

            // Assert
            Assert.False(raised);
            Assert.Equal(ProcessPriorityClass.AboveNormal, current.PriorityClass);
        }
        finally
        {
            current.PriorityClass = original;
        }
    }

    [Fact]
    public void EnsureAtLeastNormal_Normal_ReportsNoChange()
    {
        // Arrange
        using var current = Process.GetCurrentProcess();
        var original = current.PriorityClass;

        try
        {
            current.PriorityClass = ProcessPriorityClass.Normal;

            // Act
            var raised = ProcessPriority.EnsureAtLeastNormal(current);

            // Assert
            Assert.False(raised);
            Assert.Equal(ProcessPriorityClass.Normal, current.PriorityClass);
        }
        finally
        {
            current.PriorityClass = original;
        }
    }
}
