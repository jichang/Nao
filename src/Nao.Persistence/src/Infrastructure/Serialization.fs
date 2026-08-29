namespace Nao.Persistence

open System
open System.Collections.Generic
open System.Globalization
open System.IO
open System.Text.Json
open System.Text.Json.Serialization
open Nao.Agents
open Nao.Agents

/// JSON helpers used for serializing collection/value types into TEXT columns
/// and to standalone files. Uses System.Text.Json with CLIMutable DTOs so the
/// same payloads round-trip through both backends.
module Json =
    let internal options =
        let o = JsonSerializerOptions(WriteIndented = false)
        o

    let serialize (value: 'a) : string = JsonSerializer.Serialize(value, options)
    let deserialize<'a> (s: string) : 'a = JsonSerializer.Deserialize<'a>(s, options)

    let tagsToJson (tags: string list) : string = serialize (List.toArray tags)

    let tagsFromJson (s: string) : string list =
        if String.IsNullOrWhiteSpace s then []
        else deserialize<string[]> s |> List.ofArray

    let mapToJson (m: Map<string, string>) : string =
        let d = Dictionary<string, string>()
        for KeyValue(k, v) in m do
            d.[k] <- v
        serialize d

    let mapFromJson (s: string) : Map<string, string> =
        if String.IsNullOrWhiteSpace s then Map.empty
        else
            deserialize<Dictionary<string, string>> s
            |> Seq.map (fun kv -> kv.Key, kv.Value)
            |> Map.ofSeq

    let floatsToJson (a: float array) : string = serialize a

    let floatsFromJson (s: string) : float array =
        if String.IsNullOrWhiteSpace s then [||] else deserialize<float array> s

/// ISO-8601 round-trippable timestamp helpers (stored as TEXT for portability).
module Time =
    let toIso (t: DateTimeOffset) : string = t.ToString("o", CultureInfo.InvariantCulture)

    let fromIso (s: string) : DateTimeOffset =
        DateTimeOffset.Parse(s, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind)

/// Filesystem-safe identifier sanitization.
module Sanitize =
    let id (value: string) : string =
        value.Replace('/', '_').Replace('\\', '_').Replace(':', '_')

/// Codec for permission decisions. Legacy permission-level values remain readable.
module PermissionDecisionCodec =
    let toString (decision: PermissionDecision) : string =
        match decision with
        | PermissionDecision.Allow -> "Allow"
        | PermissionDecision.Ask -> "Ask"
        | PermissionDecision.Deny -> "Deny"

    let fromString (s: string) : PermissionDecision =
        match s with
        | "Allow"
        | "AllowWithAudit" -> PermissionDecision.Allow
        | "Ask"
        | "RequireConfirmation" -> PermissionDecision.Ask
        | "Deny" -> PermissionDecision.Deny
        | other -> failwithf "Unknown PermissionDecision: %s" other

/// Codec for the AuditAction discriminated union (encoded as a small JSON DTO).
module AuditActionCodec =
    [<CLIMutable>]
    type Dto = { Kind: string; A: string; B: string }

    let toJson (action: AuditAction) : string =
        let dto =
            match action with
            | AuditAction.LlmCall m -> { Kind = "LlmCall"; A = m; B = null }
            | AuditAction.ToolInvocation t -> { Kind = "ToolInvocation"; A = t; B = null }
            | AuditAction.AgentDelegation a -> { Kind = "AgentDelegation"; A = a; B = null }
            | AuditAction.MemoryWrite k -> { Kind = "MemoryWrite"; A = k; B = null }
            | AuditAction.MemoryRead k -> { Kind = "MemoryRead"; A = k; B = null }
            | AuditAction.ResourceAccess(rt, r) -> { Kind = "ResourceAccess"; A = rt; B = r }
            | AuditAction.PermissionCheck c -> { Kind = "PermissionCheck"; A = c; B = null }
            | AuditAction.ConstitutionCheck -> { Kind = "ConstitutionCheck"; A = null; B = null }
            | AuditAction.LifecycleTransition(f, t) -> { Kind = "LifecycleTransition"; A = f; B = t }
        Json.serialize dto

    let fromJson (s: string) : AuditAction =
        let dto = Json.deserialize<Dto> s
        match dto.Kind with
        | "LlmCall" -> AuditAction.LlmCall dto.A
        | "ToolInvocation" -> AuditAction.ToolInvocation dto.A
        | "AgentDelegation" -> AuditAction.AgentDelegation dto.A
        | "MemoryWrite" -> AuditAction.MemoryWrite dto.A
        | "MemoryRead" -> AuditAction.MemoryRead dto.A
        | "ResourceAccess" -> AuditAction.ResourceAccess(dto.A, dto.B)
        | "PermissionCheck" -> AuditAction.PermissionCheck dto.A
        | "ConstitutionCheck" -> AuditAction.ConstitutionCheck
        | "LifecycleTransition" -> AuditAction.LifecycleTransition(dto.A, dto.B)
        | other -> failwithf "Unknown AuditAction kind: %s" other

/// CLIMutable DTOs for file persistence and conversion helpers shared by both backends.
module Dto =

    let internal dictOfMap (m: Map<string, string>) : Dictionary<string, string> =
        let d = Dictionary<string, string>()
        for KeyValue(k, v) in m do
            d.[k] <- v
        d

    let internal mapOfDict (d: Dictionary<string, string>) : Map<string, string> =
        if isNull (box d) then Map.empty
        else d |> Seq.map (fun kv -> kv.Key, kv.Value) |> Map.ofSeq

    let internal listOfArray (a: string array) : string list =
        if isNull a then [] else List.ofArray a

    // ---- MemoryEntry ----
    [<CLIMutable>]
    type MemoryEntryDto =
        { Key: string
          Value: string
          Timestamp: DateTimeOffset
          Tags: string array }

    let toMemoryDto (e: MemoryEntry) : MemoryEntryDto =
        { Key = e.Key; Value = e.Value; Timestamp = e.Timestamp; Tags = List.toArray e.Tags }

    let ofMemoryDto (d: MemoryEntryDto) : MemoryEntry =
        { Key = d.Key; Value = d.Value; Timestamp = d.Timestamp; Tags = listOfArray d.Tags }

    // ---- SemanticEntry ----
    [<CLIMutable>]
    type SemanticEntryDto =
        { Key: string
          Content: string
          Embedding: float array
          Timestamp: DateTimeOffset
          Tags: string array }

    let toSemanticDto (e: SemanticEntry) : SemanticEntryDto =
        { Key = e.Key
          Content = e.Content
          Embedding = e.Embedding
          Timestamp = e.Timestamp
          Tags = List.toArray e.Tags }

    let ofSemanticDto (d: SemanticEntryDto) : SemanticEntry =
        { Key = d.Key
          Content = d.Content
          Embedding = (if isNull d.Embedding then [||] else d.Embedding)
          Timestamp = d.Timestamp
          Tags = listOfArray d.Tags }

    // ---- ExecutionRecord ----
    [<CLIMutable>]
    type ExecutionRecordDto =
        { ToolName: string
          Input: string
          Output: string
          ExecutedAt: DateTimeOffset
          Reverted: bool
          Metadata: Dictionary<string, string> }

    let toExecutionDto (r: ExecutionRecord) : ExecutionRecordDto =
        { ToolName = r.ToolName
          Input = r.Input
          Output = r.Output
          ExecutedAt = r.ExecutedAt
          Reverted = r.Reverted
          Metadata = dictOfMap r.Metadata }

    let ofExecutionDto (d: ExecutionRecordDto) : ExecutionRecord =
        { ToolName = d.ToolName
          Input = d.Input
          Output = d.Output
          ExecutedAt = d.ExecutedAt
          Reverted = d.Reverted
          Metadata = mapOfDict d.Metadata }

    // ---- AuditEntry ----
    [<CLIMutable>]
    type AuditEntryDto =
        { Id: Guid
          Timestamp: DateTimeOffset
          AgentId: string
          ActionJson: string
          Input: string
          Output: string
          Permitted: bool
          [<JsonPropertyName("PermissionLevel")>]
          Decision: string
          ConstitutionViolations: string array
          ExecutionId: string
          Metadata: Dictionary<string, string> }

    let toAuditDto (e: AuditEntry) : AuditEntryDto =
        { Id = e.Id
          Timestamp = e.Timestamp
          AgentId = e.AgentId
          ActionJson = AuditActionCodec.toJson e.Action
          Input = (match e.Input with Some s -> s | None -> null)
          Output = (match e.Output with Some s -> s | None -> null)
          Permitted = e.Permitted
          Decision = PermissionDecisionCodec.toString e.Decision
          ConstitutionViolations = List.toArray e.ConstitutionViolations
          ExecutionId = (match e.ExecutionId with Some g -> g.ToString("D") | None -> null)
          Metadata = dictOfMap e.Metadata }

    let ofAuditDto (d: AuditEntryDto) : AuditEntry =
        { Id = d.Id; Timestamp = d.Timestamp; AgentId = d.AgentId; Action = AuditActionCodec.fromJson d.ActionJson; Input = (if isNull d.Input then None else Some d.Input); Output = (if isNull d.Output then None else Some d.Output); Permitted = d.Permitted; Decision = PermissionDecisionCodec.fromString d.Decision; ConstitutionViolations = listOfArray d.ConstitutionViolations; ExecutionId = (if isNull d.ExecutionId then None else Some(Guid.Parse d.ExecutionId)); Metadata = mapOfDict d.Metadata }

/// Simple file-backed JSON document helpers (whole-file read/write).
module FileJson =
    let read<'a> (path: string) (fallback: 'a) : 'a =
        if File.Exists path then
            let txt = File.ReadAllText path
            if String.IsNullOrWhiteSpace txt then fallback else Json.deserialize<'a> txt
        else
            fallback

    let write (path: string) (value: 'a) : unit =
        let dir = Path.GetDirectoryName(path: string)
        if not (String.IsNullOrEmpty dir) && not (Directory.Exists dir) then
            Directory.CreateDirectory dir |> ignore
        File.WriteAllText(path, Json.serialize value)
