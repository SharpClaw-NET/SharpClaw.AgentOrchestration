using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using SharpClaw.Contracts.Persistence;

namespace SharpClaw.Modules.Context;

public sealed class ContextDbContext(DbContextOptions<ContextDbContext> options) : DbContext(options)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public DbSet<ContextChannelEntity> Channels => Set<ContextChannelEntity>();
    public DbSet<ContextEntity> Contexts => Set<ContextEntity>();
    public DbSet<ContextThreadEntity> Threads => Set<ContextThreadEntity>();
    public DbSet<ContextMessageEntity> Messages => Set<ContextMessageEntity>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<ContextChannelEntity>(entity =>
        {
            entity.HasKey(item => item.Id);
            entity.HasIndex(item => item.OwnerAgentId);
            entity.HasIndex(item => item.ContextId);
            entity.Property(item => item.AllowedAgentIds)
                .HasConversion(
                    value => JsonSerializer.Serialize(value, JsonOptions),
                    value => JsonSerializer.Deserialize<IReadOnlyList<Guid>>(value, JsonOptions) ?? Array.Empty<Guid>());
            entity.Property(item => item.ContextAllowedAgentIds)
                .HasConversion(
                    value => JsonSerializer.Serialize(value, JsonOptions),
                    value => JsonSerializer.Deserialize<IReadOnlyList<Guid>>(value, JsonOptions) ?? Array.Empty<Guid>());
        });
        modelBuilder.Entity<ContextEntity>(entity =>
        {
            entity.HasKey(item => item.Id);
            entity.HasIndex(item => item.DefaultAgentId);
            entity.Property(item => item.AllowedAgentIds)
                .HasConversion(
                    value => JsonSerializer.Serialize(value, JsonOptions),
                    value => JsonSerializer.Deserialize<IReadOnlyList<Guid>>(value, JsonOptions) ?? Array.Empty<Guid>());
        });
        modelBuilder.Entity<ContextThreadEntity>(entity =>
        {
            entity.HasKey(item => item.Id);
            entity.HasIndex(item => item.ChannelId);
            entity.HasIndex(item => item.ContextId);
            entity.HasIndex(item => item.UpdatedAt);
            entity.HasOne<ContextChannelEntity>().WithMany().HasForeignKey(item => item.ChannelId);
            entity.HasOne<ContextEntity>().WithMany().HasForeignKey(item => item.ContextId);
        });
        modelBuilder.Entity<ContextMessageEntity>(entity =>
        {
            entity.HasKey(item => item.Id);
            entity.HasIndex(item => item.ThreadId);
            entity.HasIndex(item => item.CreatedAt);
            entity.HasOne<ContextThreadEntity>().WithMany().HasForeignKey(item => item.ThreadId);
        });
    }
}

public sealed class ContextChannelEntity
{
    public Guid Id { get; set; }
    public string Title { get; set; } = string.Empty;
    public Guid? OwnerAgentId { get; set; }
    public Guid? ContextId { get; set; }
    public Guid? DefaultContextAgentId { get; set; }
    public IReadOnlyList<Guid> AllowedAgentIds { get; set; } = [];
    public IReadOnlyList<Guid> ContextAllowedAgentIds { get; set; } = [];
    public bool CrossThreadOptedIn { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

public sealed class ContextEntity
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public Guid? DefaultAgentId { get; set; }
    public IReadOnlyList<Guid> AllowedAgentIds { get; set; } = [];
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

public sealed class ContextThreadEntity
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public Guid ChannelId { get; set; }
    public Guid? ContextId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

public sealed class ContextMessageEntity
{
    public Guid Id { get; set; }
    public Guid ThreadId { get; set; }
    public Guid ChannelId { get; set; }
    public string Role { get; set; } = string.Empty;
    public string Content { get; set; } = string.Empty;
    public string Sender { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}
