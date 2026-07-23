using DuckDB.NET.Data;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace BronzeDataLoader.Library.Models;

/// <summary>
/// Application configuration loaded from a YAML file.
/// </summary>
public record AppConfig
{
    /// <summary>Path to the manifest CSV file.</summary>
    public string ManifestPath { get; init; } = "manifest.csv";

    /// <summary>Root folder containing source data files/subdirectories.</summary>
    public string DataFolder { get; init; } = ".";

    /// <summary>Folder containing contract YAML files.</summary>
    public string ContractsFolder { get; init; } = ".";

    /// <summary>Output folder for generated artifacts.</summary>
    public string OutputFolder { get; init; } = ".";

    /// <summary>Name of the DuckDB database file (placed inside <see cref="OutputFolder"/>).</summary>
    public string DatabaseName { get; init; } = "bronze-database";

    /// <summary>Default raw/staging schema name.</summary>
    public string RawSchema { get; init; } = "bronze_raw";

    /// <summary>Default valid/clean schema name.</summary>
    public string Schema { get; init; } = "bronze";

    /// <summary>Default quarantine schema name.</summary>
    public string SchemaQuarantine { get; init; } = "bronze_quarantine";

    /// <summary>The DuckDB database connection.</summary>
    public DuckDBConnection? Connection { get; init; }

    /// <summary>
    /// Create an <see cref="AppConfig"/> from a YAML configuration file.
    /// </summary>
    /// <param name="yamlPath">Path to the YAML configuration file.</param>
    /// <returns>A populated <see cref="AppConfig"/> with an open database connection.</returns>
    /// <exception cref="FileNotFoundException">The specified file does not exist.</exception>
    public static AppConfig FromYaml(string yamlPath)
    {
        if (!File.Exists(yamlPath))
            throw new FileNotFoundException($"Configuration file not found: {yamlPath}", yamlPath);

        var yaml = File.ReadAllText(yamlPath);

        var deserializer = new DeserializerBuilder()
            .WithNamingConvention(UnderscoredNamingConvention.Instance)
            .IgnoreUnmatchedProperties()
            .WithMaximumRecursion(10)
            .Build();

        var rawConfig = deserializer.Deserialize<AppConfigRaw>(yaml);

        var configDir = Path.GetDirectoryName(Path.GetFullPath(yamlPath)) ?? ".";

        // Resolve relative paths relative to the config file's directory
        var manifestPath = ResolvePath(rawConfig.ManifestPath ?? "manifest.csv", configDir);
        var dataFolder = ResolvePath(rawConfig.DataFolder ?? ".", configDir);
        var contractsFolder = ResolvePath(rawConfig.ContractsFolder ?? ".", configDir);
        var outputFolder = ResolvePath(rawConfig.OutputFolder ?? ".", configDir);

        // The database is always placed inside the output folder.
        // :memory: is a special value that bypasses file creation.
        Directory.CreateDirectory(outputFolder);
        var databaseName = rawConfig.DatabaseName ?? "bronze-database";
        var databasePath = string.Equals(databaseName, ":memory:", StringComparison.OrdinalIgnoreCase)
            ? databaseName
            : Path.Combine(outputFolder, databaseName);

        var conn = new DuckDBConnection($"DataSource={databasePath}");
        conn.Open();

        // Extract schema names early so they can be used in the CREATE statements below.
        // These same values are used in the AppConfig returned at the end of this method.
        var rawSchema = rawConfig.RawSchema ?? "bronze_raw";
        var schemaQuarantine = rawConfig.SchemaQuarantine ?? "bronze_quarantine";

        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"CREATE SCHEMA IF NOT EXISTS {EscapeSqlIdentifier(rawSchema)};";
        cmd.ExecuteNonQuery();

        // Create the quarantine schema using the configured name
        cmd.CommandText = $"CREATE SCHEMA IF NOT EXISTS {EscapeSqlIdentifier(schemaQuarantine)};";
        cmd.ExecuteNonQuery();

        // The metadata schema is always "metadata" — it is not user-configurable
        cmd.CommandText = "CREATE SCHEMA IF NOT EXISTS \"metadata\";";
        cmd.ExecuteNonQuery();

        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS "metadata"."table_load" (
                table_schema VARCHAR,
                table_name VARCHAR,
                file_name VARCHAR,
                file_path VARCHAR,
                imported_at TIMESTAMP DEFAULT CURRENT_TIMESTAMP,
                row_count BIGINT
            );
            """;
        cmd.ExecuteNonQuery();

        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS "metadata"."quarantine" (
                table_name VARCHAR,
                error_message VARCHAR,
                quarantined_at TIMESTAMP DEFAULT CURRENT_TIMESTAMP
            );
            """;
        cmd.ExecuteNonQuery();

        cmd.CommandText = """
            CREATE OR REPLACE VIEW "metadata"."v_failed_loads" AS
            SELECT * FROM "metadata"."table_load"
            WHERE row_count IS NULL;
            """;
        cmd.ExecuteNonQuery();

        return new AppConfig
        {
            ManifestPath = manifestPath,
            DataFolder = dataFolder,
            ContractsFolder = contractsFolder,
            OutputFolder = outputFolder,
            DatabaseName = databaseName,
            RawSchema = rawConfig.RawSchema ?? "bronze_raw",
            Schema = rawConfig.Schema ?? "bronze",
            SchemaQuarantine = rawConfig.SchemaQuarantine ?? "bronze_quarantine",
            Connection = conn,
        };
    }

    /// <summary>
    /// Escape a SQL identifier by doubling any embedded double-quote characters
    /// and wrapping it in double quotes.
    /// </summary>
    private static string EscapeSqlIdentifier(string name)
    {
        return $"\"{name.Replace("\"", "\"\"")}\"";
    }

    private static string ResolvePath(string path, string configDir)
    {
        return Path.IsPathRooted(path)
            ? Path.GetFullPath(path)
            : Path.GetFullPath(Path.Combine(configDir, path));
    }

    // ReSharper disable once ClassNeverInstantiated.Local
    private sealed record AppConfigRaw
    {
        public string? ManifestPath { get; init; }
        public string? DataFolder { get; init; }
        public string? ContractsFolder { get; init; }
        public string? OutputFolder { get; init; }
        public string? DatabaseName { get; init; }
        public string? RawSchema { get; init; }
        public string? Schema { get; init; }
        public string? SchemaQuarantine { get; init; }
    }
}
