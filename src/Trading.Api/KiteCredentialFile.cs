using Microsoft.Extensions.Options;

namespace Trading.Api;

// Deliberately separate from merged app configuration: env/user-secrets/CLI values
// cannot silently override the file that the operator edits each day.
public sealed class KiteCredentialFile : IDisposable
{
    public const string FileName = "kite.credentials.Local.json";
    private readonly IConfigurationRoot configuration;

    public KiteCredentialFile(string contentRoot)
    {
        configuration = new ConfigurationBuilder().SetBasePath(contentRoot)
            .AddJsonFile(source =>
            {
                source.Path = FileName;
                source.Optional = true;
                source.ReloadOnChange = true;
                // An incomplete edit must not terminate the host or fall back to env.
                source.OnLoadException = context => context.Ignore = true;
            }).Build();
    }

    public IConfigurationSection Section => configuration.GetSection("Kite");
    public void Dispose() => (configuration as IDisposable)?.Dispose();
}

public static class KiteCredentialFileRegistration
{
    public static IServiceCollection AddKiteCredentialFile(this IServiceCollection services, string contentRoot)
    {
        services.AddOptions<KiteCredentialOptions>();
        services.AddSingleton(_ => new KiteCredentialFile(contentRoot));
        services.AddSingleton<IConfigureOptions<KiteCredentialOptions>>(provider =>
            new NamedConfigureFromConfigurationOptions<KiteCredentialOptions>(Options.DefaultName,
                provider.GetRequiredService<KiteCredentialFile>().Section));
        services.AddSingleton<IOptionsChangeTokenSource<KiteCredentialOptions>>(provider =>
            new ConfigurationChangeTokenSource<KiteCredentialOptions>(Options.DefaultName,
                provider.GetRequiredService<KiteCredentialFile>().Section));
        return services;
    }
}
