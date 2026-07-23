using CommandLine;
using BronzeDataLoader.Library.Models;
using BronzeDataLoader.Library.Sql;

// ReSharper disable once RedundantUsingDirective
using SysConsole = System.Console;

var result = Parser.Default.ParseArguments<InitOptions, NewOptions, LoadOptions>(args)
    .MapResult(
        (InitOptions opts) => RunInit(opts),
        (NewOptions opts) => RunNew(opts),
        (LoadOptions opts) => RunLoad(opts),
        errors => 1
    );

return result;

static int RunInit(InitOptions opts)
{
    try
    {
        var outputFolder = Directory.GetCurrentDirectory();
        GenerateConfig(outputFolder);
        GenerateContract(outputFolder);
        GenerateManifest(outputFolder);

        Output.WriteLine(opts, $"Generated configuration files in {outputFolder}");
        Output.WriteLine(opts, "  config.yaml");
        Output.WriteLine(opts, "  contract_customer.yaml");
        Output.WriteLine(opts, "  manifest.csv");

        Output.WriteJson(opts, new
        {
            command = "init",
            output_folder = outputFolder,
            files = new[] { "config.yaml", "contract_customer.yaml", "manifest.csv" },
        });
        return 0;
    }
    catch (Exception ex)
    {
        SysConsole.Error.WriteLine($"Error: {ex.Message}");
        return 1;
    }
}

static int RunNew(NewOptions opts)
{
    try
    {
        var outputFolder = string.IsNullOrEmpty(opts.OutputFolder)
            ? Directory.GetCurrentDirectory()
            : Path.GetFullPath(opts.OutputFolder);

        if (!Directory.Exists(outputFolder))
            Directory.CreateDirectory(outputFolder);

        var generatedFile = opts.SubCommand?.ToLowerInvariant() switch
        {
            "config" => "config.yaml",
            "contract" => "contract_customer.yaml",
            "manifest" => "manifest.csv",
            _ => null,
        };

        if (generatedFile is null)
        {
            SysConsole.Error.WriteLine($"Unknown sub-command: {opts.SubCommand}");
            SysConsole.Error.WriteLine("Usage: bronze-data-loader new [config|contract|manifest] --output-folder <path>");
            return 1;
        }

        switch (opts.SubCommand!.ToLowerInvariant())
        {
            case "config":
                GenerateConfig(outputFolder);
                break;
            case "contract":
                GenerateContract(outputFolder);
                break;
            case "manifest":
                GenerateManifest(outputFolder);
                break;
        }

        Output.WriteLine(opts, $"Generated {generatedFile} in {outputFolder}");
        Output.WriteJson(opts, new
        {
            command = "new",
            sub_command = opts.SubCommand!.ToLowerInvariant(),
            file = generatedFile,
            output_folder = outputFolder,
        });
        return 0;
    }
    catch (Exception ex)
    {
        SysConsole.Error.WriteLine($"Error: {ex.Message}");
        return 1;
    }
}

static int RunLoad(LoadOptions opts)
{
    try
    {
        var configPath = Path.GetFullPath(opts.ConfigPath!);

        var appConfig = AppConfig.FromYaml(configPath);

        if (appConfig.Connection is null)
        {
            SysConsole.Error.WriteLine("Database connection could not be established.");
            return 1;
        }

        using (appConfig.Connection)
        {
            var manifest = Manifest.FromCsv(appConfig.ManifestPath);
            var sourceFiles = manifest.ToSourceFileList(appConfig);

            // Collect all SQL statements across all source files
            var allStatements = new List<SqlStatement>();
            var seenSchemas = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            var processedCount = 0;
            var errorCount = 0;

            foreach (var sourceFile in sourceFiles)
            {
                try
                {
                    if (opts.GenerateSql)
                    {
                        // Generate mode: load raw data so DESCRIBE works, capture all SQL
                        sourceFile.GenerateRawLoad(appConfig);
                        var statements = sourceFile.CollectSqlStatements(appConfig);

                        // Deduplicate schema statements across source files
                        foreach (var stmt in statements)
                        {
                            if (stmt.Operation == "create_schema")
                            {
                                if (!seenSchemas.Add(stmt.ObjectName))
                                    continue;
                            }
                            allStatements.Add(stmt);
                        }

                        Output.WriteLine(opts, $"Collected SQL for {sourceFile.FilePath}.");
                    }
                    else
                    {
                        // Normal mode: execute against DuckDB
                        sourceFile.GenerateAndExecuteSql(appConfig);
                        Output.WriteLine(opts, $"Processed {sourceFile.FilePath} successfully.");
                    }
                    processedCount++;
                }
                catch (Exception ex)
                {
                    SysConsole.Error.WriteLine($"Error processing {sourceFile.FilePath}: {ex.Message}");
                    errorCount++;
                }
            }

            var sqlFileCount = 0;

            // Write SQL files if in generate mode
            if (opts.GenerateSql && allStatements.Count > 0)
            {
                var outputFolder = appConfig.OutputFolder;
                if (!Directory.Exists(outputFolder))
                    Directory.CreateDirectory(outputFolder);

                var ordinal = 1;
                foreach (var stmt in allStatements)
                {
                    var fileName = $"{ordinal:D3}_{stmt.Operation}_{stmt.ObjectName}.sql";
                    var filePath = Path.Combine(outputFolder, fileName);
                    File.WriteAllText(filePath, stmt.Sql + "\n");
                    ordinal++;
                }

                sqlFileCount = allStatements.Count;
                Output.WriteLine(opts, $"Generated {allStatements.Count} SQL files in {outputFolder}");
            }

            Output.WriteJson(opts, new
            {
                command = "load",
                config_path = configPath,
                total_files = sourceFiles.Count,
                processed = processedCount,
                errors = errorCount,
                generate_sql = opts.GenerateSql,
                sql_files_generated = sqlFileCount,
            });
        }

        return 0;
    }
    catch (Exception ex)
    {
        SysConsole.Error.WriteLine($"Error: {ex.Message}");
        return 1;
    }
}

static void GenerateConfig(string outputFolder)
{
    var configPath = Path.Combine(outputFolder, "config.yaml");
    var configContent = """
manifest_path: "manifest.csv"
data_folder: "data"
contracts_folder: "contacts"
output_folder: "output"
database_name: "warehouse.duckdb"
""";
    File.WriteAllText(configPath, configContent);
}

static void GenerateContract(string outputFolder)
{
    var contractPath = Path.Combine(outputFolder, "contract_customer.yaml");
    var contractContent = """
# Destination table
table: customer
# Schema definitions
schema:
  staging: bronze_raw
  valid: bronze
  invalid: bronze_quarantine
# Column definitions
columns:
  - canonical: customer_id
    accepts: [customer_id, cust_id, "Customer ID", customerid]
    type: VARCHAR
    required: true
  - canonical: signup_date
    accepts: [signup_date, sign_up_date, "Signup Date"]
    type: DATE
    required: true
  - canonical: email
    accepts: [email, Email, email_address]
    type: VARCHAR
    required: false
""";
    File.WriteAllText(contractPath, contractContent);
}

static void GenerateManifest(string outputFolder)
{
    var manifestPath = Path.Combine(outputFolder, "manifest.csv");
    var manifestContent = "submitter,source_folder,file_pattern,contract\n";
    File.WriteAllText(manifestPath, manifestContent);
}

/// <summary>
/// Shared CLI options available on every verb.
/// </summary>
public interface ICliOptions
{
    bool Quiet { get; }
    bool Json { get; }
}

/// <summary>
/// Helper to write output respecting --quiet and --json flags.
/// </summary>
static class Output
{
    public static void WriteLine(ICliOptions opts, string message)
    {
        if (!opts.Quiet && !opts.Json)
            SysConsole.WriteLine(message);
    }

    public static void WriteJson(ICliOptions opts, object data)
    {
        if (!opts.Json) return;
        var json = System.Text.Json.JsonSerializer.Serialize(data,
            new System.Text.Json.JsonSerializerOptions
            {
                WriteIndented = true,
                PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.SnakeCaseLower,
            });
        SysConsole.WriteLine(json);
    }
}

[Verb("init", HelpText = "Generate all configuration files in the current working directory.")]
public class InitOptions : ICliOptions
{
    [Option('q', "quiet", Required = false, HelpText = "Suppress stdout output.")]
    public bool Quiet { get; set; }

    [Option("json", Required = false, HelpText = "Output results as JSON.")]
    public bool Json { get; set; }
}

[Verb("new", HelpText = "Generate a new configuration file.")]
public class NewOptions : ICliOptions
{
    [Value(0, MetaName = "type", Required = true, HelpText = "Type of file to generate: config, contract, or manifest.")]
    public string? SubCommand { get; set; }

    [Option('o', "output-folder", Required = false, HelpText = "Output folder for the generated file (default: current directory).")]
    public string? OutputFolder { get; set; }

    [Option('q', "quiet", Required = false, HelpText = "Suppress stdout output.")]
    public bool Quiet { get; set; }

    [Option("json", Required = false, HelpText = "Output results as JSON.")]
    public bool Json { get; set; }
}

[Verb("load", HelpText = "Load data files using the specified configuration.")]
public class LoadOptions : ICliOptions
{
    [Value(0, MetaName = "config", Required = true, HelpText = "Path to the YAML configuration file.")]
    public string? ConfigPath { get; set; }

    [Option('g', "generate-sql", Required = false, HelpText = "Generate .sql files for each statement instead of executing against the database.")]
    public bool GenerateSql { get; set; }

    [Option('q', "quiet", Required = false, HelpText = "Suppress stdout output.")]
    public bool Quiet { get; set; }

    [Option("json", Required = false, HelpText = "Output results as JSON.")]
    public bool Json { get; set; }
}
