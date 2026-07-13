using Microsoft.Data.Sqlite;

namespace EdgePasswordBulkManager.Tests;

public sealed class DeleteServiceTests
{
    [Fact]
    public void Execute_RejectsWritesInReadOnlyMode()
    {
        using var fixture = new ServiceTestFixture(readOnly: true);
        var profile = fixture.CreateProfile(Path.Combine(fixture.Root, "missing.sqlite"));

        var result = fixture.CreateDeleteService().Execute(
            profile,
            new[] { ServiceTestFixture.Entry(1, "https://example.com") },
            dryRun: false);

        Assert.Contains("read-only", result.FatalError, StringComparison.OrdinalIgnoreCase);
        Assert.False(result.Committed);
    }

    [Fact]
    public void Execute_DeletesModernIdAndCreatesBackup()
    {
        using var fixture = new ServiceTestFixture();
        var database = fixture.CreateLoginDatabase();
        ServiceTestFixture.InsertLogin(database, 1, "https://example.com");
        var profile = fixture.CreateProfile(database);

        var result = fixture.CreateDeleteService().Execute(
            profile,
            new[] { ServiceTestFixture.Entry(1, "https://example.com") },
            dryRun: false);

        Assert.True(result.Committed, result.FatalError);
        Assert.Equal(1, result.TotalRowsAffected);
        Assert.Equal(0, ServiceTestFixture.CountLogins(database));
        Assert.Single(result.BackupPaths);
    }

    [Fact]
    public void Execute_DeletesLegacyCompositeKey()
    {
        using var fixture = new ServiceTestFixture();
        var database = fixture.CreateLoginDatabase(legacySchema: true);
        ServiceTestFixture.InsertLogin(database, null, "https://legacy.example");
        var profile = fixture.CreateProfile(database);

        var result = fixture.CreateDeleteService().Execute(
            profile,
            new[] { ServiceTestFixture.Entry(null, "https://legacy.example") },
            dryRun: false);

        Assert.True(result.Committed, result.FatalError);
        Assert.Equal(1, result.TotalRowsAffected);
        Assert.Equal(0, ServiceTestFixture.CountLogins(database));
    }

    [Fact]
    public void Execute_ReportsZeroRowMismatch()
    {
        using var fixture = new ServiceTestFixture();
        var database = fixture.CreateLoginDatabase();
        var profile = fixture.CreateProfile(database);

        var result = fixture.CreateDeleteService().Execute(
            profile,
            new[] { ServiceTestFixture.Entry(99, "https://missing.example") },
            dryRun: false);

        Assert.True(result.Committed, result.FatalError);
        var row = Assert.Single(result.Rows);
        Assert.False(row.Success);
        Assert.Equal(0, row.RowsAffected);
        Assert.NotNull(row.Error);
    }

    [Fact]
    public void Execute_RollsBackEntireBatchWhenOneDeleteFails()
    {
        using var fixture = new ServiceTestFixture();
        var database = fixture.CreateLoginDatabase();
        ServiceTestFixture.InsertLogin(database, 1, "https://one.example");
        ServiceTestFixture.InsertLogin(database, 2, "https://two.example");
        using (var connection = new SqliteConnection($"Data Source={database};Pooling=False"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = """
                CREATE TRIGGER prevent_second_delete
                BEFORE DELETE ON logins WHEN OLD.id = 2
                BEGIN
                    SELECT RAISE(ABORT, 'blocked by test');
                END;
                """;
            command.ExecuteNonQuery();
        }
        var profile = fixture.CreateProfile(database);

        var result = fixture.CreateDeleteService().Execute(
            profile,
            new[]
            {
                ServiceTestFixture.Entry(1, "https://one.example"),
                ServiceTestFixture.Entry(2, "https://two.example"),
            },
            dryRun: false);

        Assert.False(result.Committed);
        Assert.Contains("rolled back", result.FatalError, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(2, ServiceTestFixture.CountLogins(database));
    }
}