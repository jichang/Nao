namespace Nao.Assistant

open System
open System.IO
open Microsoft.Data.Sqlite
open Nao.Agents

/// SQLite-backed persistence for resource permission rules. Rules are scoped either to a
/// single session (the default when a user grants access for one conversation) or globally
/// (the user chose to remember the grant for every session). Lives in the same nao.db as
/// the rest of the app's data; the table is created lazily so this module does not depend
/// on Database.initialize ordering.
module PermissionStore =

    let private connectionString =
        let dataDir =
            match Environment.GetEnvironmentVariable("NAO_DATA_DIR") with
            | path when not (String.IsNullOrWhiteSpace path) -> path
            | _ -> Path.Combine(Environment.CurrentDirectory, ".nao-data")
        Directory.CreateDirectory dataDir |> ignore
        sprintf "Data Source=%s;" (Path.Combine(dataDir, "nao.db"))

    let private gate = obj ()
    let mutable private ensured = false

    let private ensureTable () =
        if not ensured then
            lock gate (fun () ->
                if not ensured then
                    use conn = new SqliteConnection(connectionString)
                    conn.Open()
                    use cmd = conn.CreateCommand()
                    cmd.CommandText <-
                        """
                        CREATE TABLE IF NOT EXISTS PermissionRules (
                            Id           TEXT PRIMARY KEY,
                            Kind         TEXT NOT NULL,   -- 'file' | 'web' | 'tool'
                            Pattern      TEXT NOT NULL,
                            Operations   TEXT NOT NULL,   -- comma-separated, '' = any
                            Decision     TEXT NOT NULL,   -- 'allow' | 'deny' | 'ask'
                            ScopeKind    TEXT NOT NULL,   -- 'global' | 'session'
                            ScopeSession TEXT,            -- session key when ScopeKind='session'
                            CreatedAt    TEXT NOT NULL
                        );
                        CREATE INDEX IF NOT EXISTS IX_PermissionRules_Scope
                            ON PermissionRules(ScopeKind, ScopeSession);
                        """
                    cmd.ExecuteNonQuery() |> ignore
                    ensured <- true)

    // ─── (de)serialization between the domain type and text columns ───

    let private kindToText =
        function
        | ResourceKind.File -> "file"
        | ResourceKind.Web -> "web"
        | ResourceKind.Tool -> "tool"

    let private kindOfText =
        function
        | "web" -> ResourceKind.Web
        | "tool" -> ResourceKind.Tool
        | _ -> ResourceKind.File

    let private decisionToText =
        function
        | PermissionDecision.Allow -> "allow"
        | PermissionDecision.Deny -> "deny"
        | PermissionDecision.Ask -> "ask"

    let private decisionOfText =
        function
        | "deny" -> PermissionDecision.Deny
        | "ask" -> PermissionDecision.Ask
        | _ -> PermissionDecision.Allow

    let private opsToText (ops: string list) = String.Join(",", ops)

    let private opsOfText (s: string) =
        if String.IsNullOrWhiteSpace s then []
        else s.Split(',') |> Array.map (fun x -> x.Trim()) |> Array.filter (fun x -> x <> "") |> Array.toList

    let private readRule (r: SqliteDataReader) : PermissionRule =
        let scopeKind = r.GetString(5)
        let scope =
            if scopeKind = "session" then
                RuleScope.Session(if r.IsDBNull(6) then "" else r.GetString(6))
            else
                RuleScope.Global
        { Id = r.GetString(0)
          Kind = kindOfText (r.GetString(1))
          Pattern = r.GetString(2)
          Operations = opsOfText (r.GetString(3))
          Decision = decisionOfText (r.GetString(4))
          Scope = scope
          CreatedAt = DateTimeOffset.Parse(r.GetString(7)) }

    // ─── public API ───

    /// All rules, newest first.
    let list () : PermissionRule list =
        ensureTable ()
        use conn = new SqliteConnection(connectionString)
        conn.Open()
        use cmd = conn.CreateCommand()
        cmd.CommandText <-
            "SELECT Id, Kind, Pattern, Operations, Decision, ScopeKind, ScopeSession, CreatedAt \
             FROM PermissionRules ORDER BY CreatedAt DESC"
        use reader = cmd.ExecuteReader()
        [ while reader.Read() do
              yield readRule reader ]

    /// Cross-session ("global") rules only. Per-session grants are owned by the SessionGrain
    /// in its own persisted state, so the store contributes only global rules to a session's
    /// effective rule set; this is the single source of truth for cross-session permissions.
    let globalRules () : PermissionRule list =
        list ()
        |> List.filter (fun r ->
            match r.Scope with
            | RuleScope.Global -> true
            | _ -> false)

    /// Persist a rule. Returns the stored rule (with its assigned Id/CreatedAt when blank).
    let grant (rule: PermissionRule) : PermissionRule =
        ensureTable ()
        let stored =
            { rule with
                Id = (if String.IsNullOrWhiteSpace rule.Id then Guid.NewGuid().ToString("N") else rule.Id)
                CreatedAt = (if rule.CreatedAt = Unchecked.defaultof<DateTimeOffset> then DateTimeOffset.UtcNow else rule.CreatedAt) }
        let scopeKind, scopeSession =
            match stored.Scope with
            | RuleScope.Global -> "global", None
            | RuleScope.Session k -> "session", Some k
        lock gate (fun () ->
            use conn = new SqliteConnection(connectionString)
            conn.Open()
            use cmd = conn.CreateCommand()
            cmd.CommandText <-
                "INSERT OR REPLACE INTO PermissionRules \
                 (Id, Kind, Pattern, Operations, Decision, ScopeKind, ScopeSession, CreatedAt) \
                 VALUES (@id, @kind, @pattern, @ops, @decision, @scopeKind, @scopeSession, @createdAt)"
            cmd.Parameters.AddWithValue("@id", stored.Id) |> ignore
            cmd.Parameters.AddWithValue("@kind", kindToText stored.Kind) |> ignore
            cmd.Parameters.AddWithValue("@pattern", stored.Pattern) |> ignore
            cmd.Parameters.AddWithValue("@ops", opsToText stored.Operations) |> ignore
            cmd.Parameters.AddWithValue("@decision", decisionToText stored.Decision) |> ignore
            cmd.Parameters.AddWithValue("@scopeKind", scopeKind) |> ignore
            cmd.Parameters.AddWithValue("@scopeSession", (match scopeSession with Some s -> box s | None -> box DBNull.Value)) |> ignore
            cmd.Parameters.AddWithValue("@createdAt", stored.CreatedAt.ToString("o")) |> ignore
            cmd.ExecuteNonQuery() |> ignore)
        stored

    /// Remove a rule by id. Returns true if a row was deleted.
    let revoke (id: string) : bool =
        ensureTable ()
        lock gate (fun () ->
            use conn = new SqliteConnection(connectionString)
            conn.Open()
            use cmd = conn.CreateCommand()
            cmd.CommandText <- "DELETE FROM PermissionRules WHERE Id = @id"
            cmd.Parameters.AddWithValue("@id", id) |> ignore
            cmd.ExecuteNonQuery() > 0)
