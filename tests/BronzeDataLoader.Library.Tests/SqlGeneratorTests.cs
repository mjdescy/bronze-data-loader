using DuckDB.NET.Data;
using BronzeDataLoader.Library.Contract;
using BronzeDataLoader.Library.Sql;

namespace BronzeDataLoader.Library.Tests;

public class SqlGeneratorTests
{
    private static Contract.Contract MakeContract(
        string table = "customer",
        string staging = "bronze_raw",
        string valid = "bronze",
        string invalid = "bronze_quarantine")
    {
        return new Contract.Contract
        {
            Table = table,
            Schema = new ContractSchema
            {
                Staging = staging,
                Valid = valid,
                Invalid = invalid,
            },
            Columns =
            [
                new ContractColumn { Canonical = "customer_id", Accepts = ["customer_id", "cust_id"], Type = "VARCHAR", Required = true },
                new ContractColumn { Canonical = "signup_date", Accepts = ["signup_date"], Type = "DATE", Required = true },
                new ContractColumn { Canonical = "email", Accepts = ["email"], Type = "VARCHAR", Required = false },
            ],
        };
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

    private static string[] GetColumnNames(DuckDBConnection conn, string tableName)
    {
        var cols = new List<string>();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT column_name FROM (DESCRIBE SELECT * FROM {tableName})";
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            cols.Add(reader.GetString(0));
        return [.. cols];
    }

    // ===== Pure Logic Tests =====

    public class PureLogicTests
    {
        [Fact]
        public void BuildCreateSchemaIfNotExists_ReturnsCorrectSql()
        {
            using var conn = CreateInMemoryConnection();
            var csvPath = CreateTempCsv("x\ny\n");
            try
            {
                var gen = new SqlGenerator(csvPath, "Acme", MakeContract(), conn);
                var sql = gen.BuildCreateSchemaIfNotExists();
                Assert.Contains("CREATE SCHEMA IF NOT EXISTS \"bronze_raw\"", sql);
                Assert.Contains("CREATE SCHEMA IF NOT EXISTS \"bronze\"", sql);
                Assert.Contains("CREATE SCHEMA IF NOT EXISTS \"bronze_quarantine\"", sql);
            }
            finally
            {
                if (File.Exists(csvPath)) File.Delete(csvPath);
            }
        }

        [Fact]
        public void FullTableName_ReturnsCorrectFormat()
        {
            using var conn = CreateInMemoryConnection();
            var csvPath = CreateTempCsv("x\ny\n");
            try
            {
                var gen = new SqlGenerator(csvPath, "Acme", MakeContract(), conn);
                var name = gen.FullTableName;
                Assert.StartsWith("bronze.customer_Acme_", name);
                Assert.Matches(@"^bronze\.customer_Acme_[a-f0-9]{8}$", name);
            }
            finally
            {
                if (File.Exists(csvPath)) File.Delete(csvPath);
            }
        }

        [Fact]
        public void RawTableName_ReturnsCorrectFormat()
        {
            using var conn = CreateInMemoryConnection();
            var csvPath = CreateTempCsv("x\ny\n");
            try
            {
                var gen = new SqlGenerator(csvPath, "Acme", MakeContract(), conn);
                var name = gen.RawTableName;
                Assert.Matches(@"^bronze_raw\.customer_Acme_[a-f0-9]{8}$", name);
            }
            finally
            {
                if (File.Exists(csvPath)) File.Delete(csvPath);
            }
        }

        [Fact]
        public void QuarantineTableName_ReturnsCorrectFormat()
        {
            using var conn = CreateInMemoryConnection();
            var csvPath = CreateTempCsv("x\ny\n");
            try
            {
                var gen = new SqlGenerator(csvPath, "Acme", MakeContract(), conn);
                var name = gen.QuarantineTableName;
                Assert.Matches(@"^bronze_quarantine\.customer_Acme_[a-f0-9]{8}$", name);
            }
            finally
            {
                if (File.Exists(csvPath)) File.Delete(csvPath);
            }
        }

        [Fact]
        public void Hash_IsConsistentForSameStem()
        {
            using var conn = CreateInMemoryConnection();
            var csvPath = CreateTempCsv("x\ny\n");
            try
            {
                var gen1 = new SqlGenerator(csvPath, "Acme", MakeContract(), conn);
                var hash1 = gen1.StemHash;

                var gen2 = new SqlGenerator(csvPath, "Beta", MakeContract(), conn);
                var hash2 = gen2.StemHash;

                Assert.Equal(hash1, hash2);
            }
            finally
            {
                if (File.Exists(csvPath)) File.Delete(csvPath);
            }
        }

        [Fact]
        public void BuildCreateSchemaIfNotExists_CreatesSchema()
        {
            using var conn = CreateInMemoryConnection();
            var csvPath = CreateTempCsv("x\ny\n");
            try
            {
                var gen = new SqlGenerator(csvPath, "Acme", MakeContract(), conn);
                var sql = gen.BuildCreateSchemaIfNotExists();
                ExecuteSql(conn, sql);

                // Verify all three schemas exist
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "SELECT schema_name FROM information_schema.schemata WHERE schema_name IN ('bronze_raw', 'bronze', 'bronze_quarantine') ORDER BY schema_name";
                using var reader = cmd.ExecuteReader();
                Assert.True(reader.Read()); // bronze
                Assert.True(reader.Read()); // bronze_quarantine
                Assert.True(reader.Read()); // bronze_raw
            }
            finally
            {
                if (File.Exists(csvPath)) File.Delete(csvPath);
            }
        }
    }

    // ===== Read Function Tests =====

    public class BuildReadFunctionCallTests
    {
        [Fact]
        public void CsvFile_ReturnsReadCsvWithAllVarchar()
        {
            using var conn = CreateInMemoryConnection();
            var csvPath = CreateTempCsv("a,b\n1,2\n");
            try
            {
                var gen = new SqlGenerator(csvPath, "Acme", MakeContract(), conn);
                var sql = gen.BuildReadFunctionCall();
                Assert.Contains("read_csv(", sql);
                Assert.Contains("all_varchar=true", sql);
                Assert.Contains("header=true", sql);
            }
            finally
            {
                if (File.Exists(csvPath)) File.Delete(csvPath);
            }
        }

        [Fact]
        public void TsvFile_ReturnsReadCsvWithTabDelimiter()
        {
            using var conn = CreateInMemoryConnection();
            var tsvPath = Path.GetTempFileName() + ".tsv";
            File.WriteAllText(tsvPath, "a\tb\n1\t2\n");
            try
            {
                var gen = new SqlGenerator(tsvPath, "Acme", MakeContract(), conn);
                var sql = gen.BuildReadFunctionCall();
                Assert.Contains("read_csv(", sql);
                Assert.Contains("delim='\\t'", sql);
                Assert.Contains("all_varchar=true", sql);
            }
            finally
            {
                if (File.Exists(tsvPath)) File.Delete(tsvPath);
            }
        }

        [Fact]
        public void XlsxFile_ReturnsReadXlsx()
        {
            using var conn = CreateInMemoryConnection();
            var xlsxPath = Path.GetTempFileName() + ".xlsx";
            File.WriteAllText(xlsxPath, "dummy");
            try
            {
                var gen = new SqlGenerator(xlsxPath, "Acme", MakeContract(), conn);
                var sql = gen.BuildReadFunctionCall();
                Assert.Contains("read_xlsx(", sql);
            }
            finally
            {
                if (File.Exists(xlsxPath)) File.Delete(xlsxPath);
            }
        }

        [Fact]
        public void UnsupportedExtension_ThrowsInvalidOperationException()
        {
            using var conn = CreateInMemoryConnection();
            var badPath = Path.GetTempFileName() + ".xyz";
            File.WriteAllText(badPath, "dummy");
            try
            {
                var gen = new SqlGenerator(badPath, "Acme", MakeContract(), conn);
                Assert.Throws<InvalidOperationException>(() => gen.BuildReadFunctionCall());
            }
            finally
            {
                if (File.Exists(badPath)) File.Delete(badPath);
            }
        }

        [Fact]
        public void FileWithWhitespaceInPath_HandlesCorrectly()
        {
            using var conn = CreateInMemoryConnection();
            var dir = Path.Combine(Path.GetTempPath(), "test dir");
            Directory.CreateDirectory(dir);
            var csvPath = Path.Combine(dir, "data file.csv");
            File.WriteAllText(csvPath, "a,b\n1,2\n");
            try
            {
                var gen = new SqlGenerator(csvPath, "Acme", MakeContract(), conn);
                var sql = gen.BuildReadFunctionCall();
                Assert.Contains(csvPath, sql);
            }
            finally
            {
                if (File.Exists(csvPath)) File.Delete(csvPath);
                if (Directory.Exists(dir)) Directory.Delete(dir);
            }
        }
    }

    // ===== Raw Load SQL Tests =====

    public class BuildRawLoadSqlTests
    {
        [Fact]
        public void BuildRawLoadSql_ReturnsCreateOrReplaceTable()
        {
            using var conn = CreateInMemoryConnection();
            var csvPath = CreateTempCsv("customer_id,signup_date,email\n1,2024-01-01,a@b.com\n");
            try
            {
                var gen = new SqlGenerator(csvPath, "Acme", MakeContract(), conn);
                var sql = gen.BuildRawLoadSql();

                Assert.Contains("CREATE OR REPLACE TABLE", sql);
                Assert.Contains("\"bronze_raw\".\"customer_Acme_", sql);
                Assert.Contains("read_csv(", sql);
                Assert.EndsWith(";", sql.Trim());
            }
            finally
            {
                if (File.Exists(csvPath)) File.Delete(csvPath);
            }
        }

        [Fact]
        public void BuildRawLoadSql_ExecutesSuccessfully()
        {
            using var conn = CreateInMemoryConnection();
            var csvPath = CreateTempCsv("customer_id,signup_date,email\n1,2024-01-01,a@b.com\n");
            try
            {
                var gen = new SqlGenerator(csvPath, "Acme", MakeContract(), conn);

                ExecuteSql(conn, gen.BuildCreateSchemaIfNotExists());
                ExecuteSql(conn, gen.BuildRawLoadSql());

                var cols = GetColumnNames(conn, SqlGenerator.QuoteQualified(gen.RawTableName));
                Assert.Contains("customer_id", cols);
                Assert.Contains("signup_date", cols);
                Assert.Contains("email", cols);
            }
            finally
            {
                if (File.Exists(csvPath)) File.Delete(csvPath);
            }
        }
    }

    // ===== BuildSelect Tests =====

    public class BuildSelectTests
    {
        [Fact]
        public void AllColumnsPresent_ReturnsCorrectSelect()
        {
            using var conn = CreateInMemoryConnection();
            var csvPath = CreateTempCsv("customer_id,signup_date,email\n1,2024-01-01,a@b.com\n");
            try
            {
                var gen = new SqlGenerator(csvPath, "Acme", MakeContract(), conn);
                ExecuteSql(conn, gen.BuildCreateSchemaIfNotExists());
                ExecuteSql(conn, gen.BuildRawLoadSql());

                var (sql, warnings) = gen.BuildSelect();

                Assert.Contains("customer_id", sql);
                Assert.Contains("signup_date", sql);
                Assert.Contains("email", sql);
                Assert.Empty(warnings);
            }
            finally
            {
                if (File.Exists(csvPath)) File.Delete(csvPath);
            }
        }

        [Fact]
        public void MissingRequiredColumn_ThrowsInvalidOperationException()
        {
            using var conn = CreateInMemoryConnection();
            var csvPath = CreateTempCsv("customer_id,signup_date\n1,2024-01-01\n"); // missing email is OK (optional)
            try
            {
                var gen = new SqlGenerator(csvPath, "Acme", MakeContract(), conn);
                ExecuteSql(conn, gen.BuildCreateSchemaIfNotExists());
                ExecuteSql(conn, gen.BuildRawLoadSql());

                // Creates a contract where signup_date is also missing but required
                var contractWithMoreRequired = new Contract.Contract
                {
                    Table = "customer",
                    Schema = new ContractSchema(),
                    Columns =
                    [
                        new ContractColumn { Canonical = "customer_id", Accepts = ["customer_id"], Type = "VARCHAR", Required = true },
                        new ContractColumn { Canonical = "signup_date", Accepts = ["signup_date"], Type = "DATE", Required = true },
                        new ContractColumn { Canonical = "email", Accepts = ["email"], Type = "VARCHAR", Required = true },
                        new ContractColumn { Canonical = "missing_col", Accepts = ["missing_col"], Type = "VARCHAR", Required = true },
                    ],
                };
                var gen2 = new SqlGenerator(csvPath, "Acme", contractWithMoreRequired, conn);
                Assert.Throws<InvalidOperationException>(() => gen2.BuildSelect());
            }
            finally
            {
                if (File.Exists(csvPath)) File.Delete(csvPath);
            }
        }

        [Fact]
        public void MissingOptionalColumn_ProducesNull()
        {
            using var conn = CreateInMemoryConnection();
            var csvPath = CreateTempCsv("customer_id,signup_date\n1,2024-01-01\n"); // email missing (optional)
            try
            {
                var gen = new SqlGenerator(csvPath, "Acme", MakeContract(), conn);
                ExecuteSql(conn, gen.BuildCreateSchemaIfNotExists());
                ExecuteSql(conn, gen.BuildRawLoadSql());

                var (sql, warnings) = gen.BuildSelect();

                Assert.Contains("NULL::VARCHAR AS email", sql);
                Assert.Empty(warnings);
            }
            finally
            {
                if (File.Exists(csvPath)) File.Delete(csvPath);
            }
        }

        [Fact]
        public void ExtraColumns_ProducesWarning()
        {
            using var conn = CreateInMemoryConnection();
            var csvPath = CreateTempCsv("customer_id,signup_date,email,extra_col\n1,2024-01-01,a@b.com,extra\n");
            try
            {
                var gen = new SqlGenerator(csvPath, "Acme", MakeContract(), conn);
                ExecuteSql(conn, gen.BuildCreateSchemaIfNotExists());
                ExecuteSql(conn, gen.BuildRawLoadSql());

                var (sql, warnings) = gen.BuildSelect();

                Assert.Contains("customer_id", sql);
                Assert.NotEmpty(warnings);
                Assert.Contains("extra_col", warnings[0], StringComparison.OrdinalIgnoreCase);
            }
            finally
            {
                if (File.Exists(csvPath)) File.Delete(csvPath);
            }
        }

        [Fact]
        public void CaseInsensitiveColumnMatching_Works()
        {
            using var conn = CreateInMemoryConnection();
            var csvPath = CreateTempCsv("CUSTOMER_ID,SIGNUP_DATE,EMAIL\n1,2024-01-01,a@b.com\n");
            try
            {
                var gen = new SqlGenerator(csvPath, "Acme", MakeContract(), conn);
                ExecuteSql(conn, gen.BuildCreateSchemaIfNotExists());
                ExecuteSql(conn, gen.BuildRawLoadSql());

                var (sql, warnings) = gen.BuildSelect();

                Assert.Contains("customer_id", sql);
                Assert.Contains("signup_date", sql);
                Assert.Contains("email", sql);
                Assert.Empty(warnings);
            }
            finally
            {
                if (File.Exists(csvPath)) File.Delete(csvPath);
            }
        }

        [Fact]
        public void AlternativeAcceptName_ResolvesCorrectly()
        {
            using var conn = CreateInMemoryConnection();
            var csvPath = CreateTempCsv("cust_id,signup_date,email\n1,2024-01-01,a@b.com\n");
            try
            {
                var contractWithAlias = new Contract.Contract
                {
                    Table = "customer",
                    Schema = new ContractSchema(),
                    Columns =
                    [
                        new ContractColumn { Canonical = "customer_id", Accepts = ["customer_id", "cust_id"], Type = "VARCHAR", Required = true },
                        new ContractColumn { Canonical = "signup_date", Accepts = ["signup_date"], Type = "DATE", Required = true },
                        new ContractColumn { Canonical = "email", Accepts = ["email"], Type = "VARCHAR", Required = false },
                    ],
                };
                var gen = new SqlGenerator(csvPath, "Acme", contractWithAlias, conn);
                ExecuteSql(conn, gen.BuildCreateSchemaIfNotExists());
                ExecuteSql(conn, gen.BuildRawLoadSql());

                var (sql, warnings) = gen.BuildSelect();

                Assert.Contains("customer_id", sql);
                Assert.Empty(warnings);
            }
            finally
            {
                if (File.Exists(csvPath)) File.Delete(csvPath);
            }
        }

        [Fact]
        public void ColumnsWithWhitespace_AreStripped()
        {
            using var conn = CreateInMemoryConnection();
            var csvPath = CreateTempCsv(" customer_id , signup_date , email \n1,2024-01-01,a@b.com\n");
            try
            {
                var gen = new SqlGenerator(csvPath, "Acme", MakeContract(), conn);
                ExecuteSql(conn, gen.BuildCreateSchemaIfNotExists());
                ExecuteSql(conn, gen.BuildRawLoadSql());

                var (sql, warnings) = gen.BuildSelect();

                Assert.Contains("customer_id", sql);
                Assert.Contains("signup_date", sql);
                Assert.Contains("email", sql);
            }
            finally
            {
                if (File.Exists(csvPath)) File.Delete(csvPath);
            }
        }
    }

    // ===== Bronze View SQL Tests =====

    public class BuildBronzeViewSqlTests
    {
        [Fact]
        public void BuildBronzeViewSql_ReturnsCreateOrReplaceView()
        {
            using var conn = CreateInMemoryConnection();
            var csvPath = CreateTempCsv("customer_id,signup_date,email\n1,2024-01-01,a@b.com\n");
            try
            {
                var gen = new SqlGenerator(csvPath, "Acme", MakeContract(), conn);
                ExecuteSql(conn, gen.BuildCreateSchemaIfNotExists());
                ExecuteSql(conn, gen.BuildRawLoadSql());

                var (sql, warnings) = gen.BuildBronzeViewSql();

                Assert.Contains("CREATE OR REPLACE VIEW", sql);
                Assert.Contains("\"bronze\".\"customer_Acme_", sql);
                Assert.Contains("FROM", sql);
                Assert.Empty(warnings);
            }
            finally
            {
                if (File.Exists(csvPath)) File.Delete(csvPath);
            }
        }

        [Fact]
        public void BronzeViewSql_ExecutesAndReturnsData()
        {
            using var conn = CreateInMemoryConnection();
            var csvPath = CreateTempCsv("customer_id,signup_date,email\n42,2024-01-15,a@b.com\n");
            try
            {
                var gen = new SqlGenerator(csvPath, "Acme", MakeContract(), conn);
                ExecuteSql(conn, gen.BuildCreateSchemaIfNotExists());
                ExecuteSql(conn, gen.BuildRawLoadSql());

                var (sql, warnings) = gen.BuildBronzeViewSql();
                ExecuteSql(conn, sql);

                // Query the view (cast DATE to VARCHAR for locale-independent comparison)
                using var cmd = conn.CreateCommand();
                cmd.CommandText = $"SELECT customer_id, signup_date::VARCHAR, email FROM {SqlGenerator.QuoteQualified(gen.FullTableName)}";
                using var reader = cmd.ExecuteReader();
                Assert.True(reader.Read());
                Assert.Equal("42", reader.GetString(0));
                Assert.Equal("2024-01-15", reader.GetString(1));
                Assert.Equal("a@b.com", reader.GetString(2));
            }
            finally
            {
                if (File.Exists(csvPath)) File.Delete(csvPath);
            }
        }

        [Fact]
        public void BronzeView_IsViewNotTable()
        {
            using var conn = CreateInMemoryConnection();
            var csvPath = CreateTempCsv("customer_id,signup_date,email\n1,2024-01-01,a@b.com\n");
            try
            {
                var gen = new SqlGenerator(csvPath, "Acme", MakeContract(), conn);
                ExecuteSql(conn, gen.BuildCreateSchemaIfNotExists());
                ExecuteSql(conn, gen.BuildRawLoadSql());

                var (sql, _) = gen.BuildBronzeViewSql();
                ExecuteSql(conn, sql);

                // Verify it's a view
                using var cmd = conn.CreateCommand();
                cmd.CommandText = $"SELECT table_type FROM information_schema.tables WHERE table_name = '{gen.FullTableName.Split('.')[1]}' AND table_schema = 'bronze'";
                using var reader = cmd.ExecuteReader();
                Assert.True(reader.Read());
                Assert.Equal("VIEW", reader.GetString(0));
            }
            finally
            {
                if (File.Exists(csvPath)) File.Delete(csvPath);
            }
        }
    }

    // ===== Quarantine SQL Tests =====

    public class BuildQuarantineSqlTests
    {
        [Fact]
        public void BuildQuarantineSql_ReturnsDdlAndMetadata()
        {
            using var conn = CreateInMemoryConnection();
            var csvPath = CreateTempCsv("customer_id,signup_date,email\n1,2024-01-01,a@b.com\n");
            try
            {
                var gen = new SqlGenerator(csvPath, "Acme", MakeContract(), conn);
                var error = "Missing required column test";
                var (quarantineSql, metadataSql) = gen.BuildQuarantineSql(error);

                Assert.Contains("CREATE OR REPLACE VIEW", quarantineSql);
                Assert.Contains("\"bronze_quarantine\".\"customer_Acme_", quarantineSql);

                Assert.Contains("INSERT INTO", metadataSql);
                Assert.Contains("\"metadata\".\"quarantine\"", metadataSql);
                Assert.Contains(error, metadataSql);
            }
            finally
            {
                if (File.Exists(csvPath)) File.Delete(csvPath);
            }
        }

        [Fact]
        public void QuarantineSql_ExecutesAndCreatesView()
        {
            using var conn = CreateInMemoryConnection();
            var csvPath = CreateTempCsv("customer_id,signup_date,email\n1,2024-01-01,a@b.com\n");
            try
            {
                var gen = new SqlGenerator(csvPath, "Acme", MakeContract(), conn);
                ExecuteSql(conn, gen.BuildCreateSchemaIfNotExists());
                ExecuteSql(conn, gen.BuildRawLoadSql());

                // Ensure metadata schema and quarantine table exist
                ExecuteSql(conn, "CREATE SCHEMA IF NOT EXISTS \"metadata\";");
                ExecuteSql(conn, "CREATE TABLE IF NOT EXISTS \"metadata\".\"quarantine\" (table_name VARCHAR, error_message VARCHAR, quarantined_at TIMESTAMP DEFAULT CURRENT_TIMESTAMP);");

                var (quarantineSql, metadataSql) = gen.BuildQuarantineSql("test error msg");
                ExecuteSql(conn, quarantineSql);
                ExecuteSql(conn, metadataSql);

                // Verify quarantine view exists
                using var cmd = conn.CreateCommand();
                var viewName = gen.QuarantineTableName.Split('.')[1];
                cmd.CommandText = $"SELECT table_type FROM information_schema.tables WHERE table_name = '{viewName}' AND table_schema = 'bronze_quarantine'";
                using var reader = cmd.ExecuteReader();
                Assert.True(reader.Read());
                Assert.Equal("VIEW", reader.GetString(0));

                // Verify log entry in metadata.quarantine
                cmd.CommandText = "SELECT error_message FROM metadata.quarantine";
                using var reader2 = cmd.ExecuteReader();
                Assert.True(reader2.Read());
                Assert.Contains("test error msg", reader2.GetString(0));
            }
            finally
            {
                if (File.Exists(csvPath)) File.Delete(csvPath);
            }
        }

        [Fact]
        public void QuarantineSql_ViewContainsAllRawData()
        {
            using var conn = CreateInMemoryConnection();
            var csvPath = CreateTempCsv("customer_id,signup_date,email\n99,2024-06-15,z@test.com\n");
            try
            {
                var gen = new SqlGenerator(csvPath, "Acme", MakeContract(), conn);
                ExecuteSql(conn, gen.BuildCreateSchemaIfNotExists());
                ExecuteSql(conn, gen.BuildRawLoadSql());

                // Ensure metadata schema and quarantine table exist
                ExecuteSql(conn, "CREATE SCHEMA IF NOT EXISTS \"metadata\";");
                ExecuteSql(conn, "CREATE TABLE IF NOT EXISTS \"metadata\".\"quarantine\" (table_name VARCHAR, error_message VARCHAR, quarantined_at TIMESTAMP DEFAULT CURRENT_TIMESTAMP);");

                var (quarantineSql, metadataSql) = gen.BuildQuarantineSql("error");
                ExecuteSql(conn, quarantineSql);
                ExecuteSql(conn, metadataSql);

                // Verify data is preserved in quarantine view
                using var cmd = conn.CreateCommand();
                cmd.CommandText = $"SELECT * FROM {SqlGenerator.QuoteQualified(gen.QuarantineTableName)}";
                using var reader = cmd.ExecuteReader();
                Assert.True(reader.Read());
                Assert.Equal("99", reader.GetString(0));
            }
            finally
            {
                if (File.Exists(csvPath)) File.Delete(csvPath);
            }
        }
    }

    // ===== CollectStatements Tests =====

    public class CollectStatementsTests
    {
        [Fact]
        public void CollectStatements_ValidData_ReturnsSchemaTableAndView()
        {
            using var conn = CreateInMemoryConnection();
            var csvPath = CreateTempCsv("customer_id,signup_date,email\n1,2024-01-01,a@b.com\n");
            try
            {
                var gen = new SqlGenerator(csvPath, "Test", MakeContract("customer"), conn);

                // Load raw data so DESCRIBE works
                ExecuteSql(conn, gen.BuildCreateSchemaIfNotExists());
                ExecuteSql(conn, gen.BuildRawLoadSql());

                var statements = gen.CollectStatements();

                // 3 contract schemas + 1 metadata schema + 1 table + 1 view + 1 failed-loads view + 1 table_load = 8
                Assert.Equal(8, statements.Count);

                Assert.Equal("create_schema", statements[0].Operation);
                Assert.Equal("bronze_raw", statements[0].ObjectName);
                Assert.Contains("CREATE SCHEMA", statements[0].Sql);

                Assert.Equal("create_schema", statements[1].Operation);
                Assert.Equal("bronze", statements[1].ObjectName);

                Assert.Equal("create_schema", statements[2].Operation);
                Assert.Equal("bronze_quarantine", statements[2].ObjectName);

                Assert.Equal("create_schema", statements[3].Operation);
                Assert.Equal("metadata", statements[3].ObjectName);

                Assert.Equal("create_table", statements[4].Operation);
                Assert.Contains("CREATE OR REPLACE TABLE", statements[4].Sql);

                Assert.Equal("create_view", statements[5].Operation);
                Assert.Contains("CREATE OR REPLACE VIEW", statements[5].Sql);

                Assert.Equal("create_view", statements[6].Operation);
                Assert.Equal("v_failed_loads", statements[6].ObjectName);
                Assert.Contains("CREATE OR REPLACE VIEW", statements[6].Sql);
                Assert.Contains("row_count IS NULL", statements[6].Sql);

                Assert.Equal("insert_table_load", statements[7].Operation);
                Assert.Contains("INSERT INTO", statements[7].Sql);
                Assert.Contains("\"metadata\".\"table_load\"", statements[7].Sql);
                Assert.Contains("NULL", statements[7].Sql);
            }
            finally
            {
                if (File.Exists(csvPath)) File.Delete(csvPath);
            }
        }

        [Fact]
        public void CollectStatements_MissingRequired_ReturnsQuarantineStatements()
        {
            using var conn = CreateInMemoryConnection();
            var csvPath = CreateTempCsv("id\n1\n");
            try
            {
                var contract = new Contract.Contract
                {
                    Table = "customer",
                    Schema = new ContractSchema(),
                    Columns =
                    [
                        new ContractColumn { Canonical = "id", Accepts = ["id"], Type = "VARCHAR", Required = true },
                        new ContractColumn { Canonical = "required_col", Accepts = ["required_col"], Type = "VARCHAR", Required = true },
                    ],
                };

                var gen = new SqlGenerator(csvPath, "Test", contract, conn);

                ExecuteSql(conn, gen.BuildCreateSchemaIfNotExists());
                ExecuteSql(conn, gen.BuildRawLoadSql());

                var statements = gen.CollectStatements();

                // 3 contract schemas + 1 metadata schema + 1 table + 1 quarantine_view
                // + 1 quarantine_log + 1 failed-loads view + 1 table_load = 9
                Assert.Equal(9, statements.Count);

                Assert.Equal("create_quarantine_view", statements[5].Operation);
                Assert.Contains("CREATE OR REPLACE VIEW", statements[5].Sql);

                Assert.Equal("insert_quarantine_log", statements[6].Operation);
                Assert.Contains("INSERT INTO", statements[6].Sql);
                Assert.Contains("\"metadata\".\"quarantine\"", statements[6].Sql);

                Assert.Equal("create_view", statements[7].Operation);
                Assert.Equal("v_failed_loads", statements[7].ObjectName);
                Assert.Contains("CREATE OR REPLACE VIEW", statements[7].Sql);
                Assert.Contains("row_count IS NULL", statements[7].Sql);

                Assert.Equal("insert_table_load", statements[8].Operation);
                Assert.Contains("INSERT INTO", statements[8].Sql);
                Assert.Contains("\"metadata\".\"table_load\"", statements[8].Sql);
                Assert.Contains("NULL", statements[8].Sql);
            }
            finally
            {
                if (File.Exists(csvPath)) File.Delete(csvPath);
            }
        }
    }

    // ===== SanitizeIdentifier Tests =====

    public class SanitizeIdentifierTests
    {
        [Fact]
        public void Sanitize_SimpleAlphanumeric_Unchanged()
        {
            Assert.Equal("customer", SqlGenerator.SanitizeIdentifier("customer"));
        }

        [Fact]
        public void Sanitize_Underscores_Unchanged()
        {
            Assert.Equal("bronze_raw", SqlGenerator.SanitizeIdentifier("bronze_raw"));
        }

        [Fact]
        public void Sanitize_Spaces_ReplacedWithUnderscore()
        {
            Assert.Equal("Capitalism_Inc", SqlGenerator.SanitizeIdentifier("Capitalism Inc"));
        }

        [Fact]
        public void Sanitize_Commas_ReplacedWithUnderscore()
        {
            Assert.Equal("Capitalism_Inc", SqlGenerator.SanitizeIdentifier("Capitalism,Inc"));
        }

        [Fact]
        public void Sanitize_Dots_ReplacedWithUnderscore()
        {
            Assert.Equal("Capitalism_Inc", SqlGenerator.SanitizeIdentifier("Capitalism.Inc"));
        }

        [Fact]
        public void Sanitize_Hyphens_ReplacedWithUnderscore()
        {
            Assert.Equal("my_table_v2", SqlGenerator.SanitizeIdentifier("my-table-v2"));
        }

        [Fact]
        public void Sanitize_LeadingDigit_PrefixedWithUnderscore()
        {
            Assert.Equal("_123table", SqlGenerator.SanitizeIdentifier("123table"));
        }

        [Fact]
        public void Sanitize_MixedSpecialChars_AllReplaced()
        {
            Assert.Equal("A_B_C_D_E_", SqlGenerator.SanitizeIdentifier("A.B-C D,E!"));
        }

        [Fact]
        public void Sanitize_EmptyString_ReturnsUnderscore()
        {
            Assert.Equal("_", SqlGenerator.SanitizeIdentifier(""));
        }

        [Fact]
        public void Sanitize_NullInput_ReturnsUnderscore()
        {
            Assert.Equal("_", SqlGenerator.SanitizeIdentifier(null!));
        }

        [Fact]
        public void Sanitize_DoubleQuotes_ReplacedWithUnderscore()
        {
            Assert.Equal("Table_Name", SqlGenerator.SanitizeIdentifier("Table\"Name"));
        }

        [Fact]
        public void Sanitize_SingleQuotes_ReplacedWithUnderscore()
        {
            Assert.Equal("Table_Name", SqlGenerator.SanitizeIdentifier("Table'Name"));
        }
    }

    // ===== QuoteQualified Tests =====

    public class QuoteQualifiedTests
    {
        [Fact]
        public void QuoteQualified_SimpleName_ReturnsQuoted()
        {
            var result = SqlGenerator.QuoteQualified("schema.table");
            Assert.Equal("\"schema\".\"table\"", result);
        }

        [Fact]
        public void QuoteQualified_NameWithUnderscores_ReturnsQuoted()
        {
            var result = SqlGenerator.QuoteQualified("bronze_raw.customer_Acme_abc12345");
            Assert.Equal("\"bronze_raw\".\"customer_Acme_abc12345\"", result);
        }

        [Fact]
        public void QuoteQualified_NameWithDotInTablePart_SplitsOnFirstDot()
        {
            var result = SqlGenerator.QuoteQualified("a.b.c");
            Assert.Equal("\"a\".\"b.c\"", result);
        }

        [Fact]
        public void QuoteQualified_EmbeddedDoubleQuote_Escaped()
        {
            // After sanitization this shouldn't occur, but QuoteQualified should still be safe
            var result = SqlGenerator.QuoteQualified("sch\"ema.table");
            Assert.Equal("\"sch\"\"ema\".\"table\"", result);
        }
    }
}
