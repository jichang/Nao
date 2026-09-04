namespace Nao.Runtime.Orleans

open System.Threading.Tasks
open Nao.Agents

/// Identity and owner scopes required to destroy one session.
type SessionDeletionRequest =
    { SessionKey: string
      MemoryOwner: string
      UserId: string
      SessionId: string }

/// Effects participating in coordinated session destruction.
type SessionDeletion =
    { DeleteConversationDataAsync: string -> Task
      DeleteTurnDataAsync: string -> Task<Result<int, PlatformFailure>>
      DeleteMemoryOwnerAsync: string -> Task<Result<int, PlatformFailure>>
      DeleteMetricsOwnerAsync: string -> Task<Result<int, PlatformFailure>>
      DeleteJournalOwnerAsync: string -> Task<Result<int, PlatformFailure>>
      RemoveDirectoryEntryAsync: string -> string -> Task
      ClearRuntimeStateAsync: unit -> Task }

[<RequireQualifiedAccess>]
module SessionDeletion =
    let request sessionKey memoryOwner userId sessionId : SessionDeletionRequest =
        { SessionKey = sessionKey
          MemoryOwner = memoryOwner
          UserId = userId
          SessionId = sessionId }

    let create
        deleteConversationDataAsync
        deleteTurnDataAsync
        deleteMemoryOwnerAsync
        deleteMetricsOwnerAsync
        deleteJournalOwnerAsync
        removeDirectoryEntryAsync
        clearRuntimeStateAsync
        : SessionDeletion =
        { DeleteConversationDataAsync = deleteConversationDataAsync
          DeleteTurnDataAsync = deleteTurnDataAsync
          DeleteMemoryOwnerAsync = deleteMemoryOwnerAsync
          DeleteMetricsOwnerAsync = deleteMetricsOwnerAsync
          DeleteJournalOwnerAsync = deleteJournalOwnerAsync
          RemoveDirectoryEntryAsync = removeDirectoryEntryAsync
          ClearRuntimeStateAsync = clearRuntimeStateAsync }

    /// Delete session-owned data before clearing the runtime state that identifies it.
    let executeAsync request deletion =
        task {
            do! deletion.DeleteConversationDataAsync request.SessionKey

            let deletions =
                [ request.SessionKey, deletion.DeleteTurnDataAsync
                  request.MemoryOwner, deletion.DeleteMemoryOwnerAsync
                  request.SessionKey, deletion.DeleteMetricsOwnerAsync
                  request.SessionKey, deletion.DeleteJournalOwnerAsync ]

            let mutable deletionFailure = None

            for owner, deleteOwnerAsync in deletions do
                if deletionFailure.IsNone then
                    match! deleteOwnerAsync owner with
                    | Error failure -> deletionFailure <- Some failure
                    | Ok _ -> ()

            match deletionFailure with
            | Some failure -> return Error failure
            | None ->
                do! deletion.RemoveDirectoryEntryAsync request.UserId request.SessionId
                do! deletion.ClearRuntimeStateAsync()
                return Ok()
        }

    let executeForSessionAsync
        sessionKey
        memoryOwner
        parseKey
        deleteConversationDataAsync
        deleteTurnDataAsync
        deleteMemoryOwnerAsync
        deleteMetricsOwnerAsync
        deleteJournalOwnerAsync
        removeDirectoryEntryAsync
        clearRuntimeStateAsync
        =
        let userId, sessionId = parseKey sessionKey
        let request = request sessionKey memoryOwner userId sessionId

        let deletion =
            create
                deleteConversationDataAsync
                deleteTurnDataAsync
                deleteMemoryOwnerAsync
                deleteMetricsOwnerAsync
                deleteJournalOwnerAsync
                removeDirectoryEntryAsync
                clearRuntimeStateAsync

        executeAsync request deletion
