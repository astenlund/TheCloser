namespace TheCloser.Tests;

internal static class TestNames
{
    public static string UniqueMapName() => $"TheCloser.Tests.{Guid.NewGuid():N}";

    public static string UniqueMutexName() => $"TheCloser.Tests.Mutex.{Guid.NewGuid():N}";
}
