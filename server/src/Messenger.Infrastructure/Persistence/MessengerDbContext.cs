using Messenger.Application.Abstractions;
using Messenger.Domain.Common;
using Messenger.Domain.Conversations;
using Messenger.Domain.Messages;
using Messenger.Domain.Users;
using Microsoft.EntityFrameworkCore;

namespace Messenger.Infrastructure.Persistence;

public sealed class MessengerDbContext(DbContextOptions<MessengerDbContext> options) : DbContext(options), IApplicationDbContext
{
    public DbSet<User> UserSet => Set<User>();
    public DbSet<ExternalLogin> ExternalLoginSet => Set<ExternalLogin>();
    public DbSet<UserToken> UserTokenSet => Set<UserToken>();
    public DbSet<RefreshToken> RefreshTokenSet => Set<RefreshToken>();
    public DbSet<Conversation> ConversationSet => Set<Conversation>();
    public DbSet<ConversationParticipant> ParticipantSet => Set<ConversationParticipant>();
    public DbSet<Message> MessageSet => Set<Message>();
    public DbSet<MessageEditHistory> EditHistorySet => Set<MessageEditHistory>();
    public DbSet<MessageReceipt> ReceiptSet => Set<MessageReceipt>();
    public DbSet<MessageAttachment> AttachmentSet => Set<MessageAttachment>();
    public DbSet<PushSubscription> PushSubscriptionSet => Set<PushSubscription>();
    public DbSet<AuditLog> AuditLogSet => Set<AuditLog>();

    IQueryable<User> IApplicationDbContext.Users => UserSet.AsNoTracking();
    IQueryable<Conversation> IApplicationDbContext.Conversations => ConversationSet.AsNoTracking();
    IQueryable<ConversationParticipant> IApplicationDbContext.Participants => ParticipantSet.AsNoTracking();
    IQueryable<Message> IApplicationDbContext.Messages => MessageSet.AsNoTracking();
    IQueryable<MessageEditHistory> IApplicationDbContext.MessageEditHistory => EditHistorySet.AsNoTracking();
    IQueryable<MessageReceipt> IApplicationDbContext.MessageReceipts => ReceiptSet.AsNoTracking();
    IQueryable<MessageAttachment> IApplicationDbContext.MessageAttachments => AttachmentSet.AsNoTracking();
    IQueryable<RefreshToken> IApplicationDbContext.RefreshTokens => RefreshTokenSet.AsNoTracking();
    IQueryable<AuditLog> IApplicationDbContext.AuditLogs => AuditLogSet.AsNoTracking();
    IQueryable<ExternalLogin> IApplicationDbContext.ExternalLogins => ExternalLoginSet.AsNoTracking();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.HasPostgresExtension("pg_trgm");

        b.Entity<User>(e =>
        {
            e.ToTable("users");
            e.HasKey(x => x.Id);
            e.Ignore(x => x.DomainEvents);
            e.Ignore(x => x.IsActive);
            e.Property(x => x.Email).HasMaxLength(320).IsRequired();
            e.Property(x => x.EmailNormalized).HasMaxLength(320).IsRequired();
            e.Property(x => x.Username).HasMaxLength(64).IsRequired();
            e.Property(x => x.UsernameNormalized).HasMaxLength(64).IsRequired();
            e.Property(x => x.DisplayName).HasMaxLength(128).IsRequired();
            e.Property(x => x.PasswordHash).HasMaxLength(100);
            e.Property(x => x.Role).HasConversion<string>().HasMaxLength(16);
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(16);
            // Partial unique indexes: deleted accounts free their username/email (USR-05).
            e.HasIndex(x => x.EmailNormalized).IsUnique().HasFilter("status <> 'Deleted'");
            e.HasIndex(x => x.UsernameNormalized).IsUnique().HasFilter("status <> 'Deleted'");
        });

        b.Entity<ExternalLogin>(e =>
        {
            e.ToTable("external_logins");
            e.HasKey(x => x.Id);
            e.Property(x => x.Provider).HasMaxLength(32);
            e.Property(x => x.ProviderUserId).HasMaxLength(128);
            e.HasIndex(x => new { x.Provider, x.ProviderUserId }).IsUnique();
            e.HasOne<User>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<UserToken>(e =>
        {
            e.ToTable("user_tokens");
            e.HasKey(x => x.Id);
            e.Property(x => x.Type).HasConversion<string>().HasMaxLength(32);
            e.Property(x => x.TokenHash).HasMaxLength(64);
            e.HasIndex(x => x.TokenHash).IsUnique();
            e.HasIndex(x => new { x.UserId, x.Type });
            e.HasOne<User>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<RefreshToken>(e =>
        {
            e.ToTable("refresh_tokens");
            e.HasKey(x => x.Id);
            e.Property(x => x.TokenHash).HasMaxLength(64);
            e.Property(x => x.UserAgent).HasMaxLength(256);
            e.Property(x => x.Ip).HasMaxLength(64);
            e.HasIndex(x => x.TokenHash).IsUnique();
            e.HasIndex(x => x.FamilyId);
            e.HasIndex(x => x.UserId);
            e.HasOne<User>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<Conversation>(e =>
        {
            e.ToTable("conversations");
            e.HasKey(x => x.Id);
            e.Ignore(x => x.DomainEvents);
            e.Ignore(x => x.IsDeleted);
            e.Ignore(x => x.ActiveParticipants);
            e.Property(x => x.Type).HasConversion<string>().HasMaxLength(16);
            e.Property(x => x.DirectKey).HasMaxLength(80);
            e.Property(x => x.Title).HasMaxLength(200);
            e.HasIndex(x => x.DirectKey).IsUnique().HasFilter("direct_key IS NOT NULL");
            e.HasMany(x => x.Participants).WithOne().HasForeignKey(p => p.ConversationId).OnDelete(DeleteBehavior.Cascade);
            e.Navigation(x => x.Participants).UsePropertyAccessMode(PropertyAccessMode.Field);
        });

        b.Entity<ConversationParticipant>(e =>
        {
            e.ToTable("conversation_participants");
            e.HasKey(x => new { x.ConversationId, x.UserId });
            e.Ignore(x => x.IsActive);
            e.Ignore(x => x.VisibilityFloor);
            e.Property(x => x.Role).HasConversion<string>().HasMaxLength(16);
            e.HasIndex(x => x.UserId);
            e.HasOne<User>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Restrict);
        });

        b.Entity<Message>(e =>
        {
            e.ToTable("messages");
            e.HasKey(x => x.Id);
            e.Ignore(x => x.DomainEvents);
            e.Ignore(x => x.IsDeleted);
            e.Property(x => x.Body).HasMaxLength(16000);
            e.Property(x => x.SenderUsernameSnapshot).HasMaxLength(64);
            e.Property(x => x.SenderDisplayNameSnapshot).HasMaxLength(128);
            // P-03: ordering is per conversation; never a global identity.
            e.HasIndex(x => new { x.ConversationId, x.Sequence }).IsUnique().IsDescending(false, true);
            e.HasIndex(x => new { x.ConversationId, x.UpdatedSequence });
            // API-13: idempotent send.
            e.HasIndex(x => new { x.SenderId, x.ClientMessageId }).IsUnique();
            e.HasOne<Conversation>().WithMany().HasForeignKey(x => x.ConversationId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<User>().WithMany().HasForeignKey(x => x.SenderId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<Message>().WithMany().HasForeignKey(x => x.ReplyToMessageId).OnDelete(DeleteBehavior.Restrict);
            e.HasMany(x => x.EditHistory).WithOne().HasForeignKey(h => h.MessageId).OnDelete(DeleteBehavior.Cascade);
            e.HasMany(x => x.Attachments).WithOne().HasForeignKey(a => a.MessageId).OnDelete(DeleteBehavior.Cascade);
            e.Navigation(x => x.EditHistory).UsePropertyAccessMode(PropertyAccessMode.Field);
            e.Navigation(x => x.Attachments).UsePropertyAccessMode(PropertyAccessMode.Field);
        });

        b.Entity<MessageEditHistory>(e =>
        {
            e.ToTable("message_edit_history");
            e.HasKey(x => x.Id);
            e.Property(x => x.PreviousBody).HasMaxLength(16000);
        });

        b.Entity<MessageReceipt>(e =>
        {
            e.ToTable("message_receipts");
            e.HasKey(x => new { x.MessageId, x.UserId });
            e.HasOne<Message>().WithMany().HasForeignKey(x => x.MessageId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<User>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<MessageAttachment>(e =>
        {
            e.ToTable("message_attachments");
            e.HasKey(x => x.Id);
            e.Property(x => x.FileName).HasMaxLength(255);
            e.Property(x => x.ContentType).HasMaxLength(127);
            e.Property(x => x.StorageKey).HasMaxLength(255);
        });

        b.Entity<PushSubscription>(e =>
        {
            e.ToTable("push_subscriptions");
            e.HasKey(x => x.Id);
            e.Property(x => x.Endpoint).HasMaxLength(1024);
            e.Property(x => x.P256dh).HasMaxLength(256);
            e.Property(x => x.Auth).HasMaxLength(128);
            e.HasIndex(x => x.Endpoint).IsUnique();
            e.HasIndex(x => x.UserId);
            e.HasOne<User>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<AuditLog>(e =>
        {
            e.ToTable("audit_logs");
            e.HasKey(x => x.Id);
            e.Property(x => x.Action).HasMaxLength(64);
            e.Property(x => x.EntityType).HasMaxLength(32);
            e.Property(x => x.EntityId).HasMaxLength(64);
            e.Property(x => x.Ip).HasMaxLength(64);
            e.Property(x => x.DataJson).HasColumnType("jsonb");
            e.HasIndex(x => x.CreatedAt);
        });

        // Keys are GUID v7 generated by the domain, so entities reached through navigations are new (INSERT), not existing.
        foreach (var entity in b.Model.GetEntityTypes())
        {
            if (entity.FindProperty("Id") is { ClrType: var type } id && type == typeof(Guid))
            {
                id.ValueGenerated = Microsoft.EntityFrameworkCore.Metadata.ValueGenerated.Never;
            }
        }
    }

    /// <summary>Entities that raised domain events and are tracked by this context.</summary>
    internal IEnumerable<Entity> TrackedEntitiesWithEvents() =>
        ChangeTracker.Entries<Entity>().Select(e => e.Entity).Where(e => e.DomainEvents.Count > 0);
}
