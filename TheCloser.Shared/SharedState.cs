using System.IO.MemoryMappedFiles;

using static TheCloser.Shared.Constants;

namespace TheCloser.Shared;

public sealed class SharedState : IDisposable
{
    private const int ThrottleTickOffset = 0;
    private const int RepairFlagOffset = 8;
    private const int RepairValueOffset = 12;
    private const int RepairClear = 0;
    private const int RepairPending = 1;

    // Activation payload offsets. Duplicated by hand in TheCloser.ahk (NumPut sites); keep in sync.
    private const int ActivationQpcOffset = 16;
    private const int ActivationButtonOffset = 24;

    private readonly MemoryMappedFile _mmf;
    private readonly MemoryMappedViewAccessor _accessor;

    public SharedState(string mapName)
    {
        _mmf = MemoryMappedFile.CreateOrOpen(mapName, MemoryMappedFileSize);
        _accessor = _mmf.CreateViewAccessor();
    }

    public void WriteThrottleTick(long tick) => _accessor.Write(ThrottleTickOffset, tick);

    public long ReadThrottleTick() => _accessor.ReadInt64(ThrottleTickOffset);

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
        // The flag must be read before the value, mirroring the value-then-flag write order:
        // a pending flag then guarantees the value load observes a committed value.
        var pending = _accessor.ReadInt32(RepairFlagOffset) == RepairPending;

        originalTimeout = _accessor.ReadUInt32(RepairValueOffset);

        return pending;
    }

    public void WriteActivationPayload(long launchQpc, int buttonCode)
    {
        // Values before the event signal: the activation event acts as the payload-ready flag,
        // mirroring the value-before-flag discipline of the repair record above.
        _accessor.Write(ActivationQpcOffset, launchQpc);
        _accessor.Write(ActivationButtonOffset, buttonCode);
    }

    // Consume-once: zeroing after the read keeps a failed later mapping from replaying this
    // press's values as a fresh latency (see the fix design's payload contract).
    public (long LaunchQpc, int ButtonCode) ConsumeActivationPayload()
    {
        var launchQpc = _accessor.ReadInt64(ActivationQpcOffset);
        var buttonCode = _accessor.ReadInt32(ActivationButtonOffset);

        _accessor.Write(ActivationQpcOffset, 0L);
        _accessor.Write(ActivationButtonOffset, 0);

        return (launchQpc, buttonCode);
    }

    public void Dispose()
    {
        _accessor.Dispose();
        _mmf.Dispose();
    }
}
