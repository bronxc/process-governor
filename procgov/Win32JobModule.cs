using System.Diagnostics;
using System.Runtime.InteropServices;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Security;
using Windows.Win32.System.JobObjects;
using System.ComponentModel;
using Windows.Win32.System.SystemInformation;
using Windows.Win32.Storage.FileSystem;
using static ProcessGovernor.NtApi;

namespace ProcessGovernor;

sealed class Win32Job(SafeHandle jobHandle, string jobName) : IDisposable
{
    public SafeHandle Handle => jobHandle;

    public nuint NativeHandle => (nuint)jobHandle.DangerousGetHandle();

    public string Name => jobName;

    public void Dispose()
    {
        jobHandle.Dispose();
    }
}

static class Win32JobModule
{
    private static readonly TraceSource logger = Program.Logger;

    public static string GetNewJobName()
    {
        return $"procgov-{Guid.NewGuid():D}";
    }

    public static unsafe Win32Job CreateJob(string jobName)
    {
        var securityAttributes = new SECURITY_ATTRIBUTES();
        securityAttributes.nLength = (uint)Marshal.SizeOf(securityAttributes);

        var hJob = CheckWin32Result(PInvoke.CreateJobObject(securityAttributes, $"Local\\{jobName}"));
        var job = new Win32Job(hJob, jobName);

        return job;
    }

    public static unsafe Win32Job OpenJob(string jobName)
    {
        var jobHandle = PInvoke.OpenJobObject(PInvoke.JOB_OBJECT_QUERY | PInvoke.JOB_OBJECT_SET_ATTRIBUTES |
            PInvoke.JOB_OBJECT_TERMINATE | PInvoke.JOB_OBJECT_ASSIGN_PROCESS | (uint)FILE_ACCESS_RIGHTS.SYNCHRONIZE,
            false, $"Local\\{jobName}");
        if (jobHandle.IsInvalid)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }
        return new Win32Job(jobHandle, jobName);
    }

    public static void AssignProcess(Win32Job job, SafeHandle processHandle)
    {
        CheckWin32Result(PInvoke.AssignProcessToJobObject(job.Handle, processHandle));
    }

    public static unsafe int AssignIOCompletionPort(Win32Job job, SafeHandle iocp)
    {
        var assocInfo = new JOBOBJECT_ASSOCIATE_COMPLETION_PORT
        {
            CompletionKey = (void*)job.NativeHandle,
            CompletionPort = new HANDLE(iocp.DangerousGetHandle())
        };
        uint size = (uint)Marshal.SizeOf(assocInfo);
        if (!PInvoke.SetInformationJobObject(job.Handle, JOBOBJECTINFOCLASS.JobObjectAssociateCompletionPortInformation, &assocInfo, size))
        {
            return Marshal.GetLastWin32Error();
        }
        return 0;
    }

    public static void SetLimits(Win32Job job, JobSettings session)
    {
        if (session.NumaNode != ushort.MaxValue)
        {
            CheckWin32Result(PInvoke.GetNumaHighestNodeNumber(out var highestNodeNumber));
            if (session.NumaNode > highestNodeNumber)
            {
                throw new ArgumentException($"incorrect NUMA node number (the highest accepted number is {highestNodeNumber})");
            }
        }

        ulong systemOrProcessorGroupAffinityMask = GetSystemOrProcessorGroupAffinity();

        SetBasicLimits(job, session);
        SetMaxCpuRate(job, session, systemOrProcessorGroupAffinityMask);
        SetNumaAffinity(job, session, systemOrProcessorGroupAffinityMask);
        SetMaxBandwith(job, session);


        static ulong GetSystemOrProcessorGroupAffinity()
        {
            nuint processAffinityMask = 0, systemAffinityMask = 0;
            unsafe
            {
                CheckWin32Result(PInvoke.GetProcessAffinityMask(
                    PInvoke.GetCurrentProcess(), &processAffinityMask, &systemAffinityMask));
                if (systemAffinityMask == 0 && processAffinityMask == 0)
                {
                    logger.TraceEvent(TraceEventType.Warning, 0, "There is more than 1 processor group in the system. " +
                        "Procgov will not able to set the process affinity.");
                }
            }
            return systemAffinityMask;
        }
    }

    // Process affinity is updated in the SetNumaAffinity method - updating through basic limits
    // could fail if Numa node was previously set (issue #46)
    private static unsafe void SetBasicLimits(Win32Job job, JobSettings session)
    {
        var limitInfo = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION();
        var size = (uint)Marshal.SizeOf(limitInfo);
        var length = 0u;
        CheckWin32Result(PInvoke.QueryInformationJobObject(job.Handle,
            JOBOBJECTINFOCLASS.JobObjectExtendedLimitInformation, &limitInfo, size, &length));
        Debug.Assert(length == size);

        JOB_OBJECT_LIMIT flags = limitInfo.BasicLimitInformation.LimitFlags & ~JOB_OBJECT_LIMIT.JOB_OBJECT_LIMIT_AFFINITY;

        if (flags.HasFlag(JOB_OBJECT_LIMIT.JOB_OBJECT_LIMIT_SILENT_BREAKAWAY_OK) || !session.PropagateOnChildProcesses)
        {
            flags |= JOB_OBJECT_LIMIT.JOB_OBJECT_LIMIT_SILENT_BREAKAWAY_OK;
        }

        if (session.MaxProcessMemory > 0)
        {
            flags |= JOB_OBJECT_LIMIT.JOB_OBJECT_LIMIT_PROCESS_MEMORY;
            limitInfo.ProcessMemoryLimit = checked((nuint)session.MaxProcessMemory);
        }

        if (session.MaxJobMemory > 0)
        {
            flags |= JOB_OBJECT_LIMIT.JOB_OBJECT_LIMIT_JOB_MEMORY;
            limitInfo.JobMemoryLimit = checked((nuint)session.MaxJobMemory);
        }

        if (session.MaxWorkingSetSize > 0)
        {
            flags |= JOB_OBJECT_LIMIT.JOB_OBJECT_LIMIT_WORKINGSET;
            limitInfo.BasicLimitInformation.MaximumWorkingSetSize = checked((nuint)session.MaxWorkingSetSize);
            limitInfo.BasicLimitInformation.MinimumWorkingSetSize = checked((nuint)session.MinWorkingSetSize);
        }

        if (session.ProcessUserTimeLimitInMilliseconds > 0)
        {
            flags |= JOB_OBJECT_LIMIT.JOB_OBJECT_LIMIT_PROCESS_TIME;
            limitInfo.BasicLimitInformation.PerProcessUserTimeLimit = 10_000 * session.ProcessUserTimeLimitInMilliseconds; // in 100ns
        }

        if (session.JobUserTimeLimitInMilliseconds > 0)
        {
            flags |= JOB_OBJECT_LIMIT.JOB_OBJECT_LIMIT_JOB_TIME;
            limitInfo.BasicLimitInformation.PerJobUserTimeLimit = 10_000 * session.JobUserTimeLimitInMilliseconds; // in 100ns
        }

        if (session.ActiveProcessLimit > 0)
        {
            flags |= JOB_OBJECT_LIMIT.JOB_OBJECT_LIMIT_ACTIVE_PROCESS;
            limitInfo.BasicLimitInformation.ActiveProcessLimit = session.ActiveProcessLimit;
        }

        if (session.PriorityClass != PriorityClass.Undefined)
        {
            if (session.PriorityClass == PriorityClass.AboveNormal || session.PriorityClass == PriorityClass.High ||
                session.PriorityClass == PriorityClass.Realtime)
            {
                // we need to acquire the SE_INC_BASE_PRIORITY_NAME privilege to set the priority class to AboveNormal, High or Realtime
                AccountPrivilegeModule.EnableProcessPrivileges(PInvoke.GetCurrentProcess_SafeHandle(), [("SeIncreaseBasePriorityPrivilege", true)]);
            }

            flags |= JOB_OBJECT_LIMIT.JOB_OBJECT_LIMIT_PRIORITY_CLASS;
            Debug.Assert(Enum.IsDefined(session.PriorityClass));
            limitInfo.BasicLimitInformation.PriorityClass = (uint)session.PriorityClass;
        }

        if (flags != 0)
        {
            limitInfo.BasicLimitInformation.LimitFlags = flags;
            CheckWin32Result(PInvoke.SetInformationJobObject(job.Handle,
                JOBOBJECTINFOCLASS.JobObjectExtendedLimitInformation, &limitInfo, size));
        }
    }

    private static unsafe void SetMaxCpuRate(Win32Job job, JobSettings session, ulong systemOrProcessorGroupAffinityMask)
    {
        if (session.CpuMaxRate > 0)
        {
            // Set CpuRate to a percentage times 100. For example, to let the job use 20% of the CPU, 
            // set CpuRate to 2,000.
            uint finalCpuRate = session.CpuMaxRate * 100;

            // CPU rate is set for the whole system so includes all the logical CPUs. When 
            // we have the CPU affinity set, we will divide the rate accordingly.
            if ((systemOrProcessorGroupAffinityMask & session.CpuAffinityMask) is var cpuAffinityMask && cpuAffinityMask != 0)
            {
                var numberOfSelectedCores = (uint)Enumerable.Range(0, 8 * sizeof(ulong)).Count(i => (cpuAffinityMask & (1UL << i)) != 0);
                Debug.Assert(numberOfSelectedCores < Environment.ProcessorCount);
                finalCpuRate /= ((uint)Environment.ProcessorCount / numberOfSelectedCores);
            }

            // configure CPU rate limit
            var limitInfo = new JOBOBJECT_CPU_RATE_CONTROL_INFORMATION
            {
                ControlFlags = JOB_OBJECT_CPU_RATE_CONTROL.JOB_OBJECT_CPU_RATE_CONTROL_ENABLE |
                    JOB_OBJECT_CPU_RATE_CONTROL.JOB_OBJECT_CPU_RATE_CONTROL_HARD_CAP,

                Anonymous = new JOBOBJECT_CPU_RATE_CONTROL_INFORMATION._Anonymous_e__Union { CpuRate = finalCpuRate }
            };
            var size = (uint)Marshal.SizeOf(limitInfo);
            CheckWin32Result(PInvoke.SetInformationJobObject(job.Handle, JOBOBJECTINFOCLASS.JobObjectCpuRateControlInformation,
                &limitInfo, size));
        }
    }

    static unsafe void SetNumaAffinity(Win32Job job, JobSettings session, ulong systemOrProcessorGroupAffinityMask)
    {
        if (session.NumaNode != ushort.MaxValue || session.CpuAffinityMask != 0)
        {
            GROUP_AFFINITY calculateGroupAffinity()
            {
                if (session.NumaNode != ushort.MaxValue)
                {
                    CheckWin32Result(PInvoke.GetNumaNodeProcessorMaskEx(session.NumaNode, out var groupAffinity));
                    /*
                    In my test Hyper-V environment, I set the number of NUMA nodes to four with two cores per node.
                    The results of running the following loop are presented below:

                    GetNumaHighestNodeNumber(out var highestNode);

                    for (ulong i = 0; i <= highestNode; i++) {
                        Console.WriteLine($"Node: {i:X}");
                        GetNumaNodeProcessorMaskEx((ushort)i, out var affinity);
                        $"Mask: {affinity.Mask:X}".Dump();
                    }

                    Results:

                    Node: 0
                    Mask: 3  (0x000000011)
                    Node: 1
                    Mask: 12  (0x00001100)
                    Node: 2
                    Mask: 48  (0x00110000)
                    Node: 3
                    Mask: 192 (0x11000000)
                    */

                    var cpuAffinityMask = session.CpuAffinityMask;
                    var nodeAffinityMask = Convert.ToUInt64(groupAffinity.Mask);
                    if (session.CpuAffinityMask != 0)
                    {
                        // When CPU affinity is set, we can't simply use 
                        // NUMA affinity, but rather need to apply the CPU affinity
                        // settings to the select NUMA node.
                        var firstNonZeroBitPosition = 0;
                        while ((nodeAffinityMask & (1UL << firstNonZeroBitPosition)) == 0)
                        {
                            firstNonZeroBitPosition++;
                        }
                        cpuAffinityMask <<= firstNonZeroBitPosition & 0x3f;
                    }
                    else
                    {
                        cpuAffinityMask = nodeAffinityMask;
                    }
                    // NOTE: this could result in an overflow on 32-bit apps, but I can't
                    // think of a nice way of handling it here
                    groupAffinity.Mask = (nuint)(cpuAffinityMask & groupAffinity.Mask);

                    return groupAffinity;
                }
                else
                {
                    GROUP_AFFINITY groupAffinity = new();
                    var size = (uint)Marshal.SizeOf(groupAffinity);
                    var length = 0u;
                    CheckWin32Result(PInvoke.QueryInformationJobObject(job.Handle, JOBOBJECTINFOCLASS.JobObjectGroupInformationEx,
                            &groupAffinity, size, &length));

                    // currently we support only one processor group per process
                    Debug.Assert(length == size);

                    // groupAffinity.Mask retrieved from the QueryInformationJobObject is the affinity mask
                    // of the group. It is hard to tell if it's correct - I would expect the currently set affinity,
                    // but I will stick to the affinity retrieved from the GetProcessAffinityMask
                    groupAffinity.Mask = (nuint)(session.CpuAffinityMask & systemOrProcessorGroupAffinityMask);

                    return groupAffinity;
                }
            }

            var groupAffinity = calculateGroupAffinity();
            logger.TraceInformation($"Numba group affinity number: {groupAffinity.Group}, mask: 0x{groupAffinity.Mask:x}");

            var size = (uint)Marshal.SizeOf(groupAffinity);
            CheckWin32Result(PInvoke.SetInformationJobObject(job.Handle, JOBOBJECTINFOCLASS.JobObjectGroupInformationEx,
                &groupAffinity, size));

        }
    }

    static unsafe void SetMaxBandwith(Win32Job job, JobSettings session)
    {
        if (session.MaxBandwidth > 0)
        {
            var limitInfo = new JOBOBJECT_NET_RATE_CONTROL_INFORMATION
            {
                ControlFlags = JOB_OBJECT_NET_RATE_CONTROL_FLAGS.JOB_OBJECT_NET_RATE_CONTROL_ENABLE |
                                JOB_OBJECT_NET_RATE_CONTROL_FLAGS.JOB_OBJECT_NET_RATE_CONTROL_MAX_BANDWIDTH,
                MaxBandwidth = session.MaxBandwidth,
                DscpTag = 0
            };
            var size = (uint)Marshal.SizeOf(limitInfo);
            CheckWin32Result(PInvoke.SetInformationJobObject(job.Handle, JOBOBJECTINFOCLASS.JobObjectNetRateControlInformation,
                &limitInfo, size));
        }
    }

    public static unsafe void WaitForTheJobToComplete(Win32Job job, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            switch (PInvoke.WaitForSingleObject(job.Handle, 200 /* ms */))
            {
                case WAIT_EVENT.WAIT_OBJECT_0:
                    logger.TraceInformation("Job or process got signaled.");
                    return;
                case WAIT_EVENT.WAIT_FAILED:
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                default:
                    JOBOBJECT_BASIC_ACCOUNTING_INFORMATION jobBasicAcctInfo;
                    uint length;
                    CheckWin32Result(PInvoke.QueryInformationJobObject(job.Handle, JOBOBJECTINFOCLASS.JobObjectBasicAccountingInformation,
                        &jobBasicAcctInfo, (uint)Marshal.SizeOf<JOBOBJECT_BASIC_ACCOUNTING_INFORMATION>(), &length));
                    Debug.Assert((uint)Marshal.SizeOf<JOBOBJECT_BASIC_ACCOUNTING_INFORMATION>() == length);

                    if (jobBasicAcctInfo.ActiveProcesses == 0)
                    {
                        logger.TraceInformation("No active processes in the job - terminating.");
                        return;
                    }
                    break;
            }
        }
    }

    public static void TerminateJob(Win32Job job, uint exitCode)
    {
        PInvoke.TerminateJobObject(job.Handle, exitCode);
    }
}
