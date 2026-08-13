using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using SharpClaw.Contracts.Persistence;

namespace SharpClaw.Modules.Agents;

public sealed class AgentsDbContext(DbContextOptions<AgentsDbContext> options) : DbContext(options)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public DbSet<AgentEntity> Agents => Set<AgentEntity>();
    public DbSet<SkillEntity> Skills => Set<SkillEntity>();
    public DbSet<MemoryEntity> Memory => Set<MemoryEntity>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<AgentEntity>(entity =>
        {
            entity.HasKey(item => item.Id);
            entity.HasIndex(item => item.Name);
        });
        modelBuilder.Entity<SkillEntity>(entity =>
        {
            entity.HasKey(item => item.Id);
            entity.HasIndex(item => item.Name);
            entity.Property(item => item.AllowedAgentIds)
                .HasConversion(
                    value => JsonSerializer.Serialize(value, JsonOptions),
                    value => JsonSerializer.Deserialize<IReadOnlyList<Guid>>(value, JsonOptions) ?? Array.Empty<Guid>());
        });
        modelBuilder.Entity<MemoryEntity>(entity =>
        {
            entity.HasKey(item => item.Id);
            entity.HasIndex(item => new { item.AgentId, item.Key });
            entity.HasIndex(item => item.UpdatedAt);
            entity.Property(item => item.Tags)
                .HasConversion(
                    value => JsonSerializer.Serialize(value, JsonOptions),
                    value => JsonSerializer.Deserialize<IReadOnlyList<string>>(value, JsonOptions) ?? Array.Empty<string>());
        });
    }
}

public sealed class AgentEntity
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public Guid ModelId { get; set; }
    public string ProviderKey { get; set; } = string.Empty;
    public string? ModelName { get; set; }
    public string? SystemPrompt { get; set; }
    public string CreatedBy { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

public sealed class SkillEntity
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public string SkillText { get; set; } = string.Empty;
    public IReadOnlyList<Guid> AllowedAgentIds { get; set; } = [];
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

public sealed class MemoryEntity
{
    public Guid Id { get; set; }
    public Guid AgentId { get; set; }
    public string Key { get; set; } = string.Empty;
    public string Content { get; set; } = string.Empty;
    public IReadOnlyList<string> Tags { get; set; } = [];
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}
