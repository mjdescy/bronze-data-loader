using DuckDB.NET.Data;
using BronzeDataLoader.Library.Sql;

namespace BronzeDataLoader.Library.Models;

/// <summary>
/// Represents a source file matched from the manifest and its associated contract.
/// </summary>
public record SourceFile
{
    /// <summary>Full path to the source data file.</summary>
    public string FilePath { get; init; } = string.Empty;

    /// <summary>The submitter/owner name.</summary>
    public string Submitter { get; init; } = string.Empty;

    /// <summary>The contract defining the expected column structure.</summary>
    public Contract.Contract Contract { get; init; } = new();

    /// <summary>
    /// Generate and execute all SQL statements for this source file (load, validate, view/quarantine).
    /// </summary>
    /// <param name="appConfig">Application configuration with an open DuckDB connection.</param>
    public void GenerateAndExecuteSql(AppConfig appConfig)
    {
        var connection = appConfig.Connection
            ?? throw new InvalidOperationException("AppConfig.Connection is not initialized");

        var sqlGenerator = new SqlGenerator(FilePath, Submitter, Contract, connection);

        ExecuteNonQuery(connection, sqlGenerator.BuildCreateSchemaIfNotExists());

        // Ensure metadata schema and tables exist for table_load and quarantine logging
        ExecuteNonQuery(connection, "CREATE SCHEMA IF NOT EXISTS \"metadata\";");
        ExecuteNonQuery(connection, """
            CREATE TABLE IF NOT EXISTS "metadata"."table_load" (
                table_schema VARCHAR,
                table_name VARCHAR,
                file_name VARCHAR,
                file_path VARCHAR,
                imported_at TIMESTAMP DEFAULT CURRENT_TIMESTAMP,
                row_count BIGINT
            );
            """);
        ExecuteNonQuery(connection, """
            CREATE TABLE IF NOT EXISTS "metadata"."quarantine" (
                table_name VARCHAR,
                error_message VARCHAR,
                quarantined_at TIMESTAMP DEFAULT CURRENT_TIMESTAMP
            );
            """);
        ExecuteNonQuery(connection, sqlGenerator.BuildCreateFailedLoadsViewSql());

        // Insert row with NULL row_count BEFORE load (negative tracking).
        // If the load fails, this row persists with NULL, indicating a failed load.
        ExecuteNonQuery(connection, sqlGenerator.BuildInsertTableLoadMetadataSql());

        var rawSql = sqlGenerator.BuildRawLoadSql();
        ExecuteNonQuery(connection, rawSql);

        // Compute row count for metadata and update the placeholder row
        var rowCount = GetRowCount(connection, sqlGenerator.RawTableName);
        ExecuteNonQuery(connection, sqlGenerator.BuildUpdateTableLoadMetadataSql(rowCount));

        try
        {
            var (viewSql, warnings) = sqlGenerator.BuildBronzeViewSql();
            ExecuteNonQuery(connection, viewSql);

            foreach (var warning in warnings)
            {
                Console.WriteLine($"Warning: {warning}");
            }
        }
        catch (InvalidOperationException ex)
        {
            var (quarantineSql, metadataSql) = sqlGenerator.BuildQuarantineSql(ex.Message);
            ExecuteNonQuery(connection, quarantineSql);
            ExecuteNonQuery(connection, metadataSql);
            Console.WriteLine($"Quarantined {FilePath}: {ex.Message}");
        }
    }

    /// <summary>
    /// Execute only the raw data loading (schema creation + table import),
    /// without creating views or quarantine. Used by --generate-sql mode
    /// to populate the database so that DESCRIBE works for SQL collection.
    /// </summary>
    /// <param name="appConfig">Application configuration with an open DuckDB connection.</param>
    public void GenerateRawLoad(AppConfig appConfig)
    {
        var connection = appConfig.Connection
            ?? throw new InvalidOperationException("AppConfig.Connection is not initialized");

        var sqlGenerator = new SqlGenerator(FilePath, Submitter, Contract, connection);

        ExecuteNonQuery(connection, sqlGenerator.BuildCreateSchemaIfNotExists());
        ExecuteNonQuery(connection, sqlGenerator.BuildRawLoadSql());
    }

    /// <summary>
    /// Generate the bronze view SQL without executing it.
    /// </summary>
    /// <param name="appConfig">Application configuration.</param>
    /// <returns>A tuple of the SQL string and any warnings.</returns>
    public (string Sql, string[] Warnings) GenerateSql(AppConfig appConfig)
    {
        var connection = appConfig.Connection
            ?? throw new InvalidOperationException("AppConfig.Connection is not initialized");

        var sqlGenerator = new SqlGenerator(FilePath, Submitter, Contract, connection);
        return sqlGenerator.BuildBronzeViewSql();
    }

    /// <summary>
    /// Collect all SQL statements generated for this source file as individual
    /// <see cref="SqlStatement"/> records suitable for writing to .sql files.
    /// </summary>
    /// <param name="appConfig">Application configuration with an open DuckDB connection.</param>
    /// <returns>A list of <see cref="SqlStatement"/>.</returns>
    public List<SqlStatement> CollectSqlStatements(AppConfig appConfig)
    {
        var connection = appConfig.Connection
            ?? throw new InvalidOperationException("AppConfig.Connection is not initialized");

        var sqlGenerator = new SqlGenerator(FilePath, Submitter, Contract, connection);
        return sqlGenerator.CollectStatements();
    }

    private static void ExecuteNonQuery(DuckDBConnection connection, string sql)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private static long GetRowCount(DuckDBConnection connection, string rawTableName)
    {
        var parts = rawTableName.Split('.');
        var schema = parts[0];
        var table = parts.Length > 1 ? parts[1] : parts[0];
        using var cmd = connection.CreateCommand();
        cmd.CommandText = $"SELECT COUNT(*) FROM \"{schema}\".\"{table}\";";
        var result = cmd.ExecuteScalar();
        return result is long count ? count : 0L;
    }
}
