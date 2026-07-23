using DuckDB.NET.Data;
using BronzeDataLoader.Library.Contract;
using BronzeDataLoader.Library.Models;
using BronzeDataLoader.Library.Sql;

namespace BronzeDataLoader.Library.Tests;

public class ManifestTests
{
    // ===== CSV Parsing Tests =====

    public class CsvParsingTests
    {
        [Fact]
        public void FromCsv_ValidCsv_ParsesEntries()
        {
            var csv = "submitter,source_folder,file_pattern,contract\nAcme,data/in,cust*.csv,contract_customer.yaml\nBeta,data/in,*.tsv,other.yaml\n";

            var path = Path.GetTempFileName() + ".csv";
            try
            {
                File.WriteAllText(path, csv);

                var manifest = Manifest.FromCsv(path);

                Assert.Equal(2, manifest.Entries.Count);
                Assert.Equal("Acme", manifest.Entries[0].Submitter);
                Assert.Equal("data/in", manifest.Entries[0].SourceFolder);
                Assert.Equal("cust*.csv", manifest.Entries[0].FilePattern);
                Assert.Equal("contract_customer.yaml", manifest.Entries[0].Contract);

                Assert.Equal("Beta", manifest.Entries[1].Submitter);
            }
            finally
            {
                if (File.Exists(path)) File.Delete(path);
            }
        }

        [Fact]
        public void FromCsv_EmptyCsv_ThrowsInvalidOperationException()
        {
            var csv = "submitter,source_folder,file_pattern,contract\n";

            var path = Path.GetTempFileName() + ".csv";
            try
            {
                File.WriteAllText(path, csv);

                Assert.Throws<InvalidOperationException>(() => Manifest.FromCsv(path));
            }
            finally
            {
                if (File.Exists(path)) File.Delete(path);
            }
        }

        [Fact]
        public void FromCsv_FileNotFound_ThrowsFileNotFoundException()
        {
            Assert.Throws<FileNotFoundException>(() => Manifest.FromCsv("/nonexistent/path.csv"));
        }

        [Fact]
        public void FromCsv_MultipleEntries_ParsesAll()
        {
            var csv = "submitter,source_folder,file_pattern,contract\n" +
                      "Alpha,path/a,*.csv,c1.yaml\n" +
                      "Beta,path/b,*.tsv,c2.yaml\n" +
                      "Gamma,path/c,*.xlsx,c3.yaml\n";

            var path = Path.GetTempFileName() + ".csv";
            try
            {
                File.WriteAllText(path, csv);

                var manifest = Manifest.FromCsv(path);

                Assert.Equal(3, manifest.Entries.Count);
                Assert.Equal("Gamma", manifest.Entries[2].Submitter);
                Assert.Equal("*.xlsx", manifest.Entries[2].FilePattern);
            }
            finally
            {
                if (File.Exists(path)) File.Delete(path);
            }
        }

        [Fact]
        public void FromCsv_EntriesWithCommas_HandlesQuoting()
        {
            var csv = "submitter,source_folder,file_pattern,contract\n\"Capitalism, Inc\",path/a,*.csv,c.yaml\n";

            var path = Path.GetTempFileName() + ".csv";
            try
            {
                File.WriteAllText(path, csv);

                var manifest = Manifest.FromCsv(path);

                Assert.Single(manifest.Entries);
                Assert.Equal("Capitalism, Inc", manifest.Entries[0].Submitter);
            }
            finally
            {
                if (File.Exists(path)) File.Delete(path);
            }
        }
    }

    // ===== Matching Files Tests =====

    public class MatchingFilesTests
    {
        [Fact]
        public void RelativePath_FindsFiles()
        {
            var dir = CreateTestDir();
            try
            {
                var dataDir = Path.Combine(dir, "data");
                Directory.CreateDirectory(dataDir);
                var file1 = Path.Combine(dataDir, "test.csv");
                File.WriteAllText(file1, "a,b\n1,2\n");

                var entry = new ManifestEntry
                {
                    Submitter = "Acme",
                    SourceFolder = "data",
                    FilePattern = "*.csv",
                    Contract = "c.yaml",
                };

                var configDir = Path.Combine(dir, "config");
                Directory.CreateDirectory(configDir);
                var configPath = Path.Combine(configDir, "config.yaml");
                File.WriteAllText(configPath, $"data_folder: \"{dir}\"\ncontracts_folder: \"{dir}\"\noutput_folder: \"{dir}\"\ndatabase_name: \":memory:\"\n");

                var appConfig = AppConfig.FromYaml(configPath);
                using (appConfig.Connection!)
                {
                    var files = entry.MatchingFiles(appConfig);
                    Assert.Single(files);
                    Assert.Equal(file1, files[0]);
                }
            }
            finally
            {
                if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
            }
        }

        [Fact]
        public void AbsolutePath_FindsFiles()
        {
            var dir = CreateTestDir();
            try
            {
                var dataDir = Path.Combine(dir, "absdata");
                Directory.CreateDirectory(dataDir);
                var file1 = Path.Combine(dataDir, "test.csv");
                File.WriteAllText(file1, "a,b\n1,2\n");

                var entry = new ManifestEntry
                {
                    Submitter = "Acme",
                    SourceFolder = dataDir,
                    FilePattern = "*.csv",
                    Contract = "c.yaml",
                };

                var configDir = Path.Combine(dir, "config");
                Directory.CreateDirectory(configDir);
                var configPath = Path.Combine(configDir, "config.yaml");
                File.WriteAllText(configPath, $"data_folder: \"{dir}\"\ncontracts_folder: \"{dir}\"\noutput_folder: \"{dir}\"\ndatabase_name: \":memory:\"\n");

                var appConfig = AppConfig.FromYaml(configPath);
                using (appConfig.Connection!)
                {
                    var files = entry.MatchingFiles(appConfig);
                    Assert.Single(files);
                    Assert.Equal(file1, files[0]);
                }
            }
            finally
            {
                if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
            }
        }

        [Fact]
        public void RecursiveGlob_FindsFilesInSubdirectories()
        {
            var dir = CreateTestDir();
            try
            {
                var dataDir = Path.Combine(dir, "data");
                var subDir = Path.Combine(dataDir, "sub");
                Directory.CreateDirectory(subDir);
                var file1 = Path.Combine(subDir, "nested.csv");
                File.WriteAllText(file1, "a,b\n1,2\n");

                var entry = new ManifestEntry
                {
                    Submitter = "Acme",
                    SourceFolder = "data",
                    FilePattern = "*.csv",
                    Contract = "c.yaml",
                };

                var configDir = Path.Combine(dir, "config");
                Directory.CreateDirectory(configDir);
                var configPath = Path.Combine(configDir, "config.yaml");
                File.WriteAllText(configPath, $"data_folder: \"{dir}\"\ncontracts_folder: \"{dir}\"\noutput_folder: \"{dir}\"\ndatabase_name: \":memory:\"\n");

                var appConfig = AppConfig.FromYaml(configPath);
                using (appConfig.Connection!)
                {
                    var files = entry.MatchingFiles(appConfig);
                    Assert.Contains(file1, files);
                }
            }
            finally
            {
                if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
            }
        }

        [Fact]
        public void NoMatchingFiles_ReturnsEmpty()
        {
            var dir = CreateTestDir();
            try
            {
                var dataDir = Path.Combine(dir, "data");
                Directory.CreateDirectory(dataDir);

                var entry = new ManifestEntry
                {
                    Submitter = "Acme",
                    SourceFolder = "data",
                    FilePattern = "*.xyz",
                    Contract = "c.yaml",
                };

                var configDir = Path.Combine(dir, "config");
                Directory.CreateDirectory(configDir);
                var configPath = Path.Combine(configDir, "config.yaml");
                File.WriteAllText(configPath, $"data_folder: \"{dir}\"\ncontracts_folder: \"{dir}\"\noutput_folder: \"{dir}\"\ndatabase_name: \":memory:\"\n");

                var appConfig = AppConfig.FromYaml(configPath);
                using (appConfig.Connection!)
                {
                    var files = entry.MatchingFiles(appConfig);
                    Assert.Empty(files);
                }
            }
            finally
            {
                if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
            }
        }

        [Fact]
        public void FolderNotFound_ThrowsDirectoryNotFoundException()
        {
            var dir = CreateTestDir();
            try
            {
                var entry = new ManifestEntry
                {
                    Submitter = "Acme",
                    SourceFolder = "nonexistent",
                    FilePattern = "*.csv",
                    Contract = "c.yaml",
                };

                var configDir = Path.Combine(dir, "config");
                Directory.CreateDirectory(configDir);
                var configPath = Path.Combine(configDir, "config.yaml");
                File.WriteAllText(configPath, $"data_folder: \"{dir}\"\ncontracts_folder: \"{dir}\"\noutput_folder: \"{dir}\"\ndatabase_name: \":memory:\"\n");

                var appConfig = AppConfig.FromYaml(configPath);
                using (appConfig.Connection!)
                {
                    Assert.Throws<DirectoryNotFoundException>(() => entry.MatchingFiles(appConfig));
                }
            }
            finally
            {
                if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
            }
        }
    }

    // ===== Resolve Contract Tests =====

    public class ResolveContractTests
    {
        [Fact]
        public void RelativePath_LoadsContract()
        {
            var dir = CreateTestDir();
            try
            {
                var contractDir = Path.Combine(dir, "contracts");
                Directory.CreateDirectory(contractDir);
                var contractPath = Path.Combine(contractDir, "contract_customer.yaml");
                File.WriteAllText(contractPath, """
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
""");

                var entry = new ManifestEntry
                {
                    Submitter = "Acme",
                    SourceFolder = "data",
                    FilePattern = "*.csv",
                    Contract = "contract_customer.yaml",
                };

                var configDir = Path.Combine(dir, "config");
                Directory.CreateDirectory(configDir);
                var configPath = Path.Combine(configDir, "config.yaml");
                File.WriteAllText(configPath, $"data_folder: \"{dir}\"\ncontracts_folder: \"{contractDir}\"\noutput_folder: \"{dir}\"\ndatabase_name: \":memory:\"\n");

                var appConfig = AppConfig.FromYaml(configPath);
                using (appConfig.Connection!)
                {
                    var contract = entry.ResolveContract(appConfig);
                    Assert.Equal("customer", contract.Table);
                    Assert.Single(contract.Columns);
                }
            }
            finally
            {
                if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
            }
        }

        [Fact]
        public void AbsolutePath_LoadsContract()
        {
            var dir = CreateTestDir();
            try
            {
                var contractPath = Path.Combine(dir, "abs.yaml");
                File.WriteAllText(contractPath, """
table: test
schema:
  staging: raw
  valid: clean
  invalid: bad
columns: []
""");

                var entry = new ManifestEntry
                {
                    Submitter = "Acme",
                    SourceFolder = "data",
                    FilePattern = "*.csv",
                    Contract = contractPath,
                };

                var configDir = Path.Combine(dir, "config");
                Directory.CreateDirectory(configDir);
                var configPath = Path.Combine(configDir, "config.yaml");
                File.WriteAllText(configPath, $"data_folder: \"{dir}\"\ncontracts_folder: \"{dir}\"\noutput_folder: \"{dir}\"\ndatabase_name: \":memory:\"\n");

                var appConfig = AppConfig.FromYaml(configPath);
                using (appConfig.Connection!)
                {
                    var contract = entry.ResolveContract(appConfig);
                    Assert.Equal("test", contract.Table);
                }
            }
            finally
            {
                if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
            }
        }

        [Fact]
        public void MissingContract_ThrowsFileNotFoundException()
        {
            var dir = CreateTestDir();
            try
            {
                var entry = new ManifestEntry
                {
                    Submitter = "Acme",
                    SourceFolder = "data",
                    FilePattern = "*.csv",
                    Contract = "nonexistent.yaml",
                };

                var configDir = Path.Combine(dir, "config");
                Directory.CreateDirectory(configDir);
                var configPath = Path.Combine(configDir, "config.yaml");
                File.WriteAllText(configPath, $"data_folder: \"{dir}\"\ncontracts_folder: \"{dir}\"\noutput_folder: \"{dir}\"\ndatabase_name: \":memory:\"\n");

                var appConfig = AppConfig.FromYaml(configPath);
                using (appConfig.Connection!)
                {
                    Assert.Throws<FileNotFoundException>(() => entry.ResolveContract(appConfig));
                }
            }
            finally
            {
                if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
            }
        }
    }

    // ===== ToSourceFileList Tests =====

    public class ToSourceFileListTests
    {
        [Fact]
        public void ToSourceFileList_AggregatesEntries()
        {
            var dir = CreateTestDir();
            try
            {
                var dataDir = Path.Combine(dir, "data");
                Directory.CreateDirectory(dataDir);
                File.WriteAllText(Path.Combine(dataDir, "f1.csv"), "a,b\n1,2\n");
                File.WriteAllText(Path.Combine(dataDir, "f2.csv"), "a,b\n3,4\n");

                var contractPath = Path.Combine(dir, "c.yaml");
                File.WriteAllText(contractPath, "table: t\nschema:\n  staging: raw\n  valid: clean\n  invalid: bad\ncolumns: []\n");

                var manifestCsv = "submitter,source_folder,file_pattern,contract\nAcme,data,*.csv,c.yaml\n";
                var manifestPath = Path.Combine(dir, "manifest.csv");
                File.WriteAllText(manifestPath, manifestCsv);

                var configPath = Path.Combine(dir, "config.yaml");
                File.WriteAllText(configPath, $"manifest_path: \"{manifestPath}\"\ndata_folder: \"{dir}\"\ncontracts_folder: \"{dir}\"\noutput_folder: \"{dir}\"\ndatabase_name: \":memory:\"\n");

                var appConfig = AppConfig.FromYaml(configPath);
                using (appConfig.Connection!)
                {
                    var manifest = Manifest.FromCsv(manifestPath);
                    var sourceFiles = manifest.ToSourceFileList(appConfig);

                    Assert.Equal(2, sourceFiles.Count);
                    Assert.All(sourceFiles, sf => Assert.Equal("Acme", sf.Submitter));
                }
            }
            finally
            {
                if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
            }
        }

        [Fact]
        public void ToSourceFileList_NoMatchingFiles_ThrowsInvalidOperationException()
        {
            var dir = CreateTestDir();
            try
            {
                var dataDir = Path.Combine(dir, "data");
                Directory.CreateDirectory(dataDir);

                var contractPath = Path.Combine(dir, "c.yaml");
                File.WriteAllText(contractPath, "table: t\nschema:\n  staging: raw\n  valid: clean\n  invalid: bad\ncolumns: []\n");

                var manifestCsv = "submitter,source_folder,file_pattern,contract\nAcme,data,*.nonexistent,c.yaml\n";
                var manifestPath = Path.Combine(dir, "manifest.csv");
                File.WriteAllText(manifestPath, manifestCsv);

                var configPath = Path.Combine(dir, "config.yaml");
                File.WriteAllText(configPath, $"manifest_path: \"{manifestPath}\"\ndata_folder: \"{dir}\"\ncontracts_folder: \"{dir}\"\noutput_folder: \"{dir}\"\ndatabase_name: \":memory:\"\n");

                var appConfig = AppConfig.FromYaml(configPath);
                using (appConfig.Connection!)
                {
                    var manifest = Manifest.FromCsv(manifestPath);
                    Assert.Throws<InvalidOperationException>(() => manifest.ToSourceFileList(appConfig));
                }
            }
            finally
            {
                if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
            }
        }
    }

    // ===== SourceFile Tests =====

    public class SourceFileExecutionTests
    {
        [Fact]
        public void GenerateSql_ReturnsBronzeViewSql()
        {
            using var conn = CreateInMemoryConnection();
            var csvPath = CreateTempCsv("customer_id,signup_date,email\n1,2024-01-01,a@b.com\n");
            try
            {
                var contract = new Contract.Contract
                {
                    Table = "customer",
                    Schema = new ContractSchema(),
                    Columns =
                    [
                        new ContractColumn { Canonical = "customer_id", Accepts = ["customer_id"], Type = "VARCHAR", Required = true },
                    ],
                };

                // First load the raw data
                var gen = new SqlGenerator(csvPath, "Acme", contract, conn);
                ExecuteSql(conn, gen.BuildCreateSchemaIfNotExists());
                ExecuteSql(conn, gen.BuildRawLoadSql());

                // Then test GenerateSql
                var sourceFile = new SourceFile
                {
                    FilePath = csvPath,
                    Submitter = "Acme",
                    Contract = contract,
                };

                var appConfig = new AppConfig
                {
                    ManifestPath = "",
                    DataFolder = ".",
                    ContractsFolder = ".",
                    OutputFolder = ".",
                    DatabaseName = ":memory:",
                    Connection = conn,
                };

                var (sql, warnings) = sourceFile.GenerateSql(appConfig);

                Assert.Contains("CREATE OR REPLACE VIEW", sql);
                Assert.Contains("\"bronze\".\"customer_Acme_", sql);
            }
            finally
            {
                if (File.Exists(csvPath)) File.Delete(csvPath);
            }
        }

        [Fact]
        public void GenerateAndExecuteSql_ValidFile_CreatesTablesAndView()
        {
            using var conn = CreateInMemoryConnection();
            var csvPath = CreateTempCsv("customer_id,signup_date,email\n1,2024-01-01,a@b.com\n");
            try
            {
                var contract = new Contract.Contract
                {
                    Table = "customer",
                    Schema = new ContractSchema(),
                    Columns =
                    [
                        new ContractColumn { Canonical = "customer_id", Accepts = ["customer_id"], Type = "VARCHAR", Required = true },
                        new ContractColumn { Canonical = "signup_date", Accepts = ["signup_date"], Type = "DATE", Required = true },
                        new ContractColumn { Canonical = "email", Accepts = ["email"], Type = "VARCHAR", Required = false },
                    ],
                };

                var sourceFile = new SourceFile
                {
                    FilePath = csvPath,
                    Submitter = "Acme",
                    Contract = contract,
                };

                var appConfig = new AppConfig
                {
                    ManifestPath = "",
                    DataFolder = ".",
                    ContractsFolder = ".",
                    OutputFolder = ".",
                    DatabaseName = ":memory:",
                    Connection = conn,
                };

                sourceFile.GenerateAndExecuteSql(appConfig);

                // Verify raw table exists
                var gen = new SqlGenerator(csvPath, "Acme", contract, conn);
                using var cmd = conn.CreateCommand();
                cmd.CommandText = $"SELECT table_name FROM information_schema.tables WHERE table_schema = 'bronze_raw'";
                using var reader = cmd.ExecuteReader();
                Assert.True(reader.Read());

                // Verify bronze view exists
                cmd.CommandText = $"SELECT table_name FROM information_schema.tables WHERE table_schema = 'bronze' AND table_type = 'VIEW'";
                using var reader2 = cmd.ExecuteReader();
                Assert.True(reader2.Read());
            }
            finally
            {
                if (File.Exists(csvPath)) File.Delete(csvPath);
            }
        }

        [Fact]
        public void GenerateAndExecuteSql_ExtraColumns_ProducesWarning()
        {
            // Capture console output
            var stringWriter = new StringWriter();
            var originalOut = System.Console.Out;
            System.Console.SetOut(stringWriter);

            try
            {
                using var conn = CreateInMemoryConnection();
                var csvPath = CreateTempCsv("customer_id,signup_date,email,extra_column\n1,2024-01-01,a@b.com,extra\n");
                try
                {
                    var contract = new Contract.Contract
                    {
                        Table = "customer",
                        Schema = new ContractSchema(),
                        Columns =
                        [
                            new ContractColumn { Canonical = "customer_id", Accepts = ["customer_id"], Type = "VARCHAR", Required = true },
                            new ContractColumn { Canonical = "signup_date", Accepts = ["signup_date"], Type = "DATE", Required = true },
                        ],
                    };

                    var sourceFile = new SourceFile
                    {
                        FilePath = csvPath,
                        Submitter = "Acme",
                        Contract = contract,
                    };

                    var appConfig = new AppConfig
                    {
                        ManifestPath = "",
                        DataFolder = ".",
                        ContractsFolder = ".",
                        OutputFolder = ".",
                        DatabaseName = ":memory:",
                        Connection = conn,
                    };

                    sourceFile.GenerateAndExecuteSql(appConfig);

                    var output = stringWriter.ToString();
                    Assert.Contains("Warning:", output);
                    Assert.Contains("extra_column", output, StringComparison.OrdinalIgnoreCase);
                }
                finally
                {
                    if (File.Exists(csvPath)) File.Delete(csvPath);
                }
            }
            finally
            {
                System.Console.SetOut(originalOut);
            }
        }

        [Fact]
        public void GenerateAndExecuteSql_MissingRequired_Quarantines()
        {
            var stringWriter = new StringWriter();
            var originalOut = System.Console.Out;
            System.Console.SetOut(stringWriter);

            try
            {
                using var conn = CreateInMemoryConnection();
                var csvPath = CreateTempCsv("customer_id,signup_date\n1,2024-01-01\n"); // missing email but it's optional - make a contract where it's required
                try
                {
                    var contract = new Contract.Contract
                    {
                        Table = "customer",
                        Schema = new ContractSchema(),
                        Columns =
                        [
                            new ContractColumn { Canonical = "customer_id", Accepts = ["customer_id"], Type = "VARCHAR", Required = true },
                            new ContractColumn { Canonical = "signup_date", Accepts = ["signup_date"], Type = "DATE", Required = true },
                            new ContractColumn { Canonical = "email", Accepts = ["email"], Type = "VARCHAR", Required = true },
                            new ContractColumn { Canonical = "missing_required", Accepts = ["missing_required"], Type = "VARCHAR", Required = true },
                        ],
                    };

                    var sourceFile = new SourceFile
                    {
                        FilePath = csvPath,
                        Submitter = "Acme",
                        Contract = contract,
                    };

                    var appConfig = new AppConfig
                    {
                        ManifestPath = "",
                        DataFolder = ".",
                        ContractsFolder = ".",
                        OutputFolder = ".",
                        DatabaseName = ":memory:",
                        Connection = conn,
                    };

                    sourceFile.GenerateAndExecuteSql(appConfig);

                    var output = stringWriter.ToString();
                    Assert.Contains("Quarantined", output);
                    Assert.Contains("missing_required", output, StringComparison.OrdinalIgnoreCase);

                    // Verify raw table still exists
                    var gen = new SqlGenerator(csvPath, "Acme", contract, conn);
                    using var cmd = conn.CreateCommand();
                    cmd.CommandText = $"SELECT table_name FROM information_schema.tables WHERE table_schema = 'bronze_raw'";
                    using var reader = cmd.ExecuteReader();
                    Assert.True(reader.Read());

                    // Verify quarantine view exists
                    cmd.CommandText = $"SELECT table_name FROM information_schema.tables WHERE table_schema = 'bronze_quarantine' AND table_type = 'VIEW'";
                    using var reader2 = cmd.ExecuteReader();
                    Assert.True(reader2.Read());

                    // No bronze view
                    cmd.CommandText = $"SELECT COUNT(*) FROM information_schema.tables WHERE table_schema = 'bronze'";
                    using var reader3 = cmd.ExecuteReader();
                    Assert.True(reader3.Read());
                    Assert.Equal(0L, reader3.GetInt64(0));
                }
                finally
                {
                    if (File.Exists(csvPath)) File.Delete(csvPath);
                }
            }
            finally
            {
                System.Console.SetOut(originalOut);
            }
        }
    }

    // ===== Helpers =====

    private static string CreateTestDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static string CreateTempCsv(string content)
    {
        var path = Path.GetTempFileName() + ".csv";
        File.WriteAllText(path, content);
        return path;
    }

    private static DuckDBConnection CreateInMemoryConnection()
    {
        var conn = new DuckDBConnection("DataSource=:memory:");
        conn.Open();
        return conn;
    }

    private static void ExecuteSql(DuckDBConnection conn, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }
}
