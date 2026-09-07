using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace TrackMeBaby.Data;

/// <summary>
/// Normalises every DateTimeOffset to UTC on the way in.
///
/// Npgsql rejects any offset other than zero for timestamptz, and git commit timestamps carry
/// the author's local offset (a commit made in India arrives as +05:30). Rather than remembering
/// to call ToUniversalTime at each of those call sites, it is enforced here once.
/// </summary>
public class UtcDateTimeOffsetConverter() : ValueConverter<DateTimeOffset, DateTimeOffset>(
    write => write.ToUniversalTime(),
    read => read);

public class TrackerDbContext(DbContextOptions<TrackerDbContext> options) : DbContext(options)
{
    public DbSet<Source> Sources => Set<Source>();
    public DbSet<Repository> Repositories => Set<Repository>();
    public DbSet<Activity> Activities => Set<Activity>();
    public DbSet<Project> Projects => Set<Project>();
    public DbSet<ProjectItem> ProjectItems => Set<ProjectItem>();
    public DbSet<SyncState> SyncStates => Set<SyncState>();
    public DbSet<Memory> Memories => Set<Memory>();
    public DbSet<PerformanceSnapshot> PerformanceSnapshots => Set<PerformanceSnapshot>();
    public DbSet<ReleaseTag> ReleaseTags => Set<ReleaseTag>();
    public DbSet<AuditEntry> AuditEntries => Set<AuditEntry>();

    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder) =>
        configurationBuilder.Properties<DateTimeOffset>().HaveConversion<UtcDateTimeOffsetConverter>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<Source>(e =>
        {
            e.ToTable("sources");
            e.HasIndex(x => x.Name).IsUnique();
        });

        b.Entity<Repository>(e =>
        {
            e.ToTable("repositories");
            e.Property(x => x.Id).ValueGeneratedNever();
            e.HasIndex(x => x.FullName).IsUnique();
        });

        b.Entity<Activity>(e =>
        {
            e.ToTable("activities");
            e.Property(x => x.Payload).HasColumnType("jsonb");
            // The dedup guarantee behind FR-06.
            e.HasIndex(x => new { x.Source, x.SourceId, x.ActivityType }).IsUnique();
            // Every read tool filters "mine, in this window, newest first".
            e.HasIndex(x => new { x.IsMine, x.OccurredAt });
            e.HasIndex(x => new { x.ActivityType, x.OccurredAt });
            // Composite because the sync's "have I already got this, and is it current?" check
            // filters on repository and number together, once per search hit.
            e.HasIndex(x => new { x.RepositoryFullName, x.Number });
        });

        b.Entity<Project>(e =>
        {
            e.ToTable("projects");
            e.Property(x => x.Id).ValueGeneratedNever();
            e.Property(x => x.StatusOptions).HasColumnType("jsonb");
            e.HasMany(x => x.Items).WithOne(x => x.Project!)
                .HasForeignKey(x => x.ProjectId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<ProjectItem>(e =>
        {
            e.ToTable("project_items");
            e.Property(x => x.Id).ValueGeneratedNever();
            e.HasIndex(x => new { x.IsMine, x.Status });
            e.HasIndex(x => x.ItemUpdatedAt);
        });

        b.Entity<SyncState>(e =>
        {
            e.ToTable("sync_states");
            e.HasKey(x => x.Source);
        });

        b.Entity<Memory>(e =>
        {
            e.ToTable("memories");
            e.HasIndex(x => x.OccurredAt);
            e.HasIndex(x => x.Kind);
            // Postgres maintains this column itself, so search_memory needs no extra write path.
            e.HasGeneratedTsVectorColumn(x => x.SearchVector!, "english", x => new { x.Title, x.Content })
                .HasIndex(x => x.SearchVector).HasMethod("GIN");
        });

        b.Entity<PerformanceSnapshot>(e =>
        {
            e.ToTable("performance_snapshots");
            e.Property(x => x.Metrics).HasColumnType("jsonb");
            e.HasIndex(x => new { x.PeriodType, x.PeriodStart, x.PeriodEnd }).IsUnique();
        });

        b.Entity<ReleaseTag>(e =>
        {
            e.ToTable("release_tags");
            e.HasIndex(x => new { x.RepositoryFullName, x.Name }).IsUnique();
            e.HasIndex(x => x.OccurredAt);
        });

        b.Entity<AuditEntry>(e =>
        {
            e.ToTable("audit_entries");
            e.HasIndex(x => x.CreatedAt);
        });
    }
}
