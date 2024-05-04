

using System.Security;

namespace ProcessGovernor;

enum ExitBehavior { WaitForJobCompletion, DontWaitForJobCompletion, TerminateJobOnExit };

internal interface IJobTarget;

record LaunchProcess(List<string> Procargs, bool NewConsole) : IJobTarget;

record AttachToProcess(uint[] Pids) : IJobTarget;

internal interface IExecutionMode;

record ShowHelpAndExit(string ErrorMessage) : IExecutionMode;

record RunAsCmdApp(
    JobSettings JobSettings,
    IJobTarget JobTarget,
    Dictionary<string, string> Environment,
    List<string> Privileges,
    bool NoGui,
    bool Quiet,
    bool NoMonitor,
    ExitBehavior ExitBehavior) : IExecutionMode;

record RunAsMonitor : IExecutionMode;

record RunAsService : IExecutionMode;

record InstallService(
    JobSettings JobSettings,
    Dictionary<string, string> Environment,
    List<string> Privilegs,
    string ExecutablePath,
    string UserName,
    SecureString? Password) : IExecutionMode;

record UninstallService(string ExecutablePath) : IExecutionMode;
