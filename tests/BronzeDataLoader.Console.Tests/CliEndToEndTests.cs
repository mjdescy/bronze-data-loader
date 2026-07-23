using System.Diagnostics;

namespace BronzeDataLoader.Console.Tests;

public class CliEndToEndTests
{
    private static string ConsoleDllPath => Path.Combine(
        AppContext.BaseDirectory,                          // tests/.../bin/Debug/net10.0/
        "..", "..", "..", "..", "..",                      // up to repo root
        "src", "BronzeDataLoader.Console",
        "bin", "Debug", "net10.0",
        "bronze-data-loader.dll"
    );

    /// <summary>
    /// Run the console app as a child process and return exit code + output.
    /// </summary>
    private static (int ExitCode, string StdOut, string StdErr) RunConsole(string args, string? workingDir = null)
    {
        var dll = Path.GetFullPath(ConsoleDllPath);
        if (!File.Exists(dll))
            throw new FileNotFoundException($"Console DLL not found at {dll}. Build the solution first.");

        var psi = new ProcessStartInfo
        {
            FileName = "dotnet",
            Arguments = $"\"{dll}\" {args}",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        if (workingDir is not null)
            psi.WorkingDirectory = workingDir;

        using var process = new Process { StartInfo = psi };
        process.Start();
        process.WaitForExit(30_000);

        var stdOut = process.StandardOutput.ReadToEnd();
        var stdErr = process.StandardError.ReadToEnd();

        return (process.ExitCode, stdOut, stdErr);
    }

    // ===== Help / Usage =====

    [Fact]
    public void Help_ShowsVerbs()
    {
        // CommandLineParser writes help to stderr and returns exit code 1
        var (exitCode, stdOut, stdErr) = RunConsole("--help");

        Assert.Equal(1, exitCode);
        var output = stdOut + stdErr;
        Assert.Contains("init", output, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("new", output, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("load", output, StringComparison.OrdinalIgnoreCase);
    }

    // ===== Init =====

    [Fact]
    public void Init_GeneratesAllConfigFiles()
    {
        var dir = CreateTempDir();
        try
        {
            var (exitCode, stdOut, _) = RunConsole("init", workingDir: dir);

            Assert.Equal(0, exitCode);
            Assert.Contains("config.yaml", stdOut);
            Assert.Contains("contract_customer.yaml", stdOut);
            Assert.Contains("manifest.csv", stdOut);
            Assert.True(File.Exists(Path.Combine(dir, "config.yaml")));
            Assert.True(File.Exists(Path.Combine(dir, "contract_customer.yaml")));
            Assert.True(File.Exists(Path.Combine(dir, "manifest.csv")));
        }
        finally
        {
            CleanupDir(dir);
        }
    }

    // ===== New Subcommands =====

    [Fact]
    public void NewConfig_GeneratesConfigYaml()
    {
        var dir = CreateTempDir();
        try
        {
            var (exitCode, stdOut, _) = RunConsole($"new config --output-folder \"{dir}\"");

            Assert.Equal(0, exitCode);
            Assert.Contains("config.yaml", stdOut);
            Assert.True(File.Exists(Path.Combine(dir, "config.yaml")));
        }
        finally
        {
            CleanupDir(dir);
        }
    }

    [Fact]
    public void NewContract_GeneratesContractYaml()
    {
        var dir = CreateTempDir();
        try
        {
            var (exitCode, stdOut, _) = RunConsole($"new contract --output-folder \"{dir}\"");

            Assert.Equal(0, exitCode);
            Assert.Contains("contract_customer.yaml", stdOut);
            Assert.True(File.Exists(Path.Combine(dir, "contract_customer.yaml")));
        }
        finally
        {
            CleanupDir(dir);
        }
    }

    [Fact]
    public void NewManifest_GeneratesManifestCsv()
    {
        var dir = CreateTempDir();
        try
        {
            var (exitCode, stdOut, _) = RunConsole($"new manifest --output-folder \"{dir}\"");

            Assert.Equal(0, exitCode);
            Assert.Contains("manifest.csv", stdOut);
            Assert.True(File.Exists(Path.Combine(dir, "manifest.csv")));
        }
        finally
        {
            CleanupDir(dir);
        }
    }

    // ===== Load — Normal Execution =====

    [Fact]
    public void Load_ValidData_ProcessesSuccessfully()
    {
        var dir = CreateTempDir();
        try
        {
            var configPath = SetupFullPipeline(dir, missingRequiredCol: false);
            var (exitCode, stdOut, _) = RunConsole($"load \"{configPath}\"");

            Assert.Equal(0, exitCode);
            Assert.Contains("Processed", stdOut);
            Assert.DoesNotContain("Error", stdOut);
            Assert.DoesNotContain("Quarantined", stdOut);
        }
        finally
        {
            CleanupDir(dir);
        }
    }

    [Fact]
    public void Load_MissingRequiredColumn_Quarantines()
    {
        var dir = CreateTempDir();
        try
        {
            var configPath = SetupFullPipeline(dir, missingRequiredCol: true);
            var (exitCode, stdOut, _) = RunConsole($"load \"{configPath}\"");

            Assert.Equal(0, exitCode);
            Assert.Contains("Quarantined", stdOut);
        }
        finally
        {
            CleanupDir(dir);
        }
    }

    // ===== Load --generate-sql =====

    [Fact]
    public void Load_GenerateSql_ProducesSqlFiles()
    {
        var dir = CreateTempDir();
        try
        {
            var configPath = SetupFullPipeline(dir, missingRequiredCol: false);
            var (exitCode, stdOut, _) = RunConsole($"load \"{configPath}\" -g");

            Assert.Equal(0, exitCode);
            Assert.Contains("SQL files", stdOut);

            var outputDir = Path.Combine(dir, "output");
            Assert.True(Directory.Exists(outputDir));
            var sqlFiles = Directory.GetFiles(outputDir, "*.sql");
            Assert.NotEmpty(sqlFiles);
            Assert.Contains(sqlFiles, f => f.Contains("create_schema"));
            Assert.Contains(sqlFiles, f => f.Contains("create_table"));
            Assert.Contains(sqlFiles, f => f.Contains("create_view"));
            Assert.Contains(sqlFiles, f => f.Contains("insert_table_load"));
        }
        finally
        {
            CleanupDir(dir);
        }
    }

    [Fact]
    public void Load_GenerateSql_WithQuarantine_IncludesQuarantineFiles()
    {
        var dir = CreateTempDir();
        try
        {
            var configPath = SetupFullPipeline(dir, missingRequiredCol: true);
            var (exitCode, stdOut, _) = RunConsole($"load \"{configPath}\" -g");

            Assert.Equal(0, exitCode);
            Assert.Contains("SQL files", stdOut);

            var outputDir = Path.Combine(dir, "output");
            var sqlFiles = Directory.GetFiles(outputDir, "*.sql");
            Assert.Contains(sqlFiles, f => f.Contains("create_quarantine_view"));
            Assert.Contains(sqlFiles, f => f.Contains("insert_quarantine_log"));
            Assert.Contains(sqlFiles, f => f.Contains("insert_table_load"));
        }
        finally
        {
            CleanupDir(dir);
        }
    }

    [Fact]
    public void Load_GenerateSqlFiles_AreOrderedNumerically()
    {
        var dir = CreateTempDir();
        try
        {
            var configPath = SetupFullPipeline(dir, missingRequiredCol: false);
            var (exitCode, _, _) = RunConsole($"load \"{configPath}\" -g");

            Assert.Equal(0, exitCode);

            var outputDir = Path.Combine(dir, "output");
            var sqlFiles = Directory.GetFiles(outputDir, "*.sql")
                .OrderBy(f => f)
                .ToArray();

            // First file should be a schema creation
            Assert.StartsWith("001_", Path.GetFileName(sqlFiles[0]));
            // Last file should be a table_load insert
            var lastFile = Path.GetFileName(sqlFiles[^1]);
            Assert.Contains("insert_table_load", lastFile);
        }
        finally
        {
            CleanupDir(dir);
        }
    }

    // ===== Error handling =====

    [Fact]
    public void Load_MissingConfigFile_ReturnsError()
    {
        var (exitCode, _, stdErr) = RunConsole("load /nonexistent/path.yaml");
        Assert.Equal(1, exitCode);
        Assert.NotEmpty(stdErr);
    }

    [Fact]
    public void UnknownVerb_ReturnsError()
    {
        var (exitCode, _, _) = RunConsole("nonexistent");
        Assert.Equal(1, exitCode);
    }

    // ===== Test Infrastructure =====

    /// <summary>
    /// Create a temp directory with config.yaml, manifest.csv, contract YAML,
    /// and source data file(s) for a full pipeline test.
    /// </summary>
    private static string SetupFullPipeline(string rootDir, bool missingRequiredCol)
    {
        // config.yaml
        var configPath = Path.Combine(rootDir, "config.yaml");
        var outputDir = Path.Combine(rootDir, "output");
        Directory.CreateDirectory(outputDir);

        File.WriteAllText(configPath, $"""
manifest_path: "{rootDir}/manifest.csv"
data_folder: "{rootDir}"
contracts_folder: "{rootDir}"
output_folder: "{outputDir}"
database_name: ":memory:"
""");

        // contract_customer.yaml
        var contractContent = missingRequiredCol
            ? """
table: customer
schema:
  staging: bronze_raw
  valid: bronze
  invalid: bronze_quarantine
columns:
  - canonical: id
    accepts: [id]
    type: VARCHAR
    required: true
  - canonical: required_col
    accepts: [required_col]
    type: VARCHAR
    required: true
"""
            : """
table: customer
schema:
  staging: bronze_raw
  valid: bronze
  invalid: bronze_quarantine
columns:
  - canonical: id
    accepts: [id]
    type: VARCHAR
    required: true
  - canonical: name
    accepts: [name]
    type: VARCHAR
    required: false
""";

        File.WriteAllText(Path.Combine(rootDir, "contract_customer.yaml"), contractContent);

        // Source data
        var dataDir = Path.Combine(rootDir, "data");
        Directory.CreateDirectory(dataDir);

        var dataContent = missingRequiredCol
            ? "id\n1\n2\n"
            : "id,name\n1,Alice\n2,Bob\n";

        File.WriteAllText(Path.Combine(dataDir, "data.csv"), dataContent);

        // manifest.csv
        File.WriteAllText(Path.Combine(rootDir, "manifest.csv"),
            "submitter,source_folder,file_pattern,contract\n" +
            "Test,data,*.csv,contract_customer.yaml\n");

        return configPath;
    }

    private static string CreateTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void CleanupDir(string dir)
    {
        if (Directory.Exists(dir))
            Directory.Delete(dir, recursive: true);
    }
}
