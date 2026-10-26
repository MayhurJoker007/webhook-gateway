using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using WebhookGateway;

namespace WebhookGateway.Tests;

public class GatewayTests : IDisposable
{
    private const string Secret = "test-secret";
    private readonly SqliteConnection _db = new("Data Source=:memory:");
    private readonly FakeClock _clock = new(DateTimeOffset.Parse("2026-10-14T09:00:00Z"));
    private readonly FakeSubscriber _subscriber = new();
    private readonly WebApplicationFactory<Program> _factory;

    public GatewayTests()
    {
        _db.Open();
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b => b.ConfigureServices(services =>
        {
            foreach (var d in services.Where(d => d.ServiceType == typeof(DbContextOptions<GatewayDb>) ||
                                                  d.ServiceType == typeof(IHostedService) ||
                                                  d.ServiceType == typeof(TimeProvider)).ToList())
                services.Remove(d);
            services.AddDbContext<GatewayDb>(o => o.UseSqlite(_db));
            services.AddSingleton<TimeProvider>(_clock);
            services.AddHttpClient("delivery").ConfigurePrimaryHttpMessageHandler(() => _subscriber);
            services.PostConfigure<GatewayOptions>(o =>
            {
                o.Sources = new() { ["payments"] = Secret };
                o.MaxAttempts = 3;
                o.Subscribers =
                [
                    new Subscriber { Name = "orders", Url = "http://orders.test/hook", Secret = "orders-secret", EventTypes = ["payment.*"] },
                    new Subscriber { Name = "refunds-only", Url = "http://refunds.test/hook", Secret = "r", EventTypes = ["payment.refunded"] },
                ];
            });
        }));
    }

    private Task<HttpResponseMessage> Send(string id, string type = "payment.succeeded", long? timestamp = null,
        string secret = Secret, string source = "payments")
    {
        var body = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { id, type, amount = 1499.00 }));
        var ts = timestamp ?? _clock.GetUtcNow().ToUnixTimeSeconds();
        var request = new HttpRequestMessage(HttpMethod.Post, $"/hooks/{source}") { Content = new ByteArrayContent(body) };
        request.Headers.Add("X-Signature", Signatures.Sign(secret, ts, body));
        return _factory.CreateClient().SendAsync(request);
    }

    private DeliveryWorker Worker => _factory.Services.GetRequiredService<DeliveryWorker>();

    [Fact]
    public async Task Accepts_a_signed_event_and_routes_by_type()
    {
        var response = await Send("evt_1");
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(1, body.GetProperty("deliveries").GetInt32());   // refunds-only doesn't want payment.succeeded
    }

    [Fact]
    public async Task Rejects_bad_signature_wrong_secret_and_unknown_source()
    {
        Assert.Equal(HttpStatusCode.Unauthorized, (await Send("evt_2", secret: "guess")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Send("evt_2", source: "nobody")).StatusCode);

        var request = new HttpRequestMessage(HttpMethod.Post, "/hooks/payments") { Content = new StringContent("{}") };
        Assert.Equal(HttpStatusCode.Unauthorized, (await _factory.CreateClient().SendAsync(request)).StatusCode);
    }

    [Fact]
    public async Task Rejects_replayed_old_requests()
    {
        var tenMinutesAgo = _clock.GetUtcNow().AddMinutes(-10).ToUnixTimeSeconds();
        Assert.Equal(HttpStatusCode.Unauthorized, (await Send("evt_3", timestamp: tenMinutesAgo)).StatusCode);
    }

    [Fact]
    public async Task Duplicate_event_ids_are_stored_once()
    {
        await Send("evt_4");
        var again = await Send("evt_4");
        Assert.Equal(HttpStatusCode.OK, again.StatusCode);
        Assert.True((await again.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("duplicate").GetBoolean());

        using var scope = _factory.Services.CreateScope();
        Assert.Equal(1, scope.ServiceProvider.GetRequiredService<GatewayDb>().Events.Count());
    }

    [Fact]
    public async Task Delivers_with_a_signature_the_subscriber_can_verify()
    {
        await Send("evt_5");
        Assert.Equal(1, await Worker.RunOnceAsync());

        var (request, body) = Assert.Single(_subscriber.Received);
        Assert.Equal("http://orders.test/hook", request.RequestUri!.ToString());
        Assert.Equal("payment.succeeded", request.Headers.GetValues("X-Webhook-Type").Single());
        var signature = request.Headers.GetValues("X-Webhook-Signature").Single();
        Assert.Equal(Signatures.Result.Valid,
            Signatures.Verify(signature, "orders-secret", body, _clock.GetUtcNow(), TimeSpan.FromMinutes(5)));
    }

    [Fact]
    public async Task Retries_server_errors_with_backoff_then_gives_up()
    {
        _subscriber.Respond = HttpStatusCode.ServiceUnavailable;
        await Send("evt_6");

        await Worker.RunOnceAsync();
        Assert.Equal(0, await Worker.RunOnceAsync());           // not due yet

        _clock.Advance(TimeSpan.FromMinutes(2));
        await Worker.RunOnceAsync();
        _clock.Advance(TimeSpan.FromMinutes(2));
        await Worker.RunOnceAsync();
        Assert.Equal(3, _subscriber.Received.Count);

        var status = await _factory.CreateClient().GetFromJsonAsync<JsonElement>("/events/1");
        var delivery = status.GetProperty("deliveries")[0];
        Assert.Equal("Failed", delivery.GetProperty("status").GetString());
        Assert.Equal(503, delivery.GetProperty("lastStatusCode").GetInt32());

        _subscriber.Respond = HttpStatusCode.OK;
        await _factory.CreateClient().PostAsync("/events/1/redeliver", null);
        await Worker.RunOnceAsync();
        status = await _factory.CreateClient().GetFromJsonAsync<JsonElement>("/events/1");
        Assert.Equal("Delivered", status.GetProperty("deliveries")[0].GetProperty("status").GetString());
    }

    [Fact]
    public async Task Client_errors_are_not_retried()
    {
        _subscriber.Respond = HttpStatusCode.BadRequest;
        await Send("evt_7");
        await Worker.RunOnceAsync();
        _clock.Advance(TimeSpan.FromHours(2));
        Assert.Equal(0, await Worker.RunOnceAsync());
    }

    [Theory]
    [InlineData("t=1,v1=abc,extra")]
    [InlineData("v1=abc")]
    [InlineData("t=abc,v1=abc")]
    public void Malformed_headers(string header) =>
        Assert.Equal(Signatures.Result.Malformed, Signatures.Verify(header, "s", [], DateTimeOffset.UtcNow, TimeSpan.FromMinutes(5)));

    public void Dispose()
    {
        _factory.Dispose();
        _db.Dispose();
    }

    private sealed class FakeSubscriber : HttpMessageHandler
    {
        public HttpStatusCode Respond { get; set; } = HttpStatusCode.OK;
        public List<(HttpRequestMessage Request, byte[] Body)> Received { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Received.Add((request, await request.Content!.ReadAsByteArrayAsync(ct)));
            return new HttpResponseMessage(Respond);
        }
    }

    private sealed class FakeClock(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset _now = start;
        public void Advance(TimeSpan by) => _now += by;
        public override DateTimeOffset GetUtcNow() => _now;
    }
}
