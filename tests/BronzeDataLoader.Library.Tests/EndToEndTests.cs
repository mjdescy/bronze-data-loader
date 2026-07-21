using DuckDB.NET.Data;
using BronzeDataLoader.Library.Contract;
using BronzeDataLoader.Library.Models;
using BronzeDataLoader.Library.Sql;

namespace BronzeDataLoader.Library.Tests;

public class EndToEndTests
{
    [Fact]
    public void FullPipeline_ProcessesMultipleSubmitters_CreatesCorrectTables()
    {
        var testDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        try
        {
            // ===== Setup =====
            Directory.CreateDirectory(testDir);

            // Contract YAML
            var contractPath = Path.Combine(testDir, "customer.yaml");
            File.WriteAllText(contractPath, """
table: customer
schema:
  staging: bronze_raw
  valid: bronze
  invalid: bronze_quarantine
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
""");

            // Source data files
            var acmeDataDir = Path.Combine(testDir, "Acme", "data-in");
            Directory.CreateDirectory(acmeDataDir);
            File.WriteAllText(Path.Combine(acmeDataDir, "customer_data_1.csv"), "customer_id,signup_date,email\n1,2024-01-15,alice@acme.com\n2,2024-02-20,bob@acme.com\n");
            File.WriteAllText(Path.Combine(acmeDataDir, "customer_data_2.csv"), "customer_id,signup_date,email\n3,2024-03-10,charlie@acme.com\n");

            var betaDataDir = Path.Combine(testDir, "BetaCorp", "data-in");
            Directory.CreateDirectory(betaDataDir);
            File.WriteAllText(Path.Combine(betaDataDir, "customer_list.tsv"), "cust_id\tsignup_date\temail\n101\t2024-01-01\tbeta1@corp.com\n102\t2024-02-14\tbeta2@corp.com\n");

            // Manifest CSV
            var manifestPath = Path.Combine(testDir, "manifest.csv");
            File.WriteAllText(manifestPath, "submitter,source_folder,file_pattern,contract\n" +
                "Acme,Acme/data-in,customer_data_*.csv,customer.yaml\n" +
                "BetaCorp,BetaCorp/data-in,customer_list.tsv,customer.yaml\n");

            // Config YAML
            var configPath = Path.Combine(testDir, "config.yaml");
            File.WriteAllText(configPath, $"manifest_path: \"{manifestPath}\"\ndata_folder: \"{testDir}\"\ncontracts_folder: \"{testDir}\"\noutput_folder: \"{testDir}\"\ndatabase_path: \":memory:\"\n");

            // ===== Execute Pipeline =====
            var appConfig = AppConfig.FromYaml(configPath);

            using (appConfig.Connection!)
            {
                var manifest = Manifest.FromCsv(manifestPath);
                var sourceFiles = manifest.ToSourceFileList(appConfig);

                Assert.Equal(3, sourceFiles.Count);
                Assert.Contains(sourceFiles, sf => sf.Submitter == "Acme" && sf.FilePath.EndsWith("customer_data_1.csv"));
                Assert.Contains(sourceFiles, sf => sf.Submitter == "Acme" && sf.FilePath.EndsWith("customer_data_2.csv"));
                Assert.Contains(sourceFiles, sf => sf.Submitter == "BetaCorp" && sf.FilePath.EndsWith("customer_list.tsv"));

                foreach (var sourceFile in sourceFiles)
                {
                    sourceFile.GenerateAndExecuteSql(appConfig);
                }

                // ===== Verify bronze_raw tables =====
                var rawTableNames = GetTableNames(appConfig.Connection!, "bronze_raw");
                Assert.Equal(3, rawTableNames.Length);

                // Build expected table names from source files
                var acmeFiles = sourceFiles.Where(sf => sf.Submitter == "Acme").ToArray();
                var betaFiles = sourceFiles.Where(sf => sf.Submitter == "BetaCorp").ToArray();
                Assert.Equal(2, acmeFiles.Length);
                Assert.Single(betaFiles);

                var acmeRaw1 = SqlGenerator.QuoteQualified(
                    new SqlGenerator(acmeFiles[0].FilePath, acmeFiles[0].Submitter, acmeFiles[0].Contract, appConfig.Connection!).RawTableName);
                var acmeRaw2 = SqlGenerator.QuoteQualified(
                    new SqlGenerator(acmeFiles[1].FilePath, acmeFiles[1].Submitter, acmeFiles[1].Contract, appConfig.Connection!).RawTableName);
                var betaRaw = SqlGenerator.QuoteQualified(
                    new SqlGenerator(betaFiles[0].FilePath, betaFiles[0].Submitter, betaFiles[0].Contract, appConfig.Connection!).RawTableName);

                // Verify raw data for first Acme file
                using (var cmd = appConfig.Connection!.CreateCommand())
                {
                    cmd.CommandText = $"SELECT customer_id FROM {acmeRaw1} ORDER BY customer_id";
                    using var reader = cmd.ExecuteReader();
                    Assert.True(reader.Read());
                    Assert.Equal("1", reader.GetString(0));
                    Assert.True(reader.Read());
                    Assert.Equal("2", reader.GetString(0));
                    Assert.False(reader.Read());
                }

                // Verify raw data for second Acme file
                using (var cmd = appConfig.Connection!.CreateCommand())
                {
                    cmd.CommandText = $"SELECT customer_id FROM {acmeRaw2} ORDER BY customer_id";
                    using var reader = cmd.ExecuteReader();
                    Assert.True(reader.Read());
                    Assert.Equal("3", reader.GetString(0));
                    Assert.False(reader.Read());
                }

                // Verify BetaCorp raw data (using alternative column name cust_id)
                using (var cmd = appConfig.Connection!.CreateCommand())
                {
                    cmd.CommandText = $"SELECT cust_id FROM {betaRaw} ORDER BY cust_id";
                    using var reader = cmd.ExecuteReader();
                    Assert.True(reader.Read());
                    Assert.Equal("101", reader.GetString(0));
                    Assert.True(reader.Read());
                    Assert.Equal("102", reader.GetString(0));
                    Assert.False(reader.Read());
                }

                // ===== Verify bronze views =====
                var bronzeViewNames = GetTableNames(appConfig.Connection!, "bronze");
                Assert.Equal(3, bronzeViewNames.Length);

                var acmeView1 = SqlGenerator.QuoteQualified(
                    new SqlGenerator(acmeFiles[0].FilePath, acmeFiles[0].Submitter, acmeFiles[0].Contract, appConfig.Connection!).FullTableName);
                var acmeView2 = SqlGenerator.QuoteQualified(
                    new SqlGenerator(acmeFiles[1].FilePath, acmeFiles[1].Submitter, acmeFiles[1].Contract, appConfig.Connection!).FullTableName);
                var betaView = SqlGenerator.QuoteQualified(
                    new SqlGenerator(betaFiles[0].FilePath, betaFiles[0].Submitter, betaFiles[0].Contract, appConfig.Connection!).FullTableName);

                // Verify bronze view uses canonical column names
                using (var cmd = appConfig.Connection!.CreateCommand())
                {
                    cmd.CommandText = $"SELECT customer_id, signup_date::VARCHAR, email FROM {acmeView1} ORDER BY customer_id";
                    using var reader = cmd.ExecuteReader();
                    Assert.True(reader.Read());
                    Assert.Equal("1", reader.GetString(0));
                    Assert.Equal("2024-01-15", reader.GetString(1));
                    Assert.Equal("alice@acme.com", reader.GetString(2));
                    Assert.True(reader.Read());
                    Assert.Equal("2", reader.GetString(0));
                    Assert.False(reader.Read());
                }

                // Verify BetaCorp bronze view has canonical column names (cust_id -> customer_id)
                using (var cmd = appConfig.Connection!.CreateCommand())
                {
                    cmd.CommandText = $"SELECT customer_id FROM {betaView} ORDER BY customer_id";
                    using var reader = cmd.ExecuteReader();
                    Assert.True(reader.Read());
                    Assert.Equal("101", reader.GetString(0));
                    Assert.True(reader.Read());
                    Assert.Equal("102", reader.GetString(0));
                }

                // Verify bronze views are actually VIEWs
                foreach (var viewName in bronzeViewNames)
                {
                    using var cmd = appConfig.Connection!.CreateCommand();
                    cmd.CommandText = $"SELECT table_type FROM information_schema.tables WHERE table_name = '{viewName}' AND table_schema = 'bronze'";
                    using var reader = cmd.ExecuteReader();
                    Assert.True(reader.Read());
                    Assert.Equal("VIEW", reader.GetString(0));
                }
            }
        }
        finally
        {
            if (Directory.Exists(testDir))
                Directory.Delete(testDir, recursive: true);
        }
    }

    [Fact]
    public void FullPipeline_OptionalMissingColumn_ProducesNull()
    {
        var testDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        try
        {
            Directory.CreateDirectory(testDir);

            var contractPath = Path.Combine(testDir, "c.yaml");
            File.WriteAllText(contractPath, """
table: t
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
""");

            var dataDir = Path.Combine(testDir, "data");
            Directory.CreateDirectory(dataDir);
            File.WriteAllText(Path.Combine(dataDir, "test.csv"), "id\n1\n2\n");

            var manifestPath = Path.Combine(testDir, "manifest.csv");
            File.WriteAllText(manifestPath, "submitter,source_folder,file_pattern,contract\nTest,data,test.csv,c.yaml\n");

            var configPath = Path.Combine(testDir, "config.yaml");
            File.WriteAllText(configPath, $"manifest_path: \"{manifestPath}\"\ndata_folder: \"{testDir}\"\ncontracts_folder: \"{testDir}\"\noutput_folder: \"{testDir}\"\ndatabase_path: \":memory:\"\n");

            var appConfig = AppConfig.FromYaml(configPath);
            using (appConfig.Connection!)
            {
                var manifest = Manifest.FromCsv(manifestPath);
                var sourceFiles = manifest.ToSourceFileList(appConfig);

                foreach (var sf in sourceFiles)
                    sf.GenerateAndExecuteSql(appConfig);

                // Verify the bronze view has NULL for the optional missing column
                var viewName = GetTableNames(appConfig.Connection!, "bronze")[0];
                using var cmd = appConfig.Connection!.CreateCommand();
                cmd.CommandText = $"SELECT id, name FROM \"bronze\".\"{viewName}\" ORDER BY id";
                using var reader = cmd.ExecuteReader();
                Assert.True(reader.Read());
                Assert.Equal("1", reader.GetString(0));
                Assert.True(reader.IsDBNull(1));
                Assert.True(reader.Read());
                Assert.Equal("2", reader.GetString(0));
                Assert.True(reader.IsDBNull(1));
            }
        }
        finally
        {
            if (Directory.Exists(testDir))
                Directory.Delete(testDir, recursive: true);
        }
    }

    [Fact]
    public void FullPipeline_AlternativeAcceptName_Resolves()
    {
        var testDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        try
        {
            Directory.CreateDirectory(testDir);

            var contractPath = Path.Combine(testDir, "c.yaml");
            File.WriteAllText(contractPath, """
table: t
schema:
  staging: bronze_raw
  valid: bronze
  invalid: bronze_quarantine
columns:
  - canonical: customer_id
    accepts: [customer_id, cust_id, "Customer ID"]
    type: VARCHAR
    required: true
  - canonical: name
    accepts: [name, "Full Name"]
    type: VARCHAR
    required: false
""");

            var dataDir = Path.Combine(testDir, "data");
            Directory.CreateDirectory(dataDir);
            File.WriteAllText(Path.Combine(dataDir, "test.csv"), "\"Customer ID\",\"Full Name\"\n101,\"Alice Smith\"\n");

            var manifestPath = Path.Combine(testDir, "manifest.csv");
            File.WriteAllText(manifestPath, "submitter,source_folder,file_pattern,contract\nTest,data,test.csv,c.yaml\n");

            var configPath = Path.Combine(testDir, "config.yaml");
            File.WriteAllText(configPath, $"manifest_path: \"{manifestPath}\"\ndata_folder: \"{testDir}\"\ncontracts_folder: \"{testDir}\"\noutput_folder: \"{testDir}\"\ndatabase_path: \":memory:\"\n");

            var appConfig = AppConfig.FromYaml(configPath);
            using (appConfig.Connection!)
            {
                var manifest = Manifest.FromCsv(manifestPath);
                var sourceFiles = manifest.ToSourceFileList(appConfig);

                foreach (var sf in sourceFiles)
                    sf.GenerateAndExecuteSql(appConfig);

                var viewName = GetTableNames(appConfig.Connection!, "bronze")[0];
                using var cmd = appConfig.Connection!.CreateCommand();
                cmd.CommandText = $"SELECT customer_id, name FROM \"bronze\".\"{viewName}\"";
                using var reader = cmd.ExecuteReader();
                Assert.True(reader.Read());
                Assert.Equal("101", reader.GetString(0));
                Assert.Equal("Alice Smith", reader.GetString(1));
            }
        }
        finally
        {
            if (Directory.Exists(testDir))
                Directory.Delete(testDir, recursive: true);
        }
    }

    [Fact]
    public void FullPipeline_QuarantineOnMissingRequired_NoBronzeView()
    {
        var testDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        try
        {
            Directory.CreateDirectory(testDir);

            var contractPath = Path.Combine(testDir, "c.yaml");
            File.WriteAllText(contractPath, """
table: t
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
""");

            var dataDir = Path.Combine(testDir, "data");
            Directory.CreateDirectory(dataDir);
            File.WriteAllText(Path.Combine(dataDir, "bad.csv"), "id\n1\n"); // missing required_col

            var manifestPath = Path.Combine(testDir, "manifest.csv");
            File.WriteAllText(manifestPath, "submitter,source_folder,file_pattern,contract\nTest,data,bad.csv,c.yaml\n");

            var configPath = Path.Combine(testDir, "config.yaml");
            File.WriteAllText(configPath, $"manifest_path: \"{manifestPath}\"\ndata_folder: \"{testDir}\"\ncontracts_folder: \"{testDir}\"\noutput_folder: \"{testDir}\"\ndatabase_path: \":memory:\"\n");

            var appConfig = AppConfig.FromYaml(configPath);
            using (appConfig.Connection!)
            {
                var manifest = Manifest.FromCsv(manifestPath);
                var sourceFiles = manifest.ToSourceFileList(appConfig);

                foreach (var sf in sourceFiles)
                    sf.GenerateAndExecuteSql(appConfig);

                // Raw table should exist
                var rawTables = GetTableNames(appConfig.Connection!, "bronze_raw");
                Assert.NotEmpty(rawTables);

                // Bronze view should NOT exist
                var bronzeViews = GetTableNames(appConfig.Connection!, "bronze");
                Assert.Empty(bronzeViews);

                // Quarantine view should exist
                var quarantineViews = GetTableNames(appConfig.Connection!, "bronze_quarantine");
                Assert.Equal(2, quarantineViews.Length); // _quarantine_log table + the quarantined view
                var quarantineView = quarantineViews.First(n => !n.StartsWith("_"));
                Assert.NotNull(quarantineView);

                // Quarantine log should have entry
                using var cmd = appConfig.Connection!.CreateCommand();
                cmd.CommandText = "SELECT error_message FROM bronze_quarantine._quarantine_log";
                using var reader = cmd.ExecuteReader();
                Assert.True(reader.Read());
                Assert.Contains("required_col", reader.GetString(0), StringComparison.OrdinalIgnoreCase);
            }
        }
        finally
        {
            if (Directory.Exists(testDir))
                Directory.Delete(testDir, recursive: true);
        }
    }

    [Fact]
    public void FullPipeline_SpecialCharactersInSubmitter_SanitizesCorrectly()
    {
        var testDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        try
        {
            Directory.CreateDirectory(testDir);

            var contractPath = Path.Combine(testDir, "c.yaml");
            File.WriteAllText(contractPath, """
table: my-table_v2
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

            var dataDir = Path.Combine(testDir, "data");
            Directory.CreateDirectory(dataDir);
            // Submitter with special characters: comma, space, period, hyphen
            File.WriteAllText(Path.Combine(dataDir, "data.csv"), "id\n42\n");

            var manifestPath = Path.Combine(testDir, "manifest.csv");
            File.WriteAllText(manifestPath, "submitter,source_folder,file_pattern,contract\n\"Capitalism, Inc.\",data,data.csv,c.yaml\n");

            var configPath = Path.Combine(testDir, "config.yaml");
            File.WriteAllText(configPath, $"manifest_path: \"{manifestPath}\"\ndata_folder: \"{testDir}\"\ncontracts_folder: \"{testDir}\"\noutput_folder: \"{testDir}\"\ndatabase_path: \":memory:\"\n");

            var appConfig = AppConfig.FromYaml(configPath);
            using (appConfig.Connection!)
            {
                var manifest = Manifest.FromCsv(manifestPath);
                var sourceFiles = manifest.ToSourceFileList(appConfig);

                foreach (var sf in sourceFiles)
                    sf.GenerateAndExecuteSql(appConfig);

                // Verify raw table exists with sanitized name (special chars → underscores)
                var rawTables = GetTableNames(appConfig.Connection!, "bronze_raw");
                Assert.Single(rawTables);

                // The submitter "Capitalism, Inc." should be sanitized to "Capitalism__Inc_"
                // The table "my-table_v2" should be sanitized to "my_table_v2"
                Assert.Contains("my_table_v2", rawTables[0]);
                Assert.Contains("Capitalism__Inc_", rawTables[0]);

                // Verify bronze view exists
                var bronzeViews = GetTableNames(appConfig.Connection!, "bronze");
                Assert.Single(bronzeViews);

                // Verify data is accessible
                using var cmd = appConfig.Connection!.CreateCommand();
                cmd.CommandText = $"SELECT id FROM \"bronze\".\"{bronzeViews[0]}\"";
                using var reader = cmd.ExecuteReader();
                Assert.True(reader.Read());
                Assert.Equal("42", reader.GetString(0));
            }
        }
        finally
        {
            if (Directory.Exists(testDir))
                Directory.Delete(testDir, recursive: true);
        }
    }

    [Fact]
    public void GenerateSqlMode_ProducesFilesInOutputFolder()
    {
        var testDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        try
        {
            Directory.CreateDirectory(testDir);

            // Contract
            var contractPath = Path.Combine(testDir, "c.yaml");
            File.WriteAllText(contractPath, """
table: t
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
""");

            // Source data
            var dataDir = Path.Combine(testDir, "data");
            Directory.CreateDirectory(dataDir);
            File.WriteAllText(Path.Combine(dataDir, "d.csv"), "id,name\n1,alice\n2,bob\n");

            // Manifest
            var manifestPath = Path.Combine(testDir, "manifest.csv");
            File.WriteAllText(manifestPath, "submitter,source_folder,file_pattern,contract\nTest,data,d.csv,c.yaml\n");

            // Output folder
            var outputDir = Path.Combine(testDir, "output");
            Directory.CreateDirectory(outputDir);

            // Config
            var configPath = Path.Combine(testDir, "config.yaml");
            File.WriteAllText(configPath, $"manifest_path: \"{manifestPath}\"\ndata_folder: \"{testDir}\"\ncontracts_folder: \"{testDir}\"\noutput_folder: \"{outputDir}\"\ndatabase_path: \":memory:\"\n");

            // Execute pipeline in generate-sql mode (simulate what CLI does)
            var appConfig = AppConfig.FromYaml(configPath);

            using (appConfig.Connection!)
            {
                var manifest = Manifest.FromCsv(manifestPath);
                var sourceFiles = manifest.ToSourceFileList(appConfig);

                var allStatements = new List<SqlStatement>();
                var seenSchemas = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                foreach (var sourceFile in sourceFiles)
                {
                    // Load raw data so DESCRIBE works
                    sourceFile.GenerateRawLoad(appConfig);

                    // Collect statements
                    var statements = sourceFile.CollectSqlStatements(appConfig);

                    // Deduplicate schemas
                    foreach (var stmt in statements)
                    {
                        if (stmt.Operation == "create_schema")
                        {
                            if (!seenSchemas.Add(stmt.ObjectName))
                                continue;
                        }
                        allStatements.Add(stmt);
                    }
                }

                // Write SQL files
                Assert.NotEmpty(allStatements);

                var ordinal = 1;
                foreach (var stmt in allStatements)
                {
                    var fileName = $"{ordinal:D3}_{stmt.Operation}_{stmt.ObjectName}.sql";
                    var filePath = Path.Combine(outputDir, fileName);
                    File.WriteAllText(filePath, stmt.Sql + "\n");
                    ordinal++;
                }

                // Verify files
                var sqlFiles = Directory.GetFiles(outputDir, "*.sql").OrderBy(f => f).ToArray();

                // Should have: 3 schema files + 1 table file + 1 view file = 5 files
                Assert.Equal(5, sqlFiles.Length);

                // Verify ordering and naming
                Assert.StartsWith("001_create_schema_bronze_raw", Path.GetFileName(sqlFiles[0]));
                Assert.StartsWith("002_create_schema_bronze", Path.GetFileName(sqlFiles[1]));
                Assert.StartsWith("003_create_schema_bronze_quarantine", Path.GetFileName(sqlFiles[2]));
                Assert.StartsWith("004_create_table_t_Test_", Path.GetFileName(sqlFiles[3]));
                Assert.StartsWith("005_create_view_t_Test_", Path.GetFileName(sqlFiles[4]));

                // Verify SQL content
                var tableSql = File.ReadAllText(sqlFiles[3]);
                Assert.Contains("CREATE OR REPLACE TABLE", tableSql);
                Assert.Contains("read_csv(", tableSql);

                var viewSql = File.ReadAllText(sqlFiles[4]);
                Assert.Contains("CREATE OR REPLACE VIEW", viewSql);
                Assert.Contains("SELECT", viewSql);
                Assert.Contains("FROM", viewSql);
            }
        }
        finally
        {
            if (Directory.Exists(testDir))
                Directory.Delete(testDir, recursive: true);
        }
    }

    [Fact]
    public void GenerateSqlMode_QuarantineFile_ProducesQuarantineFiles()
    {
        var testDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        try
        {
            Directory.CreateDirectory(testDir);

            // Contract with required column not in source
            var contractPath = Path.Combine(testDir, "c.yaml");
            File.WriteAllText(contractPath, """
table: t
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
""");

            // Source missing required_col
            var dataDir = Path.Combine(testDir, "data");
            Directory.CreateDirectory(dataDir);
            File.WriteAllText(Path.Combine(dataDir, "bad.csv"), "id\n1\n");

            var manifestPath = Path.Combine(testDir, "manifest.csv");
            File.WriteAllText(manifestPath, "submitter,source_folder,file_pattern,contract\nTest,data,bad.csv,c.yaml\n");

            var outputDir = Path.Combine(testDir, "output");
            Directory.CreateDirectory(outputDir);

            var configPath = Path.Combine(testDir, "config.yaml");
            File.WriteAllText(configPath, $"manifest_path: \"{manifestPath}\"\ndata_folder: \"{testDir}\"\ncontracts_folder: \"{testDir}\"\noutput_folder: \"{outputDir}\"\ndatabase_path: \":memory:\"\n");

            var appConfig = AppConfig.FromYaml(configPath);

            using (appConfig.Connection!)
            {
                var manifest = Manifest.FromCsv(manifestPath);
                var sourceFiles = manifest.ToSourceFileList(appConfig);

                var allStatements = new List<SqlStatement>();
                var seenSchemas = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                foreach (var sourceFile in sourceFiles)
                {
                    sourceFile.GenerateRawLoad(appConfig);
                    var statements = sourceFile.CollectSqlStatements(appConfig);

                    foreach (var stmt in statements)
                    {
                        if (stmt.Operation == "create_schema")
                        {
                            if (!seenSchemas.Add(stmt.ObjectName))
                                continue;
                        }
                        allStatements.Add(stmt);
                    }
                }

                var ordinal = 1;
                foreach (var stmt in allStatements)
                {
                    var fileName = $"{ordinal:D3}_{stmt.Operation}_{stmt.ObjectName}.sql";
                    var filePath = Path.Combine(outputDir, fileName);
                    File.WriteAllText(filePath, stmt.Sql + "\n");
                    ordinal++;
                }

                var sqlFiles = Directory.GetFiles(outputDir, "*.sql").OrderBy(f => f).ToArray();

                // Should have: 3 schema + 1 table + 1 quarantine_view + 1 quarantine_log = 6
                Assert.Equal(6, sqlFiles.Length);

                // Verify quarantine view file exists
                Assert.Contains("create_quarantine_view", sqlFiles[4]);
                Assert.Contains("insert_quarantine_log", sqlFiles[5]);

                var quarantineViewSql = File.ReadAllText(sqlFiles[4]);
                Assert.Contains("CREATE OR REPLACE VIEW", quarantineViewSql);
                Assert.Contains("bronze_quarantine", quarantineViewSql);

                var logSql = File.ReadAllText(sqlFiles[5]);
                Assert.Contains("INSERT INTO", logSql);
                Assert.Contains("_quarantine_log", logSql);
            }
        }
        finally
        {
            if (Directory.Exists(testDir))
                Directory.Delete(testDir, recursive: true);
        }
    }

    // ===== Helpers =====

    private static string[] GetTableNames(DuckDBConnection conn, string schema)
    {
        var names = new List<string>();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT table_name FROM information_schema.tables WHERE table_schema = '{schema}' ORDER BY table_name";
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            names.Add(reader.GetString(0));
        return [.. names];
    }
}
