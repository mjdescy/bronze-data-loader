namespace BronzeDataLoader.Library.Contract;

/// <summary>
/// Defines the three schema names used by a contract for staging, valid, and invalid data.
/// </summary>
public record ContractSchema
{
    /// <summary>Schema for all source tables to be loaded to (default: "bronze_raw").</summary>
    public string Staging { get; init; } = "bronze_raw";

    /// <summary>Schema for views that conform to the contract (default: "bronze").</summary>
    public string Valid { get; init; } = "bronze";

    /// <summary>Schema for views that do not conform to the contract (default: "bronze_quarantine").</summary>
    public string Invalid { get; init; } = "bronze_quarantine";
}
