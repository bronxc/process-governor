using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO.Pipes;
using System.Threading;
using System.Threading.Tasks;

namespace ProcessGovernor.Tests;

public static partial class ProgramTests
{
    [Test]
    public static void ServiceProcessSavedSettings()
    {
        var settings = new Program.ProcessSavedSettings(
            JobSettings: new(
                MaxProcessMemory: 100 * 1024 * 1024,
                ActiveProcessLimit: 10
            ),
            Environment: new Dictionary<string, string>() {
                { "TESTVAR1", "TESTVAL1" },
                { "TESTVAR2", "TESTVAL2" }
            }.ToImmutableDictionary(),
            Privileges: ["TestPriv1", "TestPriv2"]
        );

        Program.SetProcessSavedSettings("test.exe", settings);
        Program.SetProcessSavedSettings("broken.exe", new Program.ProcessSavedSettings(
            JobSettings: new(),
            Environment: new Dictionary<string, string>().ToImmutableDictionary(),
            Privileges: []
        ));

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

    [Test]
    public static async Task ServiceInteractiveRun()
    {
        using var cts = new CancellationTokenSource(10000);

        var svc = new Program.ProcessGovernorService();

        svc.Start([]);

        // test monitor connection
        using var pipe = new NamedPipeClientStream(".", Program.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        while (!pipe.IsConnected && !cts.IsCancellationRequested)
        {
            try { await pipe.ConnectAsync(cts.Token); } catch (TimeoutException) { }
        }
        Assert.That(pipe.IsConnected, Is.True);

        svc.Stop();

        Memory<byte> buffer = new byte[10];
        Assert.That(await pipe.ReadAsync(buffer, cts.Token), Is.Zero);
        Assert.That(pipe.IsConnected, Is.False);

        Assert.Pass();
    }


    /* FIXME: tests to implement:
     * - if privileged, install service, start it and try service mode (maybe separate set of tests)
     */
    [Test]
    public static async Task ServiceSetupAndRunMonitoredProcess()
    {
        if (!Environment.IsPrivilegedProcess)
        {
            Assert.Ignore("This test requires elevated privileges");
        }

        // FIXME: will install and start the service with some monitored process,
        // then will query monitor if the process is monitored
    }

    [Test]
    public static void ServiceSetupAndRunPrivilegedCmdApp()
    {
        if (!Environment.IsPrivilegedProcess)
        {
            Assert.Ignore("This test requires elevated privileges");
        }

        // FIXME: privileged mode should start the service if it's not running
        // and use the system named pipe
    }
}
