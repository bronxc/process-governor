using MessagePack;
using System.Buffers;
using System.Diagnostics;
using System.IO.Pipes;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.WindowsAndMessaging;
using static ProcessGovernor.NtApi;

namespace ProcessGovernor;

static partial class Program
{
    public static async Task<int> Execute(RunAsCmdApp app, CancellationToken ct)
    {
        if (app.LaunchConfig.HasFlag(LaunchConfig.NoGui))
        {
            PInvoke.ShowWindow(PInvoke.GetConsoleWindow(), SHOW_WINDOW_CMD.SW_HIDE);
        }

        var quiet = app.LaunchConfig.HasFlag(LaunchConfig.Quiet);
        if (!quiet)
        {
            Logger.Listeners.Add(new ConsoleTraceListener());

            ShowHeader();
        }

        var buffer = new ArrayBufferWriter<byte>(1024);

        using var _ = new ScopedAccountPrivileges(["SeDebugPrivilege"]);

        var (targetProcesses, environmentVariablesToSet) = app.JobTarget switch
        {
            AttachToProcess { Pids: var pids } => (pids.Select(
                pid => ProcessModule.OpenProcess(pid, app.Environment.Count != 0)).ToArray(), app.Environment),
            LaunchProcess l => ([ProcessModule.CreateSuspendedProcess(l.Procargs, l.NewConsole, app.Environment)], []),
            _ => throw new ArgumentException("invalid job target")
        };

        using var pipe = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);

        var noMonitor = app.LaunchConfig.HasFlag(LaunchConfig.NoMonitor);
        if (!noMonitor)
        {
            await SetupMonitorConnection();
        }

        using var job = await SetupProcessGovernorJob();

        foreach (var targetProcess in targetProcesses)
        {
            ProcessModule.SetProcessEnvironmentVariables(targetProcess.Handle, environmentVariablesToSet);

            foreach (var accountPrivilege in AccountPrivilegeModule.EnablePrivileges(targetProcess.Handle, app.Privileges).Where(
                ap => ap.Result != (int)WIN32_ERROR.NO_ERROR))
            {
                Logger.TraceEvent(TraceEventType.Error, 0, $"Setting privilege {accountPrivilege.PrivilegeName} for process " +
                    $"{targetProcess.Id} failed - 0x{accountPrivilege.Result:x}");
            }

            if (!targetProcess.MainThreadHandle.IsInvalid)
            {
                Debug.Assert(app.JobTarget is LaunchProcess);
                CheckWin32Result(PInvoke.ResumeThread(targetProcess.MainThreadHandle));
            }
        }

        uint exitCode = 0;
        if (app.ExitBehavior != ExitBehavior.DontWaitForJobCompletion)
        {
            if (!quiet)
            {
                if (app.ExitBehavior == ExitBehavior.TerminateJobOnExit)
                {
                    Console.WriteLine("Press Ctrl-C to end execution and terminate the job.");
                }
                else
                {
                    Console.WriteLine("Press Ctrl-C to end execution without terminating the process.");
                }
                Console.WriteLine();
            }

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            if (app.JobSettings.ClockTimeLimitInMilliseconds > 0)
            {
                cts.CancelAfter((int)app.JobSettings.ClockTimeLimitInMilliseconds);
            }

            if (noMonitor)
            {
                Win32JobModule.WaitForTheJobToComplete(job, cts.Token);
            }
            else
            {
                await MonitorJobUntilCompletion(cts.Token);
            }

            if (ct.IsCancellationRequested && app.ExitBehavior == ExitBehavior.TerminateJobOnExit ||
                !ct.IsCancellationRequested && cts.IsCancellationRequested && app.JobSettings.ClockTimeLimitInMilliseconds > 0)
            {
                Win32JobModule.TerminateJob(job, 0x1f);
            }

            exitCode = app.JobTarget is LaunchProcess ?
                (PInvoke.GetExitCodeProcess(targetProcesses[0].Handle, out var rc) ? rc : 0) : 0;
        }

        return (int)exitCode;

        /* *** Helper functions to connect with the monitor *** */

        async Task<Win32Job> SetupProcessGovernorJob()
        {
            // FIXME: moze lepiej bedzie zrobic ifa na app.NoMonitor i rozdzielic logike na dwa rozne bloki
            string[] assignedJobNames = app.JobTarget switch
            {
                AttachToProcess { Pids: var pids } when !noMonitor => [.. await GetProcessJobNames(pids)],
                _ => [.. Enumerable.Repeat("", targetProcesses.Length)], // includes Launch process and Attach to process without monitor
            };
            Debug.Assert(assignedJobNames.Length == targetProcesses.Length);

            var jobName = "";
            var jobSettings = app.JobSettings;
            if (!noMonitor && Array.FindIndex(assignedJobNames, jobName => jobName != "") is var monitoredProcessIndex && monitoredProcessIndex != -1)
            {
                jobName = assignedJobNames[monitoredProcessIndex];
                jobSettings = MergeJobSettings(jobSettings, await GetProcessJobSettings(jobName));
                // check if all assigned jobs are the same
                if (Array.FindIndex(assignedJobNames, n => n != jobName && n != "") is var idx && idx != -1)
                {
                    throw new ArgumentException($"found ProcessGovernor job: {jobName}, but {targetProcesses[idx].Id} " +
                        $"is assigned to a different Process Governor job ('{assignedJobNames[idx]}')");
                }
            }

            var job = jobName != "" ? Win32JobModule.OpenJob(jobName) : Win32JobModule.CreateJob(Win32JobModule.GetNewJobName());
            try
            {
                if (!noMonitor)
                {
                    await StartOrUpdateMonitoring();
                }

                Win32JobModule.SetLimits(job, jobSettings);

                // assign job to all the processes which do not have a job assigned
                for (int i = 0; i < targetProcesses.Length; i++)
                {
                    var targetProcess = targetProcesses[i];

                    if (assignedJobNames[i] == "")
                    {
                        Win32JobModule.AssignProcess(job, targetProcess.Handle);
                    }
                }

                if (!quiet)
                {
                    ShowLimits(jobSettings);
                }

                return job;
            }
            catch
            {
                job.Dispose();
                throw;
            }

            // helper methods

            async Task StartOrUpdateMonitoring()
            {
                buffer.ResetWrittenCount();
                bool subscribeToEvents = app.ExitBehavior != ExitBehavior.DontWaitForJobCompletion;
                MessagePackSerializer.Serialize<IMonitorRequest>(buffer, new MonitorJobReq(job.Name,
                    subscribeToEvents, jobSettings), cancellationToken: ct);
                await pipe.WriteAsync(buffer.WrittenMemory, ct);
            }

            async Task<string[]> GetProcessJobNames(uint[] processIds)
            {
                buffer.ResetWrittenCount();

                var jobNames = new string[processIds.Length];
                for (int i = 0; i < jobNames.Length; i++)
                {
                    MessagePackSerializer.Serialize<IMonitorRequest>(buffer, new GetJobNameReq(processIds[i]), cancellationToken: ct);
                    await pipe.WriteAsync(buffer.WrittenMemory, ct);

                    buffer.ResetWrittenCount();
                    int readBytes = await pipe.ReadAsync(buffer.GetMemory(), ct);
                    buffer.Advance(readBytes);

                    jobNames[i] = MessagePackSerializer.Deserialize<IMonitorResponse>(buffer.WrittenMemory,
                        bytesRead: out var deseralizedBytes, cancellationToken: ct) switch
                    {
                        GetJobNameResp { JobName: var jobName } => jobName,
                        _ => throw new InvalidOperationException("Unexpected monitor response")
                    };
                }
                return jobNames;
            }

            async Task<JobSettings> GetProcessJobSettings(string jobName)
            {
                buffer.ResetWrittenCount();
                MessagePackSerializer.Serialize<IMonitorRequest>(buffer, new GetJobSettingsReq(jobName), cancellationToken: ct);
                await pipe.WriteAsync(buffer.WrittenMemory, ct);

                buffer.ResetWrittenCount();
                int readBytes = await pipe.ReadAsync(buffer.GetMemory(), ct);
                buffer.Advance(readBytes);

                return MessagePackSerializer.Deserialize<IMonitorResponse>(buffer.WrittenMemory,
                    bytesRead: out var deseralizedBytes, cancellationToken: ct) switch
                {
                    GetJobSettingsResp { JobSettings: var jobSettings } => jobSettings,
                    _ => throw new InvalidOperationException("Unexpected monitor response")
                };
            }

            static JobSettings MergeJobSettings(JobSettings existing, JobSettings updated)
            {
                return new JobSettings
                {
                    CpuAffinityMask = existing.CpuAffinityMask != 0 ? existing.CpuAffinityMask : updated.CpuAffinityMask,
                    CpuMaxRate = existing.CpuMaxRate > 0 ? existing.CpuMaxRate : updated.CpuMaxRate,
                    MaxBandwidth = existing.MaxBandwidth > 0 ? existing.MaxBandwidth : updated.MaxBandwidth,
                    MaxProcessMemory = existing.MaxProcessMemory > 0 ? existing.MaxProcessMemory : updated.MaxProcessMemory,
                    MaxJobMemory = existing.MaxJobMemory > 0 ? existing.MaxJobMemory : updated.MaxJobMemory,
                    MinWorkingSetSize = existing.MinWorkingSetSize > 0 ? existing.MinWorkingSetSize : updated.MinWorkingSetSize,
                    MaxWorkingSetSize = existing.MaxWorkingSetSize > 0 ? existing.MaxWorkingSetSize : updated.MaxWorkingSetSize,
                    NumaNode = existing.NumaNode != ushort.MaxValue ? existing.NumaNode : updated.NumaNode,
                    ProcessUserTimeLimitInMilliseconds = existing.ProcessUserTimeLimitInMilliseconds > 0 ?
                        existing.ProcessUserTimeLimitInMilliseconds : updated.ProcessUserTimeLimitInMilliseconds,
                    JobUserTimeLimitInMilliseconds = existing.JobUserTimeLimitInMilliseconds > 0 ?
                        existing.JobUserTimeLimitInMilliseconds : updated.JobUserTimeLimitInMilliseconds,
                    // clock time limit is governed by the running cmd client, so we always overwrite its value
                    ClockTimeLimitInMilliseconds = updated.ClockTimeLimitInMilliseconds,
                    PropagateOnChildProcesses = existing.PropagateOnChildProcesses || updated.PropagateOnChildProcesses,
                };
            }
        }

        async Task MonitorJobUntilCompletion(CancellationToken ct)
        {
            try
            {
                while (pipe.IsConnected && await pipe.ReadAsync(buffer.GetMemory(), ct) is var bytesRead && bytesRead > 0)
                {
                    buffer.Advance(bytesRead);

                    var processedBytes = 0;
                    while (processedBytes < buffer.WrittenCount)
                    {
                        var notification = MessagePackSerializer.Deserialize<IMonitorResponse>(
                            buffer.WrittenMemory[processedBytes..], out var deserializedBytes, ct);

                        switch (notification)
                        {
                            case NewProcessEvent ev:
                                Logger.TraceEvent(TraceEventType.Information, 11, $"New process {ev.ProcessId} in job.");
                                break;
                            case ExitProcessEvent ev:
                                Logger.TraceEvent(TraceEventType.Information, 12, $"Process {ev.ProcessId} exited.");
                                break;
                            case JobLimitExceededEvent ev:
                                Logger.TraceEvent(TraceEventType.Information, 13, $"Job limit '{ev.ExceededLimit}' exceeded.");
                                break;
                            case ProcessLimitExceededEvent:
                                Logger.TraceEvent(TraceEventType.Information, 14, $"Process limit exceeded.");
                                break;
                            case NoProcessesInJobEvent:
                                Logger.TraceEvent(TraceEventType.Information, 15, "No processes in job.");
                                return;
                        }

                        processedBytes += deserializedBytes;
                    }

                    buffer.ResetWrittenCount();
                }
            }
            catch (Exception ex) when (ex is OperationCanceledException || (
                    ex is AggregateException && ex.InnerException is TaskCanceledException))
            {
                Logger.TraceEvent(TraceEventType.Verbose, 0, "Stopping monitoring because of cancellation.");
            }
        }

        async Task SetupMonitorConnection()
        {
            try { await pipe.ConnectAsync(10, ct); }
            catch
            {
                string args = Logger.Switch.Level == SourceLevels.Verbose ? "--monitor --verbose" : "--monitor";
                using var _ = Process.Start(Environment.ProcessPath!, args);

                while (!pipe.IsConnected && !ct.IsCancellationRequested)
                {
                    try
                    {
                        await pipe.ConnectAsync(100, ct);
                        Logger.TraceEvent(TraceEventType.Verbose, 0, "Waiting for monitor to start...");
                    }
                    catch { }
                }
            }
        }
    }

    static void ShowLimits(JobSettings session)
    {
        if (session.CpuAffinityMask != 0)
        {
            Console.WriteLine($"CPU affinity mask:                          0x{session.CpuAffinityMask:X}");
        }
        if (session.CpuMaxRate > 0)
        {
            Console.WriteLine($"Max CPU rate:                               {session.CpuMaxRate}%");
        }
        if (session.MaxBandwidth > 0)
        {
            Console.WriteLine($"Max bandwidth (B):                          {(session.MaxBandwidth):#,0}");
        }
        if (session.MaxProcessMemory > 0)
        {
            Console.WriteLine($"Maximum committed memory (MB):              {(session.MaxProcessMemory / 1048576):0,0}");
        }
        if (session.MaxJobMemory > 0)
        {
            Console.WriteLine($"Maximum job committed memory (MB):          {(session.MaxJobMemory / 1048576):0,0}");
        }
        if (session.MinWorkingSetSize > 0)
        {
            Debug.Assert(session.MaxWorkingSetSize > 0);
            Console.WriteLine($"Minimum WS memory (MB):                     {(session.MinWorkingSetSize / 1048576):0,0}");
        }
        if (session.MaxWorkingSetSize > 0)
        {
            Debug.Assert(session.MinWorkingSetSize > 0);
            Console.WriteLine($"Maximum WS memory (MB):                     {(session.MaxWorkingSetSize / 1048576):0,0}");
        }
        if (session.NumaNode != ushort.MaxValue)
        {
            Console.WriteLine($"Preferred NUMA node:                        {session.NumaNode}");
        }
        if (session.ProcessUserTimeLimitInMilliseconds > 0)
        {
            Console.WriteLine($"Process user-time execution limit (ms):     {session.ProcessUserTimeLimitInMilliseconds:0,0}");
        }
        if (session.JobUserTimeLimitInMilliseconds > 0)
        {
            Console.WriteLine($"Job user-time execution limit (ms):         {session.JobUserTimeLimitInMilliseconds:0,0}");
        }
        if (session.ClockTimeLimitInMilliseconds > 0)
        {
            Console.WriteLine($"Clock-time execution limit (ms):            {session.ClockTimeLimitInMilliseconds:0,0}");
        }
        if (session.PropagateOnChildProcesses)
        {
            Console.WriteLine();
            Console.WriteLine("All configured limits will also apply to the child processes.");
        }
        Console.WriteLine();
    }
}

