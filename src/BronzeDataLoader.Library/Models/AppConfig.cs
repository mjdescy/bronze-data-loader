using System.Text;
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

    /// <summary>Path to the DuckDB database file.</summary>
    public string DatabasePath { get; init; } = ".";

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
        var normalizedYaml = NormalizeQuotedYamlStrings(yaml);

        var deserializer = new DeserializerBuilder()
            .WithNamingConvention(UnderscoredNamingConvention.Instance)
            .Build();

        var rawConfig = deserializer.Deserialize<AppConfigRaw>(normalizedYaml);

        var configDir = Path.GetDirectoryName(Path.GetFullPath(yamlPath)) ?? ".";

        // Resolve relative paths relative to the config file's directory
        var manifestPath = ResolvePath(rawConfig.ManifestPath ?? "manifest.csv", configDir);
        var dataFolder = ResolvePath(rawConfig.DataFolder ?? ".", configDir);
        var contractsFolder = ResolvePath(rawConfig.ContractsFolder ?? ".", configDir);
        var outputFolder = ResolvePath(rawConfig.OutputFolder ?? ".", configDir);
        var databasePath = ResolvePath(rawConfig.DatabasePath ?? ".", configDir);

        var conn = new DuckDBConnection($"DataSource={databasePath}");
        conn.Open();

        using var cmd = conn.CreateCommand();
        cmd.CommandText = "CREATE SCHEMA IF NOT EXISTS \"bronze_raw\";";
        cmd.ExecuteNonQuery();

        cmd.CommandText = "CREATE SCHEMA IF NOT EXISTS \"bronze_quarantine\";";
        cmd.ExecuteNonQuery();

        return new AppConfig
        {
            ManifestPath = manifestPath,
            DataFolder = dataFolder,
            ContractsFolder = contractsFolder,
            OutputFolder = outputFolder,
            DatabasePath = databasePath,
            RawSchema = rawConfig.RawSchema ?? "bronze_raw",
            Schema = rawConfig.Schema ?? "bronze",
            SchemaQuarantine = rawConfig.SchemaQuarantine ?? "bronze_quarantine",
            Connection = conn,
        };
    }

    private static string NormalizeQuotedYamlStrings(string yaml)
    {
        var builder = new StringBuilder(yaml.Length);
        var inSingleQuotedScalar = false;
        var inDoubleQuotedScalar = false;

        for (var i = 0; i < yaml.Length; i++)
        {
            var current = yaml[i];

            if (current == '\'' && !inDoubleQuotedScalar)
            {
                inSingleQuotedScalar = !inSingleQuotedScalar;
                builder.Append(current);
                continue;
            }

            if (current == '"' && !inSingleQuotedScalar)
            {
                inDoubleQuotedScalar = !inDoubleQuotedScalar;
                builder.Append(current);
                continue;
            }

            if (current == '\\' && inDoubleQuotedScalar)
            {
                builder.Append("\\\\");
                continue;
            }

            builder.Append(current);
        }

        return builder.ToString();
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
        public string? DatabasePath { get; init; }
        public string? RawSchema { get; init; }
        public string? Schema { get; init; }
        public string? SchemaQuarantine { get; init; }
    }
}
