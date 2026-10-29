namespace Nao.DeveloperTools

open System
open System.Globalization
open System.IO
open System.Text.Json
open System.Threading
open System.Threading.Tasks
open Nao.Extensions
open Nao.Platform

[<RequireQualifiedAccess>]
module ConfigurationDiagnostics =
    let validateFile path =
        let fullPath = Path.GetFullPath path

        try
            let configuration = File.ReadAllText fullPath |> PlatformConfigurationJson.parse
            let failures = PlatformConfigurationValidator.validate configuration

            let issues =
                failures
                |> List.map (fun failure -> DeveloperIssue.error failure.Code (Some failure.Field) failure.Detail)

            DeveloperCommandResult.create
                "config.validate"
                (Map
                    [ "path", fullPath
                      "profile", string (PlatformConfiguration.profile configuration)
                      "instanceId", PlatformConfiguration.instanceId configuration ])
                issues
        with exceptionValue ->
            DeveloperCommandResult.failure "config.validate" "configuration_invalid" exceptionValue.Message

[<RequireQualifiedAccess>]
module DatasetDiagnostics =
    let private requiredString (name: string) (element: JsonElement) =
        let mutable value = Unchecked.defaultof<JsonElement>

        if
            not (element.TryGetProperty(name, &value))
            || value.ValueKind <> JsonValueKind.String
        then
            invalidArg name $"Dataset case requires string property '{name}'."

        let text = value.GetString()

        if String.IsNullOrWhiteSpace text || text <> text.Trim() then
            invalidArg name $"Dataset property '{name}' must be canonical and non-blank."

        text

    let inspect evaluate path =
        let command = if evaluate then "dataset.evaluate" else "dataset.lint"
        let fullPath = Path.GetFullPath path

        try
            use document = JsonDocument.Parse(File.ReadAllText fullPath)
            let root = document.RootElement
            let schemaVersion = root.GetProperty("schemaVersion").GetInt32()

            if schemaVersion <> 1 then
                invalidArg (nameof path) "Unsupported local evaluation dataset schema version."

            let cases = root.GetProperty("cases")

            if cases.ValueKind <> JsonValueKind.Array then
                invalidArg (nameof path) "Dataset cases must be an array."

            let parsed =
                cases.EnumerateArray()
                |> Seq.map (fun caseValue ->
                    let id = requiredString "id" caseValue
                    let input = requiredString "input" caseValue
                    let expected = requiredString "expected" caseValue
                    let mutable actual = Unchecked.defaultof<JsonElement>

                    let actual =
                        if
                            caseValue.TryGetProperty("actual", &actual)
                            && actual.ValueKind = JsonValueKind.String
                        then
                            Some(actual.GetString())
                        else
                            None

                    id, input, expected, actual)
                |> Seq.toList

            let duplicateIds =
                parsed
                |> List.countBy (fun (id, _, _, _) -> id)
                |> List.choose (fun (id, count) -> if count > 1 then Some id else None)

            let mutable issues =
                duplicateIds
                |> List.map (fun id ->
                    DeveloperIssue.error "duplicate_case_id" (Some id) "Dataset case identities must be unique.")

            let passed =
                if evaluate then
                    parsed
                    |> List.sumBy (fun (id, _, expected, actual) ->
                        match actual with
                        | None ->
                            issues <-
                                DeveloperIssue.error
                                    "missing_actual_output"
                                    (Some id)
                                    "Local evaluation requires an actual output for every case."
                                :: issues

                            0
                        | Some value when value = expected -> 1
                        | Some _ ->
                            issues <-
                                DeveloperIssue.error
                                    "exact_match_failed"
                                    (Some id)
                                    "Actual output does not exactly match the expected output."
                                :: issues

                            0)
                else
                    0

            DeveloperCommandResult.create
                command
                (Map
                    [ "path", fullPath
                      "caseCount", string parsed.Length
                      "passedCount", string passed
                      "failedCount", string (if evaluate then parsed.Length - passed else issues.Length) ])
                (List.rev issues)
        with exceptionValue ->
            DeveloperCommandResult.failure command "dataset_invalid" exceptionValue.Message

[<RequireQualifiedAccess>]
module TraceReplayDiagnostics =
    let inspect replay path =
        let command = if replay then "replay.inspect" else "trace.inspect"
        let fullPath = Path.GetFullPath path

        try
            use document = JsonDocument.Parse(File.ReadAllText fullPath)
            let root = document.RootElement

            if root.GetProperty("schemaVersion").GetInt32() <> 1 then
                invalidArg (nameof path) "Unsupported diagnostic export schema version."

            let arrayName = if replay then "fixtures" else "traces"
            let values = root.GetProperty(arrayName)

            if values.ValueKind <> JsonValueKind.Array then
                invalidArg (nameof path) $"Diagnostic property '{arrayName}' must be an array."

            let mutable issues = []
            let mutable successful = 0
            let mutable duration = 0L

            for value in values.EnumerateArray() do
                let id = value.GetProperty("id").GetString()

                if replay then
                    let replayable = value.GetProperty("replayable").GetBoolean()

                    if replayable then
                        successful <- successful + 1
                    else
                        issues <-
                            DeveloperIssue.warning
                                "non_replayable_fixture"
                                (Some id)
                                "The recorded dependency cannot be replayed deterministically."
                            :: issues
                else
                    if value.GetProperty("success").GetBoolean() then
                        successful <- successful + 1

                    let currentDuration = value.GetProperty("durationMs").GetInt64()

                    if currentDuration < 0L then
                        issues <-
                            DeveloperIssue.error
                                "negative_trace_duration"
                                (Some id)
                                "Trace duration cannot be negative."
                            :: issues
                    else
                        duration <- duration + currentDuration

            DeveloperCommandResult.create
                command
                (Map
                    [ "path", fullPath
                      "recordCount", string (values.GetArrayLength())
                      "successfulCount", string successful
                      "totalDurationMs", string duration ])
                (List.rev issues)
        with exceptionValue ->
            DeveloperCommandResult.failure command "diagnostic_export_invalid" exceptionValue.Message

[<RequireQualifiedAccess>]
module AdapterDiagnostics =
    let private version (name: string) (root: JsonElement) =
        let value: string = root.GetProperty(name).GetString()

        match Version.TryParse value with
        | true, parsed -> parsed
        | false, _ -> invalidArg name $"Adapter diagnostic property '{name}' must be a semantic version."

    let private digestMap (name: string) (root: JsonElement) =
        let value = root.GetProperty(name)

        if value.ValueKind <> JsonValueKind.Object then
            invalidArg name $"Adapter diagnostic property '{name}' must be an object."

        value.EnumerateObject()
        |> Seq.map (fun (property: JsonProperty) ->
            let digest: string = property.Value.GetString()

            if
                String.IsNullOrWhiteSpace property.Name
                || isNull digest
                || digest.Length <> 64
                || digest
                   |> Seq.exists (fun character ->
                       not (Char.IsDigit character || character >= 'a' && character <= 'f'))
            then
                invalidArg name $"Adapter diagnostic property '{name}' contains an invalid dependency identity."

            property.Name, digest)
        |> Map.ofSeq

    let inspect path =
        let command = "adapter.diagnose"
        let fullPath = Path.GetFullPath path

        try
            use document = JsonDocument.Parse(File.ReadAllText fullPath)
            let root = document.RootElement

            if root.GetProperty("schemaVersion").GetInt32() <> 1 then
                invalidArg (nameof path) "Unsupported adapter diagnostic schema version."

            let compatibility =
                AdapterCompatibility.create
                    (version "sdkVersion" root)
                    (root.GetProperty("protocolVersion").GetInt32())
                    (version "minimumHostVersion" root)
                    (version "maximumHostVersion" root)
                    (root.GetProperty("targetFramework").GetString())

            let dependencies = digestMap "dependencies" root
            let approved = digestMap "approvedDependencies" root

            let compatibilityIssues =
                match AdapterHostCompatibility.validate AdapterHostCompatibility.current compatibility with
                | Ok() -> []
                | Error failure ->
                    [ DeveloperIssue.error
                          "compatibility_mismatch"
                          (Some "compatibility")
                          (AdapterFailure.message failure) ]

            let dependencyIssues =
                dependencies
                |> Map.toList
                |> List.choose (fun (name, digest) ->
                    if Map.tryFind name approved = Some digest then
                        None
                    else
                        Some(
                            DeveloperIssue.error
                                "dependency_rejected"
                                (Some name)
                                "Dependency name and SHA-256 identity are not approved."
                        ))

            DeveloperCommandResult.create
                command
                (Map
                    [ "path", fullPath
                      "dependencyCount", string dependencies.Count
                      "approvedDependencyCount", string approved.Count ])
                (compatibilityIssues @ dependencyIssues)
        with exceptionValue ->
            DeveloperCommandResult.failure command "adapter_diagnostic_invalid" exceptionValue.Message

type MigrationManifest =
    { Id: string
      SourceRevision: string
      TargetRevision: string
      VerifiedBackupId: string
      VerifiedAt: DateTimeOffset
      ActiveWriters: int
      AvailableBytes: int64
      RequiredBytes: int64
      Steps: string list }

[<RequireQualifiedAccess>]
module MigrationDiagnostics =
    let private requiredString (name: string) (root: JsonElement) =
        let value = root.GetProperty(name).GetString()

        if String.IsNullOrWhiteSpace value || value <> value.Trim() then
            invalidArg name $"Migration property '{name}' must be canonical and non-blank."

        value

    let load path =
        use document = JsonDocument.Parse(File.ReadAllText(Path.GetFullPath path))
        let root = document.RootElement

        if root.GetProperty("schemaVersion").GetInt32() <> 1 then
            invalidArg (nameof path) "Unsupported migration manifest schema version."

        { Id = requiredString "id" root
          SourceRevision = requiredString "sourceRevision" root
          TargetRevision = requiredString "targetRevision" root
          VerifiedBackupId = requiredString "verifiedBackupId" root
          VerifiedAt =
            DateTimeOffset.Parse(
                requiredString "verifiedAt" root,
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind
            )
          ActiveWriters = root.GetProperty("activeWriters").GetInt32()
          AvailableBytes = root.GetProperty("availableBytes").GetInt64()
          RequiredBytes = root.GetProperty("requiredBytes").GetInt64()
          Steps =
            root.GetProperty("steps").EnumerateArray()
            |> Seq.map (fun value -> value.GetString())
            |> Seq.toList }

    let plan manifest =
        let preflight =
            MigrationPreflight.create
                manifest.SourceRevision
                manifest.TargetRevision
                manifest.VerifiedBackupId
                manifest.VerifiedAt
                manifest.ActiveWriters
                manifest.AvailableBytes
                manifest.RequiredBytes

        let steps =
            manifest.Steps
            |> List.map (fun id -> MigrationStep.create id (fun _ -> Task.CompletedTask) (fun _ -> Task.CompletedTask))

        MigrationPlan.create manifest.Id preflight steps
