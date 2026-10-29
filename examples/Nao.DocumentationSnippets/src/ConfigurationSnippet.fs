namespace Nao.DocumentationSnippets

open Nao.Platform

[<RequireQualifiedAccess>]
module ConfigurationSnippet =
    let validate () =
        let configuration =
            """
{
  "schemaVersion": 1,
  "profile": "local-development",
  "instanceId": "documentation",
  "identityMode": "development-permissive",
  "administrationMode": "disabled",
  "toolPolicy": "restricted",
  "workerIsolation": "in-process",
  "durability": "ephemeral",
  "clusterMembership": "standalone",
  "secretReferences": []
}
"""
            |> PlatformConfigurationJson.parse

        let _, effective = PlatformStartup.validate configuration

        if
            EffectivePlatformConfiguration.values effective |> Map.tryFind "instanceId"
            <> Some "documentation"
        then
            invalidOp "The documented configuration did not produce expected effective diagnostics."
