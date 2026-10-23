using ChatPlatform.Infrastructure.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace ChatPlatform.Infrastructure.Data;

public sealed class PlatformDbContext : DbContext
{
    public PlatformDbContext(DbContextOptions<PlatformDbContext> options) : base(options) { }
    
    public DbSet<ConnectorEntity> Connectors => Set<ConnectorEntity>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        // تطبيق كل إعدادات الجداول تلقائياً
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(PlatformDbContext).Assembly);
        base.OnModelCreating(modelBuilder);
    }
}
