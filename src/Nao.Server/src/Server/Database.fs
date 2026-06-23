namespace Nao.Assistant

open System
open System.IO
open System.Net.WebSockets
open System.Net.Sockets
open System.Data.Common
open System.Text
open System.Text.RegularExpressions
open System.Text.Json
open System.Threading
open System.Threading.Tasks
open Microsoft.AspNetCore.Builder
open Microsoft.AspNetCore.Hosting
open Microsoft.AspNetCore.Http
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.Hosting
open Microsoft.Extensions.Logging
open Orleans
open Orleans.Configuration
open Orleans.Hosting
open Nao.Core
open Nao.Agents
open Nao.Loader
open Nao.Providers
open Nao.Persistence
open Nao.Feedback
open Nao.Runtime.Orleans
open Nao.Runtime.Orleans.Grains

module Database =


    let dataDir =
        // Default to a folder under the current working directory so the app's data
        // (SQLite db, conversations, observability) lands in the repo and can be
        // inspected. Override with NAO_DATA_DIR to point elsewhere.
        let dir =
            match Environment.GetEnvironmentVariable("NAO_DATA_DIR") with
            | path when not (String.IsNullOrWhiteSpace path) -> path
            | _ -> Path.Combine(Environment.CurrentDirectory, ".nao-data")
        Directory.CreateDirectory(dir) |> ignore
        dir

    let dbPath = Path.Combine(dataDir, "nao.db")
    let connectionString = sprintf "Data Source=%s;" dbPath

    let initialize () =
        DbProviderFactories.RegisterFactory("System.Data.SQLite", Microsoft.Data.Sqlite.SqliteFactory.Instance)

        use conn = new Microsoft.Data.Sqlite.SqliteConnection(connectionString)
        conn.Open()
        use cmd = conn.CreateCommand()
        cmd.CommandText <- """
            CREATE TABLE IF NOT EXISTS OrleansQuery (
                QueryKey TEXT NOT NULL,
                QueryText TEXT NOT NULL,
                CONSTRAINT OrleansQuery_Key PRIMARY KEY (QueryKey)
            );

            CREATE TABLE IF NOT EXISTS OrleansStorage (
                GrainIdHash INT NOT NULL,
                GrainIdN0 BIGINT NOT NULL,
                GrainIdN1 BIGINT NOT NULL,
                GrainTypeHash INT NOT NULL,
                GrainTypeString NVARCHAR(512) NOT NULL,
                GrainIdExtensionString NVARCHAR(512) NULL,
                ServiceId NVARCHAR(150) NOT NULL,
                PayloadBinary BLOB NULL,
                ModifiedOn DATETIME NOT NULL,
                Version INT NULL
            );

            CREATE INDEX IF NOT EXISTS IX_OrleansStorage ON OrleansStorage(GrainIdHash, GrainTypeHash);

            INSERT OR IGNORE INTO OrleansQuery (QueryKey, QueryText) VALUES
            ('WriteToStorageKey', '
                BEGIN TRANSACTION;

                CREATE TEMP TABLE IF NOT EXISTS OrleansStorageWriteState
                (
                    TotalChangesBefore INT NOT NULL
                );
                DELETE FROM OrleansStorageWriteState;
                INSERT INTO OrleansStorageWriteState (TotalChangesBefore) VALUES (total_changes() + 1);

                UPDATE OrleansStorage
                SET
                    PayloadBinary = @PayloadBinary,
                    ModifiedOn = datetime(''now''),
                    Version = Version + 1
                WHERE
                    GrainIdHash = @GrainIdHash AND GrainTypeHash = @GrainTypeHash
                    AND GrainIdN0 = @GrainIdN0 AND GrainIdN1 = @GrainIdN1
                    AND GrainTypeString = @GrainTypeString
                    AND (GrainIdExtensionString = @GrainIdExtensionString OR (GrainIdExtensionString IS NULL AND @GrainIdExtensionString IS NULL))
                    AND ServiceId = @ServiceId
                    AND Version = @GrainStateVersion;

                INSERT INTO OrleansStorage (GrainIdHash, GrainIdN0, GrainIdN1, GrainTypeHash, GrainTypeString, GrainIdExtensionString, ServiceId, PayloadBinary, ModifiedOn, Version)
                SELECT @GrainIdHash, @GrainIdN0, @GrainIdN1, @GrainTypeHash, @GrainTypeString, @GrainIdExtensionString, @ServiceId, @PayloadBinary, datetime(''now''), 1
                WHERE changes() = 0
                  AND @GrainStateVersion IS NULL
                  AND NOT EXISTS (
                    SELECT 1 FROM OrleansStorage
                    WHERE GrainIdHash = @GrainIdHash AND GrainTypeHash = @GrainTypeHash
                        AND GrainIdN0 = @GrainIdN0 AND GrainIdN1 = @GrainIdN1
                        AND GrainTypeString = @GrainTypeString
                        AND (GrainIdExtensionString = @GrainIdExtensionString OR (GrainIdExtensionString IS NULL AND @GrainIdExtensionString IS NULL))
                        AND ServiceId = @ServiceId
                  );

                SELECT Version AS NewGrainStateVersion FROM OrleansStorage
                WHERE total_changes() > (SELECT TotalChangesBefore FROM OrleansStorageWriteState LIMIT 1)
                    AND GrainIdHash = @GrainIdHash AND GrainTypeHash = @GrainTypeHash
                    AND GrainIdN0 = @GrainIdN0 AND GrainIdN1 = @GrainIdN1
                    AND GrainTypeString = @GrainTypeString
                    AND (GrainIdExtensionString = @GrainIdExtensionString OR (GrainIdExtensionString IS NULL AND @GrainIdExtensionString IS NULL))
                    AND ServiceId = @ServiceId;

                SELECT @GrainStateVersion AS NewGrainStateVersion
                WHERE total_changes() = (SELECT TotalChangesBefore FROM OrleansStorageWriteState LIMIT 1)
                    AND @GrainStateVersion IS NOT NULL;

                COMMIT;
            ');

            INSERT OR IGNORE INTO OrleansQuery (QueryKey, QueryText) VALUES
            ('ReadFromStorageKey', '
                SELECT
                    PayloadBinary,
                    Version AS Version
                FROM
                    OrleansStorage
                WHERE
                    GrainIdHash = @GrainIdHash AND GrainTypeHash = @GrainTypeHash
                    AND GrainIdN0 = @GrainIdN0 AND GrainIdN1 = @GrainIdN1
                    AND GrainTypeString = @GrainTypeString
                    AND (GrainIdExtensionString = @GrainIdExtensionString OR (GrainIdExtensionString IS NULL AND @GrainIdExtensionString IS NULL))
                    AND ServiceId = @ServiceId
                LIMIT 1;
            ');

            INSERT OR IGNORE INTO OrleansQuery (QueryKey, QueryText) VALUES
            ('ClearStorageKey', '
                UPDATE OrleansStorage
                SET
                    PayloadBinary = NULL,
                    ModifiedOn = datetime(''now''),
                    Version = Version + 1
                WHERE
                    GrainIdHash = @GrainIdHash AND GrainTypeHash = @GrainTypeHash
                    AND GrainIdN0 = @GrainIdN0 AND GrainIdN1 = @GrainIdN1
                    AND GrainTypeString = @GrainTypeString
                    AND (GrainIdExtensionString = @GrainIdExtensionString OR (GrainIdExtensionString IS NULL AND @GrainIdExtensionString IS NULL))
                    AND ServiceId = @ServiceId
                    AND Version = @GrainStateVersion;

                SELECT Version AS NewGrainStateVersion FROM OrleansStorage
                WHERE changes() > 0
                    AND GrainIdHash = @GrainIdHash AND GrainTypeHash = @GrainTypeHash
                    AND GrainIdN0 = @GrainIdN0 AND GrainIdN1 = @GrainIdN1
                    AND GrainTypeString = @GrainTypeString
                    AND (GrainIdExtensionString = @GrainIdExtensionString OR (GrainIdExtensionString IS NULL AND @GrainIdExtensionString IS NULL))
                    AND ServiceId = @ServiceId;

                SELECT @GrainStateVersion AS NewGrainStateVersion
                WHERE changes() = 0
                    AND @GrainStateVersion IS NOT NULL;
            ');
        """
        cmd.ExecuteNonQuery() |> ignore


