namespace Nao.Knowledge

open System
open System.Threading
open System.Threading.Tasks
open Nao.Agents

[<RequireQualifiedAccess>]
type SourceRetryPolicy =
    | None
    | Fixed of maxRetries: int * delay: TimeSpan
    | Exponential of maxRetries: int * initialDelay: TimeSpan * maximumDelay: TimeSpan

[<Struct>]
type SourceFailureId = private SourceFailureId of Guid

module SourceFailureId =
    let create () = SourceFailureId(Guid.NewGuid())
    let value (SourceFailureId value) = value

type QuarantinedSourceItem =
    private
        { Version: int
          Id: SourceFailureId
          Owner: AuthorizationIdentity
          SourceId: KnowledgeSourceId
          Connector: ComponentVersion
          Uri: Uri
          Kind: SourceItemFailureKind
          Message: string
          ConnectorAttempts: int
          SyncAttempts: int
          QuarantinedAt: DateTimeOffset }

module QuarantinedSourceItem =
    let currentVersion = 1
    let version item = item.Version
    let id item = item.Id
    let owner item = item.Owner
    let sourceId item = item.SourceId
    let connector item = item.Connector
    let uri item = item.Uri
    let kind item = item.Kind
    let message item = item.Message
    let connectorAttempts item = item.ConnectorAttempts
    let syncAttempts item = item.SyncAttempts
    let quarantinedAt item = item.QuarantinedAt

type DeadLetteredSourceSync =
    private
        { Version: int
          Id: SourceFailureId
          Owner: AuthorizationIdentity
          SourceId: KnowledgeSourceId
          Connector: ComponentVersion
          Mode: SourceConnectorMode
          Failure: PlatformFailure
          Attempts: int
          FailedAt: DateTimeOffset }

module DeadLetteredSourceSync =
    let currentVersion = 1
    let version item = item.Version
    let id item = item.Id
    let owner item = item.Owner
    let sourceId item = item.SourceId
    let connector item = item.Connector
    let mode item = item.Mode
    let failure item = item.Failure
    let attempts item = item.Attempts
    let failedAt item = item.FailedAt

type SourceFailureStore =
    { SaveQuarantinedAsync: AuthorizationScope -> QuarantinedSourceItem -> CancellationToken -> Task
      SaveDeadLetteredAsync: AuthorizationScope -> DeadLetteredSourceSync -> CancellationToken -> Task
      ListQuarantinedAsync:
          AuthorizationScope -> KnowledgeSourceId -> CancellationToken -> Task<QuarantinedSourceItem list>
      ListDeadLetteredAsync:
          AuthorizationScope -> KnowledgeSourceId -> CancellationToken -> Task<DeadLetteredSourceSync list> }

type SourceResilienceConfig =
    { Retry: SourceRetryPolicy
      MaximumConcurrency: int
      MaximumPending: int
      FailureStore: SourceFailureStore
      DelayAsync: TimeSpan -> CancellationToken -> Task
      Clock: unit -> DateTimeOffset }

type SourceSyncWorkItem =
    { Connector: PullSourceConnector
      Request: SourceSyncRequest }

[<RequireQualifiedAccess>]
type SourceSyncOutcome =
    | Completed of SourceSyncBatch
    | DeadLettered of DeadLetteredSourceSync

type SourceSyncRunner =
    { RunAsync: PullSourceConnector -> SourceSyncRequest -> CancellationToken -> Task<SourceSyncOutcome>
      RunManyAsync: SourceSyncWorkItem list -> CancellationToken -> Task<SourceSyncOutcome list> }
