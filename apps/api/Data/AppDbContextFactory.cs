using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace CodeInsights.Api.Data;

/// <summary>Builds the EF design-time model without starting the application or opening a database.</summary>
public sealed class AppDbContextFactory : IDesignTimeDbContextFactory<AppDbContext>
{
    public AppDbContext CreateDbContext(string[] args) => new(new DbContextOptionsBuilder<AppDbContext>()
        .UseNpgsql("Host=localhost;Database=codeinsights_design;Username=design")
        .Options);
}
