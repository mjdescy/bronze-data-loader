# Bronze Layer Data Loader

## Purpose

1. Load data files into the bronze_raw_data schema in a database.
2. Create clean views for each table that matches the table contract in the bronze schema.
3. Create views for each table that does not match the table contact in the bronze_quarantine schema.

## Usage

### Configuration

The command line program is configured by a set of files:

| File          | Purpose                                                                                                    |
| ------------- | ---------------------------------------------------------------------------------------------------------- |
| config.yaml   | Defines default values for file paths, etc.                                                                |
| manifest.csv  | Defines the source files and which contract each source file is mapped to.                                 |
| contract.yaml | Defines the canonical and allowed structure for a table's data files. Multiple contract files can be used. |

You can generate these files in the current working directory with the following command:

```bash
bronze-data-loader init
```

This command is equivalent to the following three commands:

```bash
bronze-data-loader new config --output-folder .
bronze-data-loader new contract --output-folder .
bronze-data-loader new manifest --output-folder .
```

Edit the "config.yaml" file in your favorite editor. It will look like this:

```yaml
manifest_path: "manifest.csv" # default "manifest.csv"
data_folder: "data"           # default "." for current working directory
contracts_folder: "."         # default "." for current working directory
output_folder: "output"       # default "." for current working directory
database_path: "database"     # default "." for current working directory
```

The values in "manifest.csv" drive what data files are imported and what contract is applied to each data file. Edit the "manifest.csv" file in your favorite text editor or spreadsheet program. It has the following structure:

| submitter  | source_folder | file_pattern  | contract               |
| ---------- | ------------- | ------------- | ---------------------- |
| Acme, Inc. | Acme/data-in  | customer*.txt | contract_customer.yaml |
| Beta, LLC  | Beta/data-in  | customer*.tsv | contract_customer.yaml |

Source files can be defined as file patterns with wildcards (?, *). Tables for source files are named as follows: `[contract.table]_[submitter]_[file_stem_hash]`.

A contract defines the field listing for the data file. A contract is defined in a YAML file that looks like this:

```yaml
table: customer              # Destination table
schema:
  staging: bronze_raw        # Schema for all source tables to be loaded to
  valid: bronze              # Schema for views that conform to the contract
  invalid: bronze_quarantine # Schema for views that do not conform to the contract
columns:                     # Column definitions
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
```

### Execution

After setting up the configuration files, execute the program like this:

```bash
bronze-data-loader load example/config.yaml
```

The program will output a DuckDB database that contains the following:

1. Tables for all matching source files in the "bronze_raw" schema.
2. Views for all source files that conform to their contracts in the "bronze" schema.
3. Views for all source files that do not conform to their contracts in the "bronze_quarantine" schema.
