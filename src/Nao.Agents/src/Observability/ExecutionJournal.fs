namespace Nao.Agents

open System
open System.Threading
open System.Threading.Tasks

/// A record of a single tool execution (immutable, for journaling)
type ExecutionRecord =
    { Id: Guid
      Owner: string
      TurnId: string
      ToolName: string
      Input: string
      Output: string
      ExecutedAt: DateTimeOffset
      Reverted: bool
      Metadata: Map<string, string> }

/// Functional journal operations for tool execution revert/audit support.
type ExecutionJournal =
    { RecordAsync: ExecutionRecord -> Task
      GetHistoryAsync: unit -> Task<ExecutionRecord list>
      GetRevertibleAsync: unit -> Task<ExecutionRecord list>
      MarkRevertedAsync: Guid -> Task
      DeleteOwnerAsync: string -> Task<Result<int, PlatformFailure>>
      DeleteExpiredAsync: string -> DateTimeOffset -> Task<Result<int, PlatformFailure>> }

module internal RuntimeExecutionJournal =
    let private current = AsyncLocal<ExecutionJournal option>()

    let get () = current.Value
    let set value = current.Value <- value

/// Orchestrates reverting tool executions in reverse order
module ExecutionJournal =

    /// Revert all revertible executions in reverse chronological order.
    /// Returns list of (toolName, result) for each attempted revert.
    let revertAllAsync (journal: ExecutionJournal) (tools: Tool list) : Task<(string * Result<unit, string>) list> =
        task {
            let! revertible = journal.GetRevertibleAsync()
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
                    | Ok() -> do! journal.MarkRevertedAsync record.Id
                    | _ -> ()

                    results.Add(record.ToolName, result)
                | _ -> results.Add(record.ToolName, Error "Tool not found or does not support revert")

            return results |> Seq.toList
        }

    /// Revert the most recent execution only.
    let revertLastAsync (journal: ExecutionJournal) (tools: Tool list) : Task<Result<unit, string>> =
        task {
            let! revertible = journal.GetRevertibleAsync()

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
                    | Ok() -> do! journal.MarkRevertedAsync record.Id
                    | _ -> ()

                    return result
                | _ -> return Error(sprintf "Tool '%s' does not support revert" record.ToolName)
        }
