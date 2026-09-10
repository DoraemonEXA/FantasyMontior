using System.IO;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Xml.Linq;

namespace FantasyMontior;

public interface IAutoStartService
{
    Task<bool> IsEnabledAsync();
    Task SetEnabledAsync(bool enabled);
}

// The scheduled task is the source of truth; loading settings never registers a task.
public sealed class AutoStartService : IAutoStartService
{
    public Task<bool> IsEnabledAsync() => Task.Run(() => WithFolder((folder, name) =>
    {
        object? task = null;
        try
        {
            task = folder.GetTask(name);
            return (bool)((dynamic)task).Enabled;
        }
        catch (COMException ex) when (ex.HResult == unchecked((int)0x80070002)) { return false; }
        finally { Release(task); }
    }));

    public Task SetEnabledAsync(bool enabled) => Task.Run(() => WithFolder((folder, name) =>
    {
        if (!enabled)
        {
            try { folder.DeleteTask(name, 0); }
            catch (COMException ex) when (ex.HResult == unchecked((int)0x80070002)) { }
        }
        else
        {
            using var identity = WindowsIdentity.GetCurrent();
            var path = Path.Combine(AppContext.BaseDirectory, "FantasyMontior.exe");
            if (!File.Exists(path)) throw new FileNotFoundException("FantasyMontior.exe was not found.", path);
            object task = folder.RegisterTask(name, CreateTaskXml(identity.User!.Value, path),
                6 /* create or update */, identity.User.Value, null, 3 /* interactive token */, null);
            Release(task);
        }
        return true;
    }));

    private static T WithFolder<T>(Func<dynamic, string, T> action)
    {
        object? service = null;
        object? folder = null;
        try
        {
            service = Activator.CreateInstance(Type.GetTypeFromProgID("Schedule.Service", throwOnError: true)!)!;
            ((dynamic)service).Connect();
            folder = ((dynamic)service).GetFolder(@"\");
            using var identity = WindowsIdentity.GetCurrent();
            return action(folder, "FantasyMontior-AutoStart-" + identity.User!.Value);
        }
        finally { Release(folder); Release(service); }
    }

    private static void Release(object? value)
    {
        if (value is not null && Marshal.IsComObject(value)) Marshal.FinalReleaseComObject(value);
    }

    public static string CreateTaskXml(string userId, string executable)
    {
        XNamespace ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";
        XElement E(string name, params object[] contents) => new(ns + name, contents);
        return E("Task", new XAttribute("version", "1.2"),
            E("Triggers", E("LogonTrigger", E("Enabled", true), E("UserId", userId), E("Delay", "PT30S"))),
            E("Principals", E("Principal", new XAttribute("id", "User"), E("UserId", userId),
                E("LogonType", "InteractiveToken"), E("RunLevel", "HighestAvailable"))),
            E("Settings", E("MultipleInstancesPolicy", "IgnoreNew"), E("DisallowStartIfOnBatteries", false),
                E("StopIfGoingOnBatteries", false), E("ExecutionTimeLimit", "PT0S")),
            E("Actions", new XAttribute("Context", "User"), E("Exec", E("Command", executable),
                E("WorkingDirectory", Path.GetDirectoryName(executable)!)))).ToString();
    }
}
