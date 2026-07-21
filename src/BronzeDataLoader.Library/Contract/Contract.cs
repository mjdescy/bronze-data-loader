using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace BronzeDataLoader.Library.Contract;

/// <summary>
/// Defines the canonical structure for a table's data files.
/// </summary>
public record Contract
{
    /// <summary>Destination table name.</summary>
    public string Table { get; init; } = string.Empty;

    /// <summary>Schema definitions for staging, valid, and invalid data.</summary>
    public ContractSchema Schema { get; init; } = new();

    /// <summary>Column definitions.</summary>
    public List<ContractColumn> Columns { get; init; } = [];

    /// <summary>
    /// Load a contract from a YAML file and return a <see cref="Contract"/> object.
    /// </summary>
    /// <param name="path">Path to the contract YAML file.</param>
    /// <returns>A populated <see cref="Contract"/>.</returns>
    /// <exception cref="FileNotFoundException">The specified file does not exist.</exception>
    public static Contract FromYaml(string path)
    {
        if (!File.Exists(path))
            throw new FileNotFoundException($"Contract file not found: {path}", path);

        var yaml = File.ReadAllText(path);

        var deserializer = new DeserializerBuilder()
            .WithNamingConvention(UnderscoredNamingConvention.Instance)
            .Build();

        return deserializer.Deserialize<Contract>(yaml);
    }
}
