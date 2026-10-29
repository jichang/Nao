namespace Nao.DeveloperTools

open System
open System.Threading
open System.Threading.Tasks
open Nao.Platform
open Nao.Runtime.Orleans
open Nao.Runtime.Orleans.Grains

type DeveloperCommandServices =
    { Administration: RuntimeAdministrationClient option }

[<RequireQualifiedAccess>]
module DeveloperCommandServices =
    let localOnly = { Administration = None }

[<RequireQualifiedAccess>]
module OperationalDiagnostics =
    let private administration command (services: DeveloperCommandServices) =
        match services.Administration with
        | Some client -> Ok client
        | None ->
            Error(
                DeveloperCommandResult.failure
                    command
                    "authenticated_control_plane_required"
                    "This command requires a host-injected RuntimeAdministrationClient with an authenticated principal."
            )

    let resourceAsync
        command
        (kind: AdministrativeResourceKind)
        resourceId
        (services: DeveloperCommandServices)
        cancellationToken
        =
        task {
            match administration command services with
            | Error failure -> return failure
            | Ok client ->
                try
                    let! (snapshot: AdministrativeResourceSnapshot) =
                        client.GetResourceAsync(kind, resourceId, cancellationToken)

                    let data =
                        snapshot.Metadata
                        |> Seq.map (fun pair -> pair.Key, pair.Value)
                        |> Map.ofSeq
                        |> Map.add "resourceKind" snapshot.ResourceKind
                        |> Map.add "resourceId" snapshot.ResourceId
                        |> Map.add "revision" (string snapshot.Revision)
                        |> Map.add "disabled" (string snapshot.Disabled)
                        |> Map.add "updatedAt" (snapshot.UpdatedAt.ToString("O"))

                    return DeveloperCommandResult.success command data
                with
                | :? OperationCanceledException when cancellationToken.IsCancellationRequested ->
                    return! Task.FromCanceled<DeveloperCommandResult>(cancellationToken)
                | :? UnauthorizedAccessException as error ->
                    return DeveloperCommandResult.failure command "authorization_denied" error.Message
                | error -> return DeveloperCommandResult.failure command "control_plane_failure" error.Message
        }

    let migrationDryRunAsync
        (manifest: MigrationManifest)
        (plan: MigrationPlan)
        (services: DeveloperCommandServices)
        cancellationToken
        =
        task {
            let command = "migration.dry-run"

            match administration command services with
            | Error failure -> return failure
            | Ok client ->
                try
                    let change =
                        AdministrativeChange.create
                            (Guid.NewGuid())
                            AdministrativeResourceKind.Operation
                            manifest.Id
                            "migrate"
                            "developer migration dry-run"
                            0L
                            (Map
                                [ "plan_digest", MigrationPlan.digest plan
                                  "source_revision", manifest.SourceRevision
                                  "target_revision", manifest.TargetRevision
                                  "step_count", string manifest.Steps.Length ])

                    let! (preview: AdministrativeImpactPreview) = client.PreviewAsync(change, cancellationToken)

                    return
                        DeveloperCommandResult.success
                            command
                            (Map
                                [ "migrationId", manifest.Id
                                  "planDigest", MigrationPlan.digest plan
                                  "summary", preview.Summary
                                  "warningCount", string (Seq.length preview.Warnings)
                                  "affectedResourceCount", string (Seq.length preview.AffectedResourceIds) ])
                with
                | :? OperationCanceledException when cancellationToken.IsCancellationRequested ->
                    return! Task.FromCanceled<DeveloperCommandResult>(cancellationToken)
                | :? UnauthorizedAccessException as error ->
                    return DeveloperCommandResult.failure command "authorization_denied" error.Message
                | error -> return DeveloperCommandResult.failure command "migration_preview_failed" error.Message
        }
