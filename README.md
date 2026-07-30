# Bronze Layer Data Loader

## Purpose

1. Load data files into the bronze_raw_data schema in a database.
2. Create clean views for each table that matches the table contract in the bronze schema.
3. Create views for each table that does not match the table contract in the bronze_quarantine schema.

## Description

This program was created with a certain type of data project in mind: the data call. In a data call, one or more submitters are asked to provide data files that conform to a specific contract (data requirements). The submitters may not be familiar with the database or the contract, and they may not have the ability to validate their data files against the contract. This program loads the data files into a database and creates views that conform to the contract. Files that do not conform to the contract are placed in a quarantine schema for further investigation. This program is designed to be run from the command line, and it is configured by a set of files that define the source files, the contracts, and the database connection.

## Database

The database used in this program is DuckDB, which is an in-process SQL OLAP database management system. DuckDB is designed to support analytical query workloads and is optimized for fast query execution on large datasets. It is a self-contained database that does not require a separate server process, making it easy to use and deploy.

Raw tables are created in the database for each source file that is loaded. The raw tables are created in a separate schema from the clean views, allowing for easy separation of the raw data and the clean views. The raw tables are named uniquely using the contract name, submitter name, and a hash of the file stem (the file name without its extension). Each raw table includes three metadata columns (`metadata_row_number`, `metadata_file_name`, `metadata_submitter`) that provide per-row lineage information. Metadata about the raw tables is also stored in a separate schema, allowing for easy tracking of the source files and their corresponding raw tables.

Views are created in the database for each table that conforms to the contract, and these views can be queried like any other table in the database. The views are created in a separate schema from the raw data tables, allowing for easy separation of the raw data and the clean views. Views were chosen over tables because they do not require additional storage space and can be created and dropped quickly. The views are created with the same name as the raw data tables, but they are placed in a different schema. This allows for easy access to the clean views without having to worry about naming conflicts with the raw data tables.

## Installation

The program is written in C# and compiles to a single executable file. The program can be run on any platform that supports .NET 10.0 or later. The program can be installed by downloading the latest release from the GitHub repository and extracting the files to a directory of your choice.

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

This command is equivalent to the following commands:

```bash
mkdir data
mkdir output
mkdir contracts
bronze-data-loader new config --output-folder .
bronze-data-loader new manifest --output-folder .
bronze-data-loader new contract --output-folder contracts
```

Edit the "config.yaml" file in your favorite editor. All file and folder paths are interpreted as relative to the config.yaml file. Absolute paths are also allowed. It will look like this:

```yaml
manifest_path: "manifest.csv"     # relative to config.yaml
data_folder: "data"               # relative to config.yaml
contracts_folder: "contracts"     # relative to config.yaml
output_folder: "output"           # relative to config.yaml
database_name: "warehouse.duckdb" # relative to output_folder
keep: false                       # keep existing database (default: false)
```

The values in "manifest.csv" determine which data files are imported and which contract is applied to each data file. Edit the "manifest.csv" file in your favorite text editor or spreadsheet program. It has the following structure:

| submitter  | source_folder | file_pattern  | contract            |
| ---------- | ------------- | ------------- | ------------------- |
| Acme, Inc. | Acme/data-in  | customer*.csv | customer_table.yaml |
| Beta, LLC  | Beta/data-in  | customer*.tsv | customer_table.yaml |

Source files can be defined as file patterns with wildcards (?, *). Tables for source files are named as follows: `[contract.table]_[submitter]_[file_stem_hash]`. The source folder is relative to the `data_folder` defined in config.yaml. The contract is a YAML file that defines the canonical structure of the data file. The contract file is located in the `contracts_folder` defined in config.yaml.

Only the following data file types are supported. The file extension is used to determine how to read the file. Note that support for .xlsx import supports only the first sheet in the workbook. The first row of the sheet is assumed to be the header row.

| Extension | Reader                          |
| --------- | ------------------------------- |
| `.csv`    | `read_csv()`                    |
| `.tsv`    | `read_csv()` with tab delimiter |
| `.xlsx`   | `read_xlsx()` with header       |

Raw tables are always imported as all varchar columns. The contract defines the canonical column names and the allowed column names for each column. The contract also defines which columns are required and which columns are optional. The program will validate the source file against the contract and create a view that conforms to the contract. If the source file does not conform to the contract, a view is created in the `bronze_quarantine` schema with the same name as the raw table.

A contract is defined in a YAML file that looks like this:

```yaml
table: customer              # Destination table
schema:
  staging: bronze_raw        # Schema for all source tables to be loaded to
  valid: bronze              # Schema for views that conform to the contract
  invalid: bronze_quarantine # Schema for views that do not conform to the contract
columns:                     # Column definitions
  - canonical: customer_id
    accepts: [customer_id, cust_id, "Customer ID", customerid]
    required: true
  - canonical: signup_date
    accepts: [signup_date, sign_up_date, "Signup Date", "Sign-up Date"]
    required: true
  - canonical: email
    accepts: [email, E-mail, email_address]
    required: false
```

### Execution

After setting up the configuration files, execute the program like this:

```bash
bronze-data-loader load example/config.yaml
```

The program will output a DuckDB database that contains the following:

1. Tables for all matching source files in the `bronze_raw` schema.
2. Views for all source files that conform to their contracts in the `bronze` schema.
3. Views for all source files that do not conform to their contracts in the `bronze_quarantine` schema.
4. Combined views in the `bronze` schema that union together all per-file views belonging to the same contract (see [combined views](#combined-views) below).
5. Tracking tables and views in the `metadata` schema (see below).

### Keeping or Replacing an Existing Database

By default, `bronze-data-loader load` **deletes the existing DuckDB database file** before loading fresh data. This ensures a clean slate on every run.

```bash
bronze-data-loader load example/config.yaml          # delete existing DB, fresh start
```

#### `--keep`

Preserve the existing database and load into it (raw tables are replaced, previously loaded tables remain):

```bash
bronze-data-loader load example/config.yaml --keep
```

You can set `keep: true` in `config.yaml` to make this the project-level default:

```yaml
manifest_path: "manifest.csv"
data_folder: "data"
contracts_folder: "contracts"
output_folder: "output"
database_name: "warehouse.duckdb"
keep: true
```

#### `--drop`

Force a fresh start by deleting the existing database, even if the config says `keep: true`:

```bash
bronze-data-loader load example/config.yaml --drop
```

This overrides both `--keep` and the config file's `keep` setting, ensuring a clean slate for that single run.

| Config `keep`      | CLI                                 | Result                   |
| :----------------- | :---------------------------------- | :----------------------- |
| *(absent / false)* | *(none)*                            | Delete existing database |
| *(absent / false)* | `--keep`                            | Keep existing database   |
| `true`             | *(none)*                            | Keep existing database   |
| `true`             | `--drop`                            | Delete existing database |
| any                | `--drop` (with or without `--keep`) | Delete existing database |

The database is never deleted when using `:memory:` as `database_name`.

## Combined Views

After all source files are processed, the program automatically creates **combined views** in the `bronze` schema that union together all per-file bronze views belonging to the same contract. This lets you query all data for a contract — across all submitters and source files — from a single view.

For example, if the manifest references the `customer_table.yaml` contract and two submitters each send data, the per-file views might be:

```
bronze.customer_Acme__Inc_abc12345
bronze.customer_Beta_LLC_def67890
```

The program then creates a combined view:

```sql
CREATE OR REPLACE VIEW "bronze"."customer" AS
SELECT * FROM "bronze"."customer_Acme__Inc_abc12345"
UNION ALL BY NAME
SELECT * FROM "bronze"."customer_Beta_LLC_def67890";
```

You can query it directly like any other view:

```sql
SELECT customer_id, signup_date, email
FROM bronze.customer
ORDER BY customer_id;
```

Combined views are always named `bronze.<contract_table>` — the contract table name without any suffix. Only contracts that have at least one successfully loaded bronze view will have a combined view created.

### Interaction with `union_view_query_sql` macro

The built-in `union_view_query_sql(source_schema, table_prefix)` macro generates a `UNION ALL BY NAME` SQL string across all views in a schema matching a prefix. The macro intentionally excludes the combined view itself (exact name match) to prevent recursive or duplicate unions. To union only the per-file sub-views, use:

```sql
SELECT union_view_query_sql('bronze', 'customer');
```

This will include `customer_Acme__Inc_abc12345` and `customer_Beta_LLC_def67890` but **not** the combined view `customer`.

## Metadata Schema

The `metadata` schema tracks every import operation and provides visibility into load failures.

### `metadata.table_load`

Records each raw table import, one row per source file. A row is inserted **before** the load begins (with `row_count = NULL`) and updated with the actual count **after** a successful load. If the load fails, the row stays with `NULL`, marking a failed attempt.

| Column         | Type        | Description                                                     |
| -------------- | ----------- | --------------------------------------------------------------- |
| `table_schema` | `VARCHAR`   | The schema containing the imported table (e.g. `bronze_raw`)    |
| `table_name`   | `VARCHAR`   | The sanitized unique table name                                 |
| `file_name`    | `VARCHAR`   | The source file name only (no path)                             |
| `file_path`    | `VARCHAR`   | The full path to the source file                                |
| `submitter`    | `VARCHAR`   | The submitter/owner name from the manifest                      |
| `imported_at`  | `TIMESTAMP` | When the import was attempted (defaults to `CURRENT_TIMESTAMP`) |
| `row_count`    | `BIGINT`    | Number of rows loaded, or `NULL` if the load failed             |

### Metadata Columns in Loaded Tables

Every raw table created in the `bronze_raw` schema includes four metadata columns that are prepended before the source data columns during import. These columns make each row self-describing and provide lineage information that flows through to the bronze and bronze_quarantine views.

| Column                  | Type      | Description                                                                                  |
| ----------------------- | --------- | -------------------------------------------------------------------------------------------- |
| `metadata_row_number`   | `BIGINT`  | Row position within the source file, starting at 1 for the first data row. Assigned via `ROW_NUMBER() OVER ()` during import, so it reflects the source file's row order. |
| `metadata_file_name`    | `VARCHAR` | The source file name only (extracted from the full path using a DuckDB macro). Does not include directory components. |
| `metadata_submitter`    | `VARCHAR` | The submitter/owner name from the manifest. Preserved as a column rather than relying on the table name, so it survives joins and downstream processing. |
| `metadata_loaded_at`    | `TIMESTAMP` | Timestamp when the row was loaded into the raw table. Defaults to `CURRENT_TIMESTAMP`.       |

**How the metadata columns are populated:**

```sql
CREATE OR REPLACE TABLE bronze_raw.<table_name> AS
SELECT
  row_number() OVER ()               AS metadata_row_number,
  metadata_filename('<file_path>')   AS metadata_file_name,
  '<submitter>'                      AS metadata_submitter,
  CURRENT_TIMESTAMP                  AS metadata_loaded_at,
  *
FROM read_csv('<file_path>', header=true, all_varchar=true);
```

The `metadata_filename` DuckDB macro extracts just the file name from a full file path using the regex pattern `[^/\\]+$`, which handles both Unix (forward slash) and Windows (backslash) path separators.

**Metadata columns in bronze views:**

When a source file conforms to its contract, the bronze view automatically includes these metadata columns alongside the contract-defined columns. This means consumers querying the `bronze` schema can see provenance information without needing to join against metadata tables:

```sql
-- Example: bronze view includes both contract and metadata columns
SELECT customer_id, email, metadata_file_name, metadata_submitter
FROM bronze.customer_Acme_abc12345;
```

**Metadata columns in quarantine views:**

The `bronze_quarantine` view uses `SELECT * FROM bronze_raw.<table>`, so metadata columns are included automatically when a file fails contract validation. This aids debugging by showing which rows came from which file and their original positions.

**No special handling needed for queries —** the `metadata_` prefix clearly separates operational metadata from business data. Use `SELECT * EXCLUDE (metadata_row_number, metadata_file_name, metadata_submitter, metadata_loaded_at)` to retrieve only the source data columns.

### `metadata.quarantine`

Records error messages for source files that failed contract validation. Each row describes why a file was quarantined.

| Column           | Type        | Description                                                    |
| ---------------- | ----------- | -------------------------------------------------------------- |
| `table_name`     | `VARCHAR`   | The fully-qualified raw table name                             |
| `error_message`  | `VARCHAR`   | The validation error that triggered quarantine                 |
| `quarantined_at` | `TIMESTAMP` | When the quarantine occurred (defaults to `CURRENT_TIMESTAMP`) |

### `metadata.v_failed_loads`

A view that selects rows from `metadata.table_load` where `row_count IS NULL`. Query it to list every file that was attempted but never successfully loaded:

```sql
SELECT * FROM metadata.v_failed_loads;
```
