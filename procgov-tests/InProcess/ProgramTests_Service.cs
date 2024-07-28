using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Threading;
using System.Threading.Tasks;

namespace ProcessGovernor.Tests.InProcess;

public static partial class ProgramTests
{
    [Test]
    public static void ServiceProcessSavedSettings()
    {
        var settings = new Program.ProcessSavedSettings(
            JobSettings: new(
                MaxProcessMemory: 100 * 1024 * 1024,
                CpuAffinityMask: 0x1,
                ActiveProcessLimit: 10
            ),
            Environment: new Dictionary<string, string>() {
                { "TESTVAR1", "TESTVAL1" },
                { "TESTVAR2", "TESTVAL2" }
            },
            Privileges: ["TestPriv1", "TestPriv2"]
        );

        Program.SaveProcessSettings("test.exe", settings);
        Program.SaveProcessSettings("broken.exe", new Program.ProcessSavedSettings(
            JobSettings: new(), Environment: [], Privileges: []));

        try
        {
            // manually break the settings
            using (var procgovKey = (Environment.IsPrivilegedProcess ?
                Registry.LocalMachine : Registry.CurrentUser).OpenSubKey(Program.RegistrySubKeyPath))
            {
                Assert.That(procgovKey, Is.Not.Null);
                using var processKey = procgovKey!.OpenSubKey("broken.exe", true);
                Assert.That(processKey, Is.Not.Null);

                processKey!.SetValue("JobSettings", new byte[] { 1 }, RegistryValueKind.Binary);
                processKey.SetValue("Environment", new string[] { "TESTVAR1" }, RegistryValueKind.MultiString);
                processKey.SetValue("Privileges", "TestPriv", RegistryValueKind.String);
            }

            var savedSettings = Program.GetProcessesSavedSettings();

            Assert.That(savedSettings, Contains.Key("test.exe"));
            var testExeSettings = savedSettings["test.exe"];

            Assert.Multiple(() =>
            {
                Assert.That(testExeSettings.JobSettings, Is.EqualTo(settings.JobSettings));
                Assert.That(testExeSettings.Environment, Is.EquivalentTo(settings.Environment));
                Assert.That(testExeSettings.Privileges, Is.EquivalentTo(settings.Privileges));
            });

            Assert.That(savedSettings, Contains.Key("broken.exe"));
            var brokenExeSettings = savedSettings["broken.exe"];
            Assert.Multiple(() =>
            {
                Assert.That(brokenExeSettings.JobSettings, Is.EqualTo(new JobSettings()));
                Assert.That(brokenExeSettings.Environment, Is.EquivalentTo(ImmutableDictionary<string, string>.Empty));
                Assert.That(brokenExeSettings.Privileges, Is.EquivalentTo(ImmutableArray<string>.Empty));
            });
        }
        finally
        {
            Program.RemoveSavedProcessSettings("test.exe");
            Program.RemoveSavedProcessSettings("broken.exe");
        }
    }

    [Test]
    public static async Task ServiceInteractiveWithNewProcess()
    {
        ProcessGovernorTestContext.Initialize();

        const string executablePath = "winver.exe";

        using var cts = new CancellationTokenSource(30000);

        // start the monitor so the run command (started by service) won't hang
        var monitorTask = Task.Run(() => Program.Execute(new RunAsMonitor(), cts.Token));
        using (var pipe = new NamedPipeClientStream(".", Program.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous))
        {
            while (!pipe.IsConnected && !cts.IsCancellationRequested)
            {
                try { await pipe.ConnectAsync(cts.Token); } catch (TimeoutException) { }
            }
        }

        var settings = new Program.ProcessSavedSettings(
            JobSettings: new(
                MaxProcessMemory: 100 * 1024 * 1024,
                ActiveProcessLimit: 10
            ),
            Environment: [],
            Privileges: []
        );

        Program.SaveProcessSettings(executablePath, settings);

        try
        {
            var svc = new Program.ProcessGovernorService();

            svc.Start();

            // give it some time to start (it enumerates running processes)
            await Task.Delay(1000, cts.Token);

            // the monitor should start with the first monitored process
            using var monitoredProcess = Process.Start(executablePath);

            // give it time to discover a new process
            await Task.Delay(Program.ServiceProcessObserverIntervalInMilliseconds * 2, cts.Token);

            try
            {
                Assert.That(await SharedApi.GetJobSettingsFromMonitor((uint)monitoredProcess.Id, cts.Token),
                    Is.EqualTo(settings.JobSettings));

                svc.Stop();
            }
            finally
            {

                monitoredProcess.CloseMainWindow();
                if (!monitoredProcess.WaitForExit(500))
                {
                    monitoredProcess.Kill();
                }
            }
        }
        finally
        {
            Program.RemoveSavedProcessSettings("winver.exe");
        }
    }
}
