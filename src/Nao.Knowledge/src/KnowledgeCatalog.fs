namespace Nao.Knowledge

open System
open System.Collections.Concurrent
open System.Threading
open System.Threading.Tasks
open Nao.Agents

type KnowledgeVersionBundle =
    { Source: KnowledgeSource
      Document: KnowledgeDocument
      Version: KnowledgeDocumentVersion
      ContentUnits: ContentUnit list
      Chunks: KnowledgeChunk list
      IndexEntries: KnowledgeIndexEntry list }

type KnowledgeCatalogSnapshot =
    { Sources: KnowledgeSource list
      Documents: KnowledgeDocument list
      Versions: KnowledgeDocumentVersion list
      ContentUnits: ContentUnit list
      Chunks: KnowledgeChunk list
      IndexEntries: KnowledgeIndexEntry list
      CacheEntries: KnowledgeCacheEntry list }

type KnowledgeDeletionEvidence =
    { SourceId: KnowledgeSourceId
      Status: KnowledgeDeletionStatus
      Documents: int
      Versions: int
      ContentUnits: int
      Chunks: int
      IndexEntries: int
      CacheEntries: int
      AppliedAt: DateTimeOffset }

type KnowledgeCatalog =
    { SaveVersionAsync: AuthorizationScope -> KnowledgeVersionBundle -> CancellationToken -> Task
      SaveCacheEntryAsync: AuthorizationScope -> KnowledgeCacheEntry -> CancellationToken -> Task
      SnapshotAsync: AuthorizationScope -> CancellationToken -> Task<KnowledgeCatalogSnapshot>
      TombstoneSourceAsync:
          AuthorizationScope
              -> KnowledgeSourceId
              -> DateTimeOffset
              -> string
              -> CancellationToken
              -> Task<KnowledgeDeletionEvidence>
      PurgeSourceAsync:
          AuthorizationScope
              -> KnowledgeSourceId
              -> DateTimeOffset
              -> CancellationToken
              -> Task<KnowledgeDeletionEvidence> }

module KnowledgeCatalog =
    type private CatalogState =
        { Sources: Map<KnowledgeSourceId, KnowledgeSource>
          Documents: Map<KnowledgeDocumentId, KnowledgeDocument>
          Versions: Map<KnowledgeDocumentVersionId, KnowledgeDocumentVersion>
          ContentUnits: Map<ContentUnitId, ContentUnit>
          Chunks: Map<KnowledgeChunkId, KnowledgeChunk>
          IndexEntries: Map<KnowledgeIndexEntryId, KnowledgeIndexEntry>
          CacheEntries: Map<RetrievalResultId, KnowledgeCacheEntry> }

    let private empty =
        { Sources = Map.empty
          Documents = Map.empty
          Versions = Map.empty
          ContentUnits = Map.empty
          Chunks = Map.empty
          IndexEntries = Map.empty
          CacheEntries = Map.empty }

    let private requireOwner (authorization: AuthorizationScope) (source: KnowledgeSource) =
        if source.Owner <> AuthorizationScope.identity authorization then
            raise (UnauthorizedAccessException "Knowledge source belongs to another authorization identity.")

    let private requireActive name lifecycle =
        match lifecycle.DeletionStatus with
        | KnowledgeDeletionStatus.Active -> ()
        | _ -> invalidArg name "Deleted knowledge cannot be committed or cached."

    let private requireBundleRelations (bundle: KnowledgeVersionBundle) =
        requireActive "source" bundle.Source.Lifecycle
        requireActive "document" bundle.Document.Lifecycle
        requireActive "document version" bundle.Version.Lifecycle

        if bundle.Document.SourceId <> bundle.Source.Id then
            invalidArg (nameof bundle) "Document does not belong to the supplied source."

        if
            bundle.Version.SourceId <> bundle.Source.Id
            || bundle.Version.DocumentId <> bundle.Document.Id
        then
            invalidArg (nameof bundle) "Document version does not belong to the supplied source and document."

        bundle.ContentUnits
        |> List.iter (fun unit ->
            requireActive "content unit" unit.Lifecycle

            if unit.DocumentVersionId <> bundle.Version.Id then
                invalidArg (nameof bundle) "Content unit does not belong to the supplied document version.")

        let unitIds = bundle.ContentUnits |> List.map _.Id |> Set.ofList

        bundle.Chunks
        |> List.iter (fun chunk ->
            requireActive "chunk" chunk.Lifecycle

            if chunk.DocumentVersionId <> bundle.Version.Id then
                invalidArg (nameof bundle) "Chunk does not belong to the supplied document version."

            if chunk.ContentUnitIds |> List.exists (unitIds.Contains >> not) then
                invalidArg (nameof bundle) "Chunk refers to a content unit outside the supplied version.")

        let chunks = bundle.Chunks |> Seq.map (fun chunk -> chunk.Id, chunk) |> Map.ofSeq

        bundle.IndexEntries
        |> List.iter (fun entry ->
            requireActive "index entry" entry.Lifecycle

            match chunks |> Map.tryFind entry.ChunkId with
            | Some chunk when
                entry.DocumentVersionId = bundle.Version.Id
                && entry.ContentHash = chunk.ContentHash
                ->
                ()
            | _ -> invalidArg (nameof bundle) "Index entry does not match a supplied chunk and document version.")

    let private addImmutable name key value entries =
        match entries |> Map.tryFind key with
        | Some existing when existing <> value -> invalidOp $"Cannot replace immutable {name} record."
        | _ -> entries |> Map.add key value

    let private sourceState sourceId (state: CatalogState) : KnowledgeSource =
        match state.Sources |> Map.tryFind sourceId with
        | Some source -> source
        | None -> raise (Collections.Generic.KeyNotFoundException "Knowledge source was not found.")

    let private related sourceId (state: CatalogState) =
        let documents =
            state.Documents |> Map.filter (fun _ document -> document.SourceId = sourceId)

        let documentIds = documents |> Map.toSeq |> Seq.map fst |> Set.ofSeq

        let versions =
            state.Versions
            |> Map.filter (fun _ version -> documentIds.Contains version.DocumentId)

        let versionIds = versions |> Map.toSeq |> Seq.map fst |> Set.ofSeq

        let units =
            state.ContentUnits
            |> Map.filter (fun _ unit -> versionIds.Contains unit.DocumentVersionId)

        let chunks =
            state.Chunks
            |> Map.filter (fun _ chunk -> versionIds.Contains chunk.DocumentVersionId)

        let chunkIds = chunks |> Map.toSeq |> Seq.map fst |> Set.ofSeq

        let indexes =
            state.IndexEntries
            |> Map.filter (fun _ entry -> chunkIds.Contains entry.ChunkId)

        let caches =
            state.CacheEntries |> Map.filter (fun _ entry -> entry.SourceId = sourceId)

        documents, versions, units, chunks, indexes, caches

    let private ensureNoLegalHold sourceId (state: CatalogState) =
        let source = sourceState sourceId state
        let documents, versions, units, chunks, indexes, caches = related sourceId state

        let held =
            source.Lifecycle.LegalHold
            || (documents |> Map.exists (fun _ item -> item.Lifecycle.LegalHold))
            || (versions |> Map.exists (fun _ item -> item.Lifecycle.LegalHold))
            || (units |> Map.exists (fun _ item -> item.Lifecycle.LegalHold))
            || (chunks |> Map.exists (fun _ item -> item.Lifecycle.LegalHold))
            || (indexes |> Map.exists (fun _ item -> item.Lifecycle.LegalHold))
            || (caches |> Map.exists (fun _ item -> item.Lifecycle.LegalHold))

        if held then
            invalidOp "Knowledge under legal hold cannot be deleted."

    let private transitionSource
        sourceId
        appliedAt
        (transition: DateTimeOffset -> KnowledgeLifecycle -> KnowledgeLifecycle)
        (state: CatalogState)
        =
        ensureNoLegalHold sourceId state
        let source = sourceState sourceId state
        let documents, versions, units, chunks, indexes, caches = related sourceId state

        let next =
            { state with
                Sources =
                    state.Sources
                    |> Map.add
                        sourceId
                        { source with
                            Lifecycle = transition appliedAt source.Lifecycle }
                Documents =
                    documents
                    |> Map.fold
                        (fun current key value ->
                            current
                            |> Map.add
                                key
                                { value with
                                    Lifecycle = transition appliedAt value.Lifecycle })
                        state.Documents
                Versions =
                    versions
                    |> Map.fold
                        (fun current key value ->
                            current
                            |> Map.add
                                key
                                { value with
                                    Lifecycle = transition appliedAt value.Lifecycle })
                        state.Versions
                ContentUnits =
                    units
                    |> Map.fold
                        (fun current key value ->
                            current
                            |> Map.add
                                key
                                { value with
                                    Lifecycle = transition appliedAt value.Lifecycle })
                        state.ContentUnits
                Chunks =
                    chunks
                    |> Map.fold
                        (fun current key value ->
                            current
                            |> Map.add
                                key
                                { value with
                                    Lifecycle = transition appliedAt value.Lifecycle })
                        state.Chunks
                IndexEntries =
                    indexes
                    |> Map.fold
                        (fun current key value ->
                            current
                            |> Map.add
                                key
                                { value with
                                    Lifecycle = transition appliedAt value.Lifecycle })
                        state.IndexEntries
                CacheEntries =
                    caches
                    |> Map.fold
                        (fun current key value ->
                            current
                            |> Map.add
                                key
                                { value with
                                    Lifecycle = transition appliedAt value.Lifecycle })
                        state.CacheEntries }

        next,
        { SourceId = sourceId
          Status = (next.Sources |> Map.find sourceId).Lifecycle.DeletionStatus
          Documents = documents.Count
          Versions = versions.Count
          ContentUnits = units.Count
          Chunks = chunks.Count
          IndexEntries = indexes.Count
          CacheEntries = caches.Count
          AppliedAt = appliedAt }

    let private snapshot (state: CatalogState) : KnowledgeCatalogSnapshot =
        { Sources = state.Sources |> Map.values |> List.ofSeq
          Documents = state.Documents |> Map.values |> List.ofSeq
          Versions = state.Versions |> Map.values |> List.ofSeq
          ContentUnits = state.ContentUnits |> Map.values |> List.ofSeq
          Chunks = state.Chunks |> Map.values |> List.ofSeq
          IndexEntries = state.IndexEntries |> Map.values |> List.ofSeq
          CacheEntries = state.CacheEntries |> Map.values |> List.ofSeq }

    let inMemory () =
        let partitions = ConcurrentDictionary<string, CatalogState>()

        let read authorization =
            partitions.GetOrAdd(
                authorization |> AuthorizationScope.identity |> AuthorizationIdentity.storageKey,
                fun _ -> empty
            )

        let update authorization operation =
            let key =
                authorization |> AuthorizationScope.identity |> AuthorizationIdentity.storageKey

            lock partitions (fun () ->
                let state = partitions.GetOrAdd(key, fun _ -> empty)
                let next, result = operation state
                partitions.[key] <- next
                result)

        { SaveVersionAsync =
            fun authorization bundle cancellationToken ->
                cancellationToken.ThrowIfCancellationRequested()
                requireOwner authorization bundle.Source
                requireBundleRelations bundle

                update authorization (fun state ->
                    let next =
                        { state with
                            Sources = addImmutable "source" bundle.Source.Id bundle.Source state.Sources
                            Documents = addImmutable "document" bundle.Document.Id bundle.Document state.Documents
                            Versions = addImmutable "document version" bundle.Version.Id bundle.Version state.Versions
                            ContentUnits =
                                bundle.ContentUnits
                                |> List.fold
                                    (fun entries unit -> addImmutable "content unit" unit.Id unit entries)
                                    state.ContentUnits
                            Chunks =
                                bundle.Chunks
                                |> List.fold
                                    (fun entries chunk -> addImmutable "chunk" chunk.Id chunk entries)
                                    state.Chunks
                            IndexEntries =
                                bundle.IndexEntries
                                |> List.fold
                                    (fun entries entry -> addImmutable "index entry" entry.Id entry entries)
                                    state.IndexEntries }

                    next, ())
                |> Task.FromResult
                :> Task
          SaveCacheEntryAsync =
            fun authorization entry cancellationToken ->
                cancellationToken.ThrowIfCancellationRequested()

                update authorization (fun state ->
                    let source = sourceState entry.SourceId state
                    requireOwner authorization source
                    requireActive "source" source.Lifecycle
                    requireActive "cache entry" entry.Lifecycle

                    let version =
                        state.Versions
                        |> Map.tryFind entry.DocumentVersionId
                        |> Option.defaultWith (fun () ->
                            invalidArg (nameof entry) "Cached document version was not found.")

                    requireActive "document version" version.Lifecycle

                    if version.SourceId <> entry.SourceId then
                        invalidArg (nameof entry) "Cached document version belongs to another source."

                    entry.ChunkIds
                    |> List.iter (fun chunkId ->
                        match state.Chunks |> Map.tryFind chunkId with
                        | Some chunk when chunk.DocumentVersionId = entry.DocumentVersionId ->
                            requireActive "chunk" chunk.Lifecycle
                        | _ ->
                            invalidArg
                                (nameof entry)
                                "Knowledge cache refers to an unknown chunk or another document version.")

                    { state with
                        CacheEntries = addImmutable "cache entry" entry.Id entry state.CacheEntries },
                    ())
                |> Task.FromResult
                :> Task
          SnapshotAsync =
            fun authorization cancellationToken ->
                cancellationToken.ThrowIfCancellationRequested()
                read authorization |> snapshot |> Task.FromResult
          TombstoneSourceAsync =
            fun authorization sourceId deletedAt reason cancellationToken ->
                cancellationToken.ThrowIfCancellationRequested()

                update authorization (fun state ->
                    let source = sourceState sourceId state
                    requireOwner authorization source

                    transitionSource
                        sourceId
                        deletedAt
                        (fun at lifecycle -> KnowledgeLifecycle.tombstone at reason lifecycle)
                        state)
                |> Task.FromResult
          PurgeSourceAsync =
            fun authorization sourceId deletedAt cancellationToken ->
                cancellationToken.ThrowIfCancellationRequested()

                update authorization (fun state ->
                    let source = sourceState sourceId state
                    requireOwner authorization source
                    transitionSource sourceId deletedAt KnowledgeLifecycle.purge state)
                |> Task.FromResult }
