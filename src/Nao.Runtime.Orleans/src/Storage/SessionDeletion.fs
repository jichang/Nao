namespace Nao.Runtime.Orleans

open System.Threading.Tasks

/// Identity and owner scopes required to destroy one session.
type SessionDeletionRequest =
    { SessionKey: string
      MemoryOwner: string
      UserId: string
      SessionId: string }

/// Effects participating in coordinated session destruction.
type SessionDeletion =
    { DeleteConversationDataAsync: string -> Task; ClearMemoryAsync: string -> Task<unit>; RemoveDirectoryEntryAsync: string -> string -> Task; ClearRuntimeStateAsync: unit -> Task }

[<RequireQualifiedAccess>]
module SessionDeletion =
    let request sessionKey memoryOwner userId sessionId : SessionDeletionRequest =
        { SessionKey = sessionKey; MemoryOwner = memoryOwner; UserId = userId; SessionId = sessionId }

    let create deleteConversationDataAsync clearMemoryAsync removeDirectoryEntryAsync clearRuntimeStateAsync : SessionDeletion =
        { DeleteConversationDataAsync = deleteConversationDataAsync; ClearMemoryAsync = clearMemoryAsync; RemoveDirectoryEntryAsync = removeDirectoryEntryAsync; ClearRuntimeStateAsync = clearRuntimeStateAsync }

    /// Delete session-owned data before clearing the runtime state that identifies it.
    let executeAsync request deletion =
        task {
            do! deletion.DeleteConversationDataAsync request.SessionKey
            do! deletion.ClearMemoryAsync request.MemoryOwner
            do! deletion.RemoveDirectoryEntryAsync request.UserId request.SessionId
            do! deletion.ClearRuntimeStateAsync ()
        }

    let executeForSessionAsync sessionKey memoryOwner parseKey deleteConversationDataAsync clearMemoryAsync removeDirectoryEntryAsync clearRuntimeStateAsync =
        let userId, sessionId = parseKey sessionKey
        let request = request sessionKey memoryOwner userId sessionId
        let deletion = create deleteConversationDataAsync clearMemoryAsync removeDirectoryEntryAsync clearRuntimeStateAsync
        executeAsync request deletion