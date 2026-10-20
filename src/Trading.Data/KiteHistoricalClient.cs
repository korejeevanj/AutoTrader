using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Trading.Data;

public sealed record KiteCredentials([property: JsonIgnore] string ApiKey, [property: JsonIgnore] string AccessToken)
{
    public bool Configured => !string.IsNullOrWhiteSpace(ApiKey) && !string.IsNullOrWhiteSpace(AccessToken)
        && !ApiKey.ContainsAny('\r', '\n', ':') && !AccessToken.ContainsAny('\r', '\n', ':');
    [JsonIgnore] public string Fingerprint => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(ApiKey + ":" + AccessToken)));
    public override string ToString() => "KiteCredentials { Redacted }";
}
public sealed class KiteDataException(string code) : Exception(code)
{
    public string Code { get; } = code;
}
public sealed record KiteRequestAttempt(int Attempt, int? StatusCode, double ElapsedMilliseconds);

public sealed class KiteHistoricalClient(HttpClient client, Func<KiteCredentials> credentials,
    TimeProvider clock, TimeSpan spacing, int maximumAttempts = 3, TimeSpan? requestTimeout = null)
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private long lastRequestTimestamp;
    private bool requestStarted;
    private string? rejectedCredentials;
    private string? validatedCredentials;
    public string CredentialStatus
    {
        get { var current = credentials(); return !current.Configured ? "Missing" : rejectedCredentials == current.Fingerprint ? "AuthenticationRequired"
            : validatedCredentials == current.Fingerprint ? "Validated" : "ConfiguredNotVerified"; }
    }

    public Task<byte[]> HistoricalAsync(long instrumentToken, DateOnly date, TimeOnly start, TimeOnly end, CancellationToken cancellationToken, Action<KiteRequestAttempt>? onAttempt = null)
    {
        if (instrumentToken <= 0 || start >= end) throw new ArgumentException("Invalid instrument or session.");
        var from = date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + " " + start.ToString("HH:mm:ss", CultureInfo.InvariantCulture);
        // Kite examples include the 'to' candle: avoid admitting a bar starting at session close.
        var to = date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + " " + end.Add(TimeSpan.FromSeconds(-1)).ToString("HH:mm:ss", CultureInfo.InvariantCulture);
        return GetAsync($"instruments/historical/{instrumentToken}/minute?from={Uri.EscapeDataString(from)}&to={Uri.EscapeDataString(to)}&continuous=0&oi=1", cancellationToken, onAttempt);
    }
    public Task<byte[]> MasterAsync(CancellationToken cancellationToken) => GetAsync("instruments", cancellationToken);

    private async Task<byte[]> GetAsync(string path, CancellationToken cancellationToken, Action<KiteRequestAttempt>? onAttempt = null)
    {
        if (spacing < TimeSpan.Zero || maximumAttempts < 1 || requestTimeout <= TimeSpan.Zero) throw new ArgumentException("Invalid provider request policy.");
        for (var attempt = 1; attempt <= maximumAttempts; attempt++)
        {
            var secret = credentials();
            if (!secret.Configured) throw new KiteDataException("CREDENTIALS_MISSING");
            var started = clock.GetTimestamp();
            using var read = new BoundedSourceRead(requestTimeout ?? TimeSpan.FromSeconds(30), clock, cancellationToken);
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, "https://api.kite.trade/" + path);
                request.Headers.Add("X-Kite-Version", "3");
                request.Headers.Authorization = new AuthenticationHeaderValue("token", secret.ApiKey + ":" + secret.AccessToken);
                using var response = await SendThrottledAsync(request, read.Token);
                onAttempt?.Invoke(new(attempt, (int)response.StatusCode, clock.GetElapsedTime(started).TotalMilliseconds));
                if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                {
                    var tokenError = response.StatusCode == HttpStatusCode.Unauthorized;
                    try
                    {
                        using var body = JsonDocument.Parse(await read.ResponseAsync(response.Content, 64 * 1024 * 1024,
                            () => new KiteDataException("RESPONSE_TOO_LARGE")));
                        tokenError |= body.RootElement.ValueKind == JsonValueKind.Object && body.RootElement.TryGetProperty("error_type", out var type)
                            && type.ValueKind == JsonValueKind.String && type.GetString() == "TokenException";
                    }
                    catch (JsonException) { }
                    if (tokenError) { rejectedCredentials = secret.Fingerprint; throw new KiteDataException("AUTHENTICATION_REQUIRED"); }
                    throw new KiteDataException("ACCESS_DENIED");
                }
                if ((int)response.StatusCode == 429 || (int)response.StatusCode >= 500)
                {
                    if (attempt == maximumAttempts) throw new KiteDataException((int)response.StatusCode == 429 ? "RATE_LIMITED" : "PROVIDER_UNAVAILABLE");
                    await Task.Delay(TimeSpan.FromMilliseconds(500 * attempt), clock, cancellationToken);
                    continue;
                }
                if (!response.IsSuccessStatusCode) throw new KiteDataException("INSTRUMENT_OR_DATA_UNAVAILABLE");
                var bytes = await read.ResponseAsync(response.Content, 64 * 1024 * 1024, () => new KiteDataException("RESPONSE_TOO_LARGE"));
                rejectedCredentials = null;
                validatedCredentials = secret.Fingerprint;
                return bytes;
            }
            catch (Exception error) when (error is HttpRequestException or IOException || error is OperationCanceledException && !cancellationToken.IsCancellationRequested)
            {
                onAttempt?.Invoke(new(attempt, null, clock.GetElapsedTime(started).TotalMilliseconds));
                if (attempt == maximumAttempts) throw new KiteDataException("PROVIDER_UNAVAILABLE");
                await Task.Delay(TimeSpan.FromMilliseconds(500 * attempt), clock, cancellationToken);
            }
        }
        throw new KiteDataException("PROVIDER_UNAVAILABLE");
    }
    private async Task<HttpResponseMessage> SendThrottledAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            while (requestStarted)
            {
                var wait = spacing - clock.GetElapsedTime(lastRequestTimestamp);
                if (wait <= TimeSpan.Zero) break;
                await Task.Delay(wait + TimeSpan.FromMilliseconds(1), clock, cancellationToken);
            }
            try { return await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken); }
            finally
            {
                // Space from response headers (or failure), keeping the gate through send.
                // A descheduled caller must not let a later reservation overtake its request.
                lastRequestTimestamp = clock.GetTimestamp(); requestStarted = true;
            }
        }
        finally { gate.Release(); }
    }
}

internal static class CredentialTextExtensions
{
    public static bool ContainsAny(this string value, params char[] chars) => value.IndexOfAny(chars) >= 0;
}
