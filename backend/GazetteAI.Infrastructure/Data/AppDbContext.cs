using GazetteAI.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using Pgvector;

namespace GazetteAI.Infrastructure.Data;

public class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options)
        : base(options)
    {
    }

    public DbSet<User> Users => Set<User>();
    public DbSet<Document> Documents => Set<Document>();
    public DbSet<DocumentChunk> DocumentChunks => Set<DocumentChunk>();
    public DbSet<ChatMessage> ChatMessages => Set<ChatMessage>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        // Enable PostgreSQL pgvector extension
        modelBuilder.HasPostgresExtension("vector");

        // Email must be unique
        modelBuilder.Entity<User>()
            .HasIndex(user => user.Email)
            .IsUnique();

        // User -> Documents
        modelBuilder.Entity<Document>()
            .HasOne(document => document.User)
            .WithMany(user => user.Documents)
            .HasForeignKey(document => document.UserId)
            .OnDelete(DeleteBehavior.Cascade);

        // Document -> Chunks
        modelBuilder.Entity<DocumentChunk>()
            .HasOne(chunk => chunk.Document)
            .WithMany(document => document.Chunks)
            .HasForeignKey(chunk => chunk.DocumentId)
            .OnDelete(DeleteBehavior.Cascade);

        // Chunk -> User
        modelBuilder.Entity<DocumentChunk>()
            .HasOne(chunk => chunk.User)
            .WithMany()
            .HasForeignKey(chunk => chunk.UserId)
            .OnDelete(DeleteBehavior.Restrict);

        // User -> Chat messages
        modelBuilder.Entity<ChatMessage>()
            .HasOne(message => message.User)
            .WithMany(user => user.ChatMessages)
            .HasForeignKey(message => message.UserId)
            .OnDelete(DeleteBehavior.Cascade);

        // Document -> Chat messages
        modelBuilder.Entity<ChatMessage>()
            .HasOne(message => message.Document)
            .WithMany(document => document.ChatMessages)
            .HasForeignKey(message => message.DocumentId)
            .OnDelete(DeleteBehavior.SetNull);

        // Convert float[] into PostgreSQL vector
        var embeddingConverter = new ValueConverter<float[], Vector>(
            value => new Vector(value),
            value => value.ToArray());

        var embeddingComparer = new ValueComparer<float[]>(
            (left, right) => left.SequenceEqual(right),
            value => value.Aggregate(
                0,
                (hash, item) => HashCode.Combine(hash, item)),
            value => value.ToArray());

        var embeddingProperty = modelBuilder.Entity<DocumentChunk>()
            .Property(chunk => chunk.Embedding)
            .HasConversion(embeddingConverter)
            .HasColumnType("vector(768)");

        embeddingProperty.Metadata.SetValueComparer(embeddingComparer);
    }
}