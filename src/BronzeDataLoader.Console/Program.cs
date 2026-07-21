using CommandLine;
using BronzeDataLoader.Library.Models;

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

        SysConsole.WriteLine($"Generated configuration files in {outputFolder}");
        SysConsole.WriteLine("  config.yaml");
        SysConsole.WriteLine("  contract_customer.yaml");
        SysConsole.WriteLine("  manifest.csv");
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

        switch (opts.SubCommand?.ToLowerInvariant())
        {
            case "config":
                GenerateConfig(outputFolder);
                SysConsole.WriteLine($"Generated config.yaml in {outputFolder}");
                break;
            case "contract":
                GenerateContract(outputFolder);
                SysConsole.WriteLine($"Generated contract_customer.yaml in {outputFolder}");
                break;
            case "manifest":
                GenerateManifest(outputFolder);
                SysConsole.WriteLine($"Generated manifest.csv in {outputFolder}");
                break;
            default:
                SysConsole.Error.WriteLine($"Unknown sub-command: {opts.SubCommand}");
                SysConsole.Error.WriteLine("Usage: bronze-data-loader new [config|contract|manifest] --output-folder <path>");
                return 1;
        }

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

            foreach (var sourceFile in sourceFiles)
            {
                try
                {
                    sourceFile.GenerateAndExecuteSql(appConfig);
                    SysConsole.WriteLine($"Processed {sourceFile.FilePath} successfully.");
                }
                catch (Exception ex)
                {
                    SysConsole.WriteLine($"Error processing {sourceFile.FilePath}: {ex.Message}");
                }
            }
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
manifest_path: "data-call-manifest.csv"
data_folder: "data"
contracts_folder: "contacts"
output_folder: "output"
database_path: "database"
raw_schema: "bronze_raw"
schema: "bronze"
schema_quarantine: "bronze_quarantine"
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

[Verb("init", HelpText = "Generate all configuration files in the current working directory.")]
public class InitOptions { }

[Verb("new", HelpText = "Generate a new configuration file.")]
public class NewOptions
{
    [Value(0, MetaName = "type", Required = true, HelpText = "Type of file to generate: config, contract, or manifest.")]
    public string? SubCommand { get; set; }

    [Option('o', "output-folder", Required = false, HelpText = "Output folder for the generated file (default: current directory).")]
    public string? OutputFolder { get; set; }
}

[Verb("load", HelpText = "Load data files using the specified configuration.")]
public class LoadOptions
{
    [Value(0, MetaName = "config", Required = true, HelpText = "Path to the YAML configuration file.")]
    public string? ConfigPath { get; set; }
}
