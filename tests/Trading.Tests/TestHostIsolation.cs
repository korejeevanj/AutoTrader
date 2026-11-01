using System.Runtime.CompilerServices;

namespace Trading.Tests;

// Every WebApplicationFactory<Program> host in this assembly would otherwise use the real data/live folder, and its archiver compresses
// closed day files after 15:45 IST. Tests must never read, write or archive live recordings, so default hosts get a throwaway root.
// Tests that need a specific root still set one explicitly through PostConfigure.
internal static class TestHostIsolation
{
    public static readonly string LiveRoot = Path.Combine(Path.GetTempPath(), "autotrader-tests-live-" + Guid.NewGuid().ToString("N"));

    [ModuleInitializer]
    internal static void Apply()
    {
        Environment.SetEnvironmentVariable("LivePaper__RootDirectory", LiveRoot);
        Environment.SetEnvironmentVariable("LivePaper__ArchiveClosedDays", "false");
        Environment.SetEnvironmentVariable("LivePaper__ConnectOnStartup", "false");
    }
}
