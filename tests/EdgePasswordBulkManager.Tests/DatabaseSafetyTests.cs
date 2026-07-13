using EdgePasswordBulkManager.Services;
using Microsoft.Data.Sqlite;

namespace EdgePasswordBulkManager.Tests;

public sealed class DatabaseSafetyTests
{
    [Fact]
    public void CreateSnapshot_IncludesCommittedWalData()
    {
        using var fixture = new ServiceTestFixture();
        var source = fixture.CreateLoginDatabase();
        using var writer = new SqliteConnection($"Data Source={source};Pooling=False");
        writer.Open();
        using (var command = writer.CreateCommand())
        {
            command.CommandText = "PRAGMA journal_mode=WAL;";
            command.ExecuteScalar();
        }
        ServiceTestFixture.InsertLogin(source, 1, "https://wal.example");

        var snapshot = Path.Combine(fixture.Root, "snapshot.sqlite");
        BackupExportService.CreateSnapshot(source, snapshot);

        Assert.Equal(1, ServiceTestFixture.CountLogins(snapshot));
    }

    [Fact]
    public void BackupLoginData_AlwaysCreatesUniqueValidatedSnapshots()
    {
        using var fixture = new ServiceTestFixture();
        var database = fixture.CreateLoginDatabase();
        ServiceTestFixture.InsertLogin(database, 1, "https://example.com");
        var profile = fixture.CreateProfile(database);

        var first = fixture.Backup.BackupLoginData(profile);
        var second = fixture.Backup.BackupLoginData(profile);

        Assert.NotEqual(first, second);
        BackupExportService.ValidateDatabase(Path.Combine(first, "Login Data"));
        BackupExportService.ValidateDatabase(Path.Combine(second, "Login Data"));
    }

    [Fact]
    public void ValidateDatabase_RejectsCorruptInput()
    {
        using var fixture = new ServiceTestFixture();
        var corrupt = Path.Combine(fixture.Root, "corrupt.sqlite");
        File.WriteAllText(corrupt, "not a sqlite database");

        Assert.ThrowsAny<Exception>(() => BackupExportService.ValidateDatabase(corrupt));
    }

    [Fact]
    public void Restore_ReplacesDatabaseAndCreatesSafetyBackup()
    {
        using var fixture = new ServiceTestFixture();
        var live = fixture.CreateLoginDatabase("live.sqlite");
        ServiceTestFixture.InsertLogin(live, 1, "https://old.example");
        var profile = fixture.CreateProfile(live);

        var backupDirectory = Path.Combine(fixture.Root, "restore-source");
        Directory.CreateDirectory(backupDirectory);
        var replacement = fixture.CreateLoginDatabase("replacement.sqlite");
        ServiceTestFixture.InsertLogin(replacement, 2, "https://new.example");
        BackupExportService.CreateSnapshot(replacement, Path.Combine(backupDirectory, "Login Data"));

        var result = fixture.CreateRestoreService().Restore(profile, backupDirectory);

        Assert.True(result.Success, result.Error);
        Assert.NotNull(result.SafetyBackupPath);
        Assert.Equal(1, ServiceTestFixture.CountLogins(live));
        Assert.True(File.Exists(Path.Combine(result.SafetyBackupPath!, "Login Data")));
    }
}