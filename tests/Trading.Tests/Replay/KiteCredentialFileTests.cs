using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Trading.Api;
using Xunit;

namespace Trading.Tests.Replay;

public sealed class KiteCredentialFileTests
{
    [Fact]
    public void Missing_credential_file_does_not_fall_back_to_merged_application_settings()
    {
        var root = CreateRoot();
        try
        {
            using var provider = Services(root).BuildServiceProvider();
            var credentials = provider.GetRequiredService<IOptionsMonitor<KiteCredentialOptions>>().CurrentValue;
            Assert.Equal("", credentials.ApiKey);
            Assert.Equal("", credentials.AccessToken);
            Assert.False(File.Exists(Path.Combine(root, KiteCredentialFile.FileName)));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task Local_file_is_authoritative_and_daily_edits_reload_without_rebuilding()
    {
        var root = CreateRoot();
        try
        {
            var path = Path.Combine(root, KiteCredentialFile.FileName);
            await File.WriteAllTextAsync(path, Json("file-key", "first-file-token"));
            using var provider = Services(root).BuildServiceProvider();
            var monitor = provider.GetRequiredService<IOptionsMonitor<KiteCredentialOptions>>();
            Assert.Equal("file-key", monitor.CurrentValue.ApiKey);
            Assert.Equal("first-file-token", monitor.CurrentValue.AccessToken);
            await File.WriteAllTextAsync(path, Json("file-key", "second-file-token"));
            await WaitForToken(monitor, "second-file-token");
            // An editor's incomplete save must not terminate the process; a valid save recovers.
            await File.WriteAllTextAsync(path, "{");
            await Task.Delay(500);
            await File.WriteAllTextAsync(path, Json("file-key", "third-file-token"));
            await WaitForToken(monitor, "third-file-token");
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static ServiceCollection Services(string root)
    {
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Kite:ApiKey"] = "unrelated-application-key",
            ["Kite:AccessToken"] = "unrelated-environment-or-user-secret-token"
        }).Build());
        services.AddKiteCredentialFile(root);
        return services;
    }

    private static string CreateRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "opencode", "kite-file-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }
    private static string Json(string key, string token) => System.Text.Json.JsonSerializer.Serialize(new { Kite = new { ApiKey = key, AccessToken = token } });
    private static async Task WaitForToken(IOptionsMonitor<KiteCredentialOptions> monitor, string token)
    {
        for (var i = 0; i < 100 && monitor.CurrentValue.AccessToken != token; i++) await Task.Delay(50);
        Assert.Equal(token, monitor.CurrentValue.AccessToken);
    }
}
