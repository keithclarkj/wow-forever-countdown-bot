using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

var token = Environment.GetEnvironmentVariable("DISCORD_BOT_TOKEN");
if (string.IsNullOrWhiteSpace(token))
    throw new InvalidOperationException("Set the DISCORD_BOT_TOKEN environment variable.");

using var configFile = File.OpenRead("appsettings.json");
var settings = await JsonSerializer.DeserializeAsync<Settings>(configFile, new JsonSerializerOptions
{
    PropertyNameCaseInsensitive = true
}) ?? throw new InvalidOperationException("Missing configuration.");

if (string.IsNullOrWhiteSpace(settings.EventName) ||
    string.IsNullOrWhiteSpace(settings.ChannelId) ||
    !ulong.TryParse(settings.ChannelId, out _) ||
    settings.TargetDateUtc.Offset != TimeSpan.Zero ||
    settings.UpdateIntervalMinutes < 1)
    throw new InvalidOperationException("Check EventName, numeric ChannelId, UTC date (Z), and update interval (at least 10 minutes).");

using var client = new HttpClient { BaseAddress = new Uri("https://discord.com/api/v10/") };
client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bot", token);
client.DefaultRequestHeaders.UserAgent.ParseAdd("DiscordCountdownBot/1.0");

using var shutdown = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; shutdown.Cancel(); };

Console.WriteLine($"Countdown started for '{settings.EventName}', target {settings.TargetDateUtc:u}.");

while (!shutdown.IsCancellationRequested)
{
    try
    {
        var remaining = settings.TargetDateUtc - DateTimeOffset.UtcNow;
        var name = remaining <= TimeSpan.Zero
            ? $"{settings.EventName}: Finished!"
            : $"\u267e\ufe0f {settings.EventName} \u267e\ufe0f {(int)remaining.TotalDays}d, {remaining.Hours}h, {remaining.Minutes}m";

        // Discord channel names have a 100-character maximum.
        if (name.Length > 100)
            throw new InvalidOperationException("Generated channel name exceeds 100 characters.");

        var path = $"channels/{settings.ChannelId}";
        using var get = await client.GetAsync(path, shutdown.Token);
        var getBody = await get.Content.ReadAsStringAsync(shutdown.Token);
        if (!get.IsSuccessStatusCode)
            throw new HttpRequestException($"Discord GET failed ({(int)get.StatusCode}): {getBody}");

        using var channel = JsonDocument.Parse(getBody);
        var currentName = channel.RootElement.GetProperty("name").GetString();

        if (currentName == name)
        {
            Console.WriteLine($"{DateTimeOffset.Now:t} Unchanged: {name}");
        }
        else
        {
            var payload = JsonSerializer.Serialize(new { name });
            using var patch = new HttpRequestMessage(HttpMethod.Patch, path)
            {
                Content = new StringContent(payload, Encoding.UTF8, "application/json")
            };
            using var response = await client.SendAsync(patch, shutdown.Token);
            var body = await response.Content.ReadAsStringAsync(shutdown.Token);
            if (!response.IsSuccessStatusCode)
            {
                if (response.StatusCode == HttpStatusCode.TooManyRequests)
                    Console.Error.WriteLine($"Rate limited by Discord; will retry next interval. {body}");
                else
                    throw new HttpRequestException($"Discord PATCH failed ({(int)response.StatusCode}): {body}");
            }
            else
            {
                Console.WriteLine($"{DateTimeOffset.Now:t} Updated: {name}");
            }
        }
    }
    catch (OperationCanceledException) when (shutdown.IsCancellationRequested)
    {
        break;
    }
    catch (Exception exception)
    {
        Console.Error.WriteLine($"{DateTimeOffset.Now:t} {exception.Message}");
    }

    try
    {
        await Task.Delay(TimeSpan.FromMinutes(settings.UpdateIntervalMinutes), shutdown.Token);
    }
    catch (OperationCanceledException) { break; }
}

internal sealed class Settings
{
    public string EventName { get; init; } = "";
    public DateTimeOffset TargetDateUtc { get; init; }
    public string ChannelId { get; init; } = "";
    public int UpdateIntervalMinutes { get; init; } = 10;
}
