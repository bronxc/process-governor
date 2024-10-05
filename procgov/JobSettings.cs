using MessagePack;
using Windows.Win32.System.Threading;

namespace ProcessGovernor;

[MessagePackObject]
public record JobSettings(
    [property: Key(0)] ulong MaxProcessMemory = 0,
    [property: Key(1)] ulong MaxJobMemory = 0,
    [property: Key(2)] ulong MaxWorkingSetSize = 0,
    [property: Key(3)] ulong MinWorkingSetSize = 0,
    [property: Key(4)] ulong CpuAffinityMask = 0,
    [property: Key(5)] uint CpuMaxRate = 0,
    [property: Key(6)] ulong MaxBandwidth = 0,
    [property: Key(7)] uint ProcessUserTimeLimitInMilliseconds = 0,
    [property: Key(8)] uint JobUserTimeLimitInMilliseconds = 0,
    [property: Key(9)] uint ClockTimeLimitInMilliseconds = 0,
    [property: Key(10)] bool PropagateOnChildProcesses = false,
    [property: Key(11)] ushort NumaNode = ushort.MaxValue,
    [property: Key(12)] uint ActiveProcessLimit = 0,
    [property: Key(13)] PriorityClass PriorityClass = PriorityClass.Undefined
);

public enum PriorityClass : uint
{
    Undefined = 0,
    Idle = PROCESS_CREATION_FLAGS.IDLE_PRIORITY_CLASS,
    BelowNormal = PROCESS_CREATION_FLAGS.BELOW_NORMAL_PRIORITY_CLASS,
    Normal = PROCESS_CREATION_FLAGS.NORMAL_PRIORITY_CLASS,
    AboveNormal = PROCESS_CREATION_FLAGS.ABOVE_NORMAL_PRIORITY_CLASS,
    High = PROCESS_CREATION_FLAGS.HIGH_PRIORITY_CLASS,
    Realtime = PROCESS_CREATION_FLAGS.REALTIME_PRIORITY_CLASS,
}

