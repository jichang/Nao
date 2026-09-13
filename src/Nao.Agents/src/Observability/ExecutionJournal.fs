namespace Nao.Agents

open System
open System.Threading
open System.Threading.Tasks

/// A record of a single tool execution (immutable, for journaling)
type ExecutionRecord =
    { Id: Guid
      Failure: ToolFailure option
      IdempotencyKey: IdempotencyKey
      Correlation: CorrelationContext
      TurnId: string
      ToolName: string
      ReplaySafety: ToolReplaySafety
      Input: string
      Output: string
      ExecutedAt: DateTimeOffset
      Reverted: bool
      Metadata: Map<string, string> }

[<RequireQualifiedAccess>]
type ToolSideEffectPhase =
    | IntentRecorded
    | Succeeded
    | Failed
    | Interrupted
    | CompensationSucceeded
    | CompensationFailed
    | Quarantined

/// Append-only transition for an externally side-effecting tool invocation.
type ToolSideEffectTransition =
    { Id: Guid
      IdempotencyKey: IdempotencyKey
      Correlation: CorrelationContext
      TurnId: string
      ToolName: string
      Input: string
      Phase: ToolSideEffectPhase
      Detail: string option
      RecordedAt: DateTimeOffset }

type ToolSideEffectJournal =
    { Save: AuthorizationScope -> ToolSideEffectTransition -> Task
      GetByIdempotencyKey: AuthorizationScope -> IdempotencyKey -> Task<ToolSideEffectTransition list>
      GetByExecution: AuthorizationScope -> ExecutionId -> Task<ToolSideEffectTransition list> }

/// Stable harness boundary represented by a durable checkpoint.
[<RequireQualifiedAccess>]
type HarnessCheckpointPhase =
    | Accepted
    | ExecutionStarted
    | Succeeded
    | Failed
    | Denied
    | Cancelled
    | TimedOut
    | LimitExceeded
    | Indeterminate

/// Minimal durable fact about one harness transition.
type HarnessCheckpoint =
    { Id: Guid
      IdempotencyKey: IdempotencyKey
      Correlation: CorrelationContext
      TurnId: string
      AgentId: string
      Phase: HarnessCheckpointPhase
      RecordedAt: DateTimeOffset }

/// Persistence operations for harness checkpoints.
type HarnessCheckpointJournal =
    { Save: AuthorizationScope -> HarnessCheckpoint -> Task
      GetByExecution: AuthorizationScope -> ExecutionId -> Task<HarnessCheckpoint list>
      GetByIdempotencyKey: AuthorizationScope -> IdempotencyKey -> Task<HarnessCheckpoint list> }

/// Exact recorded provider invocation used for deterministic replay.
type ProviderReplayFixture =
    { Provider: string
      Options: CompletionOptions
      Messages: (string * string) list
      Result: CompletionResult }

/// Exact recorded tool invocation used for deterministic replay.
type ToolReplayFixture =
    { ToolName: string
      Input: string
      Result: ToolInvocationResult }

/// Declares whether a dependency invocation has a usable recording.
[<RequireQualifiedAccess>]
type ReplayFixture<'Fixture> =
    | Recorded of 'Fixture
    | NonReplayable of reason: string

/// Immutable operation fixtures indexed by stable idempotency identity.
type ReplayFixtures =
    { Providers: Map<IdempotencyKey, ReplayFixture<ProviderReplayFixture>>
      Tools: Map<IdempotencyKey, ReplayFixture<ToolReplayFixture>> }

[<RequireQualifiedAccess>]
module ReplayFixtures =
    let empty: ReplayFixtures =
        { Providers = Map.empty
          Tools = Map.empty }

module internal RuntimeReplayFixtures =
    let private current = AsyncLocal<ReplayFixtures option>()

    let get () = current.Value
    let set value = current.Value <- value

/// Functional journal operations for tool execution revert/audit support.
type ExecutionJournal =
    { RecordAsync: AuthorizationScope -> ExecutionRecord -> Task
      GetHistoryAsync: AuthorizationScope -> Task<ExecutionRecord list>
      GetByExecutionAsync: AuthorizationScope -> ExecutionId -> Task<ExecutionRecord list>
      GetRevertibleAsync: AuthorizationScope -> Task<ExecutionRecord list>
      MarkRevertedAsync: AuthorizationScope -> Guid -> Task
      DeleteScopeAsync: AuthorizationScope -> Task<Result<int, PlatformFailure>>
      DeleteExpiredAsync: AuthorizationScope -> DateTimeOffset -> Task<Result<int, PlatformFailure>>
      SideEffects: ToolSideEffectJournal
      Checkpoints: HarnessCheckpointJournal }

module internal RuntimeExecutionJournal =
    let private current = AsyncLocal<ExecutionJournal option>()

    let get () = current.Value
    let set value = current.Value <- value

/// Orchestrates reverting tool executions in reverse order
module ExecutionJournal =

    /// Revert all revertible executions in reverse chronological order.
    /// Returns list of (toolName, result) for each attempted revert.
    let revertAllAsync
        (scope: AuthorizationScope)
        (journal: ExecutionJournal)
        (tools: Tool list)
        : Task<(string * Result<unit, string>) list> =
        task {
            let! revertible = journal.GetRevertibleAsync scope
            let results = System.Collections.Generic.List<string * Result<unit, string>>()

            for record in revertible do
                let tool = tools |> List.tryFind (fun t -> t.Name = record.ToolName)

                match tool with
                | Some tool when Tool.canRevert tool ->
                    let context: RevertContext =
                        { Input = record.Input
                          Output = record.Output
                          ExecutedAt = record.ExecutedAt
                          Metadata = record.Metadata }

                    let! result = Tool.revertAsync context tool

                    match result with
                    | Ok() -> do! journal.MarkRevertedAsync scope record.Id
                    | _ -> ()

                    results.Add(record.ToolName, result)
                | _ -> results.Add(record.ToolName, Error "Tool not found or does not support revert")

            return results |> Seq.toList
        }

    /// Revert the most recent execution only.
    let revertLastAsync
        (scope: AuthorizationScope)
        (journal: ExecutionJournal)
        (tools: Tool list)
        : Task<Result<unit, string>> =
        task {
            let! revertible = journal.GetRevertibleAsync scope

            match revertible with
            | [] -> return Error "No revertible executions"
            | record :: _ ->
                let tool = tools |> List.tryFind (fun candidate -> candidate.Name = record.ToolName)

                match tool with
                | Some tool when Tool.canRevert tool ->
                    let context: RevertContext =
                        { Input = record.Input
                          Output = record.Output
                          ExecutedAt = record.ExecutedAt
                          Metadata = record.Metadata }

                    let! result = Tool.revertAsync context tool

                    match result with
                    | Ok() -> do! journal.MarkRevertedAsync scope record.Id
                    | _ -> ()

                    return result
                | _ -> return Error(sprintf "Tool '%s' does not support revert" record.ToolName)
        }
