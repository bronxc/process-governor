using System;
using System.Collections.Generic;
using System.IO;

namespace ProcessGovernor.Tests;

public static partial class ProgramTests
{
    static ProgramTests()
    {
        ProcessGovernorTestContext.Initialize();
    }

    [Test]
    public static void ParseArgsExecutionMode()
    {
        switch (Program.ParseArgs(["-m=10M", "test.exe", "-c", "-m=123"]))
        {
            case RunAsCmdApp { JobTarget: var target, JobSettings.MaxProcessMemory: var maxMemory }:
                Assert.That(maxMemory, Is.EqualTo(10 * 1024 * 1024));
                Assert.That(target is LaunchProcess { Procargs: var procargs } ? procargs : null,
                    Is.EquivalentTo(new[] { "test.exe", "-c", "-m=123" }));
                break;
            default:
                Assert.Fail();
                break;
        }

        switch (Program.ParseArgs(["-m=10M", "-p", "1001", "-p=1002", "--pid=1003", "--pid", "1004"]))
        {
            case RunAsCmdApp { JobTarget: var target, JobSettings.MaxProcessMemory: var maxMemory }:
                Assert.That(maxMemory, Is.EqualTo(10 * 1024 * 1024));
                Assert.That(target is AttachToProcess { Pids: var pids } ? pids : null,
                    Is.EqualTo(new[] { 1001, 1002, 1003, 1004 }));
                break;
            default:
                Assert.Fail();
                break;
        }

        switch (Program.ParseArgs(["-m=10M", "-p", "1001", "-p=1001"]))
        {
            case RunAsCmdApp { JobTarget: var target, JobSettings.MaxProcessMemory: var maxMemory }:
                Assert.That(maxMemory, Is.EqualTo(10 * 1024 * 1024));
                Assert.That(target is AttachToProcess { Pids: var pids } ? pids : null,
                    Is.EqualTo(new[] { 1001 }));
                break;
            default:
                Assert.Fail();
                break;
        }

        switch (Program.ParseArgs(["-m=10M", "-p", "1001", "-p=1001", "--pid=1001", "--pid", "1001"]))
        {
            case RunAsCmdApp { JobTarget: var target, JobSettings.MaxProcessMemory: var maxMemory }:
                Assert.That(maxMemory, Is.EqualTo(10 * 1024 * 1024));
                Assert.That(target is AttachToProcess { Pids: var pids } ? pids : null,
                    Is.EqualTo(new[] { 1001 }));
                break;
            default:
                Assert.Fail();
                break;
        }

        switch (Program.ParseArgs(["-m=10M", "-p", "1001", "test.exe", "-c"]))
        {
            case ShowHelpAndExit { ErrorMessage: var err }:
                Assert.That(err, Is.EqualTo("invalid arguments provided"));
                break;
            default:
                Assert.Fail();
                break;
        }

        switch (Program.ParseArgs(["--monitor"]))
        {
            case RunAsMonitor:
                break;
            default:
                Assert.Fail();
                break;
        }

        switch (Program.ParseArgs(["--service"]))
        {
            case RunAsService:
                break;
            default:
                Assert.Fail();
                break;
        }

        switch (Program.ParseArgs(["--install", "-m=10M", "-p", "1001", "-p=1001"]))
        {
            case ShowHelpAndExit { ErrorMessage: var err }:
                Assert.That(err, Is.EqualTo("invalid arguments provided"));
                break;
            default:
                Assert.Fail();
                break;
        }

        switch (Program.ParseArgs(["--install", "--service-username=testu", "--service-password=testp", 
            "--service-path=C:\\test test", "-m=10M", "test.exe"]))
        {
            case SetupProcessGovernance procgov:
                Assert.That(procgov.JobSettings.MaxProcessMemory, Is.EqualTo(10 * 1024 * 1024));
                Assert.That(procgov.ExecutablePath, Is.EqualTo("test.exe"));
                Assert.That(procgov.ServiceUserName, Is.EqualTo("testu"));
                Assert.That(procgov.ServiceUserPassword, Is.EqualTo("testp"));
                Assert.That(procgov.ServiceInstallPath, Is.EqualTo("C:\\test test"));
                break;
            case ShowHelpAndExit err:
                Assert.Fail($"Error: {err.ErrorMessage}");
                break;
            default:
                Assert.Fail();
                break;
        }

        switch (Program.ParseArgs(["--install", "--service-username=testu", "--service-username=testu2", "-m=10M", "test.exe"]))
        {
            case SetupProcessGovernance procgov:
                Assert.That(procgov.JobSettings.MaxProcessMemory, Is.EqualTo(10 * 1024 * 1024));
                Assert.That(procgov.ExecutablePath, Is.EqualTo("test.exe"));
                Assert.That(procgov.ServiceUserName, Is.EqualTo("testu2"));
                Assert.That(procgov.ServiceUserPassword, Is.Null);
                break;
            case ShowHelpAndExit err:
                Assert.Fail($"Error: {err.ErrorMessage}");
                break;
            default:
                Assert.Fail();
                break;
        }

        switch (Program.ParseArgs(["--install", "-m=10M", "test.exe"]))
        {
            case SetupProcessGovernance procgov:
                Assert.That(procgov.JobSettings.MaxProcessMemory, Is.EqualTo(10 * 1024 * 1024));
                Assert.That(procgov.ExecutablePath, Is.EqualTo("test.exe"));
                Assert.That(procgov.ServiceUserName, Is.EqualTo("NT AUTHORITY\\SYSTEM"));
                Assert.That(procgov.ServiceUserPassword, Is.Null);
                Assert.That(procgov.ServiceInstallPath, Is.EqualTo(
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), Program.ServiceName)));
                break;
            case ShowHelpAndExit err:
                Assert.Fail($"Error: {err.ErrorMessage}");
                break;
            default:
                Assert.Fail();
                break;
        }

        switch (Program.ParseArgs(["--uninstall"]))
        {
            case ShowHelpAndExit { ErrorMessage: var err }:
                Assert.That(err, Is.EqualTo("invalid arguments provided"));
                break;
            default:
                Assert.Fail();
                break;
        }

        switch (Program.ParseArgs(["--uninstall", @"C:\\temp\\test.exe"]))
        {
            case RemoveProcessGovernance { ExecutablePath: var exePath }:
                Assert.That(exePath, Is.SamePath(@"C:\\temp\\test.exe"));
                break;
            default:
                Assert.Fail();
                break;
        }

        switch (Program.ParseArgs(["--uninstall-all"]))
        {
            case RemoveAllProcessGovernance:
                Assert.Pass();
                break;
            default:
                Assert.Fail();
                break;
        }
    }

    [Test]
    public static void ParseArgsWorkingSetLimits()
    {
        if (Program.ParseArgs(["--minws=1M", "--maxws=100M", "test.exe"]) is RunAsCmdApp
            {
                JobSettings: { MinWorkingSetSize: var minws, MaxWorkingSetSize: var maxws }
            })
        {
            Assert.That((minws, maxws), Is.EqualTo((1024 * 1024, 100 * 1024 * 1024)));
        }
        else { Assert.Fail(); }

        Assert.That(Program.ParseArgs(["--minws=1", "test.exe"]) is ShowHelpAndExit
        {
            ErrorMessage: "minws and maxws must be set together and be greater than 0"
        });
        Assert.That(Program.ParseArgs(["--maxws=1", "test.exe"]) is ShowHelpAndExit
        {
            ErrorMessage: "minws and maxws must be set together and be greater than 0"
        });
        Assert.That(Program.ParseArgs(["--minws=0", "--maxws=10M", "test.exe"]) is ShowHelpAndExit
        {
            ErrorMessage: "minws and maxws must be set together and be greater than 0"
        });
    }

    [Test]
    public static void ParseArgsAffinityMaskFromCpuCount()
    {
        Assert.That(Program.ParseArgs(["-c", "1", "test.exe"]) is RunAsCmdApp
        {
            JobSettings.CpuAffinityMask: var am1
        } ? am1 : 0, Is.EqualTo(0x1UL));

        Assert.That(Program.ParseArgs(["--cpu=2", "test.exe"]) is RunAsCmdApp
        {
            JobSettings.CpuAffinityMask: var am2
        } ? am2 : 0, Is.EqualTo(0x3UL));

        Assert.That(Program.ParseArgs(["--cpu", "4", "test.exe"]) is RunAsCmdApp
        {
            JobSettings.CpuAffinityMask: var am4
        } ? am4 : 0, Is.EqualTo(0xfUL));

        Assert.That(Program.ParseArgs(["--cpu=9", "test.exe"]) is RunAsCmdApp
        {
            JobSettings.CpuAffinityMask: var am9
        } ? am9 : 0, Is.EqualTo(0x1ffUL));

        Assert.That(Program.ParseArgs(["--cpu=64", "test.exe"]) is RunAsCmdApp
        {
            JobSettings.CpuAffinityMask: var am64
        } ? am64 : 0, Is.EqualTo(0xffffffffffffffffUL));
    }

    [Test]
    public static void ParseArgsMemoryString()
    {
        Assert.That(Program.ParseArgs(["-m=2K", "test.exe"]) is RunAsCmdApp
        {
            JobSettings.MaxProcessMemory: var m2k
        } ? m2k : 0, Is.EqualTo(2 * 1024u));

        Assert.That(Program.ParseArgs(["--maxmem=3M", "test.exe"]) is RunAsCmdApp
        {
            JobSettings.MaxProcessMemory: var m3m
        } ? m3m : 0, Is.EqualTo(3 * 1024u * 1024u));

        Assert.That(Program.ParseArgs(["-maxjobmem", "3G", "test.exe"]) is RunAsCmdApp
        {
            JobSettings.MaxJobMemory: var m3g
        } ? m3g : 0, Is.EqualTo(3 * 1024u * 1024u * 1024u));
    }

    [Test]
    public static void ParseArgsTime()
    {
        Assert.That(Program.ParseArgs(["--process-utime=10", "test.exe"]) is RunAsCmdApp
        {
            JobSettings.ProcessUserTimeLimitInMilliseconds: var t10
        } ? t10 : 0, Is.EqualTo(10u));

        Assert.That(Program.ParseArgs(["--job-utime", "10ms", "test.exe"]) is RunAsCmdApp
        {
            JobSettings.JobUserTimeLimitInMilliseconds: var t10ms
        } ? t10ms : 0, Is.EqualTo(10u));

        Assert.That(Program.ParseArgs(["--job-utime=10s", "test.exe"]) is RunAsCmdApp
        {
            JobSettings.JobUserTimeLimitInMilliseconds: var t10s
        } ? t10s : 0, Is.EqualTo(10000u));

        Assert.That(Program.ParseArgs(["-t=10m", "test.exe"]) is RunAsCmdApp
        {
            JobSettings.ClockTimeLimitInMilliseconds: var t10m
        } ? t10m : 0, Is.EqualTo(600000u));

        Assert.That(Program.ParseArgs(["--timeout=10h", "test.exe"]) is RunAsCmdApp
        {
            JobSettings.ClockTimeLimitInMilliseconds: var t10h
        } ? t10h : 0, Is.EqualTo(36000000u));

        Assert.That(Program.ParseArgs(["--timeout=sdfms", "test.exe"]) is ShowHelpAndExit
        {
            ErrorMessage: "invalid number in one of the constraints"
        });

        Assert.That(Program.ParseArgs(["--timeout=10h", "--nowait", "test.exe"]) is ShowHelpAndExit
        {
            ErrorMessage: "--nowait cannot be used with --timeout"
        });
    }

    [Test]
    public static void ParseArgsCustomEnvironmentVariables()
    {
        var envVarsFile = Path.GetTempFileName();
        try
        {
            using (var writer = new StreamWriter(envVarsFile, false))
            {
                writer.WriteLine("TEST=TESTVAL");
                writer.WriteLine("  TEST2 = TEST VAL2  ");
            }

            Assert.That(Program.ParseArgs([$"--env=\"{envVarsFile}\"", "test.exe"]) is RunAsCmdApp
            {
                Environment: var env
            } ? env : default, Is.EquivalentTo(new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) {
                { "TEST", "TESTVAL" },
                { "TEST2", "TEST VAL2" }
            }));

            using (var writer = new StreamWriter(envVarsFile, false))
            {
                writer.WriteLine("  = TEST VAL2  ");
            }

            Assert.That(Program.ParseArgs([$"--env=\"{envVarsFile}\"", "test.exe"]) is ShowHelpAndExit
            {
                ErrorMessage: "the environment file contains invalid data (line: 1)"
            });
        }
        finally
        {
            if (File.Exists(envVarsFile))
            {
                File.Delete(envVarsFile);
            }
        }
    }

    [Test]
    public static void ParseArgsUnknownArgument()
    {
        Assert.That(Program.ParseArgs(["-c", "1", "--maxmem", "100M", "--unknown", "test.exe"]) is ShowHelpAndExit
        {
            ErrorMessage: var err
        } ? err : "", Is.EqualTo("unrecognized arguments: unknown"));
    }

    [Test]
    public static void ParseArgsExitBehaviorArguments()
    {
        Assert.That(Program.ParseArgs(["-m=10M", "test.exe"]) is RunAsCmdApp
        {
            ExitBehavior: ExitBehavior.WaitForJobCompletion,
            LaunchConfig: LaunchConfig.Default
        });

        Assert.That(Program.ParseArgs(["-m=10M", "-q", "--nowait", "test.exe"]) is RunAsCmdApp
        {
            ExitBehavior: ExitBehavior.DontWaitForJobCompletion,
            LaunchConfig: LaunchConfig.Quiet
        });


        Assert.That(Program.ParseArgs(["-m=10M", "--nogui", "--nomonitor", "--terminate-job-on-exit", "test.exe"]) is RunAsCmdApp
        {
            ExitBehavior: ExitBehavior.TerminateJobOnExit,
            LaunchConfig: LaunchConfig.NoGui | LaunchConfig.NoMonitor
        });

        Assert.That(Program.ParseArgs(["-m=10M", "--terminate-job-on-exit", "--nowait", "test.exe"]) is ShowHelpAndExit
        {
            ErrorMessage: "--terminate-job-on-exit and --nowait cannot be used together"
        });
    }
}
