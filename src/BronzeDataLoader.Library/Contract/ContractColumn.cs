namespace BronzeDataLoader.Library.Contract;

/// <summary>
/// Represents a single column definition in a contract.
/// </summary>
public record ContractColumn
{
    /// <summary>The canonical (normalized) column name.</summary>
    public string Canonical { get; init; } = string.Empty;

    /// <summary>Alternative names this column may appear under in source files.</summary>
    public string[] Accepts { get; init; } = [];

    /// <summary>The DuckDB data type (e.g. VARCHAR, DATE, BIGINT).</summary>
    public string Type { get; init; } = string.Empty;

    /// <summary>Whether this column is required in the source data.</summary>
    public bool Required { get; init; }
}
