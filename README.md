# Bronze Layer Data Loader

## Purpose

1. Load data files into the bronze_raw_data schema in a database.
2. Create clean views for each table that matches the table contract in the bronze schema.
3. Create views for each table that does not match the table contact in the bronze_quarantine schema.

## Description

This program was created with a certain type of data project in mind: the data call. In a data call, one or more submitters are asked to provide data files that conform to a specific contract. The submitters may not be familiar with the database or the contract, and they may not have the ability to validate their data files against the contract. This program is designed to help with this process by loading the data files into a database and creating views that conform to the contract. Files that do not conform to the contract are placed in a quarantine schema for further investigation. This program is designed to be run from the command line, and it is configured by a set of files that define the source files, the contracts, and the database connection.

## Database

The database used in this program is DuckDB, which is an in-process SQL OLAP database management system. DuckDB is designed to support analytical query workloads and is optimized for fast query execution on large datasets. It is a self-contained database that does not require a separate server process, making it easy to use and deploy.

Raw tables are created in the database for each source file that is loaded. The raw tables are created in a separate schema from the clean views, allowing for easy separation of the raw data and the clean views. The raw tables are named with a unique name that includes the contract name, submitter name, and a hash of the file stem. This allows for identification of the source file that was used to create the raw table without creating file names that are extremely long.

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
contracts_folder: "contracts" # default "." for current working directory
output_folder: "output"       # default "." for current working directory
database_path: "database"     # default "." for current working directory
```

The values in "manifest.csv" drive what data files are imported and what contract is applied to each data file. Edit the "manifest.csv" file in your favorite text editor or spreadsheet program. It has the following structure:

| submitter  | source_folder | file_pattern  | contract               |
| ---------- | ------------- | ------------- | ---------------------- |
| Acme, Inc. | Acme/data-in  | customer*.csv | contract_customer.yaml |
| Beta, LLC  | Beta/data-in  | customer*.tsv | contract_customer.yaml |

Source files can be defined as file patterns with wildcards (?, *). Tables for source files are named as follows: `[contract.table]_[submitter]_[file_stem_hash]`.

Only the following data file types are supported. The file extension is used to determine how to read the file.

| Extension | Reader                            |
| --------- | --------------------------------- |
| `.csv`    | `read_csv()` with `all_varchar`   |
| `.tsv`    | `read_csv()` with tab delimiter   |
| `.xlsx`   | `read_xlsx()` with header         |

A contract defines the field listing for the data file, its destination table name, and the destination table's schema in the database. The final destination table name is defined in the contract file, and the submitter and file stem hash are appended to the table name to create a unique table name for each source file.

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
    type: VARCHAR
    required: true
  - canonical: signup_date
    accepts: [signup_date, sign_up_date, "Signup Date", "Sign-up Date"]
    type: DATE
    required: true
  - canonical: email
    accepts: [email, E-mail, email_address]
    type: VARCHAR
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
4. Tracking tables and views in the `metadata` schema (see below).

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
| `imported_at`  | `TIMESTAMP` | When the import was attempted (defaults to `CURRENT_TIMESTAMP`) |
| `row_count`    | `BIGINT`    | Number of rows loaded, or `NULL` if the load failed             |

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
