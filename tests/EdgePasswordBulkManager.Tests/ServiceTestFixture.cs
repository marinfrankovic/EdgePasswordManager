using EdgePasswordBulkManager.Models;
using EdgePasswordBulkManager.Services;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace EdgePasswordBulkManager.Tests;

internal sealed class ServiceTestFixture : IDisposable
{
    public ServiceTestFixture(bool readOnly = false)
    {
        Root = Path.Combine(Path.GetTempPath(), "edgepass-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Root);
        Options = new AppOptions
        {
            BackupPath = Path.Combine(Root, "backups"),
            ExportPath = Path.Combine(Root, "exports"),
            LogPath = Path.Combine(Root, "logs"),
            WorkPath = Path.Combine(Root, "work"),
            ListDirectory = Path.Combine(Root, "lists"),
            ReadOnlyMode = readOnly,
        };

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["EdgePassManager:LogPath"] = Options.LogPath,
            })
            .Build();
        Audit = new AuditLog(configuration, NullLogger<AuditLog>.Instance);
        Backup = new BackupExportService(Microsoft.Extensions.Options.Options.Create(Options), Audit);
    }

    public string Root { get; }
    public AppOptions Options { get; }
    public AuditLog Audit { get; }
    public BackupExportService Backup { get; }

    public EdgeProfile CreateProfile(string databasePath) => new()
    {
        Channel = "Test",
        FolderName = "Default",
        DisplayName = "Test profile",
        StoreFile = "Login Data",
        LoginDataPath = databasePath,
        LastModified = DateTimeOffset.UtcNow,
    };

    public DeleteService CreateDeleteService() => new(
        Microsoft.Extensions.Options.Options.Create(Options),
        Backup,
        Audit,
        NullLogger<DeleteService>.Instance);

    public RestoreService CreateRestoreService() => new(
        Microsoft.Extensions.Options.Options.Create(Options),
        Backup,
        Audit,
        NullLogger<RestoreService>.Instance);

    public string CreateLoginDatabase(string name = "Login Data", bool legacySchema = false)
    {
        var path = Path.Combine(Root, name);
        using var connection = new SqliteConnection($"Data Source={path};Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = legacySchema
            ? """
              CREATE TABLE logins (
                  origin_url TEXT NOT NULL,
                  username_element TEXT NOT NULL,
                  username_value TEXT NOT NULL,
                  password_element TEXT NOT NULL,
                  signon_realm TEXT NOT NULL,
                  PRIMARY KEY (origin_url, username_element, username_value, password_element, signon_realm)
              );
              """
            : """
              CREATE TABLE logins (
                  id INTEGER PRIMARY KEY,
                  origin_url TEXT NOT NULL,
                  username_element TEXT NOT NULL,
                  username_value TEXT NOT NULL,
                  password_element TEXT NOT NULL,
                  signon_realm TEXT NOT NULL
              );
              """;
        command.ExecuteNonQuery();
        return path;
    }

    public static void InsertLogin(string path, long? id, string origin, string username = "user")
    {
        using var connection = new SqliteConnection($"Data Source={path};Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = id is null
            ? """
              INSERT INTO logins
                  (origin_url, username_element, username_value, password_element, signon_realm)
              VALUES (@origin, 'username', @username, 'password', @origin);
              """
            : """
              INSERT INTO logins
                  (id, origin_url, username_element, username_value, password_element, signon_realm)
              VALUES (@id, @origin, 'username', @username, 'password', @origin);
              """;
        if (id is not null)
        {
            command.Parameters.AddWithValue("@id", id.Value);
        }
        command.Parameters.AddWithValue("@origin", origin);
        command.Parameters.AddWithValue("@username", username);
        command.ExecuteNonQuery();
    }

    public static int CountLogins(string path)
    {
        using var connection = new SqliteConnection($"Data Source={path};Mode=ReadOnly;Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM logins;";
        return Convert.ToInt32(command.ExecuteScalar());
    }

    public static LoginEntry Entry(long? id, string origin, string username = "user") => new()
    {
        Id = id,
        OriginUrl = origin,
        SignonRealm = origin,
        UsernameElement = "username",
        Username = username,
        PasswordElement = "password",
        ProfileKey = "Test|Default|Login Data",
    };

    public void Dispose()
    {
        try
        {
            Directory.Delete(Root, recursive: true);
        }
        catch
        {
        }
    }
}