using System.Security.Cryptography;
using System.Text;
using DuckDB.NET.Data;
using BronzeDataLoader.Library.Contract;

namespace BronzeDataLoader.Library.Sql;

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
        $"{SanitizeIdentifier(Contract.Table)}_{SanitizeIdentifier(Submitter)}_{StemHash}";

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
    /// Build a read function call for the source file based on its extension.
    /// </summary>
    /// <returns>A SQL read function call string.</returns>
    /// <exception cref="InvalidOperationException">Unsupported source format.</exception>
    public string BuildReadFunctionCall()
    {
        var extension = Path.GetExtension(FilePath).ToLowerInvariant();

        return extension switch
        {
            ".csv" => $"read_csv('{FilePath}', header=true, all_varchar=true)",
            ".tsv" => $"read_csv('{FilePath}', header=true, delim='\\t', all_varchar=true)",
            ".xlsx" => $"read_xlsx('{FilePath}', header=true)",
            _ => throw new InvalidOperationException($"Unsupported source format: {extension}"),
        };
    }

    /// <summary>
    /// Build SQL to load data from the source file into a raw table.
    /// </summary>
    public string BuildRawLoadSql()
    {
        var tableName = QuoteQualified(RawTableName);
        return $"CREATE OR REPLACE TABLE {tableName} AS SELECT * FROM {BuildReadFunctionCall()};";
    }

    /// <summary>
    /// Build a SELECT statement that maps raw columns to canonical contract columns.
    /// </summary>
    /// <returns>A tuple of the SELECT SQL and any warnings.</returns>
    /// <exception cref="InvalidOperationException">Required columns are missing.</exception>
    public (string Sql, string[] Warnings) BuildSelect()
    {
        var rawQuoted = QuoteQualified(RawTableName);

        // Get actual column names from the raw table
        var actualCols = new List<string>();
        using (var cmd = Connection.CreateCommand())
        {
            cmd.CommandText = $"SELECT column_name FROM (DESCRIBE SELECT * FROM {rawQuoted})";
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                actualCols.Add(reader.GetString(0));
            }
        }

        var actualColsCaseFold = actualCols
            .Select(c => c.Trim().ToLowerInvariant())
            .ToHashSet();

        var selectCols = new List<string>();
        var errors = new List<string>();
        var warnings = new List<string>();

        foreach (var col in Contract.Columns)
        {
            string? found = null;
            foreach (var acceptedName in col.Accepts)
            {
                if (actualColsCaseFold.Contains(acceptedName.Trim().ToLowerInvariant()))
                {
                    found = acceptedName;
                    break;
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
                continue;
            }

            // Quote and sanitize the found column name for safe SQL
            var safeFound = EscapeQuotes(found.Trim());
            selectCols.Add($"\"{safeFound}\"::{col.Type.ToUpperInvariant()} AS {safeCanonical}");
        }

        // Find extra columns not in the contract
        var acceptedColumnNames = Contract.Columns
            .SelectMany(c => c.Accepts)
            .Select(a => a.Trim().ToLowerInvariant())
            .ToHashSet();

        var extraColumnNames = actualCols
            .Where(name => !acceptedColumnNames.Contains(name.Trim().ToLowerInvariant()))
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

        var schemaSql = "CREATE SCHEMA IF NOT EXISTS \"bronze_quarantine\";";

        var logDdl = "CREATE TABLE IF NOT EXISTS \"bronze_quarantine\".\"_quarantine_log\" (\n" +
            "    table_name VARCHAR,\n" +
            "    error_message VARCHAR,\n" +
            "    quarantined_at TIMESTAMP DEFAULT CURRENT_TIMESTAMP\n" +
        ");";

        var quarantineSql = $"{schemaSql}\n{logDdl}\nCREATE OR REPLACE VIEW {quarantineQuoted} AS SELECT * FROM {rawQuoted};";

        // rawTable is a sanitized qualified name like "bronze_raw"."customer_Acme_hash"
        // SafeError has single quotes escaped
        var metadataSql = $"INSERT INTO \"bronze_quarantine\".\"_quarantine_log\" (table_name, error_message)\nVALUES ('{rawTable}', '{safeError}');";

        return (quarantineSql, metadataSql);
    }
}
