using System.Security.Cryptography;
using System.Text;
using DuckDB.NET.Data;
using BronzeDataLoader.Library.Contract;

namespace BronzeDataLoader.Library.Sql;

/// <summary>
/// A single SQL statement with metadata for file generation.
/// </summary>
/// <param name="Operation">The operation type (e.g. "create_schema", "create_table", "create_view", "quarantine_view", "quarantine_log").</param>
/// <param name="ObjectName">The sanitized object name for filename construction.</param>
/// <param name="Sql">The SQL statement text.</param>
public record SqlStatement(string Operation, string ObjectName, string Sql);

/// <summary>
/// Generates SQL statements for DuckDB based on a contract and source file.
/// </summary>
/// <param name="FilePath">Full path to the source data file.</param>
/// <param name="Submitter">The submitter/owner name.</param>
/// <param name="Contract">The contract defining the expected column structure.</param>
/// <param name="Connection">An open DuckDB connection.</param>
public class SqlGenerator(
    string FilePath,
    string Submitter,
    Contract.Contract Contract,
    DuckDBConnection Connection)
{
    /// <summary>First 8 hex characters of SHA256 of the file stem.</summary>
    public string StemHash
    {
        get
        {
            var stem = Path.GetFileNameWithoutExtension(FilePath);
            var hash = SHA256.HashData(Encoding.UTF8.GetBytes(stem));
            return Convert.ToHexString(hash)[..8].ToLowerInvariant();
        }
    }

    /// <summary>Safe table name part: sanitized {Table}_{Submitter}_{hash}.</summary>
    private string SafeTablePart =>
        $"{SanitizeIdentifier(Contract.Table).Trim('_')}_{SanitizeIdentifier(Submitter).Trim('_')}_{StemHash}";

    /// <summary>Full view/table name including schema and hash.</summary>
    public string FullTableName =>
        $"{SanitizeIdentifier(Contract.Schema.Valid)}.{SafeTablePart}";

    /// <summary>Bronze_raw table name for this source file.</summary>
    public string RawTableName =>
        $"{SanitizeIdentifier(Contract.Schema.Staging)}.{SafeTablePart}";

    /// <summary>Bronze_quarantine view name for this source file.</summary>
    public string QuarantineTableName =>
        $"{SanitizeIdentifier(Contract.Schema.Invalid)}.{SafeTablePart}";

    /// <summary>
    /// Allow-list of DuckDB column types that contracts may specify.
    /// Prevents SQL injection via unvalidated type names embedded in CAST expressions.
    /// </summary>
    private static readonly HashSet<string> AllowedColumnTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        // String types
        "VARCHAR", "TEXT", "STRING",
        // Integer types
        "BIGINT", "INTEGER", "INT", "SMALLINT", "TINYINT", "HUGEINT",
        "UBIGINT", "UINTEGER", "USMALLINT", "UTINYINT",
        // Numeric types
        "DOUBLE", "FLOAT", "REAL", "DECIMAL", "NUMERIC",
        // Boolean
        "BOOLEAN", "BOOL",
        // Date/time types
        "DATE", "TIME", "TIMESTAMP", "TIMESTAMP_NS", "TIMESTAMP_MS", "TIMESTAMP_S",
        "TIMESTAMPTZ", "TIMESTAMP WITH TIME ZONE",
        // Interval
        "INTERVAL",
        // UUID
        "UUID",
        // Binary
        "BLOB", "BYTEA", "BINARY", "VARBINARY",
        // JSON
        "JSON",
    };

    /// <summary>
    /// Replace any character that is not alphanumeric or underscore with underscore.
    /// DuckDB object names (schemas, tables, views, columns) must not contain
    /// special characters even when quoted, to avoid ambiguous or broken SQL.
    /// </summary>
    public static string SanitizeIdentifier(string name)
    {
        if (string.IsNullOrEmpty(name))
            return "_";

        var sanitized = new string(
            [.. name.Select(c => char.IsLetterOrDigit(c) || c == '_' ? c : '_')]
        );

        // Identifiers that start with a digit are invalid; prefix with underscore
        if (char.IsDigit(sanitized[0]))
            sanitized = "_" + sanitized;

        return sanitized;
    }

    /// <summary>
    /// Convert a qualified name like "schema.table" to properly quoted '"schema"."table"'.
    /// Each part is sanitized and any embedded double-quote characters are escaped.
    /// </summary>
    public static string QuoteQualified(string name)
    {
        var parts = name.Split('.', 2);
        return $"\"{EscapeQuotes(parts[0])}\".\"{EscapeQuotes(parts[1])}\"";
    }

    /// <summary>Escape double quotes within an identifier by doubling them.</summary>
    private static string EscapeQuotes(string value) => value.Replace("\"", "\"\"");

    /// <summary>
    /// Escape single quotes within a SQL string literal by doubling them.
    /// This is defense-in-depth; DuckDBParameter is preferred for values.
    /// </summary>
    private static string EscapeSingleQuotes(string value) => value.Replace("'", "''");

    /// <summary>Build CREATE SCHEMA IF NOT EXISTS statements for all contract schemas.</summary>
    public string BuildCreateSchemaIfNotExists()
    {
        var staging = SanitizeIdentifier(Contract.Schema.Staging);
        var valid = SanitizeIdentifier(Contract.Schema.Valid);
        var invalid = SanitizeIdentifier(Contract.Schema.Invalid);

        return $"CREATE SCHEMA IF NOT EXISTS \"{staging}\";\n" +
               $"CREATE SCHEMA IF NOT EXISTS \"{valid}\";\n" +
               $"CREATE SCHEMA IF NOT EXISTS \"{invalid}\";";
    }

    /// <summary>
    /// Build the DuckDB macro for extracting the file name from a full file path.
    /// The macro is created once during metadata setup and reused across all file loads.
    /// Uses regex to handle both forward-slash (Unix) and backslash (Windows) path separators.
    /// </summary>
    public static string BuildCreateMacroSql()
    {
        return "CREATE OR REPLACE MACRO metadata_filename(path) AS regexp_extract(path, '[^/\\\\]+$', 0);";
    }

    /// <summary>
    /// Build the DuckDB macro for dynamically generating UNION ALL BY NAME queries
    /// across all tables in a schema matching a given prefix. Useful for unioning
    /// multiple source files that share the same contract (e.g. multiple submitter files).
    /// </summary>
    public static string BuildCreateUnionViewQueryMacroSql()
    {
        return """
            CREATE OR REPLACE MACRO union_view_query_sql(source_schema, table_prefix) AS (
                SELECT string_agg(
                    'SELECT * FROM "' || source_schema || '"."' || table_name || '"',
                    chr(10) || 'UNION ALL BY NAME' || chr(10)
                )
                FROM information_schema.tables
                WHERE table_schema = source_schema
                  AND table_name LIKE table_prefix || '%'
            );
            """;
    }

    /// <summary>
    /// Build a read function call for the source file based on its extension.
    /// </summary>
    /// <returns>A SQL read function call string.</returns>
    /// <exception cref="InvalidOperationException">Unsupported source format.</exception>
    public string BuildReadFunctionCall()
    {
        var extension = Path.GetExtension(FilePath).ToLowerInvariant();

        var safePath = EscapeSingleQuotes(FilePath);

        return extension switch
        {
            ".csv" => $"read_csv('{safePath}', header=true, all_varchar=true)",
            ".tsv" => $"read_csv('{safePath}', header=true, delim='\\t', all_varchar=true)",
            ".xlsx" => $"read_xlsx('{safePath}', header=true)",
            _ => throw new InvalidOperationException($"Unsupported source format: {extension}"),
        };
    }

    /// <summary>
    /// Build SQL to load data from the source file into a raw table.
    /// Prepends metadata columns (row_number, file_name, submitter) before the source data columns.
    /// </summary>
    public string BuildRawLoadSql()
    {
        var tableName = QuoteQualified(RawTableName);
        var safePath = EscapeSingleQuotes(FilePath);
        var safeSubmitter = EscapeSingleQuotes(Submitter);
        var readFunc = BuildReadFunctionCall();

        return $"CREATE OR REPLACE TABLE {tableName} AS\n" +
               $"SELECT\n" +
               $"  row_number() OVER () AS metadata_row_number,\n" +
               $"  metadata_filename('{safePath}') AS metadata_file_name,\n" +
               $"  '{safeSubmitter}' AS metadata_submitter,\n" +
               $"  *\n" +
               $"FROM {readFunc};";
    }

    /// <summary>
    /// Build a SELECT statement that maps raw columns to canonical contract columns.
    /// Also automatically includes any <c>metadata_</c>-prefixed columns from the raw table.
    /// </summary>
    /// <returns>A tuple of the SELECT SQL and any warnings.</returns>
    /// <exception cref="InvalidOperationException">Required columns are missing.</exception>
    public (string Sql, string[] Warnings) BuildSelect()
    {
        var rawQuoted = QuoteQualified(RawTableName);

        // Get actual column names AND types from the raw table (needed for metadata column casting)
        var actualCols = new List<(string Name, string Type)>();
        using (var cmd = Connection.CreateCommand())
        {
            cmd.CommandText = $"SELECT column_name, column_type FROM (DESCRIBE SELECT * FROM {rawQuoted})";
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                actualCols.Add((reader.GetString(0), reader.GetString(1)));
            }
        }

        var actualColNames = actualCols.Select(c => c.Name).ToList();
        var actualColTypes = actualCols.ToDictionary(c => c.Name, c => c.Type, StringComparer.OrdinalIgnoreCase);

        var actualColsCaseFold = actualColNames
            .Select(c => c.Trim().ToLowerInvariant())
            .ToHashSet();

        var selectCols = new List<string>();
        var errors = new List<string>();
        var warnings = new List<string>();
        var addedCanonicalNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var col in Contract.Columns)
        {
            // Validate the column type against the allow-list before embedding it in SQL
            if (!AllowedColumnTypes.Contains(col.Type))
            {
                errors.Add(
                    $"Unsupported column type '{col.Type}' for column '{col.Canonical}'. " +
                    $"Allowed types: {string.Join(", ", AllowedColumnTypes.Order())}");
                continue;
            }

            string? found = null;
            // Always treat canonical as if it is the first member of the accepts list,
            // so it is always recognized as an allowed name even if not listed in accepts.
            if (actualColsCaseFold.Contains(col.Canonical.Trim().ToLowerInvariant()))
            {
                found = col.Canonical;
            }
            else
            {
                foreach (var acceptedName in col.Accepts)
                {
                    if (actualColsCaseFold.Contains(acceptedName.Trim().ToLowerInvariant()))
                    {
                        found = acceptedName;
                        break;
                    }
                }
            }

            var safeCanonical = SanitizeIdentifier(col.Canonical);

            if (found is null)
            {
                if (col.Required)
                {
                    errors.Add(
                        $"Missing required column '{col.Canonical}' (looked for [{string.Join(", ", col.Accepts)}])");
                }
                else
                {
                    selectCols.Add($"NULL::{col.Type.ToUpperInvariant()} AS {safeCanonical}");
                }
                addedCanonicalNames.Add(safeCanonical);
                continue;
            }

            // Quote and sanitize the found column name for safe SQL
            var safeFound = EscapeQuotes(found.Trim());
            selectCols.Add($"\"{safeFound}\"::{col.Type.ToUpperInvariant()} AS {safeCanonical}");
            addedCanonicalNames.Add(safeCanonical);
        }

        // Find extra columns not in the contract (excluding metadata_ columns — they are intentional)
        var acceptedColumnNames = Contract.Columns
            .SelectMany(c => c.Accepts.Prepend(c.Canonical))
            .Select(a => a.Trim().ToLowerInvariant())
            .ToHashSet();

        var extraColumnNames = actualColNames
            .Where(name => !acceptedColumnNames.Contains(name.Trim().ToLowerInvariant()))
            .Where(name => !name.Trim().StartsWith("metadata_", StringComparison.OrdinalIgnoreCase))
            .Select(name => name.Trim())
            .OrderBy(n => n)
            .ToArray();

        if (extraColumnNames.Length > 0)
        {
            warnings.Add($"Ignoring unexpected columns: [{string.Join(", ", extraColumnNames)}]");
        }

        if (errors.Count > 0)
        {
            throw new InvalidOperationException(string.Join("; ", errors));
        }

        // Auto-include metadata_ columns from the raw table (if not already covered by a contract column)
        foreach (var (name, type) in actualCols)
        {
            if (name.Trim().StartsWith("metadata_", StringComparison.OrdinalIgnoreCase))
            {
                var safeName = SanitizeIdentifier(name.Trim());
                if (!addedCanonicalNames.Contains(safeName))
                {
                    selectCols.Add($"\"{EscapeQuotes(name.Trim())}\"::{type.ToUpperInvariant()} AS {safeName}");
                }
            }
        }

        var sql = $"SELECT\n  {string.Join(",\n  ", selectCols)}";
        return (sql, [.. warnings]);
    }

    /// <summary>
    /// Build a CREATE OR REPLACE VIEW statement that maps raw columns to canonical names.
    /// </summary>
    /// <returns>A tuple of the view SQL and any warnings.</returns>
    public (string Sql, string[] Warnings) BuildBronzeViewSql()
    {
        var (selectSql, warnings) = BuildSelect();
        var viewQuoted = QuoteQualified(FullTableName);
        var rawQuoted = QuoteQualified(RawTableName);

        var sql = $"CREATE OR REPLACE VIEW {viewQuoted} AS\n{selectSql}\nFROM {rawQuoted};";

        return (sql, warnings);
    }

    /// <summary>
    /// Build SQL statements for quarantining a data file that doesn't meet the contract.
    /// </summary>
    /// <param name="errorMessage">The validation error message.</param>
    /// <returns>A tuple of (quarantine DDL SQL, metadata insert SQL).</returns>
    public (string QuarantineSql, string MetadataSql) BuildQuarantineSql(string errorMessage)
    {
        var rawTable = RawTableName;
        var rawQuoted = QuoteQualified(rawTable);
        var quarantineQuoted = QuoteQualified(QuarantineTableName);
        var safeError = errorMessage.Replace("'", "''");

        var quarantineSql = $"CREATE OR REPLACE VIEW {quarantineQuoted} AS SELECT * FROM {rawQuoted};";

        // rawTable is a sanitized qualified name like bronze_raw.customer_Acme_hash
        // SafeError has single quotes escaped
        var metadataSql = $"INSERT INTO \"metadata\".\"quarantine\" (table_name, error_message)\nVALUES ('{rawTable}', '{safeError}');";

        return (quarantineSql, metadataSql);
    }

    /// <summary>
    /// Build a helper that extracts the raw schema parts, file name, escaped file path,
    /// and escaped submitter for use in metadata SQL statements.
    /// </summary>
    /// <remarks>
    /// TODO: Migrate metadata INSERT/UPDATE to <c>DuckDBParameter</c>-based commands
    /// to eliminate SQL injection risk entirely instead of relying on string escaping.
    /// </remarks>
    private (string RawSchema, string RawTableName, string FileName, string EscapedFilePath, string EscapedSubmitter) GetMetadataParts()
    {
        var rawSchemaParts = RawTableName.Split('.');
        var rawSchema = rawSchemaParts[0];
        var rawTableName = rawSchemaParts.Length > 1 ? rawSchemaParts[1] : rawSchemaParts[0];
        var fileName = EscapeSingleQuotes(Path.GetFileName(FilePath));
        var filePath = EscapeSingleQuotes(FilePath);
        var submitter = EscapeSingleQuotes(Submitter);
        return (rawSchema, rawTableName, fileName, filePath, submitter);
    }

    /// <summary>
    /// Build an INSERT INTO <c>metadata.table_load</c> statement with a NULL row_count.
    /// Used before the raw load attempt to provide negative tracking — if the load fails,
    /// the row remains with NULL row_count, indicating a failed load.
    /// </summary>
    public string BuildInsertTableLoadMetadataSql()
    {
        var (rawSchema, rawTableName, fileName, filePath, submitter) = GetMetadataParts();

        return $"INSERT INTO \"metadata\".\"table_load\" (table_schema, table_name, file_name, file_path, submitter, row_count)\n" +
               $"VALUES ('{rawSchema}', '{rawTableName}', '{fileName}', '{filePath}', '{submitter}', NULL);";
    }

    /// <summary>
    /// Build an UPDATE statement for <c>metadata.table_load</c> to set the row_count
    /// on a successful load. Matches the row by file_path, submitter, and NULL row_count
    /// so that only the pre-load insertion (from <see cref="BuildInsertTableLoadMetadataSql"/>)
    /// is updated.
    /// </summary>
    /// <param name="rowCount">The number of rows in the imported table.</param>
    public string BuildUpdateTableLoadMetadataSql(long rowCount)
    {
        var (rawSchema, rawTableName, fileName, filePath, submitter) = GetMetadataParts();

        return $"UPDATE \"metadata\".\"table_load\" SET row_count = {rowCount}\n" +
               $"WHERE table_schema = '{rawSchema}'\n" +
               $"  AND table_name = '{rawTableName}'\n" +
               $"  AND file_path = '{filePath}'\n" +
               $"  AND submitter = '{submitter}'\n" +
               $"  AND row_count IS NULL;";
    }

    /// <summary>
    /// Build a CREATE OR REPLACE VIEW for failed loads, selecting rows from
    /// <c>metadata.table_load</c> where <c>row_count IS NULL</c>.
    /// </summary>
    public string BuildCreateFailedLoadsViewSql()
    {
        return "CREATE OR REPLACE VIEW \"metadata\".\"v_failed_loads\" AS\n" +
               "SELECT * FROM \"metadata\".\"table_load\"\n" +
               "WHERE row_count IS NULL;";
    }

    /// <summary>
    /// Collect all SQL statements that would be generated for this source file.
    /// Schema statements are returned individually (not batched).
    /// </summary>
    /// <returns>A list of <see cref="SqlStatement"/> with operation metadata.</returns>
    public List<SqlStatement> CollectStatements()
    {
        var statements = new List<SqlStatement>();

        // Individual schema creation statements
        var staging = SanitizeIdentifier(Contract.Schema.Staging);
        var valid = SanitizeIdentifier(Contract.Schema.Valid);
        var invalid = SanitizeIdentifier(Contract.Schema.Invalid);

        statements.Add(new SqlStatement("create_schema", staging,
            $"CREATE SCHEMA IF NOT EXISTS \"{staging}\";"));
        statements.Add(new SqlStatement("create_schema", valid,
            $"CREATE SCHEMA IF NOT EXISTS \"{valid}\";"));
        statements.Add(new SqlStatement("create_schema", invalid,
            $"CREATE SCHEMA IF NOT EXISTS \"{invalid}\";"));

        // Metadata schema
        statements.Add(new SqlStatement("create_schema", "metadata",
            "CREATE SCHEMA IF NOT EXISTS \"metadata\";"));

        // Utility macros
        statements.Add(new SqlStatement("create_macro", "metadata_filename", BuildCreateMacroSql()));
        statements.Add(new SqlStatement("create_macro", "union_view_query_sql", BuildCreateUnionViewQueryMacroSql()));

        // Raw table import
        statements.Add(new SqlStatement("create_table", SafeTablePart, BuildRawLoadSql()));

        // View or quarantine — these need DESCRIBE data so raw load must have happened
        try
        {
            var (viewSql, _) = BuildBronzeViewSql();
            statements.Add(new SqlStatement("create_view", SafeTablePart, viewSql));
        }
        catch (InvalidOperationException ex)
        {
            var (quarantineSql, metadataSql) = BuildQuarantineSql(ex.Message);
            statements.Add(new SqlStatement("create_quarantine_view", SafeTablePart, quarantineSql));
            statements.Add(new SqlStatement("insert_quarantine_log", SafeTablePart, metadataSql));
        }

        // Failed-loads view
        statements.Add(new SqlStatement("create_view", "v_failed_loads", BuildCreateFailedLoadsViewSql()));

        // Table load metadata (INSERT with NULL row_count for negative tracking)
        statements.Add(new SqlStatement("insert_table_load", SafeTablePart, BuildInsertTableLoadMetadataSql()));

        return statements;
    }
}
