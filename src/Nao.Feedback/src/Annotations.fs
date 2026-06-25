namespace Nao.Feedback

open System
open System.IO
open System.Threading.Tasks
open System.Text.Json
open System.Text.Json.Nodes
open Nao.Agents
open Nao.Loader

/// Applies annotations — persistent, dynamic overlays on tools and agents.
///
/// Annotations never mutate the legacy definition. They are layered on at load time,
/// so disabling or dropping an annotation transparently restores the original behaviour.
/// The same model covers JSON- and assembly-sourced tools (wrapped in memory) and agents
/// (extra guidance injected into the prompt constraints).
///
/// `materializeToolVersion` / `materializeAgentVersion` promote annotations into a real
/// `<name>@<version>.json` definition file so a reviewed adjustment can become a
/// first-class, coexisting workspace artifact.
module Annotations =

    let private isActive (a: Annotation) = a.Status = AnnotationStatus.Active

    let private applyDescription (descOverride: string option) (descAppend: string option) (desc: string) : string =
        let baseDesc = descOverride |> Option.defaultValue desc
        match descAppend with
        | Some extra when not (String.IsNullOrWhiteSpace extra) -> baseDesc + "\n\n" + extra
        | _ -> baseDesc

    // ----- Tools -------------------------------------------------------------

    /// Whether an annotation applies to a given tool (kind + name + base-version match).
    let appliesToTool (a: Annotation) (tool: Tool) : bool =
        a.Kind = AnnotationKind.Tool
        && a.TargetName = tool.Name
        && VersionRef.matches a.BaseVersion tool.Version

    /// Overlay a single annotation onto a tool, preserving its name and version. The
    /// original execution is wrapped with the annotation's input/output transforms.
    let applyToTool (a: Annotation) (tool: Tool) : Tool =
        let wrappedExecute (ctx: ToolContext) (input: string) : Task<string> =
            let input' =
                match a.InputPrefix with
                | Some p -> p + input
                | None -> input
            task {
                let! output = tool.Execute ctx input'
                return
                    match a.OutputSuffix with
                    | Some s -> output + s
                    | None -> output
            }
        { tool with
            Description = applyDescription a.DescriptionOverride a.DescriptionAppend tool.Description
            Execute = wrappedExecute }

    /// Overlay all active annotations onto the matching tools, in place. Multiple
    /// annotations compose in creation order. Tools with no matching annotation are
    /// returned unchanged, so dropping every annotation restores the legacy tools.
    let applyToolAnnotations (annotations: Annotation list) (tools: Tool list) : Tool list =
        let active = annotations |> List.filter isActive |> List.sortBy (fun a -> a.CreatedAt)
        tools
        |> List.map (fun tool ->
            active
            |> List.filter (fun a -> appliesToTool a tool)
            |> List.fold (fun t a -> applyToTool a t) tool)

    // ----- Agents ------------------------------------------------------------

    /// Whether an annotation applies to a given agent definition.
    let appliesToAgent (a: Annotation) (def: AgentDef) : bool =
        a.Kind = AnnotationKind.Agent
        && a.TargetName = def.Name
        && VersionRef.matches a.BaseVersion def.Version

    /// Overlay a single annotation onto an agent definition. Guidance is appended to the
    /// prompt's constraints; a description override replaces the agent's description.
    let applyToAgentDef (a: Annotation) (def: AgentDef) : AgentDef =
        let extraConstraints =
            [ a.GuidanceAppend; a.DescriptionAppend ]
            |> List.choose id
            |> List.filter (fun s -> not (String.IsNullOrWhiteSpace s))
        let prompt' = { def.Prompt with Constraints = def.Prompt.Constraints @ extraConstraints }
        let description = a.DescriptionOverride |> Option.defaultValue def.Description
        { def with Prompt = prompt'; Description = description }

    /// Overlay all active annotations onto an agent definition, composing in creation order.
    let applyAgentAnnotations (annotations: Annotation list) (def: AgentDef) : AgentDef =
        annotations
        |> List.filter isActive
        |> List.sortBy (fun a -> a.CreatedAt)
        |> List.filter (fun a -> appliesToAgent a def)
        |> List.fold (fun d a -> applyToAgentDef a d) def

    // ----- Version materialisation ------------------------------------------

    /// Compute the file path a materialised `<name>@<version>.json` would be written to,
    /// alongside the original definition file.
    let versionPath (originalPath: string) (name: string) (version: string) : string =
        let dir = Path.GetDirectoryName originalPath
        Path.Combine(dir, sprintf "%s@%s.json" name version)

    /// Load the JSON at a provenance location, apply `mutate`, and write the result to
    /// `outPath`. When `version` is set it is stamped into the object first. Shared by both
    /// the versioned materialisers (write to `<name>@<version>.json`) and the in-place
    /// rewriters used by the upgrade step (write back to the original file).
    let private mutateJsonFile (provenance: ToolProvenance option) (version: string option) (resolveOut: string -> string) (mutate: JsonObject -> unit) : Result<string, string> =
        match provenance with
        | Some prov when prov.Kind = "json" ->
            match prov.Location with
            | Some originalPath when File.Exists originalPath ->
                try
                    match JsonNode.Parse(File.ReadAllText originalPath) with
                    | :? JsonObject as obj ->
                        version |> Option.iter (fun v -> obj.["version"] <- JsonValue.Create v)
                        mutate obj
                        let outPath = resolveOut originalPath
                        let opts = JsonSerializerOptions(WriteIndented = true)
                        File.WriteAllText(outPath, obj.ToJsonString opts)
                        Ok outPath
                    | _ -> Error (sprintf "Original definition JSON is not an object: %s" originalPath)
                with ex -> Error (sprintf "Failed to write JSON definition: %s" ex.Message)
            | Some missing -> Error (sprintf "Original definition JSON not found: %s" missing)
            | None -> Error "Provenance has no location"
        | Some prov -> Error (sprintf "Definition is not JSON-sourced (kind=%s); use the runtime overlay instead" prov.Kind)
        | None -> Error "No provenance; cannot locate the source JSON"

    /// Mutation that folds the annotations' description changes into a tool's JSON.
    let private applyToolDescription (annotations: Annotation list) (obj: JsonObject) =
        let currentDesc =
            match obj.["description"] with
            | null -> ""
            | descNode -> descNode.GetValue<string>()
        let finalDesc =
            annotations
            |> List.sortBy (fun a -> a.CreatedAt)
            |> List.fold (fun d a -> applyDescription a.DescriptionOverride a.DescriptionAppend d) currentDesc
        obj.["description"] <- JsonValue.Create finalDesc

    /// Mutation that overrides an agent's description and appends guidance to its
    /// prompt constraints in the agent's JSON.
    let private applyAgentGuidance (annotations: Annotation list) (obj: JsonObject) =
        let sorted = annotations |> List.sortBy (fun a -> a.CreatedAt)
        sorted
        |> List.tryPick (fun a -> a.DescriptionOverride)
        |> Option.iter (fun d -> obj.["description"] <- JsonValue.Create d)
        let extra =
            sorted
            |> List.collect (fun a -> [ a.GuidanceAppend; a.DescriptionAppend ] |> List.choose id)
            |> List.filter (fun s -> not (String.IsNullOrWhiteSpace s))
        if not (List.isEmpty extra) then
            let promptObj =
                match obj.["prompt"] with
                | :? JsonObject as p -> p
                | _ ->
                    let p = JsonObject()
                    obj.["prompt"] <- p
                    p
            let constraintsArr =
                match promptObj.["constraints"] with
                | :? JsonArray as arr -> arr
                | _ ->
                    let arr = JsonArray()
                    promptObj.["constraints"] <- arr
                    arr
            for c in extra do
                constraintsArr.Add(JsonValue.Create c)

    /// Materialise a new `<name>@<version>.json` for a JSON-sourced tool by cloning the
    /// original definition and applying the annotations' description changes. The original
    /// execution configuration is preserved verbatim. Returns the new file path.
    let materializeToolVersion (provenance: ToolProvenance option) (toolName: string) (version: string) (annotations: Annotation list) : Result<string, string> =
        mutateJsonFile provenance (Some version) (fun orig -> versionPath orig toolName version) (applyToolDescription annotations)

    /// Materialise a new `<name>@<version>.json` for a JSON-sourced agent by cloning the
    /// original definition, overriding its description (if any) and appending the
    /// annotations' guidance to the prompt constraints. Returns the new file path.
    let materializeAgentVersion (provenance: ToolProvenance option) (agentName: string) (version: string) (annotations: Annotation list) : Result<string, string> =
        mutateJsonFile provenance (Some version) (fun orig -> versionPath orig agentName version) (applyAgentGuidance annotations)

    /// Apply the annotations' description changes back into a JSON-sourced tool's *original*
    /// file (no version bump). Used by the upgrade step to bake a confirmed improvement into
    /// the live definition. Returns the rewritten file path.
    let rewriteToolDefinition (provenance: ToolProvenance option) (annotations: Annotation list) : Result<string, string> =
        mutateJsonFile provenance None id (applyToolDescription annotations)

    /// Apply the annotations' guidance back into a JSON-sourced agent's *original* file
    /// (no version bump). Used by the upgrade step. Returns the rewritten file path.
    let rewriteAgentDefinition (provenance: ToolProvenance option) (annotations: Annotation list) : Result<string, string> =
        mutateJsonFile provenance None id (applyAgentGuidance annotations)
