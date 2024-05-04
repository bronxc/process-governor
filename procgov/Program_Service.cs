using MessagePack;
using Microsoft.Win32;
using System.Collections.Immutable;
using System.Diagnostics;
using System.ServiceProcess;

namespace ProcessGovernor;

static partial class Program
{
    public const string ServiceName = "ProcessGovernorService";
    public const string RegistrySubKeyPath = @"SOFTWARE\ProcessGovernor";

    public static int Execute(RunAsService _)
    {
        if (Environment.UserInteractive)
        {
            Console.WriteLine("This program must be run as a service");
            return 1;
        }

        ServiceBase.Run(new ProcessGovernorService());
        return 0;
    }

    internal sealed class ProcessGovernorService : ServiceBase
    {
        readonly TimeSpan CacheInvalidationTimeout = TimeSpan.FromMinutes(2);

        readonly CancellationTokenSource cts = new();

        DateTime lastSettingsReloadTimeUtc = DateTime.MinValue;
        readonly Dictionary<string, ProcessSavedSettings> settingsCache = [];

        Task monitorTask = Task.CompletedTask;
        Task processObserverTask = Task.CompletedTask;

        public ProcessGovernorService()
        {
            ServiceName = Program.ServiceName;
        }

        public void Start(string[] args)
        {
            // start monitor task - should create IOCP and the named pipe
            monitorTask = Execute(new RunAsMonitor(), cts.Token);

            // FIXME: start the process monitor task

            static void RunProcessObserver()
            {
                // FIXME: read settings from the registry
                // FIXME: wait for WMI events for process creation
                // FIXME: Execute CmdApp if process should be monitored

                //if (DateTime.UtcNow - lastSettingsReloadTimeUtc > CacheInvalidationTimeout)
                //{
                //    ReloadSettings();
                //    lastSettingsReloadTimeUtc = DateTime.UtcNow;
                //}

                //string[] possibleProcessSubKeyNames = [executablePath, Path.GetFileName(executablePath)];

                //foreach (var sk in possibleProcessSubKeyNames)
                //{
                //    if (settingsCache.TryGetValue(sk, out var settings))
                //    {
                //    }
                //}
            }
        }

        protected override void OnStart(string[] args)
        {
            Start(args);
        }

        protected override void OnStop()
        {
            cts.Cancel();

            // wait for the monitor task to complete
            Task.WaitAll([monitorTask, processObserverTask], TimeSpan.FromSeconds(10));

            monitorTask = Task.CompletedTask;
            processObserverTask = Task.CompletedTask;
        }
    }

    internal static ImmutableDictionary<string, ProcessSavedSettings> GetProcessesSavedSettings()
    {
        Dictionary<string, ProcessSavedSettings> settings = [];

        var rootKey = Environment.IsPrivilegedProcess ? Registry.LocalMachine : Registry.CurrentUser;

        if (rootKey.OpenSubKey(RegistrySubKeyPath) is { } procgovKey)
        {
            try
            {
                foreach (string sk in procgovKey.GetSubKeyNames())
                {
                    if (procgovKey.OpenSubKey(sk) is { } processKey)
                    {
                        try
                        {
                            settings[sk] = new(
                                JobSettings: ParseJobSettings(processKey),
                                Environment: ParseEnvironmentVars(processKey),
                                Privileges: ParsePrivileges(processKey)
                            );
                        }
                        finally
                        {
                            processKey.Dispose();
                        }
                    }
                }
            }
            finally
            {
                procgovKey.Dispose();
            }
        }

        return settings.ToImmutableDictionary();

        JobSettings ParseJobSettings(RegistryKey processKey)
        {
            if (processKey.GetValue("JobSettings", Array.Empty<byte>()) is byte[] serializedBytes && serializedBytes.Length > 0)
            {
                try
                {
                    return MessagePackSerializer.Deserialize<JobSettings>(serializedBytes);
                }
                catch (MessagePackSerializationException) { }
            }

            Logger.TraceEvent(TraceEventType.Warning, 0, $"Invalid job settings for process {processKey.Name}");
            return new();
        }

        ImmutableArray<string> ParsePrivileges(RegistryKey processKey)
        {
            if (processKey.GetValue("Privileges") is string[] privileges)
            {
                return [.. privileges];
            }
            return [];
        }

        ImmutableDictionary<string, string> ParseEnvironmentVars(RegistryKey processKey)
        {
            var envVars = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            if (processKey.GetValue("Environment") is string[] lines)
            {
                foreach (var line in lines)
                {
                    var ind = line.IndexOf('=');
                    if (ind > 0)
                    {
                        var key = line[..ind].Trim();
                        if (!string.IsNullOrEmpty(key))
                        {
                            var val = line.Substring(ind + 1, line.Length - ind - 1).Trim();
                            envVars.Add(key, val);
                        }
                    }
                    else
                    {
                        Logger.TraceEvent(TraceEventType.Warning, 0, $"The environment block for process {processKey.Name} contains invalid line: {line}");
                    }
                }
            }

            return envVars.ToImmutableDictionary();
        }
    }

    internal static void SetProcessSavedSettings(string executablePath, ProcessSavedSettings settings)
    {
        var rootKey = Environment.IsPrivilegedProcess ? Registry.LocalMachine : Registry.CurrentUser;
        using var procgovKey = rootKey.CreateSubKey(RegistrySubKeyPath);
        using var processKey = procgovKey.CreateSubKey(executablePath);

        processKey.SetValue("JobSettings", MessagePackSerializer.Serialize(settings.JobSettings), RegistryValueKind.Binary);
        processKey.SetValue("Environment", GetEnvironmentLines(), RegistryValueKind.MultiString);
        processKey.SetValue("Privileges", settings.Privileges.ToArray(), RegistryValueKind.MultiString);

        string[] GetEnvironmentLines()
        {
            return settings.Environment.Select(kv => $"{kv.Key}={kv.Value}").ToArray();
        }
    }

    internal sealed record ProcessSavedSettings(
        JobSettings JobSettings,
        ImmutableDictionary<string, string> Environment,
        ImmutableArray<string> Privileges
    );
}
