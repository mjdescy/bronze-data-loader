using BronzeDataLoader.Library.Contract;

namespace BronzeDataLoader.Library.Tests;

public class ContractTests
{
    [Fact]
    public void FromYaml_ValidYaml_CreatesContract()
    {
        var yaml = """
table: customer
schema:
  staging: bronze_raw
  valid: bronze
  invalid: bronze_quarantine
columns:
  - canonical: customer_id
    accepts: [customer_id, cust_id, "Customer ID", customerid]
    type: VARCHAR
    required: true
  - canonical: signup_date
    accepts: [signup_date, sign_up_date, "Signup Date"]
    type: DATE
    required: true
  - canonical: email
    accepts: [email, Email, email_address]
    type: VARCHAR
    required: false
""";

        var path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path, yaml);

            var contract = Contract.Contract.FromYaml(path);

            Assert.Equal("customer", contract.Table);
            Assert.Equal("bronze_raw", contract.Schema.Staging);
            Assert.Equal("bronze", contract.Schema.Valid);
            Assert.Equal("bronze_quarantine", contract.Schema.Invalid);

            Assert.Equal(3, contract.Columns.Count);

            var col1 = contract.Columns[0];
            Assert.Equal("customer_id", col1.Canonical);
            Assert.Equal(["customer_id", "cust_id", "Customer ID", "customerid"], col1.Accepts);
            Assert.Equal("VARCHAR", col1.Type);
            Assert.True(col1.Required);

            var col2 = contract.Columns[1];
            Assert.Equal("signup_date", col2.Canonical);
            Assert.Equal(["signup_date", "sign_up_date", "Signup Date"], col2.Accepts);
            Assert.Equal("DATE", col2.Type);
            Assert.True(col2.Required);

            var col3 = contract.Columns[2];
            Assert.Equal("email", col3.Canonical);
            Assert.Equal(["email", "Email", "email_address"], col3.Accepts);
            Assert.Equal("VARCHAR", col3.Type);
            Assert.False(col3.Required);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void FromYaml_MissingFile_ThrowsFileNotFoundException()
    {
        var path = "/path/does/not/exist/contract.yaml";

        Assert.Throws<FileNotFoundException>(() => Contract.Contract.FromYaml(path));
    }

    [Fact]
    public void FromYaml_EmptyYaml_UsesDefaults()
    {
        var yaml = "table: test\nschema:\n  staging: custom_staging\n  valid: custom_valid\n  invalid: custom_quarantine\ncolumns: []";

        var path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path, yaml);

            var contract = Contract.Contract.FromYaml(path);

            Assert.Equal("test", contract.Table);
            Assert.Equal("custom_staging", contract.Schema.Staging);
            Assert.Equal("custom_valid", contract.Schema.Valid);
            Assert.Equal("custom_quarantine", contract.Schema.Invalid);
            Assert.Empty(contract.Columns);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }
}
