namespace ZerkSense5.UI;

static class StartupTask
{
    const string Name = "Zerk Sense 5";

    public static bool IsEnabled() => Proc.Run("schtasks.exe", $"/Query /TN \"{Name}\"", 5000).code == 0;

    public static void Set(bool enabled)
    {
        var (code, output) = enabled
            ? Proc.Run("schtasks.exe",
                $"/Create /F /SC ONLOGON /RL HIGHEST /TN \"{Name}\" /TR \"\\\"{Environment.ProcessPath}\\\" --minimized\"")
            : Proc.Run("schtasks.exe", $"/Delete /F /TN \"{Name}\"");
        if (code != 0) Log.Warn($"Could not {(enabled ? "enable" : "disable")} start with Windows: {output}");
    }
}

static class SingleInstance
{
    const string EventName = @"Local\ZerkSense5.Show";

    public static void SignalExisting()
    {
        try
        {
            using var e = EventWaitHandle.OpenExisting(EventName);
            e.Set();
        }
        catch (WaitHandleCannotBeOpenedException)
        {
        }
    }

    public static void Listen(Action onShow)
    {
        var e = new EventWaitHandle(false, EventResetMode.AutoReset, EventName);
        new Thread(() =>
        {
            while (e.WaitOne()) onShow();
        }) { IsBackground = true, Name = "Single instance" }.Start();
    }
}
