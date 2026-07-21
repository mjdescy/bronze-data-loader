using System.Globalization;
using CsvHelper;
using CsvHelper.Configuration;

namespace BronzeDataLoader.Library.Models;

/// <summary>
/// The manifest is a collection of <see cref="ManifestEntry"/> objects loaded from a CSV file.
/// </summary>
public record Manifest
{
    /// <summary>Entries loaded from the manifest CSV.</summary>
    public List<ManifestEntry> Entries { get; init; } = [];

    /// <summary>
    /// Load the manifest from a CSV file.
    /// </summary>
    /// <param name="path">Path to the manifest CSV file.</param>
    /// <returns>A populated <see cref="Manifest"/>.</returns>
    /// <exception cref="FileNotFoundException">The specified file does not exist.</exception>
    /// <exception cref="InvalidOperationException">The manifest CSV is empty or has no data rows.</exception>
    public static Manifest FromCsv(string path)
    {
        if (!File.Exists(path))
            throw new FileNotFoundException($"Manifest file not found: {path}", path);

        using var reader = new StreamReader(path);
        using var csv = new CsvReader(reader, new CsvConfiguration(CultureInfo.InvariantCulture)
        {
            HasHeaderRecord = true,
            TrimOptions = TrimOptions.Trim,
            MissingFieldFound = null,
            PrepareHeaderForMatch = args => NormalizeHeader(args.Header),
        });

        var entries = csv.GetRecords<ManifestEntry>().ToList();

        if (entries.Count == 0)
            throw new InvalidOperationException($"Manifest at {path} has no entries");

        return new Manifest { Entries = entries };
    }

    /// <summary>
    /// Convert all manifest entries to a flat list of <see cref="SourceFile"/> objects.
    /// </summary>
    /// <param name="appConfig">Application configuration.</param>
    /// <returns>A flat list of <see cref="SourceFile"/> objects.</returns>
    public List<SourceFile> ToSourceFileList(AppConfig appConfig)
    {
        var sourceFiles = new List<SourceFile>();
        foreach (var entry in Entries)
        {
            sourceFiles.AddRange(entry.ToSourceFileList(appConfig));
        }
        return sourceFiles;
    }

    private static string NormalizeHeader(string header)
    {
        // Normalize both snake_case headers and PascalCase property names
        // to lowercase without underscores for matching.
        // "source_folder" -> "sourcefolder", "SourceFolder" -> "sourcefolder"
        return header.Replace("_", "").ToLowerInvariant();
    }
}
