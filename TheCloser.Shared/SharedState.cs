using System.IO.MemoryMappedFiles;

using static TheCloser.Shared.Constants;

namespace TheCloser.Shared;

public sealed class SharedState : IDisposable
{
    private const int TimestampOffset = 0;
    private const int RepairFlagOffset = 8;
    private const int RepairValueOffset = 12;
    private const int RepairClear = 0;
    private const int RepairPending = 1;

    private readonly MemoryMappedFile _mmf;
    private readonly MemoryMappedViewAccessor _accessor;

    public SharedState(string mapName)
    {
        _mmf = MemoryMappedFile.CreateOrOpen(mapName, MemoryMappedFileSize);
        _accessor = _mmf.CreateViewAccessor();
    }

    public void WriteTimestamp(DateTime timestamp) => _accessor.Write(TimestampOffset, timestamp.Ticks);

    public DateTime ReadTimestamp() => new(_accessor.ReadInt64(TimestampOffset), DateTimeKind.Utc);

    public void SetTimeoutRepair(uint originalTimeout)
    {
        // The saved value must be committed before the flag so a kill between the stores can never publish a pending flag with an unwritten value.
        _accessor.Write(RepairValueOffset, originalTimeout);
        _accessor.Write(RepairFlagOffset, RepairPending);
    }

    // Clears only the flag; the saved value must stay readable so a concurrent double-restore stays idempotent.
    public void ClearTimeoutRepair() => _accessor.Write(RepairFlagOffset, RepairClear);

    public bool TryReadTimeoutRepair(out uint originalTimeout)
    {
        originalTimeout = _accessor.ReadUInt32(RepairValueOffset);

        return _accessor.ReadInt32(RepairFlagOffset) == RepairPending;
    }

    public void Dispose()
    {
        _accessor.Dispose();
        _mmf.Dispose();
    }
}
