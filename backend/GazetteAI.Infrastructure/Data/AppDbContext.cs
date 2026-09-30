using GazetteAI.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using Pgvector;

namespace GazetteAI.Infrastructure.Data;

public class AppDbContext : DbContext
{
    public AppDbContext(
        DbContextOptions<AppDbContext> options)
        : base(options)
    {
    }

    public DbSet<User> Users =>
        Set<User>();

    public DbSet<Document> Documents =>
        Set<Document>();

    public DbSet<DocumentChunk> DocumentChunks =>
        Set<DocumentChunk>();

    public DbSet<ChatMessage> ChatMessages =>
        Set<ChatMessage>();

    public DbSet<EmailVerificationOtp>
        EmailVerificationOtps =>
            Set<EmailVerificationOtp>();

    protected override void OnModelCreating(
        ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        /*
         * PostgreSQL pgvector extension
         */

        modelBuilder.HasPostgresExtension(
            "vector");

        /*
         * User configuration
         */

        modelBuilder.Entity<User>(
            entity =>
            {
                entity.HasKey(user => user.Id);

                entity.Property(user => user.FullName)
                    .HasMaxLength(100)
                    .IsRequired();

                entity.Property(user => user.Email)
                    .HasMaxLength(200)
                    .IsRequired();

                entity.Property(user => user.PasswordHash)
                    .IsRequired();

                entity.Property(user => user.Role)
                    .HasMaxLength(50)
                    .IsRequired();

                entity.Property(
                        user => user.IsEmailVerified)
                    .HasDefaultValue(false)
                    .IsRequired();

                entity.HasIndex(user => user.Email)
                    .IsUnique();
            });

        /*
         * Email verification OTP configuration
         */

        modelBuilder.Entity<EmailVerificationOtp>(
            entity =>
            {
                entity.HasKey(otp => otp.Id);

                entity.Property(otp => otp.CodeHash)
                    .HasMaxLength(64)
                    .IsRequired();

                entity.Property(otp => otp.CreatedAt)
                    .IsRequired();

                entity.Property(otp => otp.ExpiresAt)
                    .IsRequired();

                entity.Property(otp => otp.FailedAttempts)
                    .HasDefaultValue(0)
                    .IsRequired();

                entity.HasIndex(otp => new
                {
                    otp.UserId,
                    otp.ExpiresAt
                });

                entity.HasOne(otp => otp.User)
                    .WithMany(user =>
                        user.EmailVerificationOtps)
                    .HasForeignKey(otp => otp.UserId)
                    .OnDelete(DeleteBehavior.Cascade);
            });

        /*
         * User -> Documents
         */

        modelBuilder.Entity<Document>()
            .HasOne(document => document.User)
            .WithMany(user => user.Documents)
            .HasForeignKey(document =>
                document.UserId)
            .OnDelete(DeleteBehavior.Cascade);

        /*
         * Document -> Chunks
         */

        modelBuilder.Entity<DocumentChunk>()
            .HasOne(chunk => chunk.Document)
            .WithMany(document =>
                document.Chunks)
            .HasForeignKey(chunk =>
                chunk.DocumentId)
            .OnDelete(DeleteBehavior.Cascade);

        /*
         * DocumentChunk -> User
         */

        modelBuilder.Entity<DocumentChunk>()
            .HasOne(chunk => chunk.User)
            .WithMany()
            .HasForeignKey(chunk =>
                chunk.UserId)
            .OnDelete(DeleteBehavior.Restrict);

        /*
         * User -> ChatMessages
         */

        modelBuilder.Entity<ChatMessage>()
            .HasOne(message => message.User)
            .WithMany(user =>
                user.ChatMessages)
            .HasForeignKey(message =>
                message.UserId)
            .OnDelete(DeleteBehavior.Cascade);

        /*
         * Document -> ChatMessages
         */

        modelBuilder.Entity<ChatMessage>()
            .HasOne(message =>
                message.Document)
            .WithMany(document =>
                document.ChatMessages)
            .HasForeignKey(message =>
                message.DocumentId)
            .OnDelete(DeleteBehavior.SetNull);

        /*
         * Convert float[] into PostgreSQL vector
         */

        var embeddingConverter =
            new ValueConverter<float[], Vector>(
                value => new Vector(value),
                value => value.ToArray());

        var embeddingComparer =
            new ValueComparer<float[]>(
                (left, right) =>
                    left != null &&
                    right != null &&
                    left.SequenceEqual(right),

                value =>
                    value.Aggregate(
                        0,
                        (hash, item) =>
                            HashCode.Combine(
                                hash,
                                item)),

                value => value.ToArray());

        var embeddingProperty =
            modelBuilder.Entity<DocumentChunk>()
                .Property(chunk =>
                    chunk.Embedding)
                .HasConversion(
                    embeddingConverter)
                .HasColumnType("vector(768)");

        embeddingProperty.Metadata
            .SetValueComparer(
                embeddingComparer);
    }
}