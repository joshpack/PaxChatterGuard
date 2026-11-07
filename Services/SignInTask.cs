using System.Reflection;
using System.Runtime.Versioning;
using System.Security;

namespace PaxChatterGuard.Services;

/// <summary>
/// Windows, while running as administrator: "Start when I sign in" as a scheduled task that starts the app with highest
/// privileges when this user signs in, so it runs as administrator again without Windows asking. Talks to the Task
/// Scheduler through its COM interface, late bound (nothing extra to ship), which keeps paths in any language intact.
/// </summary>
public static class SignInTask
{
    public const string Name = "PaxChatterGuard";

    private const int NotFound = unchecked((int)0x80070002);
    private const int PathNotFound = unchecked((int)0x80070003);
    private const int AccessDenied = unchecked((int)0x80070005);

    /// <summary>
    /// The task: at this user's sign-in, start the app as administrator. No time limit (the default stops a task after three
    /// days), also on battery, at normal priority (tasks default to below normal), and never a second copy.
    /// </summary>
    public static string Xml(string userSid, string userName, string command, string arguments, string workingDirectory) =>
        $"""
        <?xml version="1.0" encoding="UTF-16"?>
        <Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
          <RegistrationInfo>
            <Description>Starts PAX Chatter Guard as administrator when you sign in, so it can filter programs that run as administrator too. Switch off "Start when I sign in" in its settings to remove this.</Description>
          </RegistrationInfo>
          <Triggers>
            <LogonTrigger>
              <Enabled>true</Enabled>
              <UserId>{Escape(userName)}</UserId>
            </LogonTrigger>
          </Triggers>
          <Principals>
            <Principal id="Author">
              <UserId>{Escape(userSid)}</UserId>
              <LogonType>InteractiveToken</LogonType>
              <RunLevel>HighestAvailable</RunLevel>
            </Principal>
          </Principals>
          <Settings>
            <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
            <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
            <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
            <AllowHardTerminate>true</AllowHardTerminate>
            <StartWhenAvailable>false</StartWhenAvailable>
            <RunOnlyIfNetworkAvailable>false</RunOnlyIfNetworkAvailable>
            <IdleSettings>
              <StopOnIdleEnd>false</StopOnIdleEnd>
              <RestartOnIdle>false</RestartOnIdle>
            </IdleSettings>
            <AllowStartOnDemand>true</AllowStartOnDemand>
            <Enabled>true</Enabled>
            <Hidden>false</Hidden>
            <RunOnlyIfIdle>false</RunOnlyIfIdle>
            <WakeToRun>false</WakeToRun>
            <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
            <Priority>4</Priority>
          </Settings>
          <Actions Context="Author">
            <Exec>
              <Command>{Escape(command)}</Command>
              <Arguments>{Escape(arguments)}</Arguments>
              <WorkingDirectory>{Escape(workingDirectory)}</WorkingDirectory>
            </Exec>
          </Actions>
        </Task>
        """;

    private static string Escape(string text) => SecurityElement.Escape(text) ?? "";

    /// <summary>
    /// Whether the task exists, with the program and arguments it starts. Those are null when the task can't be read
    /// without administrator rights (it was made as administrator): it is there, but how it starts the app is unknown.
    /// </summary>
    [SupportedOSPlatform("windows")]
    public static bool Find(out string? command, out string? arguments)
    {
        command = arguments = null;
        object task;
        try
        {
            task = Call(RootFolder(), "GetTask", Name)!;
        }
        catch (Exception ex) when (Code(ex) is NotFound or PathNotFound)
        {
            return false;
        }
        catch (Exception ex) when (Code(ex) == AccessDenied)
        {
            return true;
        }

        try
        {
            var action = Get(Get(Get(task, "Definition")!, "Actions")!, "Item", 1)!;
            command = Get(action, "Path") as string;
            arguments = Get(action, "Arguments") as string ?? "";
        }
        catch
        {
            command = arguments = null; // there, but not readable as a plain "start a program" task
        }

        return true;
    }

    /// <summary>Creates the task, or replaces it. Needs administrator rights.</summary>
    [SupportedOSPlatform("windows")]
    public static void Register(string xml)
    {
        const int TASK_CREATE_OR_UPDATE = 6, TASK_LOGON_INTERACTIVE_TOKEN = 3;
        Call(RootFolder(), "RegisterTask", Name, xml, TASK_CREATE_OR_UPDATE, null, null, TASK_LOGON_INTERACTIVE_TOKEN, null);
    }

    /// <summary>Deletes the task. True when it is gone (or never was); false when Windows wants administrator rights for it.</summary>
    [SupportedOSPlatform("windows")]
    public static bool TryDelete()
    {
        try
        {
            Call(RootFolder(), "DeleteTask", Name, 0);
            return true;
        }
        catch (Exception ex) when (Code(ex) is NotFound or PathNotFound)
        {
            return true;
        }
        catch (Exception ex) when (Code(ex) == AccessDenied)
        {
            return false;
        }
    }

    [SupportedOSPlatform("windows")]
    private static object RootFolder()
    {
        var service = Activator.CreateInstance(Type.GetTypeFromProgID("Schedule.Service", throwOnError: true)!)!;
        Call(service, "Connect");
        return Call(service, "GetFolder", "\\")!;
    }

    private static object? Call(object target, string method, params object?[] args) =>
        target.GetType().InvokeMember(method, BindingFlags.InvokeMethod, null, target, args);

    private static object? Get(object target, string property, params object?[] args) =>
        target.GetType().InvokeMember(property, BindingFlags.GetProperty, null, target, args);

    /// <summary>The COM error behind an exception (late-bound calls wrap it in a TargetInvocationException).</summary>
    private static int Code(Exception ex) => (ex is TargetInvocationException { InnerException: { } inner } ? inner : ex).HResult;
}
