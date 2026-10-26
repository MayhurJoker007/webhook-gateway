using Microsoft.EntityFrameworkCore;

namespace WebhookGateway;

public enum DeliveryStatus { Pending, Delivered, Failed }

public class InboundEvent
{
    public long Id { get; set; }
    public required string Source { get; set; }
    public required string ExternalId { get; set; }      // the sender's event id, unique per source
    public required string Type { get; set; }
    public required string Payload { get; set; }
    public DateTime ReceivedUtc { get; set; }
    public List<Delivery> Deliveries { get; set; } = [];
}

/// <summary>One event going to one subscriber. Each has its own retry state, so one slow subscriber doesn't hold up the rest.</summary>
public class Delivery
{
    public long Id { get; set; }
    public long EventId { get; set; }
    public InboundEvent? Event { get; set; }
    public required string Subscriber { get; set; }
    public required string Url { get; set; }
    public DeliveryStatus Status { get; set; }
    public int Attempts { get; set; }
    public DateTime NextAttemptUtc { get; set; }
    public int? LastStatusCode { get; set; }
    public string? LastError { get; set; }
}

public class GatewayDb(DbContextOptions<GatewayDb> options) : DbContext(options)
{
    public DbSet<InboundEvent> Events => Set<InboundEvent>();
    public DbSet<Delivery> Deliveries => Set<Delivery>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<InboundEvent>().HasIndex(e => new { e.Source, e.ExternalId }).IsUnique();
        b.Entity<Delivery>().HasIndex(d => new { d.Status, d.NextAttemptUtc });
    }
}
