using System.Xml.Linq;
using FantasyMontior.ViewModels;
using Xunit;

namespace FantasyMontior.UiChecks;

internal static class AutoStartChecks
{
    public static async Task VerifyAsync(string settingsPath)
    {
        // Ordinary UI verification must not register real logon tasks.
        var service = new FakeService { Enabled = true };
        var settings = new SettingsViewModel(settingsPath);
        await settings.InitializeAutoStartAsync(service);
        Assert.True(settings.AutoStart);
        Assert.Equal(0, service.Writes);
        await settings.SetAutoStartAsync(false);
        Assert.False(settings.AutoStart);
        Assert.False(service.Enabled);
        service.Fail = true;
        await settings.SetAutoStartAsync(true);
        Assert.False(settings.AutoStart);
        Assert.NotEmpty(settings.AutoStartError);
        Assert.True(settings.CanChangeAutoStart);
        service.Fail = false;
        service.Pending = new TaskCompletionSource();
        var update = settings.SetAutoStartAsync(true);
        Assert.False(settings.CanChangeAutoStart);
        await settings.SetAutoStartAsync(false);
        service.Pending.SetResult();
        await update;
        Assert.True(settings.AutoStart);
        Assert.Empty(settings.AutoStartError);
        Assert.Equal(3, service.Writes);
        var reopened = new SettingsViewModel(settingsPath);
        await reopened.InitializeAutoStartAsync(service);
        Assert.True(reopened.AutoStart);

        const string path = @"C:\Apps & Tools\FantasyMontior.exe";
        XNamespace ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";
        var xml = XElement.Parse(AutoStartService.CreateTaskXml("test-user", path));
        Assert.Equal(path, xml.Descendants(ns + "Command").Single().Value);
        Assert.All(xml.Descendants(ns + "UserId"), id => Assert.Equal("test-user", id.Value));
        Assert.Equal("InteractiveToken", xml.Descendants(ns + "LogonType").Single().Value);
        Assert.Equal("HighestAvailable", xml.Descendants(ns + "RunLevel").Single().Value);
        Assert.Equal("PT30S", xml.Descendants(ns + "Delay").Single().Value);
        Assert.Equal("PT0S", xml.Descendants(ns + "ExecutionTimeLimit").Single().Value);
        Assert.Equal("false", xml.Descendants(ns + "StopIfGoingOnBatteries").Single().Value);
    }

    private sealed class FakeService : IAutoStartService
    {
        public bool Enabled;
        public bool Fail;
        public int Writes;
        public TaskCompletionSource? Pending;
        public Task<bool> IsEnabledAsync() => Task.FromResult(Enabled);
        public async Task SetEnabledAsync(bool enabled)
        {
            Writes++;
            if (Fail) throw new InvalidOperationException("Test registration failure");
            if (Pending is not null) await Pending.Task;
            Enabled = enabled;
        }
    }
}
