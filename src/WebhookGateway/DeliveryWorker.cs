using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace WebhookGateway;

/// <summary>
/// Sends pending deliveries. Failures back off exponentially with jitter (10s, 20s, 40s ... capped at an hour)
/// so a subscriber that's down doesn't get hammered the moment it comes back.
/// </summary>
public class DeliveryWorker(IServiceScopeFactory scopes, IHttpClientFactory http, IOptions<GatewayOptions> options,
    TimeProvider clock, ILogger<DeliveryWorker> log) : BackgroundService
{
    public static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(2);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RunOnceAsync(stoppingToken);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                log.LogError(e, "Delivery pass failed");
            }
            await Task.Delay(PollInterval, clock, stoppingToken).ContinueWith(_ => { });
        }
    }

    public async Task<int> RunOnceAsync(CancellationToken ct = default)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<GatewayDb>();
        var now = clock.GetUtcNow().UtcDateTime;

        var due = await db.Deliveries.Include(d => d.Event)
            .Where(d => d.Status == DeliveryStatus.Pending && d.NextAttemptUtc <= now)
            .OrderBy(d => d.NextAttemptUtc)
            .Take(50)
            .ToListAsync(ct);

        foreach (var delivery in due)
            await SendAsync(delivery, ct);

        await db.SaveChangesAsync(ct);
        return due.Count;
    }

    private async Task SendAsync(Delivery delivery, CancellationToken ct)
    {
        var subscriber = options.Value.Subscribers.FirstOrDefault(s => s.Name == delivery.Subscriber);
        delivery.Attempts++;
        if (subscriber is null)
        {
            delivery.Status = DeliveryStatus.Failed;
            delivery.LastError = "subscriber no longer configured";
            return;
        }

        var body = Encoding.UTF8.GetBytes(delivery.Event!.Payload);
        var timestamp = clock.GetUtcNow().ToUnixTimeSeconds();
        using var request = new HttpRequestMessage(HttpMethod.Post, delivery.Url)
        {
            Content = new ByteArrayContent(body) { Headers = { { "Content-Type", "application/json" } } },
        };
        request.Headers.Add("X-Webhook-Id", $"{delivery.EventId}");
        request.Headers.Add("X-Webhook-Type", delivery.Event.Type);
        request.Headers.Add("X-Webhook-Attempt", $"{delivery.Attempts}");
        request.Headers.Add("X-Webhook-Signature", Signatures.Sign(subscriber.Secret, timestamp, body));

        try
        {
            using var client = http.CreateClient("delivery");
            using var response = await client.SendAsync(request, ct);
            delivery.LastStatusCode = (int)response.StatusCode;
            if (response.IsSuccessStatusCode)
            {
                delivery.Status = DeliveryStatus.Delivered;
                delivery.LastError = null;
                return;
            }
            delivery.LastError = $"HTTP {(int)response.StatusCode}";
            // 4xx other than 408/429 means the subscriber rejected the content; retrying won't change that
            if ((int)response.StatusCode is >= 400 and < 500 and not 408 and not 429)
            {
                delivery.Status = DeliveryStatus.Failed;
                return;
            }
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            delivery.LastStatusCode = null;
            delivery.LastError = e.Message;
        }

        if (delivery.Attempts >= options.Value.MaxAttempts)
        {
            delivery.Status = DeliveryStatus.Failed;
            log.LogWarning("Giving up on delivery {Id} to {Subscriber}: {Error}", delivery.Id, delivery.Subscriber, delivery.LastError);
            return;
        }
        delivery.NextAttemptUtc = clock.GetUtcNow().UtcDateTime + Backoff(delivery.Attempts);
    }

    public static TimeSpan Backoff(int attempt)
    {
        var seconds = Math.Min(3600, 10 * Math.Pow(2, attempt - 1));
        var jitter = Random.Shared.NextDouble() * 0.2 * seconds;
        return TimeSpan.FromSeconds(seconds + jitter);
    }
}
