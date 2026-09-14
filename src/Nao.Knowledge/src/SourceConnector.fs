namespace Nao.Knowledge

open System
open System.Security.Cryptography
open System.Text
open System.Threading
open System.Threading.Tasks
open Nao.Agents

[<Struct>]
type SourceItemId = private SourceItemId of string

module SourceItemId =
    let tryParse (value: string) =
        if
            not (isNull value)
            && value.StartsWith("item_", StringComparison.Ordinal)
            && value.Length = 69
            && value[5..]
               |> Seq.forall (fun character -> Char.IsDigit character || character >= 'a' && character <= 'f')
        then
            Some(SourceItemId value)
        else
            None

    let parse value =
        tryParse value
        |> Option.defaultWith (fun () ->
            invalidArg (nameof value) "Source item ID must be a canonical lowercase SHA-256 identifier.")

    let private derive sourceId relativePath =
        [ KnowledgeSourceId.value sourceId; relativePath ]
        |> String.concat "\u001f"
        |> Encoding.UTF8.GetBytes
        |> SHA256.HashData
        |> Convert.ToHexString
        |> _.ToLowerInvariant()
        |> sprintf "item_%s"
        |> SourceItemId

    let forPath sourceId relativePath =
        if String.IsNullOrWhiteSpace relativePath || relativePath <> relativePath.Trim() then
            invalidArg (nameof relativePath) "Source item path must be canonical and non-blank."

        derive sourceId relativePath

    let value (SourceItemId value) = value

[<RequireQualifiedAccess>]
type SourceConnectorMode =
    | Pull
    | Push
    | Snapshot
    | ChangeFeed

type SourceConnectorDescriptor =
    { Connector: ComponentVersion
      Modes: Set<SourceConnectorMode> }

[<Struct>]
type SourceConnectorCheckpoint =
    private
        { Connector: ComponentVersion
          Cursor: string
          StateHash: ContentHash }

module SourceConnectorCheckpoint =
    let create connector cursor =
        if String.IsNullOrWhiteSpace cursor then
            invalidArg (nameof cursor) "Connector checkpoint cursor must be non-blank."

        { Connector = connector
          Cursor = cursor
          StateHash = ContentHash.computeText cursor }

    let connector checkpoint = checkpoint.Connector
    let cursor checkpoint = checkpoint.Cursor
    let stateHash checkpoint = checkpoint.StateHash

type SourceContent =
    { ItemId: SourceItemId
      Uri: Uri
      RelativePath: string
      MediaType: string
      Content: byte array
      ContentHash: ContentHash
      ByteLength: int64
      ModifiedAt: DateTimeOffset
      Metadata: Map<string, string> }

[<RequireQualifiedAccess>]
type SourceChange =
    | Added of SourceContent
    | Updated of SourceContent
    | Moved of previousUri: Uri * content: SourceContent
    | Deleted of itemId: SourceItemId * previousUri: Uri * contentHash: ContentHash

[<RequireQualifiedAccess>]
type SourceItemFailureKind =
    | AccessDenied
    | ContentTooLarge
    | Unavailable
    | InvalidContent

type SourceItemFailure =
    { Uri: Uri
      Kind: SourceItemFailureKind
      Message: string
      Attempts: int }

type SourceSyncBatch =
    { SourceId: KnowledgeSourceId
      Connector: ComponentVersion
      StartedAt: DateTimeOffset
      CompletedAt: DateTimeOffset
      Changes: SourceChange list
      Failures: SourceItemFailure list
      Checkpoint: SourceConnectorCheckpoint }

[<RequireQualifiedAccess>]
type SourceNetworkPolicy =
    | Denied
    | Allowlist of Uri list

module SourceNetworkPolicy =
    let isAllowed (destination: Uri) =
        function
        | SourceNetworkPolicy.Denied -> false
        | SourceNetworkPolicy.Allowlist allowed ->
            allowed
            |> List.exists (fun entry ->
                let sameOrigin =
                    String.Equals(entry.Scheme, destination.Scheme, StringComparison.OrdinalIgnoreCase)
                    && String.Equals(entry.IdnHost, destination.IdnHost, StringComparison.OrdinalIgnoreCase)
                    && entry.Port = destination.Port

                let pathAllowed =
                    if entry.AbsolutePath = "/" && String.IsNullOrEmpty entry.Query then
                        true
                    elif not (String.IsNullOrEmpty entry.Query) then
                        String.Equals(entry.PathAndQuery, destination.PathAndQuery, StringComparison.Ordinal)
                    elif entry.AbsolutePath.EndsWith("/", StringComparison.Ordinal) then
                        destination.AbsolutePath.StartsWith(entry.AbsolutePath, StringComparison.Ordinal)
                    else
                        String.Equals(entry.AbsolutePath, destination.AbsolutePath, StringComparison.Ordinal)

                sameOrigin && pathAllowed)

type SourceConnectorPolicy =
    { AuthorizeAsync: AuthorizationScope -> ResourceAccess -> CancellationToken -> Task<bool>
      Network: SourceNetworkPolicy }

type SourceSyncRequest =
    { Authorization: AuthorizationScope
      Source: KnowledgeSource
      Mode: SourceConnectorMode
      Checkpoint: SourceConnectorCheckpoint option
      Policy: SourceConnectorPolicy
      RequestedAt: DateTimeOffset }

type PullSourceConnector =
    { Descriptor: SourceConnectorDescriptor
      SynchronizeAsync: SourceSyncRequest -> CancellationToken -> Task<SourceSyncBatch> }

type PushedSourceItem =
    { Source: KnowledgeSource
      Item: SourceContent
      ReceivedAt: DateTimeOffset }

type PushSourceConnector =
    { Descriptor: SourceConnectorDescriptor
      PushAsync:
          AuthorizationScope -> PushedSourceItem -> SourceConnectorPolicy -> CancellationToken -> Task<SourceChange> }

type SourceChangeFeed =
    { Descriptor: SourceConnectorDescriptor
      ReadAsync: SourceSyncRequest -> int -> CancellationToken -> Task<SourceSyncBatch> }

type SnapshotSourceConnector =
    { Descriptor: SourceConnectorDescriptor
      SnapshotAsync: SourceSyncRequest -> CancellationToken -> Task<SourceSyncBatch> }

type DatabaseSourceConnector =
    { Pull: PullSourceConnector
      CheckpointStoreName: string }

type ObjectStorageSourceConnector =
    { Pull: PullSourceConnector
      ContainerName: string }
