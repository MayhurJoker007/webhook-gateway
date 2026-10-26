using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using WebhookGateway;

var builder = WebApplication.CreateBuilder(args);

builder.Services.Configure<GatewayOptions>(builder.Configuration.GetSection("Gateway"));
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddDbContext<GatewayDb>(o =>
    o.UseSqlite(builder.Configuration.GetConnectionString("Gateway") ?? "Data Source=gateway.db"));
builder.Services.AddHttpClient("delivery", c => c.Timeout = TimeSpan.FromSeconds(10));
builder.Services.AddSingleton<DeliveryWorker>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<DeliveryWorker>());

var app = builder.Build();

using (var scope = app.Services.CreateScope())
    scope.ServiceProvider.GetRequiredService<GatewayDb>().Database.EnsureCreated();

// Receive. Do as little as possible here: verify, store, answer. Senders usually time out after a few seconds
// and retry, so the slow part (calling subscribers) happens in the background worker.
app.MapPost("/hooks/{source}", async (string source, HttpRequest request, GatewayDb db,
    IOptions<GatewayOptions> options, TimeProvider clock, ILogger<Program> log) =>
{
    var opts = options.Value;
    if (!opts.Sources.TryGetValue(source, out var secret))
        return Results.NotFound();

    if (request.ContentLength > opts.MaxBodyBytes)
        return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);

    using var buffer = new MemoryStream();
    await request.Body.CopyToAsync(buffer);
    if (buffer.Length > opts.MaxBodyBytes)
        return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
    var body = buffer.ToArray();

    var check = Signatures.Verify(request.Headers["X-Signature"], secret, body, clock.GetUtcNow(),
        TimeSpan.FromSeconds(opts.ToleranceSeconds));
    if (check != Signatures.Result.Valid)
    {
        log.LogWarning("Rejected webhook from {Source}: {Reason}", source, check);
        return Results.Unauthorized();
    }

    string externalId, type;
    try
    {
        using var doc = JsonDocument.Parse(body);
        externalId = doc.RootElement.GetProperty("id").GetString() ?? throw new KeyNotFoundException();
        type = doc.RootElement.GetProperty("type").GetString() ?? throw new KeyNotFoundException();
    }
    catch (Exception e) when (e is JsonException or KeyNotFoundException or InvalidOperationException)
    {
        return Results.BadRequest(new { error = "body must be JSON with string 'id' and 'type'" });
    }

    // Senders retry. The same event id from the same source is accepted again but only stored once.
    var existing = await db.Events.FirstOrDefaultAsync(e => e.Source == source && e.ExternalId == externalId);
    if (existing is not null)
        return Results.Ok(new { id = existing.Id, duplicate = true });

    var now = clock.GetUtcNow().UtcDateTime;
    var ev = new InboundEvent
    {
        Source = source, ExternalId = externalId, Type = type,
        Payload = Encoding.UTF8.GetString(body), ReceivedUtc = now,
    };
    foreach (var sub in opts.Subscribers.Where(s => s.Wants(type)))
    {
        ev.Deliveries.Add(new Delivery
        {
            Subscriber = sub.Name, Url = sub.Url, Status = DeliveryStatus.Pending, NextAttemptUtc = now,
        });
    }
    db.Events.Add(ev);
    try
    {
        await db.SaveChangesAsync();
    }
    catch (DbUpdateException)
    {
        // Two copies of the same event arrived at the same moment; the other request stored it
        return Results.Ok(new { duplicate = true });
    }
    return Results.Accepted($"/events/{ev.Id}", new { id = ev.Id, deliveries = ev.Deliveries.Count });
});

app.MapGet("/events/{id:long}", async (long id, GatewayDb db) =>
    await db.Events.Include(e => e.Deliveries).FirstOrDefaultAsync(e => e.Id == id) is { } ev
        ? Results.Ok(new
        {
            ev.Id, ev.Source, ev.ExternalId, ev.Type, ev.ReceivedUtc,
            deliveries = ev.Deliveries.Select(d => new
            {
                d.Subscriber, status = d.Status.ToString(), d.Attempts, d.LastStatusCode, d.LastError, d.NextAttemptUtc,
            }),
        })
        : Results.NotFound());

// Send failed deliveries again, e.g. after a subscriber has fixed a bug
app.MapPost("/events/{id:long}/redeliver", async (long id, GatewayDb db, TimeProvider clock) =>
{
    var failed = await db.Deliveries.Where(d => d.EventId == id && d.Status == DeliveryStatus.Failed).ToListAsync();
    foreach (var d in failed)
    {
        d.Status = DeliveryStatus.Pending;
        d.Attempts = 0;
        d.NextAttemptUtc = clock.GetUtcNow().UtcDateTime;
    }
    await db.SaveChangesAsync();
    return Results.Ok(new { requeued = failed.Count });
});

app.MapGet("/health", () => Results.Ok("ok"));

app.Run();

public partial class Program;
