namespace Nao.DeveloperTools

open System
open System.IO
open System.Threading
open System.Threading.Tasks
open Nao.Platform
open Nao.Runtime.Orleans

[<RequireQualifiedAccess>]
module DeveloperCommandLine =
    let private usage =
        "Commands: scaffold, extension test|package, config validate, adapter diagnose, dataset lint|evaluate, trace inspect, replay inspect, knowledge status, index diagnostics, migration plan|dry-run."

    let private required command name =
        function
        | value when String.IsNullOrWhiteSpace value ->
            Error(DeveloperCommandResult.failure command "missing_argument" $"Missing required argument '{name}'.")
        | value -> Ok value

    let private argument index (arguments: string array) =
        if index < arguments.Length then arguments[index] else ""

    let private localException command operation =
        try
            operation ()
        with error ->
            DeveloperCommandResult.failure command "command_failed" error.Message

    let runAsync services arguments cancellationToken =
        task {
            match arguments |> Array.toList with
            | [ "scaffold"; template; name; output ] ->
                return
                    localException "scaffold" (fun () ->
                        let template = ExtensionTemplate.parse template
                        let root, files = ExtensionTemplate.create output template name None

                        DeveloperCommandResult.success
                            "scaffold"
                            (Map
                                [ "root", root
                                  "template", string template
                                  "projectCount", "2"
                                  "fileCount", string files.Length ]))
            | [ "scaffold"; template; name; output; contractProject ] ->
                return
                    localException "scaffold" (fun () ->
                        let template = ExtensionTemplate.parse template

                        let root, files =
                            ExtensionTemplate.create output template name (Some contractProject)

                        DeveloperCommandResult.success
                            "scaffold"
                            (Map
                                [ "root", root
                                  "template", string template
                                  "contractProject", Path.GetFullPath contractProject
                                  "projectCount", "2"
                                  "fileCount", string files.Length ]))
            | [ "extension"; verb; project ] when verb = "test" || verb = "package" ->
                let output =
                    if verb = "package" then
                        Path.Combine(Path.GetDirectoryName(Path.GetFullPath project), "artifacts")
                    else
                        ""

                try
                    let! workflow = ExtensionWorkflow.runAsync verb project output cancellationToken

                    let issues =
                        if workflow.ExitCode = 0 then
                            []
                        else
                            [ DeveloperIssue.error
                                  "extension_workflow_failed"
                                  (Some project)
                                  (if String.IsNullOrWhiteSpace workflow.StandardError then
                                       workflow.StandardOutput
                                   else
                                       workflow.StandardError) ]

                    return
                        DeveloperCommandResult.create
                            $"extension.{verb}"
                            (Map
                                [ "project", Path.GetFullPath project
                                  "exitCode", string workflow.ExitCode
                                  "output", workflow.StandardOutput.Trim() ])
                            issues
                with error ->
                    return DeveloperCommandResult.failure $"extension.{verb}" "extension_workflow_failed" error.Message
            | [ "config"; "validate"; path ] -> return ConfigurationDiagnostics.validateFile path
            | [ "adapter"; "diagnose"; path ] -> return AdapterDiagnostics.inspect path
            | [ "dataset"; "lint"; path ] -> return DatasetDiagnostics.inspect false path
            | [ "dataset"; "evaluate"; path ] -> return DatasetDiagnostics.inspect true path
            | [ "trace"; "inspect"; path ] -> return TraceReplayDiagnostics.inspect false path
            | [ "replay"; "inspect"; path ] -> return TraceReplayDiagnostics.inspect true path
            | [ "knowledge"; "status"; resourceId ] ->
                return!
                    OperationalDiagnostics.resourceAsync
                        "knowledge.status"
                        AdministrativeResourceKind.Knowledge
                        $"ingestion:{resourceId}"
                        services
                        cancellationToken
            | [ "index"; "diagnostics"; resourceId ] ->
                return!
                    OperationalDiagnostics.resourceAsync
                        "index.diagnostics"
                        AdministrativeResourceKind.Knowledge
                        $"index:{resourceId}"
                        services
                        cancellationToken
            | [ "migration"; "plan"; path ] ->
                return
                    localException "migration.plan" (fun () ->
                        let manifest = MigrationDiagnostics.load path
                        let plan = MigrationDiagnostics.plan manifest

                        DeveloperCommandResult.success
                            "migration.plan"
                            (Map
                                [ "migrationId", MigrationPlan.id plan
                                  "planDigest", MigrationPlan.digest plan
                                  "sourceRevision", manifest.SourceRevision
                                  "targetRevision", manifest.TargetRevision
                                  "stepCount", string manifest.Steps.Length ]))
            | [ "migration"; "dry-run"; path ] ->
                try
                    let manifest = MigrationDiagnostics.load path
                    let plan = MigrationDiagnostics.plan manifest
                    return! OperationalDiagnostics.migrationDryRunAsync manifest plan services cancellationToken
                with error ->
                    return DeveloperCommandResult.failure "migration.dry-run" "migration_manifest_invalid" error.Message
            | _ -> return DeveloperCommandResult.failure "help" "invalid_command" usage
        }
