using Microsoft.EntityFrameworkCore;
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

            entity.HasOne(e => e.Content)
                .WithMany()
                .HasForeignKey(e => e.ContentId)
                .OnDelete(DeleteBehavior.Restrict);
        });
    }
}
