using Microsoft.EntityFrameworkCore;
using VirtmaAi.Models.Entities;

namespace VirtmaAi.Services.Data;

public class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }

    public DbSet<Conversation> Conversations => Set<Conversation>();
    public DbSet<Message> Messages => Set<Message>();
    public DbSet<MessageAttachment> MessageAttachments => Set<MessageAttachment>();
    public DbSet<AiModel> AiModels => Set<AiModel>();
    public DbSet<ApiKey> ApiKeys => Set<ApiKey>();
    public DbSet<Skill> Skills => Set<Skill>();
    public DbSet<SkillContextFile> SkillContextFiles => Set<SkillContextFile>();
    public DbSet<Plugin> Plugins => Set<Plugin>();
    public DbSet<Routine> Routines => Set<Routine>();
    public DbSet<Theme> Themes => Set<Theme>();
    public DbSet<Integration> Integrations => Set<Integration>();
    public DbSet<Reference> References => Set<Reference>();
    public DbSet<GraphifyGraph> GraphifyGraphs => Set<GraphifyGraph>();
    public DbSet<UserSetting> UserSettings => Set<UserSetting>();
    public DbSet<ExternalApiKey> ExternalApiKeys => Set<ExternalApiKey>();
    public DbSet<NetworkPreference> NetworkPreferences => Set<NetworkPreference>();
    public DbSet<ScreenMonitorSession> ScreenMonitorSessions => Set<ScreenMonitorSession>();
    public DbSet<AiRule> AiRules => Set<AiRule>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.Entity<Message>()
            .HasIndex(m => m.ConversationId);

        modelBuilder.Entity<Message>()
            .HasIndex(m => m.ParentMessageId);

        modelBuilder.Entity<MessageAttachment>()
            .HasIndex(a => a.MessageId);

        modelBuilder.Entity<SkillContextFile>()
            .HasIndex(c => c.SkillId);

        modelBuilder.Entity<GraphifyGraph>()
            .HasIndex(g => g.ConversationId);

        modelBuilder.Entity<ApiKey>()
            .HasIndex(k => new { k.ServiceName, k.KeyName })
            .IsUnique();

        modelBuilder.Entity<Integration>()
            .HasIndex(i => i.ServiceName);

        modelBuilder.Entity<ExternalApiKey>()
            .HasIndex(k => k.HashedKey)
            .IsUnique();
    }
}
