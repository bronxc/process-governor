using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace ProcessGovernor.Tests.Application;

public static class CmdAppTests
{
    [Test]
    public static async Task LaunchProcess()
    {
        const string executablePath = "winver.exe";

        using var cts = new CancellationTokenSource(60000);

        var procgovExecutablePath = Path.Combine(AppContext.BaseDirectory, "procgov.exe");

        var psi = new ProcessStartInfo(procgovExecutablePath)
        {
            Arguments = $"-c 0x1 \"{executablePath}\"",
            UseShellExecute = false,
            RedirectStandardOutput = true
        };
        using var procgov = Process.Start(psi)!;

        // give the monitor some time to process the job start event
        await Task.Delay(2000);

        var winver = Process.GetProcessesByName("winver").FirstOrDefault(p => p.StartTime > procgov.StartTime);
        while (!cts.IsCancellationRequested && winver == null)
        {
            winver = Process.GetProcessesByName("winver").FirstOrDefault(p => p.StartTime > procgov.StartTime);
        }

        Debug.Assert(winver is not null);

        // check if the monitor is running
        var settings = await SharedApi.GetJobSettingsFromMonitor((uint)winver.Id, cts.Token);
        Assert.That(settings, Is.EqualTo(new JobSettings(CpuAffinityMask: 0x1, NumaNode: ushort.MaxValue)));

        winver.CloseMainWindow();
        if (!winver.WaitForExit(2000))
        {
            winver.Kill();
        }

        await procgov.WaitForExitAsync(cts.Token);
        TestContext.Out.WriteLine(await procgov.StandardOutput.ReadToEndAsync(cts.Token));

        // give the monitor some time to process the process exit event
        await Task.Delay(1000, cts.Token);

        Assert.That(await SharedApi.IsMonitorListening(cts.Token), Is.False);
    }

    [Test]
    public static async Task LaunchProcessNoWaitAndUpdate()
    {
        const string executablePath = "winver.exe";

        using var cts = new CancellationTokenSource(60000);

        var procgovExecutablePath = Path.Combine(AppContext.BaseDirectory, "procgov.exe");

        var psi = new ProcessStartInfo(procgovExecutablePath)
        {
            Arguments = $"-c 0x1 -v --nowait \"{executablePath}\"",
            UseShellExecute = false
        };
        var startTime = DateTime.Now;
        using (var procgov = Process.Start(psi)!)
        {
            await procgov.WaitForExitAsync(cts.Token);
        }

        // give the monitor some time to process the job start event
        await Task.Delay(2000);

        var winver = Process.GetProcessesByName("winver").FirstOrDefault(p => p.StartTime > startTime);
        while (!cts.IsCancellationRequested && winver == null)
        {
            winver = Process.GetProcessesByName("winver").FirstOrDefault(p => p.StartTime > startTime);
        }

        Debug.Assert(winver is not null);

        // check if the monitor is running
        var settings = await SharedApi.GetJobSettingsFromMonitor((uint)winver.Id, cts.Token);
        Assert.That(settings, Is.EqualTo(new JobSettings(CpuAffinityMask: 0x1, NumaNode: ushort.MaxValue)));

        // try to update the job settings
        psi = new ProcessStartInfo(procgovExecutablePath)
        {
            Arguments = $"-c 0x3 -v --nowait --pid {winver.Id}",
            UseShellExecute = false,
        };
        using (var procgov = Process.Start(psi)!)
        {
            await procgov.WaitForExitAsync(cts.Token);
        }

        // check if settings were updated
        settings = await SharedApi.GetJobSettingsFromMonitor((uint)winver.Id, cts.Token);
        Assert.That(settings, Is.EqualTo(new JobSettings(CpuAffinityMask: 0x3, NumaNode: ushort.MaxValue)));

        winver.CloseMainWindow();
        if (!winver.WaitForExit(2000))
        {
            winver.Kill();
        }

        // give the monitor some time to process the process exit event
        await Task.Delay(1000, cts.Token);

        Assert.That(await SharedApi.IsMonitorListening(cts.Token), Is.False);
    }

    [Test]
    public static async Task AttachToProcess()
    {

    }

    [Test]
    public static async Task AttachToMultipleProcesses()
    {
        // FIXME: try attaching to process belonging to a different group
    }
}
