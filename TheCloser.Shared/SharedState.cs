using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;

using static TheCloser.Shared.Constants;

namespace TheCloser.Shared;

public class SharedState
{
    private const int RepairFlagOffset = 8;
    private const int RepairValueOffset = 12;
    private const int RepairClear = 0;
    private const int RepairPending = 1;

    private readonly string _mapName;

    public SharedState(string mapName)
    {
        _mapName = mapName;
    }

    public MemoryMappedFile Pin() => MemoryMappedFile.CreateOrOpen(_mapName, MemoryMappedFileSize);

    public void WriteTimestamp(DateTime timestamp)
    {
        using var mmf = MemoryMappedFile.CreateOrOpen(_mapName, MemoryMappedFileSize);
        using var accessor = mmf.CreateViewAccessor();
        var buffer = BitConverter.GetBytes(timestamp.Ticks);
        accessor.WriteArray(0, buffer, 0, buffer.Length);
    }

    public DateTime ReadTimestamp()
    {
        using var mmf = MemoryMappedFile.CreateOrOpen(_mapName, MemoryMappedFileSize);
        using var accessor = mmf.CreateViewAccessor();
        var buffer = new byte[Marshal.SizeOf<long>()];
        accessor.ReadArray(0, buffer, 0, buffer.Length);
        var ticks = BitConverter.ToInt64(buffer, 0);

        return new DateTime(ticks, DateTimeKind.Utc);
    }

    public void SetTimeoutRepair(uint originalTimeout)
    {
        using var mmf = MemoryMappedFile.CreateOrOpen(_mapName, MemoryMappedFileSize);
        using var accessor = mmf.CreateViewAccessor();
        accessor.Write(RepairValueOffset, originalTimeout);
        accessor.Write(RepairFlagOffset, RepairPending);
    }

    public void ClearTimeoutRepair()
    {
        using var mmf = MemoryMappedFile.CreateOrOpen(_mapName, MemoryMappedFileSize);
        using var accessor = mmf.CreateViewAccessor();
        accessor.Write(RepairFlagOffset, RepairClear);
    }

    public bool TryReadTimeoutRepair(out uint originalTimeout)
    {
        using var mmf = MemoryMappedFile.CreateOrOpen(_mapName, MemoryMappedFileSize);
        using var accessor = mmf.CreateViewAccessor();
        var flag = accessor.ReadInt32(RepairFlagOffset);
        originalTimeout = accessor.ReadUInt32(RepairValueOffset);

        return flag == RepairPending;
    }
}
