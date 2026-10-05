using Microsoft.EntityFrameworkCore;
using aclearningutil.Data;

namespace aclearningutil.test.Helpers;

public static class TestDbContextFactory
{
    /// <summary>
    /// Creates an <see cref="AppDbContext"/> backed by the REAL SQLite engine in
    /// memory mode — full relational semantics (SQL translation, unique-index and
    /// FK enforcement, transactions) with no disk file and no cleanup beyond
    /// disposing the context.
    ///
    /// Lifecycle note: a SQLite in-memory database exists only while at least one
    /// connection to it is open. The shared-cache connection string gives the
    /// database a name, and the explicit <c>OpenConnection</c> pins it open for
    /// the context's lifetime — EF owns that connection, so <c>context.Dispose()</c>
    /// closes it and the RAM pages go with it. No separate connection to manage.
    ///
    /// Each call without a <paramref name="databaseName"/> gets a fresh, isolated
    /// database (unique name), matching the long-standing test convention that
    /// every test method starts from an empty schema. Pass the same name to let
    /// several contexts share one database; <c>EnsureCreated</c> is idempotent, so
    /// the second context sees the first one's schema.
    /// </summary>
    public static AppDbContext CreateInMemoryDbContext(string? databaseName = null)
    {
        var name = databaseName ?? Guid.NewGuid().ToString("N");
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite($"Data Source={name};Mode=Memory;Cache=Shared")
            .Options;

        var context = new AppDbContext(options);
        context.Database.OpenConnection();
        context.Database.EnsureCreated();
        return context;
    }
}
