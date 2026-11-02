using ChatPlatform.Infrastructure.Data.Entities;
using Microsoft.AspNetCore.DataProtection.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace ChatPlatform.Infrastructure.Data;

public class PlatformDbContext : DbContext, IDataProtectionKeyContext
{
    public PlatformDbContext(DbContextOptions<PlatformDbContext> options) : base(options) { }

    public DbSet<ConnectorEntity> Connectors => Set<ConnectorEntity>();
    public DbSet<WhatsAppTenantEntity> WhatsAppTenants => Set<WhatsAppTenantEntity>();
    
    // 👇 مطلوب لتخزين مفاتيح التشفير في قاعدة البيانات
    public DbSet<DataProtectionKey> DataProtectionKeys => Set<DataProtectionKey>();
}
