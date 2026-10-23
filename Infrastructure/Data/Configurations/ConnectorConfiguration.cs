using ChatPlatform.Infrastructure.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ChatPlatform.Infrastructure.Data.Configurations;

public sealed class ConnectorConfiguration : IEntityTypeConfiguration<ConnectorEntity>
{
    public void Configure(EntityTypeBuilder<ConnectorEntity> builder)
    {
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Name).IsRequired().HasMaxLength(100);
        builder.Property(x => x.TargetUrl).IsRequired().HasMaxLength(2048);
        builder.Property(x => x.AuthToken).HasMaxLength(2048);
        builder.Property(x => x.PayloadTemplate).IsRequired();
    }
}
