namespace Nao.Runtime.Orleans

open System.Threading.Tasks
open Nao.Agents

/// Identity and owner scopes required to destroy one session.
type SessionDeletionRequest =
    private
        { SessionKey: string
          Authorization: AuthorizationScope }

/// Effects participating in coordinated session destruction.
type SessionDeletion =
    { DeleteConversationDataAsync: AuthorizationScope -> Task
      DeleteTurnDataAsync: AuthorizationScope -> Task<Result<int, PlatformFailure>>
      DeleteMemoryOwnerAsync: AuthorizationScope -> Task<Result<int, PlatformFailure>>
      DeleteMetricsScopeAsync: AuthorizationScope -> Task<Result<int, PlatformFailure>>
      DeleteTraceScopeAsync: AuthorizationScope -> Task<Result<int, PlatformFailure>>
      DeleteJournalScopeAsync: AuthorizationScope -> Task<Result<int, PlatformFailure>>
      RemoveDirectoryEntryAsync: string -> string -> Task
      ClearRuntimeStateAsync: unit -> Task }

[<RequireQualifiedAccess>]
module SessionDeletion =
    let request sessionKey authorization : SessionDeletionRequest =
        let expected =
            RuntimeAddress.sessionFromScope authorization
            |> Option.map RuntimeAddress.sessionKey
            |> Option.defaultWith (fun () ->
                raise (System.UnauthorizedAccessException "Session deletion requires a session authorization scope."))

        if sessionKey <> expected then
            raise (System.UnauthorizedAccessException "Session deletion key does not match the authenticated scope.")

        { SessionKey = sessionKey
          Authorization = authorization }

    let create
        deleteConversationDataAsync
        deleteTurnDataAsync
        deleteMemoryOwnerAsync
        deleteMetricsScopeAsync
        deleteTraceScopeAsync
        deleteJournalScopeAsync
        removeDirectoryEntryAsync
        clearRuntimeStateAsync
        : SessionDeletion =
        { DeleteConversationDataAsync = deleteConversationDataAsync
          DeleteTurnDataAsync = deleteTurnDataAsync
          DeleteMemoryOwnerAsync = deleteMemoryOwnerAsync
          DeleteMetricsScopeAsync = deleteMetricsScopeAsync
          DeleteTraceScopeAsync = deleteTraceScopeAsync
          DeleteJournalScopeAsync = deleteJournalScopeAsync
          RemoveDirectoryEntryAsync = removeDirectoryEntryAsync
          ClearRuntimeStateAsync = clearRuntimeStateAsync }

    /// Delete session-owned data before clearing the runtime state that identifies it.
    let executeAsync request deletion =
        task {
            do! deletion.DeleteConversationDataAsync request.Authorization

            let deletions =
                [ (fun () -> deletion.DeleteTurnDataAsync request.Authorization)
                  (fun () -> deletion.DeleteMemoryOwnerAsync request.Authorization)
                  (fun () -> deletion.DeleteMetricsScopeAsync request.Authorization)
                  (fun () -> deletion.DeleteTraceScopeAsync request.Authorization)
                  (fun () -> deletion.DeleteJournalScopeAsync request.Authorization) ]

            let mutable deletionFailure = None

            for deleteOwnerAsync in deletions do
                if deletionFailure.IsNone then
                    match! deleteOwnerAsync () with
                    | Error failure -> deletionFailure <- Some failure
                    | Ok _ -> ()

            match deletionFailure with
            | Some failure -> return Error failure
            | None ->
                let sessionId =
                    AuthorizationScope.sessionId request.Authorization
                    |> Option.get
                    |> SessionId.value

                let directoryKey =
                    RuntimeAddress.directoryFromScope request.Authorization
                    |> RuntimeAddress.directoryKey

                do! deletion.RemoveDirectoryEntryAsync directoryKey sessionId
                do! deletion.ClearRuntimeStateAsync()
                return Ok()
        }

    let executeForSessionAsync
        sessionKey
        authorization
        deleteConversationDataAsync
        deleteTurnDataAsync
        deleteMemoryOwnerAsync
        deleteMetricsScopeAsync
        deleteTraceScopeAsync
        deleteJournalScopeAsync
        removeDirectoryEntryAsync
        clearRuntimeStateAsync
        =
        let request = request sessionKey authorization

        let deletion =
            create
                deleteConversationDataAsync
                deleteTurnDataAsync
                deleteMemoryOwnerAsync
                deleteMetricsScopeAsync
                deleteTraceScopeAsync
                deleteJournalScopeAsync
                removeDirectoryEntryAsync
                clearRuntimeStateAsync

        executeAsync request deletion
