using MessagePack;
using Microsoft.Win32.SafeHandles;
using System.Buffers;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using Windows.Win32;
using Windows.Win32.Foundation;
using static ProcessGovernor.NtApi;

namespace ProcessGovernor;

static partial class Program
{
    // main pipe used to receive commands from the client and respond to them
    public static readonly string PipeName = Environment.IsPrivilegedProcess ?
        "procgov-system" : "procgov-user";

    public static async Task<int> Execute(RunAsMonitor _, CancellationToken ct)
    {
        try
        {
            await StartMonitor(ct);
            return 0;
        }
        catch (Exception ex)
        {
            return ex.HResult != 0 ? ex.HResult : -1;
        }
    }

    static async Task StartMonitor(CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);

        using var monitoredJobs = new MonitoredJobs();
        using var notifier = new Notifier();
        var processes = new GovernedProcesses();

        using var iocpHandle = CheckWin32Result(PInvoke.CreateIoCompletionPort(new SafeFileHandle(HANDLE.INVALID_HANDLE_VALUE, true),
                                                    new SafeFileHandle(nint.Zero, true), nuint.Zero, 0));

        var iocpListener = Task.Factory.StartNew(IOCPListener, cts.Token, TaskCreationOptions.LongRunning, TaskScheduler.Default);

        while (!cts.IsCancellationRequested)
        {
            var pipeSecurity = new PipeSecurity();
            // we always add current user (the one who started monitor)
            if (WindowsIdentity.GetCurrent().User is { } currentUser)
            {
                pipeSecurity.SetAccessRule(new PipeAccessRule(currentUser, PipeAccessRights.FullControl, AccessControlType.Allow));
            }
            // and administrators
            pipeSecurity.SetAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
                PipeAccessRights.FullControl, AccessControlType.Allow));

            var pipe = NamedPipeServerStreamAcl.Create(PipeName, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances,
                PipeTransmissionMode.Byte, PipeOptions.WriteThrough | PipeOptions.Asynchronous, 0, 0, pipeSecurity);

            bool pipeForwarded = false;
            try
            {
                await pipe.WaitForConnectionAsync(cts.Token);

                _ = StartClientThread(pipe);

                pipeForwarded = true;
            }
            catch (IOException ex)
            {
                // IOException that is raised if the pipe is broken or disconnected.
                Logger.TraceEvent(TraceEventType.Verbose, 0, "[procgov monitor] broken named pipe: " + ex);
            }
            catch (Exception ex) when (ex is OperationCanceledException || (
                    ex is AggregateException && ex.InnerException is TaskCanceledException))
            {
                Logger.TraceEvent(TraceEventType.Verbose, 0, "[procgov monitor] cancellation: " + ex);
            }
            finally
            {
                if (!pipeForwarded)
                {
                    pipe.Dispose();
                }
            }
        }

        async Task StartClientThread(NamedPipeServerStream pipe)
        {
            PInvoke.GetNamedPipeClientProcessId(pipe.SafePipeHandle, out uint clientPid);
            bool disposePipeOnExit = true;
            try
            {
                var readBuffer = new ArrayBufferWriter<byte>(1024);
                var writeBuffer = new ArrayBufferWriter<byte>(256);

                while (!cts.IsCancellationRequested && pipe.IsConnected &&
                        await pipe.ReadAsync(readBuffer.GetMemory(), cts.Token) is var bytesRead && bytesRead > 0)
                {
                    readBuffer.Advance(bytesRead);

                    var processedBytes = 0;
                    while (processedBytes < readBuffer.WrittenCount)
                    {
                        var msg = MessagePackSerializer.Deserialize<IMonitorRequest>(readBuffer.WrittenMemory[processedBytes..],
                                        bytesRead: out var deserializedBytes, cancellationToken: cts.Token);
                        switch (msg)
                        {
                            case GetJobNameReq { ProcessId: var processId }:
                                {
                                    writeBuffer.ResetWrittenCount();
                                    var resp = new GetJobNameResp(processes.TryGetJobAssignedToProcess(processId, out var jobHandle) &&
                                            monitoredJobs.TryGetJob(jobHandle, out var jobData) ? jobData.Job.Name : "");
                                    MessagePackSerializer.Serialize<IMonitorResponse>(writeBuffer, resp, cancellationToken: cts.Token);
                                    await pipe.WriteAsync(writeBuffer.WrittenMemory, cts.Token);
                                    break;
                                }
                            case GetJobSettingsReq { JobName: var jobName }:
                                {
                                    writeBuffer.ResetWrittenCount();
                                    var resp = new GetJobSettingsResp(monitoredJobs.TryGetJob(jobName, out var jobData) ?
                                        jobData.JobSettings : new());
                                    MessagePackSerializer.Serialize<IMonitorResponse>(writeBuffer, resp, cancellationToken: cts.Token);
                                    await pipe.WriteAsync(writeBuffer.WrittenMemory, cts.Token);
                                    break;
                                }
                            case MonitorJobReq monitorJob:
                                {
                                    if (!monitoredJobs.TryGetJob(monitorJob.JobName, out var jobData))
                                    {
                                        var job = Win32JobModule.OpenJob(monitorJob.JobName);
                                        jobData = new(job, monitorJob.JobSettings);
                                        monitoredJobs.AddOrUpdateJob(jobData);
                                        Win32JobModule.AssignIOCompletionPort(job, iocpHandle);
                                    }
                                    else
                                    {
                                        monitoredJobs.AddOrUpdateJob(jobData with { JobSettings = monitorJob.JobSettings });
                                    }

                                    if (monitorJob.SubscribeToEvents)
                                    {
                                        notifier.AddNotificationStream(jobData.Job.NativeHandle, pipe);
                                        disposePipeOnExit = false;
                                    }
                                    break;
                                }
                            default:
                                Debug.Assert(false);
                                break;
                        }
                        processedBytes += deserializedBytes;
                    }

                    readBuffer.ResetWrittenCount();
                }
            }
            catch (IOException ex)
            {
                Logger.TraceEvent(TraceEventType.Verbose, 0,
                    $"[procgov monitor][{clientPid}] broken named pipe: {ex}");
            }
            catch (Exception ex) when (ex is OperationCanceledException || (
                    ex is AggregateException && ex.InnerException is TaskCanceledException))
            {
                Logger.TraceEvent(TraceEventType.Verbose, 0,
                    $"[procgov monitor][{clientPid}] cancellation: {ex}");
            }
            catch (Exception ex)
            {
                Logger.TraceEvent(TraceEventType.Verbose, 0,
                    $"[procgov monitor][{clientPid}] error: {ex}");
            }
            finally
            {
                if (disposePipeOnExit)
                {
                    pipe.Dispose();
                }
            }
        }

        void IOCPListener()
        {
            var buffer = new ArrayBufferWriter<byte>(1024);

            while (!cts.IsCancellationRequested)
            {
                unsafe
                {
                    if (!PInvoke.GetQueuedCompletionStatus(iocpHandle, out uint msgIdentifier,
                        out nuint jobHandle, out var msgData, 100 /* ms */))
                    {
                        var winerr = Marshal.GetLastWin32Error();
                        if (winerr == (int)WAIT_EVENT.WAIT_TIMEOUT)
                        {
                            // regular timeout
                            continue;
                        }

                        Logger.TraceEvent(TraceEventType.Error, 0, $"[process monitor] IOCP listener failed: {winerr:x}");
                        break;
                    }

                    if (!monitoredJobs.TryGetJob(jobHandle, out var jobData))
                    {
                        Logger.TraceEvent(TraceEventType.Warning, 0, $"[process monitor] job not found: {jobHandle}");
                        continue;
                    }

                    var jobName = jobData.Job.Name;
                    IMonitorResponse jobEvent = msgIdentifier switch
                    {
                        PInvoke.JOB_OBJECT_MSG_NEW_PROCESS => new NewProcessEvent(jobName, (uint)msgData),
                        PInvoke.JOB_OBJECT_MSG_EXIT_PROCESS => new ExitProcessEvent(jobName, (uint)msgData, false),
                        PInvoke.JOB_OBJECT_MSG_ABNORMAL_EXIT_PROCESS => new ExitProcessEvent(jobName, (uint)msgData, true),
                        PInvoke.JOB_OBJECT_MSG_ACTIVE_PROCESS_ZERO => new NoProcessesInJobEvent(jobName),
                        PInvoke.JOB_OBJECT_MSG_ACTIVE_PROCESS_LIMIT => new JobLimitExceededEvent(jobName, LimitType.ActiveProcessNumber),
                        PInvoke.JOB_OBJECT_MSG_JOB_MEMORY_LIMIT => new JobLimitExceededEvent(jobName, LimitType.Memory),
                        PInvoke.JOB_OBJECT_MSG_PROCESS_MEMORY_LIMIT => new ProcessLimitExceededEvent(jobName, (uint)msgData, LimitType.Memory),
                        PInvoke.JOB_OBJECT_MSG_END_OF_PROCESS_TIME => new ProcessLimitExceededEvent(jobName, (uint)msgData, LimitType.CpuTime),
                        PInvoke.JOB_OBJECT_MSG_END_OF_JOB_TIME => new JobLimitExceededEvent(jobName, LimitType.CpuTime),
                        _ => throw new NotImplementedException()
                    };

                    Logger.TraceInformation($"[process monitor] {jobEvent}");

                    bool stopMonitor = false;

                    switch (jobEvent)
                    {
                        case NewProcessEvent { ProcessId: var processId }:
                            processes.JobAssignedToProcess(processId, jobHandle);
                            break;
                        case ExitProcessEvent { ProcessId: var processId }:
                            processes.ProcessExited(processId);
                            break;
                        case NoProcessesInJobEvent:
                            stopMonitor = monitoredJobs.RemoveJob(jobHandle) == 0; // no more jobs to monitor
                            break;
                    }

                    if (notifier.GetNotificationStreams(jobHandle) is var jobNotificationStreams && jobNotificationStreams.Length > 0)
                    {
                        MessagePackSerializer.Serialize(buffer, jobEvent, cancellationToken: cts.Token);
                        var sendTasks = jobNotificationStreams.Select(stream => stream.WriteAsync(buffer.WrittenMemory, cts.Token).AsTask()).ToArray();

                        int completedTasksCount = 0;
                        while (completedTasksCount < sendTasks.Length)
                        {
                            var taskIndex = Task.WaitAny(sendTasks, ct);
                            if (sendTasks[taskIndex].IsFaulted || jobEvent is NoProcessesInJobEvent)
                            {
                                if (sendTasks[taskIndex].IsFaulted)
                                {
                                    Logger.TraceEvent(TraceEventType.Warning, 0,
                                        $"[process monitor] failed to send data to the pipe: {sendTasks[taskIndex].Exception}");
                                }

                                var stream = jobNotificationStreams[taskIndex];
                                notifier.RemoveNotificationStream(jobHandle, stream);
                                stream.Dispose();
                            }
                            completedTasksCount++;
                        }
                        buffer.ResetWrittenCount();
                    }

                    if (stopMonitor)
                    {
                        Logger.TraceInformation($"[process monitor] stopping monitor (no more jobs)");
                        cts.Cancel();
                    }
                }
            }
        }
    }

    private sealed class GovernedProcesses
    {
        readonly object lck = new();
        readonly Dictionary<uint, nuint> processJobMap = [];

        public void JobAssignedToProcess(uint processId, nuint jobHandle)
        {
            lock (lck)
            {
                processJobMap.Add(processId, jobHandle);
            }
        }

        public void ProcessExited(uint processId)
        {
            lock (lck)
            {
                processJobMap.Remove(processId);
            }
        }

        public bool TryGetJobAssignedToProcess(uint processId, out nuint jobHandle)
        {
            lock (lck)
            {
                return processJobMap.TryGetValue(processId, out jobHandle);
            }
        }
    }

    private sealed class Notifier : IDisposable
    {
        readonly object lck = new();
        readonly Dictionary<nuint, NamedPipeServerStream[]> notificationStreams = [];

        public void AddNotificationStream(nuint jobHandle, NamedPipeServerStream stream)
        {
            lock (lck)
            {
                if (!notificationStreams.TryGetValue(jobHandle, out var streams))
                {
                    notificationStreams.Add(jobHandle, [stream]);
                }
                else
                {
                    notificationStreams[jobHandle] = [.. streams, stream];
                }
            }
        }

        public void RemoveNotificationStream(nuint jobHandle, NamedPipeServerStream stream)
        {
            lock (lck)
            {
                if (notificationStreams.TryGetValue(jobHandle, out var streams))
                {
                    var newStreams = streams.Where(s => s != stream).ToArray();
                    if (newStreams.Length > 0)
                    {

                        notificationStreams[jobHandle] = newStreams;
                    }
                    else
                    {
                        notificationStreams.Remove(jobHandle);
                    }
                }
            }
        }

        public NamedPipeServerStream[] GetNotificationStreams(nuint jobHandle)
        {
            lock (lck)
            {
                return notificationStreams.TryGetValue(jobHandle, out var streams) ? streams : [];
            }
        }

        public void Dispose()
        {
            lock (lck)
            {
                foreach (var s in notificationStreams.Values.SelectMany(s => s))
                {
                    try { s.Dispose(); } catch { }
                }
                notificationStreams.Clear();
            }
        }
    }

    sealed class MonitoredJobs : IDisposable
    {
        readonly object lck = new();
        readonly Dictionary<nuint, MonitoredJobData> monitoredJobs = [];
        readonly Dictionary<string, nuint> monitoredJobNames = [];

        public int AddOrUpdateJob(MonitoredJobData jobData)
        {
            lock (lck)
            {
                var job = jobData.Job;
                if (monitoredJobNames.TryGetValue(job.Name, out var jobHandle))
                {
                    Debug.Assert(job.NativeHandle == jobHandle);
                    Debug.Assert(monitoredJobs.ContainsKey(jobHandle));
                    monitoredJobs[jobHandle] = jobData;
                }
                else
                {
                    Debug.Assert(!monitoredJobs.ContainsKey(jobHandle));
                    monitoredJobs.Add(job.NativeHandle, jobData);
                    monitoredJobNames.Add(job.Name, job.NativeHandle);
                }
                return monitoredJobs.Count;
            }
        }

        public int RemoveJob(nuint jobHandle)
        {
            lock (lck)
            {
                if (monitoredJobs.Remove(jobHandle, out var jobData))
                {
                    monitoredJobNames.Remove(jobData.Job.Name);

                    jobData.Job.Dispose();
                }
                return monitoredJobs.Count;
            }
        }

        public bool TryGetJob(nuint jobHandle, [NotNullWhen(true)] out MonitoredJobData? job)
        {
            lock (lck)
            {
                return monitoredJobs.TryGetValue(jobHandle, out job);
            }
        }

        public bool TryGetJob(string jobName, [NotNullWhen(true)] out MonitoredJobData? job)
        {
            lock (lck)
            {
                job = null;
                return monitoredJobNames.TryGetValue(jobName, out var jobHandle) &&
                    monitoredJobs.TryGetValue(jobHandle, out job);
            }
        }

        public void Dispose()
        {
            lock (lck)
            {
                foreach (var jobData in monitoredJobs.Values)
                {
                    jobData.Job.Dispose();
                }
                monitoredJobs.Clear();
            }
        }
    }

    sealed record MonitoredJobData(Win32Job Job, JobSettings JobSettings);
}
