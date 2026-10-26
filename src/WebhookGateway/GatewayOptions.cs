namespace WebhookGateway;

public class GatewayOptions
{
    /// <summary>Inbound sources and their signing secrets, e.g. "payments" -> secret.</summary>
    public Dictionary<string, string> Sources { get; set; } = new();

    /// <summary>Where events go. An event is sent to every subscriber whose EventTypes match.</summary>
    public List<Subscriber> Subscribers { get; set; } = [];

    public int ToleranceSeconds { get; set; } = 300;
    public int MaxAttempts { get; set; } = 8;
    public int MaxBodyBytes { get; set; } = 256 * 1024;
}

public class Subscriber
{
    public required string Name { get; set; }
    public required string Url { get; set; }
    public required string Secret { get; set; }
    /// <summary>Exact types, or a prefix ending in * like "order.*". Empty means everything.</summary>
    public List<string> EventTypes { get; set; } = [];

    public bool Wants(string type) =>
        EventTypes.Count == 0 || EventTypes.Any(p => p.EndsWith('*') ? type.StartsWith(p[..^1]) : p == type);
}
