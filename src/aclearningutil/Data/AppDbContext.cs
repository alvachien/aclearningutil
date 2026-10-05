using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using aclearningutil.Data.Entities;

namespace aclearningutil.Data;

public class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }

    public DbSet<TtsMapping> TtsMappings => Set<TtsMapping>();
    public DbSet<LearningContentCategory> LearningContentCategories => Set<LearningContentCategory>();
    public DbSet<LearningContent> LearningContents => Set<LearningContent>();
    public DbSet<UserLearningHistory> UserLearningHistories => Set<UserLearningHistory>();
    public DbSet<UserLearningRating> UserLearningRatings => Set<UserLearningRating>();
    public DbSet<UserLoginHistory> UserLoginHistories => Set<UserLoginHistory>();

    // Habit tracking (see docs/design-habit-api.md)
    public DbSet<Habit> Habits => Set<Habit>();
    public DbSet<HabitItem> HabitItems => Set<HabitItem>();
    public DbSet<ItemProperty> ItemProperties => Set<ItemProperty>();
    public DbSet<Criterion> Criteria => Set<Criterion>();
    public DbSet<CriterionCondition> CriterionConditions => Set<CriterionCondition>();
    public DbSet<CriterionConditionScopeItem> CriterionConditionScopeItems => Set<CriterionConditionScopeItem>();
    public DbSet<CriterionComposite> CriterionComposites => Set<CriterionComposite>();
    public DbSet<CriterionCompositeOperand> CriterionCompositeOperands => Set<CriterionCompositeOperand>();
    public DbSet<Punch> Punches => Set<Punch>();
    public DbSet<PunchValue> PunchValues => Set<PunchValue>();
    public DbSet<HabitShareGrant> HabitShareGrants => Set<HabitShareGrant>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<TtsMapping>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => e.Sentence).IsUnique();
            entity.Property(e => e.Sentence).IsRequired().HasMaxLength(2000);
            entity.Property(e => e.FileName).IsRequired().HasMaxLength(500);
            entity.Property(e => e.CreatedAt).HasDefaultValueSql("datetime('now')");
        });

        modelBuilder.Entity<LearningContentCategory>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.NameChinese).IsRequired().HasMaxLength(200);
            entity.Property(e => e.NameEnglish).IsRequired().HasMaxLength(200);

            // Seed default categories
            entity.HasData(
                new LearningContentCategory { Id = 1, NameChinese = "词汇", NameEnglish = "Vocabulary" },
                new LearningContentCategory { Id = 2, NameChinese = "句子", NameEnglish = "Sentences" },
                new LearningContentCategory { Id = 3, NameChinese = "听力", NameEnglish = "Listening" },
                new LearningContentCategory { Id = 4, NameChinese = "中文", NameEnglish = "Chinese" },
                new LearningContentCategory { Id = 5, NameChinese = "公式", NameEnglish = "Formula" },
                new LearningContentCategory { Id = 6, NameChinese = "知识库", NameEnglish = "Knowledge Bank" }
            );
        });

        modelBuilder.Entity<LearningContent>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.CategoryId).IsRequired();
            entity.Property(e => e.NameChinese).IsRequired().HasMaxLength(500);
            entity.Property(e => e.NameEnglish).IsRequired().HasMaxLength(500);
            entity.Property(e => e.FileUrl).IsRequired().HasMaxLength(1000);
            entity.Property(e => e.Version).HasColumnType("tinyint");
            entity.Property(e => e.IncludeLatex);
            entity.Property(e => e.TranslationDisabled);
            entity.Property(e => e.CreatedAt).HasDefaultValueSql("datetime('now')");
            entity.Property(e => e.UpdatedAt).HasDefaultValueSql("datetime('now')");

            entity.HasIndex(e => e.CategoryId);

            entity.HasOne(e => e.Category)
                .WithMany()
                .HasForeignKey(e => e.CategoryId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<UserLearningHistory>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.UserId).IsRequired().HasMaxLength(200);
            entity.Property(e => e.ContentId).IsRequired();
            entity.Property(e => e.LearnDate).HasDefaultValueSql("date('now')");

            entity.HasIndex(e => e.UserId);
            entity.HasIndex(e => e.ContentId);

            entity.HasOne(e => e.Content)
                .WithMany()
                .HasForeignKey(e => e.ContentId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<UserLearningRating>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.UserId).IsRequired().HasMaxLength(200);
            entity.Property(e => e.ContentId).IsRequired();
            entity.Property(e => e.ScoreDate).HasDefaultValueSql("date('now')");
            entity.Property(e => e.Rating).IsRequired();

            entity.HasIndex(e => e.UserId);
            entity.HasIndex(e => e.ContentId);
            // One rating row per user/content/item triple; guards against
            // duplicate rows from concurrent create requests.
            entity.HasIndex(e => new { e.UserId, e.ContentId, e.ItemId }).IsUnique();

            entity.HasOne(e => e.Content)
                .WithMany()
                .HasForeignKey(e => e.ContentId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        // ── Habit tracking (see docs/design-habit-api.md) ──────────────────────────
        // Enums are stored as strings (PascalCase members); the API wire format maps them
        // to the spec's lowercase/snake values via the JSON enum converter in Program.cs.
        // Timestamps are written as UTC instants but SQLite reads them back with
        // Kind=Unspecified — the read-side converter restores Utc so response JSON ends
        // with 'Z' (store format unchanged).

        var utcTimestampRead = new ValueConverter<DateTime, DateTime>(
            v => v,
            v => DateTime.SpecifyKind(v, DateTimeKind.Utc));

        modelBuilder.Entity<Habit>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.OwnerId).IsRequired().HasMaxLength(200);
            entity.Property(e => e.Name).IsRequired().HasMaxLength(500);
            entity.Property(e => e.Description).HasMaxLength(2000);
            entity.Property(e => e.Cycle).HasConversion<string>().HasMaxLength(20);
            entity.Property(e => e.State).HasConversion<string>().HasMaxLength(20);
            entity.Property(e => e.CreatedAt).HasConversion(utcTimestampRead);

            entity.HasIndex(e => e.OwnerId);

            // Upgrade path for databases created before DeactivatedDate existed: the column
            // is nullable and additive — HabitSchemaBootstrap adds it via ALTER when missing.
        });

        modelBuilder.Entity<HabitShareGrant>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.OwnerId).IsRequired().HasMaxLength(200);
            entity.Property(e => e.GranteeUserId).IsRequired().HasMaxLength(200);
            entity.Property(e => e.GranteeUserName).IsRequired().HasMaxLength(500);
            entity.Property(e => e.OwnerName).IsRequired().HasMaxLength(500);
            entity.Property(e => e.CreatedAt).HasConversion(utcTimestampRead);

            entity.HasIndex(e => e.GranteeUserId);
            // One grant per user per habit (maps to duplicateName at app level too).
            entity.HasIndex(e => new { e.HabitId, e.GranteeUserId }).IsUnique();

            entity.HasOne(e => e.Habit)
                .WithMany()
                .HasForeignKey(e => e.HabitId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<HabitItem>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.OwnerId).IsRequired().HasMaxLength(200);
            entity.Property(e => e.Name).IsRequired().HasMaxLength(500);
            entity.Property(e => e.CreatedAt).HasConversion(utcTimestampRead);

            entity.HasIndex(e => e.HabitId);
            // Item name uniqueness within the habit (maps to duplicateName at app level too).
            entity.HasIndex(e => new { e.HabitId, e.Name }).IsUnique();

            entity.HasOne(e => e.Habit)
                .WithMany(h => h.Items)
                .HasForeignKey(e => e.HabitId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<ItemProperty>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.OwnerId).IsRequired().HasMaxLength(200);
            entity.Property(e => e.Name).IsRequired().HasMaxLength(500);
            entity.Property(e => e.PropertyType).HasConversion<string>().HasMaxLength(20);
            entity.Property(e => e.ItemUniqueness).HasConversion<string>().HasMaxLength(20);
            entity.Property(e => e.CreatedAt).HasConversion(utcTimestampRead);

            entity.HasIndex(e => e.ItemId);
            entity.HasIndex(e => e.HabitId);
            // Property name uniqueness within the item.
            entity.HasIndex(e => new { e.ItemId, e.Name }).IsUnique();

            entity.HasOne(e => e.Item)
                .WithMany(i => i.Properties)
                .HasForeignKey(e => e.ItemId)
                .OnDelete(DeleteBehavior.Cascade);

            // Navigation used for FK fix-up when properties are created with their habit
            // (HabitId is a redundant copy for filtering, not a separately assigned value).
            entity.HasOne(e => e.Habit)
                .WithMany()
                .HasForeignKey(e => e.HabitId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<Criterion>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.OwnerId).IsRequired().HasMaxLength(200);
            entity.Property(e => e.Name).IsRequired().HasMaxLength(500);
            entity.Property(e => e.CriterionType).HasConversion<string>().HasMaxLength(20);
            entity.Property(e => e.SuccessType).HasConversion<string>().HasMaxLength(20);
            entity.Property(e => e.CreatedAt).HasConversion(utcTimestampRead);

            entity.HasIndex(e => e.HabitId);
            // Criterion name uniqueness within the habit.
            entity.HasIndex(e => new { e.HabitId, e.Name }).IsUnique();
            // Single-root invariant enforced at the DB level (SQLite partial index).
            entity.HasIndex(e => e.HabitId)
                .IsUnique()
                .HasFilter("[IsRoot] = 1")
                .HasDatabaseName("IX_Criteria_HabitId_Root");

            entity.HasOne(e => e.Habit)
                .WithMany(h => h.Criteria)
                .HasForeignKey(e => e.HabitId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<CriterionCondition>(entity =>
        {
            // FROZEN physical identifiers: the tables were created when the criterion type
            // was called "leaf"; the names are pinned so existing SQLite files and the
            // raw SQL in HabitSchemaBootstrap stay valid after the terminology rename.
            entity.ToTable("CriterionLeaves");
            entity.HasKey(e => e.Id);

            entity.Property(e => e.PropertyName).IsRequired().HasMaxLength(500);
            entity.Property(e => e.AggregationMode).HasConversion<string>().HasMaxLength(20);

            entity.HasIndex(e => e.CriterionId).IsUnique();
            // The index on the old PropertyId column is gone: conditions bind the property
            // by NAME (spec FR-2.3) — deleting property rows must never break a criterion.

            entity.HasOne(e => e.Criterion)
                .WithOne(c => c.Condition)
                .HasForeignKey<CriterionCondition>(e => e.CriterionId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<CriterionConditionScopeItem>(entity =>
        {
            entity.ToTable("CriterionLeafScopeItems"); // FROZEN physical name — see above.
            entity.Property(e => e.CriterionConditionId).HasColumnName("CriterionLeafId"); // FROZEN physical column.

            entity.HasKey(e => new { e.CriterionConditionId, e.ItemId });

            entity.HasOne(e => e.CriterionCondition)
                .WithMany(c => c.ScopeItems)
                .HasForeignKey(e => e.CriterionConditionId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasOne(e => e.Item)
                .WithMany()
                .HasForeignKey(e => e.ItemId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<CriterionComposite>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Operator).HasConversion<string>().HasMaxLength(20);

            entity.HasIndex(e => e.CriterionId).IsUnique();

            entity.HasOne(e => e.Criterion)
                .WithOne(c => c.Composite)
                .HasForeignKey<CriterionComposite>(e => e.CriterionId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<CriterionCompositeOperand>(entity =>
        {
            entity.HasKey(e => new { e.CompositeId, e.OperandCriterionId });

            entity.HasOne(e => e.Composite)
                .WithMany(c => c.Operands)
                .HasForeignKey(e => e.CompositeId)
                .OnDelete(DeleteBehavior.Cascade);

            // Deleting a criterion that is referenced as an operand is blocked (criterionInUse).
            entity.HasOne(e => e.OperandCriterion)
                .WithMany()
                .HasForeignKey(e => e.OperandCriterionId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<Punch>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.OwnerId).IsRequired().HasMaxLength(200);
            entity.Property(e => e.PunchedAt).HasConversion(utcTimestampRead);
            entity.Property(e => e.CreatedAt).HasConversion(utcTimestampRead);

            // OwnerId is the redundant tenant key — indexed so a tenant-scoped punch
            // query can use it directly (IX_Punches_OwnerId; the index also reaches
            // pre-existing databases via the schema bootstrap's Phase-1 DDL replay).
            entity.HasIndex(e => e.OwnerId);
            entity.HasIndex(e => new { e.HabitId, e.PunchDate });
            entity.HasIndex(e => new { e.ItemId, e.PunchDate });

            entity.HasOne(e => e.Habit)
                .WithMany()
                .HasForeignKey(e => e.HabitId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasOne(e => e.Item)
                .WithMany()
                .HasForeignKey(e => e.ItemId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<PunchValue>(entity =>
        {
            entity.HasKey(e => e.Id);

            entity.HasIndex(e => e.PunchId);
            entity.HasIndex(e => new { e.PropertyId, e.PunchId });

            entity.HasOne(e => e.Punch)
                .WithMany(p => p.Values)
                .HasForeignKey(e => e.PunchId)
                .OnDelete(DeleteBehavior.Cascade);

            // Deleting a property that still has punch values is blocked (itemHasPunches).
            entity.HasOne(e => e.Property)
                .WithMany()
                .HasForeignKey(e => e.PropertyId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        // ── Login history (one row per user per day; POST upserts it) ────────────────
        modelBuilder.Entity<UserLoginHistory>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.UserId).IsRequired().HasMaxLength(200);
            entity.Property(e => e.FirstLoginAt).HasConversion(utcTimestampRead);
            entity.Property(e => e.LastLoginAt).HasConversion(utcTimestampRead);

            // One row per user+day; the unique index guards the POST's upsert and doubles
            // as the tenant-scoped GET's access path (leading UserId, ordered by LoginDate),
            // so no separate HasIndex(UserId) is declared. The table and this index reach
            // pre-existing databases via the schema bootstrap's generic Phase-1 DDL replay.
            entity.HasIndex(e => new { e.UserId, e.LoginDate }).IsUnique();
        });
    }
}
