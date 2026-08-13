using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using SharpClaw.Contracts.Persistence;

namespace SharpClaw.Modules.TwoTierPermission;

public sealed class PermissionDbContext(DbContextOptions<PermissionDbContext> options) : DbContext(options)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public DbSet<PermissionPolicyEntity> Policies => Set<PermissionPolicyEntity>();
    public DbSet<PermissionGrantEntity> Grants => Set<PermissionGrantEntity>();
    public DbSet<PermissionApprovalEntity> Approvals => Set<PermissionApprovalEntity>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<PermissionPolicyEntity>(entity =>
        {
            entity.HasKey(item => item.SubjectId);
            entity.HasIndex(item => item.Clearance);
            entity.Property(item => item.Roles)
                .HasConversion(
                    value => JsonSerializer.Serialize(value, JsonOptions),
                    value => JsonSerializer.Deserialize<IReadOnlyList<string>>(value, JsonOptions) ?? Array.Empty<string>());
            entity.Property(item => item.Capabilities)
                .HasConversion(
                    value => JsonSerializer.Serialize(value, JsonOptions),
                    value => JsonSerializer.Deserialize<IReadOnlyList<string>>(value, JsonOptions) ?? Array.Empty<string>());
            entity.Property(item => item.HardDeniedCapabilities)
                .HasConversion(
                    value => JsonSerializer.Serialize(value, JsonOptions),
                    value => JsonSerializer.Deserialize<IReadOnlyList<string>>(value, JsonOptions) ?? Array.Empty<string>());
            entity.Property(item => item.DelegatedBy)
                .HasConversion(
                    value => JsonSerializer.Serialize(value, JsonOptions),
                    value => JsonSerializer.Deserialize<IReadOnlyList<string>>(value, JsonOptions) ?? Array.Empty<string>());
        });
        modelBuilder.Entity<PermissionGrantEntity>(entity =>
        {
            entity.HasKey(item => item.GrantId);
            entity.HasIndex(item => new { item.SubjectId, item.Capability, item.Scope });
        });
        modelBuilder.Entity<PermissionApprovalEntity>(entity =>
        {
            entity.HasKey(item => item.ApprovalId);
            entity.HasIndex(item => new { item.SubjectId, item.Capability, item.Scope });
        });
    }
}

public sealed class PermissionPolicyEntity
{
    public string SubjectId { get; set; } = string.Empty;
    public IReadOnlyList<string> Roles { get; set; } = [];
    public IReadOnlyList<string> Capabilities { get; set; } = [];
    public IReadOnlyList<string> HardDeniedCapabilities { get; set; } = [];
    public PermissionClearance Clearance { get; set; }
    public bool RequireSourceOptIn { get; set; }
    public IReadOnlyList<string> DelegatedBy { get; set; } = [];
    public DateTimeOffset? ExpiresAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

public sealed class PermissionGrantEntity
{
    public string GrantId { get; set; } = string.Empty;
    public string SubjectId { get; set; } = string.Empty;
    public string Capability { get; set; } = string.Empty;
    public string Scope { get; set; } = string.Empty;
    public PermissionClearance Clearance { get; set; }
    public string GrantedBy { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? ExpiresAt { get; set; }
}

public sealed class PermissionApprovalEntity
{
    public string ApprovalId { get; set; } = string.Empty;
    public string SubjectId { get; set; } = string.Empty;
    public string Capability { get; set; } = string.Empty;
    public string Scope { get; set; } = string.Empty;
    public string ApprovedBy { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? ExpiresAt { get; set; }
}
