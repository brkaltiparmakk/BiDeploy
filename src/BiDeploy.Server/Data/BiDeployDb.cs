using Microsoft.EntityFrameworkCore;

namespace BiDeploy.Server.Data;

public class BiDeployDb(DbContextOptions<BiDeployDb> options) : DbContext(options)
{
    public DbSet<Dealer> Dealers => Set<Dealer>();
    public DbSet<Company> Companies => Set<Company>();
    public DbSet<License> Licenses => Set<License>();
    public DbSet<Package> Packages => Set<Package>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<Dealer>().HasIndex(d => d.ApiKeyHash).IsUnique();
        b.Entity<Company>().HasIndex(c => c.Vkn).IsUnique();
        b.Entity<Company>().HasOne(c => c.License).WithOne(l => l.Company).HasForeignKey<License>(l => l.CompanyId);
        b.Entity<License>().HasIndex(l => l.ActivationCode).IsUnique();
        b.Entity<License>().HasIndex(l => l.DeviceTokenHash).IsUnique();
        b.Entity<Package>().HasIndex(p => p.PackageId).IsUnique();
        b.Entity<Package>().HasIndex(p => new { p.Product, p.Architecture, p.VersionSortKey });
    }
}
