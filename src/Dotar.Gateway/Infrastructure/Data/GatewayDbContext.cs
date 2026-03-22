using Dotar.Gateway.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace Dotar.Gateway.Infrastructure.Data;

/// <summary>
/// Contexto de base de datos SQLite con modo WAL para alto rendimiento.
/// </summary>
public class GatewayDbContext : DbContext
{
    public DbSet<Tenant> Tenants => Set<Tenant>();
    public DbSet<TenantGroup> TenantGroups => Set<TenantGroup>();
    public DbSet<DeliveryLog> DeliveryLogs => Set<DeliveryLog>();
    public DbSet<DeliveryAttempt> DeliveryAttempts => Set<DeliveryAttempt>();
    public DbSet<RetryPolicy> RetryPolicies => Set<RetryPolicy>();
    public DbSet<RetryStep> RetrySteps => Set<RetryStep>();
    public DbSet<AppSetting> AppSettings => Set<AppSetting>();

    public GatewayDbContext(DbContextOptions<GatewayDbContext> options)
        : base(options) { }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        // ─── RetryPolicy ───
        modelBuilder.Entity<RetryPolicy>(e =>
        {
            e.HasKey(p => p.Id);
            e.Property(p => p.Name).IsRequired().HasMaxLength(100);
            e.HasData(new RetryPolicy
            {
                Id = 1,
                Name = "Estándar",
                IsDefault = true,
                CircuitBreakerThreshold = 5,
                CircuitBreakerDurationSeconds = 30
            });
        });

        // ─── RetryStep ───
        modelBuilder.Entity<RetryStep>(e =>
        {
            e.HasKey(s => s.Id);
            e.Property(s => s.DelayUnit).HasConversion<string>().HasMaxLength(20);
            e.HasOne(s => s.RetryPolicy)
                .WithMany(p => p.Steps)
                .HasForeignKey(s => s.RetryPolicyId)
                .OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(s => new { s.RetryPolicyId, s.StepNumber }).IsUnique();

            // Seed: Política Estándar → 5s, 30s, 2min, 15min, 1h
            e.HasData(
                new RetryStep { Id = 1, RetryPolicyId = 1, StepNumber = 1, DelayValue = 5, DelayUnit = DelayUnit.Seconds },
                new RetryStep { Id = 2, RetryPolicyId = 1, StepNumber = 2, DelayValue = 30, DelayUnit = DelayUnit.Seconds },
                new RetryStep { Id = 3, RetryPolicyId = 1, StepNumber = 3, DelayValue = 2, DelayUnit = DelayUnit.Minutes },
                new RetryStep { Id = 4, RetryPolicyId = 1, StepNumber = 4, DelayValue = 15, DelayUnit = DelayUnit.Minutes },
                new RetryStep { Id = 5, RetryPolicyId = 1, StepNumber = 5, DelayValue = 1, DelayUnit = DelayUnit.Hours }
            );
        });

        // ─── TenantGroup ───
        modelBuilder.Entity<TenantGroup>(e =>
        {
            e.HasKey(g => g.Id);
            e.Property(g => g.Name).IsRequired().HasMaxLength(200);
            e.HasOne(g => g.RetryPolicy)
                .WithMany()
                .HasForeignKey(g => g.RetryPolicyId)
                .OnDelete(DeleteBehavior.SetNull);
        });

        // ─── Tenant ───
        modelBuilder.Entity<Tenant>(e =>
        {
            e.HasKey(t => t.Id);
            e.HasIndex(t => t.Slug).IsUnique();
            e.Property(t => t.Name).IsRequired().HasMaxLength(200);
            e.Property(t => t.Slug).IsRequired().HasMaxLength(100);
            e.Property(t => t.WebhookSecret).IsRequired().HasMaxLength(500);
            e.Property(t => t.TargetUrl).IsRequired().HasMaxLength(2000);
            e.HasOne(t => t.RetryPolicy)
                .WithMany(p => p.Tenants)
                .HasForeignKey(t => t.RetryPolicyId)
                .OnDelete(DeleteBehavior.SetNull);
            e.HasOne(t => t.TenantGroup)
                .WithMany(g => g.Tenants)
                .HasForeignKey(t => t.TenantGroupId)
                .OnDelete(DeleteBehavior.SetNull);
        });

        // ─── DeliveryLog ───
        modelBuilder.Entity<DeliveryLog>(e =>
        {
            e.HasKey(d => d.Id);
            e.HasIndex(d => d.WebhookEventId);
            e.HasIndex(d => new { d.TenantId, d.CreatedAt });
            e.HasIndex(d => d.Status);
            e.HasIndex(d => d.NextRetryAt);
            e.Property(d => d.Payload).HasColumnType("TEXT");
            e.Property(d => d.TargetUrl).HasMaxLength(2000);
            e.Property(d => d.Status).HasConversion<string>().HasMaxLength(20);
            e.HasOne(d => d.Tenant)
                .WithMany(t => t.DeliveryLogs)
                .HasForeignKey(d => d.TenantId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        // ─── DeliveryAttempt ───
        modelBuilder.Entity<DeliveryAttempt>(e =>
        {
            e.HasKey(a => a.Id);
            e.HasIndex(a => a.DeliveryLogId);
            e.HasOne(a => a.DeliveryLog)
                .WithMany(d => d.Attempts)
                .HasForeignKey(a => a.DeliveryLogId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        // ─── AppSetting ───
        modelBuilder.Entity<AppSetting>(e =>
        {
            e.HasKey(s => s.Id);
            e.HasIndex(s => s.Key).IsUnique();
            e.Property(s => s.Key).IsRequired().HasMaxLength(100);
            e.Property(s => s.Value).IsRequired().HasMaxLength(2000);
        });
    }
}
