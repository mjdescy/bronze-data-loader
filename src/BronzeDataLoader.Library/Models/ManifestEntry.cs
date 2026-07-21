using ContractFile = BronzeDataLoader.Library.Contract.Contract;

namespace BronzeDataLoader.Library.Models;

/// <summary>
/// A single entry in the manifest, defining a source file pattern and its associated contract.
/// </summary>
public record ManifestEntry
{
    /// <summary>The submitter/owner of the data.</summary>
    public string Submitter { get; init; } = string.Empty;

    /// <summary>Source folder (relative to <see cref="AppConfig.DataFolder"/> or absolute).</summary>
    public string SourceFolder { get; init; } = string.Empty;

    /// <summary>File pattern with wildcards (?, *).</summary>
    public string FilePattern { get; init; } = string.Empty;

    /// <summary>Path to the contract YAML file (relative to <see cref="AppConfig.ContractsFolder"/> or absolute).</summary>
    public string Contract { get; init; } = string.Empty;

    /// <summary>
    /// Return a list of source files matching the source folder and file pattern.
    /// </summary>
    /// <param name="appConfig">Application configuration.</param>
    /// <returns>Matching file paths.</returns>
    /// <exception cref="DirectoryNotFoundException">Source folder does not exist.</exception>
    public string[] MatchingFiles(AppConfig appConfig)
    {
        var sourceFolder = Path.IsPathRooted(SourceFolder)
            ? SourceFolder
            : Path.Combine(appConfig.DataFolder, SourceFolder);

        if (!Directory.Exists(sourceFolder))
            throw new DirectoryNotFoundException($"Source folder does not exist: {sourceFolder}");

        return Directory.GetFiles(sourceFolder, FilePattern, SearchOption.AllDirectories)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    /// <summary>
    /// Resolve the contract path and return a <see cref="Contract"/> object.
    /// </summary>
    /// <param name="appConfig">Application configuration.</param>
    /// <returns>The loaded <see cref="Contract"/>.</returns>
    /// <exception cref="FileNotFoundException">The contract file does not exist.</exception>
    public Contract.Contract ResolveContract(AppConfig appConfig)
    {
        var contractPath = Path.IsPathRooted(Contract)
            ? Contract
            : Path.Combine(appConfig.ContractsFolder, Contract);

        if (!File.Exists(contractPath))
            throw new FileNotFoundException($"Contract file not found: {contractPath}", contractPath);

        return ContractFile.FromYaml(contractPath);
    }

    /// <summary>
    /// Converts this manifest entry to a list of <see cref="SourceFile"/> objects based on matching files.
    /// </summary>
    /// <param name="appConfig">Application configuration.</param>
    /// <returns>A list of <see cref="SourceFile"/> objects.</returns>
    /// <exception cref="DirectoryNotFoundException">Source folder does not exist.</exception>
    /// <exception cref="FileNotFoundException">Contract file does not exist.</exception>
    /// <exception cref="InvalidOperationException">No files match the specified pattern.</exception>
    public SourceFile[] ToSourceFileList(AppConfig appConfig)
    {
        var matchingFiles = MatchingFiles(appConfig);

        if (matchingFiles.Length == 0)
            throw new InvalidOperationException(
                $"No files found in {SourceFolder} matching pattern {FilePattern}");

        var contract = ResolveContract(appConfig);

        return matchingFiles
            .Select(f => new SourceFile
            {
                FilePath = f,
                Submitter = Submitter,
                Contract = contract,
            })
            .ToArray();
    }
}
