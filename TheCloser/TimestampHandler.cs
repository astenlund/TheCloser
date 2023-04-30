using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;
using TheCloser.Shared;
using static TheCloser.Shared.Constants;

namespace TheCloser;

internal static class TimestampHandler
{
    private static readonly Logger Logger = Logger.Create(Program.AssemblyName);

    internal static void WriteTimestamp(DateTime timestamp)
    {
        using var mmf = MemoryMappedFile.CreateOrOpen(MemoryMappedFileName, MemoryMappedFileSize);
        using var accessor = mmf.CreateViewAccessor();
        var buffer = BitConverter.GetBytes(timestamp.Ticks);
        accessor.WriteArray(0, buffer, 0, buffer.Length);

        Logger.Log($"Timestamp: {timestamp:O} -> file");
    }

    internal static DateTime ReadTimestamp()
    {
        using var mmf = MemoryMappedFile.CreateOrOpen(MemoryMappedFileName, MemoryMappedFileSize);
        using var accessor = mmf.CreateViewAccessor();
        var buffer = new byte[Marshal.SizeOf<long>()];
        accessor.ReadArray(0, buffer, 0, buffer.Length);
        var ticks = BitConverter.ToInt64(buffer, 0);
        var timestamp = new DateTime(ticks, DateTimeKind.Utc);

        Logger.Log($"Timestamp: {timestamp:O} <- file");

        return timestamp;
    }
}
