using Microsoft.EntityFrameworkCore;
using MyRegistry.Domain;

namespace MyRegistry.Infrastructure;

public class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    public DbSet<Setting> Settings => Set<Setting>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<Setting>(e =>
        {
            e.ToTable("settings");
            e.HasKey(x => x.Key);
            e.Property(x => x.Key).HasColumnName("key").HasMaxLength(100);
            e.Property(x => x.Value).HasColumnName("value");
        });
    }
}
