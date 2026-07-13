using Microsoft.EntityFrameworkCore;

namespace Level6.TieredQuota.Domain;

public sealed class QuotaDbContext : DbContext
{
    public QuotaDbContext(DbContextOptions<QuotaDbContext> options) : base(options) { }

    public DbSet<Plan> Plans => Set<Plan>();
    public DbSet<Account> Accounts => Set<Account>();
    public DbSet<QuotaAuditEntry> AuditEntries => Set<QuotaAuditEntry>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<Plan>(e =>
        {
            e.ToTable("plans");
            e.HasKey(p => p.Tier);
            e.Property(p => p.Tier).HasConversion<string>();
            e.Property(p => p.Overage).HasConversion<string>();
        });

        b.Entity<Account>(e =>
        {
            e.ToTable("accounts");
            e.HasKey(a => a.UserId);
            e.Property(a => a.Tier).HasConversion<string>();
            // Map to Postgres system column xmin for optimistic concurrency.
            e.Property(a => a.Version).HasColumnName("xmin").HasColumnType("xid").ValueGeneratedOnAddOrUpdate().IsConcurrencyToken();
        });

        b.Entity<QuotaAuditEntry>(e =>
        {
            e.ToTable("quota_audit");
            e.HasKey(a => a.Id);
            e.Property(a => a.Id).ValueGeneratedOnAdd();
        });
    }

    /// <summary>Creates the schema if needed, then seeds any missing tiers from <paramref name="plans"/>. Idempotent.</summary>
    public async Task InitializeAsync(IEnumerable<Plan> plans)
    {
        await Database.EnsureCreatedAsync();

        if (!await Plans.AnyAsync())
        {
            Plans.AddRange(plans);
            await SaveChangesAsync();
        }
    }
}
