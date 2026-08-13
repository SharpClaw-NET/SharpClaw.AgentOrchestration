using Microsoft.EntityFrameworkCore;
using SharpClaw.Contracts.Persistence;

namespace SharpClaw.Modules.Context;

public sealed class ContextDbContext(DbContextOptions<ContextDbContext> options) : DbContext(options)
{
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
        });
        modelBuilder.Entity<ContextEntity>(entity =>
        {
            entity.HasKey(item => item.Id);
            entity.HasIndex(item => item.DefaultAgentId);
        });
        modelBuilder.Entity<ContextThreadEntity>(entity =>
        {
            entity.HasKey(item => item.Id);
            entity.HasIndex(item => item.ChannelId);
            entity.HasIndex(item => item.UpdatedAt);
        });
        modelBuilder.Entity<ContextMessageEntity>(entity =>
        {
            entity.HasKey(item => item.Id);
            entity.HasIndex(item => item.ThreadId);
            entity.HasIndex(item => item.CreatedAt);
        });
    }
}

public sealed class ContextChannelEntity
{
    public Guid Id { get; set; }
    public string Title { get; set; } = string.Empty;
    public Guid? OwnerAgentId { get; set; }
    public Guid? DefaultContextAgentId { get; set; }
    public bool CrossThreadOptedIn { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

public sealed class ContextEntity
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public Guid? DefaultAgentId { get; set; }
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
