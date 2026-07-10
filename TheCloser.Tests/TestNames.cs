namespace TheCloser.Tests;

internal static class TestNames
{
    public static string UniqueMapName() => UniqueName();

    public static string UniqueMutexName() => $"TheCloser.Tests.Mutex.{Guid.NewGuid():N}";

    // Map names live in the kernel object namespace and logger names in %TEMP%, so sharing the pattern cannot collide.
    public static string UniqueLoggerName() => UniqueName();

    private static string UniqueName() => $"TheCloser.Tests.{Guid.NewGuid():N}";
}
