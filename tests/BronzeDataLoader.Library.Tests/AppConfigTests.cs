namespace BronzeDataLoader.Library.Tests;

public class AppConfigTests
{
    [Fact]
    public void FromYaml_ValidYaml_CreatesAppConfig()
    {
        var yaml = """
manifest_path: "test-manifest.csv"
data_folder: "test-data"
contracts_folder: "test-contracts"
output_folder: "test-output"
database_path: ":memory:"
raw_schema: "bronze_raw"
schema: "bronze"
schema_quarantine: "bronze_quarantine"
""";

        var dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(dir);
        var yamlPath = Path.Combine(dir, "config.yaml");

        try
        {
            File.WriteAllText(yamlPath, yaml);

            var appConfig = Models.AppConfig.FromYaml(yamlPath);

            Assert.EndsWith("test-manifest.csv", appConfig.ManifestPath);
            Assert.EndsWith("test-data", appConfig.DataFolder);
            Assert.EndsWith("test-contracts", appConfig.ContractsFolder);
            Assert.EndsWith("test-output", appConfig.OutputFolder);
            Assert.EndsWith(":memory:", appConfig.DatabasePath);
            Assert.Equal("bronze_raw", appConfig.RawSchema);
            Assert.Equal("bronze", appConfig.Schema);
            Assert.Equal("bronze_quarantine", appConfig.SchemaQuarantine);

            Assert.NotNull(appConfig.Connection);
            Assert.True(appConfig.Connection.State == System.Data.ConnectionState.Open);
        }
        finally
        {
            if (Directory.Exists(dir))
                Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void FromYaml_WindowsStyleBackslashesInQuotedPaths_AreParsed()
    {
        var yaml = """
manifest_path: "manifest\file.csv"
data_folder: "nested\folder"
contracts_folder: "contracts\sub"
output_folder: "output\sub"
database_path: ":memory:"
""";

        var dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(dir);
        var yamlPath = Path.Combine(dir, "config.yaml");

        try
        {
            File.WriteAllText(yamlPath, yaml);

            var appConfig = Models.AppConfig.FromYaml(yamlPath);

            Assert.Contains("manifest\\file.csv", appConfig.ManifestPath);
            Assert.Contains("nested\\folder", appConfig.DataFolder);
            Assert.Contains("contracts\\sub", appConfig.ContractsFolder);
            Assert.Contains("output\\sub", appConfig.OutputFolder);
        }
        finally
        {
            if (Directory.Exists(dir))
                Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void FromYaml_MissingFile_ThrowsFileNotFoundException()
    {
        var path = "/path/does/not/exist/config.yaml";

        Assert.Throws<FileNotFoundException>(() => Models.AppConfig.FromYaml(path));
    }

    [Fact]
    public void FromYaml_RelativePaths_AreResolvedRelativeToConfigDir()
    {
        var yaml = """
manifest_path: "manifest.csv"
data_folder: "data"
contracts_folder: "contracts"
output_folder: "output"
database_path: ":memory:"
""";

        var dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(dir);
        var subDir = Path.Combine(dir, "subconfig");
        Directory.CreateDirectory(subDir);
        var yamlPath = Path.Combine(subDir, "config.yaml");

        try
        {
            File.WriteAllText(yamlPath, yaml);

            var appConfig = Models.AppConfig.FromYaml(yamlPath);

            Assert.Contains(subDir, appConfig.ManifestPath);
            Assert.Contains(subDir, appConfig.DataFolder);
            Assert.Contains(subDir, appConfig.ContractsFolder);
            Assert.Contains(subDir, appConfig.OutputFolder);
        }
        finally
        {
            if (Directory.Exists(dir))
                Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void FromYaml_AbsolutePaths_AreUsedAsIs()
    {
        var yaml = $"""
manifest_path: "/etc/manifest.csv"
data_folder: "/var/data"
contracts_folder: "/etc/contracts"
output_folder: "/tmp/output"
database_path: ":memory:"
""";

        var dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(dir);
        var yamlPath = Path.Combine(dir, "config.yaml");

        try
        {
            File.WriteAllText(yamlPath, yaml);

            var appConfig = Models.AppConfig.FromYaml(yamlPath);

            Assert.Equal("/etc/manifest.csv", appConfig.ManifestPath);
            Assert.Equal("/var/data", appConfig.DataFolder);
            Assert.Equal("/etc/contracts", appConfig.ContractsFolder);
            Assert.Equal("/tmp/output", appConfig.OutputFolder);
        }
        finally
        {
            if (Directory.Exists(dir))
                Directory.Delete(dir, recursive: true);
        }
    }
}
