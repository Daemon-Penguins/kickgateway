using Microsoft.AspNetCore.DataProtection.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace TailoredApps.KickGateway.Api.Data;

/// <summary>
/// Separate DbContext for the data-protection key ring. Lives in the same
/// database as the rest of the app, but isolated so EF migrations for our
/// domain don't get tangled with the DP table schema.
/// </summary>
public class DataProtectionDbContext : DbContext, IDataProtectionKeyContext
{
    public DataProtectionDbContext(DbContextOptions<DataProtectionDbContext> options) : base(options) { }

    public DbSet<DataProtectionKey> DataProtectionKeys => Set<DataProtectionKey>();
}
