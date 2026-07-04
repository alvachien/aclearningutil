using Microsoft.EntityFrameworkCore;
using aclearningutil.Data;

namespace aclearningutil.test.Helpers;

public static class TestDbContextFactory
{
    public static AppDbContext CreateInMemoryDbContext(string? databaseName = null)
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(databaseName ?? Guid.NewGuid().ToString())
            .Options;

        var context = new AppDbContext(options);
        context.Database.EnsureCreated();
        return context;
    }
}
