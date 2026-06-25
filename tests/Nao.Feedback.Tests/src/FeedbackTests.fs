module FeedbackTests

open System
open System.IO
open System.Threading.Tasks
open Microsoft.VisualStudio.TestTools.UnitTesting
open Microsoft.Data.Sqlite
open Nao.Core
open Nao.Agents
open Nao.Loader
open Nao.Persistence
open Nao.Feedback

let private tempDir () =
    let d = Path.Combine(Path.GetTempPath(), "nao-feedback-tests", Guid.NewGuid().ToString("N"))
    Directory.CreateDirectory d |> ignore
    d

let private sqliteFactory () : IDbConnectionFactory =
    let path = Path.Combine(Path.GetTempPath(), sprintf "nao-feedback-%s.db" (Guid.NewGuid().ToString("N")))
    let cs = sprintf "Data Source=%s" path
    DbConnectionFactory.ofFunc (fun () -> new SqliteConnection(cs) :> System.Data.Common.DbConnection)

let private echoTool (name: string) : Tool =
    Tool.Create(name, "Echoes its input.", (fun (s: string) -> Task.FromResult(sprintf "echo:%s" s)))

let private agentDef (name: string) : AgentDef =
    { Name = name
      Version = None
      Description = "Test agent."
      Provider = "echo"
      Model = "test"
      Prompt = { Prompt.Empty with Role = "You are a test agent." }
      Tools = []
      SubAgents = []
      Options = CompletionOptions.Default
      MaxRounds = 1
      IsAsync = false
      Provenance = None }

[<TestClass>]
type TurnRecorderTests() =

    [<TestMethod>]
    member _.``Pairs tool invocations with their results in order``() =
        let recorder =
            TurnRecorder.create("t1", "s1", "u1", "ws", "agent", None, "hello")
        let sink = recorder :> IAgentEventSink
        sink.Emit(AgentEvent.InvokingTool("search", "query"))
        sink.Emit(AgentEvent.ToolResult("search", "results"))
        sink.Emit(AgentEvent.DelegatingToAgent("helper", "subtask"))
        sink.Emit(AgentEvent.AgentResult("helper", "done"))
        sink.Emit(AgentEvent.Completed("final answer"))

        let snap = recorder.Snapshot()
        Assert.AreEqual(1, snap.ToolCalls.Length)
        Assert.AreEqual("search", snap.ToolCalls.[0].Name)
        Assert.AreEqual("query", snap.ToolCalls.[0].Input)
        Assert.AreEqual("results", snap.ToolCalls.[0].Output)
        Assert.AreEqual(1, snap.SubAgentCalls.Length)
        Assert.AreEqual("helper", snap.SubAgentCalls.[0].Name)
        Assert.AreEqual("subtask", snap.SubAgentCalls.[0].Input)
        Assert.AreEqual("final answer", snap.Output)

    [<TestMethod>]
    member _.``Resolves tool version and provenance from tool list``() =
        let versioned =
            { echoTool "search" with
                Version = Some "v1"
                Provenance = Some (ToolProvenance.json "/tools/search.json") }
        let recorder =
            TurnRecorder.forTools [ versioned ] ("t1", "s1", "u1", "ws", "agent", None, "hi")
        let sink = recorder :> IAgentEventSink
        sink.Emit(AgentEvent.InvokingTool("search", "q"))
        sink.Emit(AgentEvent.ToolResult("search", "r"))
        let snap = recorder.Snapshot()
        Assert.AreEqual(Some "v1", snap.ToolCalls.[0].Version)
        Assert.AreEqual(Some "json", snap.ToolCalls.[0].Provenance |> Option.map (fun p -> p.Kind))

[<TestClass>]
type AnnotationsTests() =

    [<TestMethod>]
    member _.``applyToolAnnotations overlays in place keeping name and version``() =
        let tool = { echoTool "search" with Version = Some "v1" }
        let ann =
            { Annotation.ForTool("search") with
                DescriptionAppend = Some "Be concise." }
        let result = Annotations.applyToolAnnotations [ ann ] [ tool ]
        Assert.AreEqual(1, result.Length)
        Assert.AreEqual(Some "v1", result.[0].Version)
        StringAssert.Contains(result.[0].Description, "Be concise.")

    [<TestMethod>]
    member _.``applyToTool wraps execute with input prefix and output suffix``() =
        (task {
            let tool = echoTool "search"
            let ann =
                { Annotation.ForTool("search") with
                    InputPrefix = Some "PRE:"
                    OutputSuffix = Some ":POST" }
            let overlaid = Annotations.applyToTool ann tool
            let! out = overlaid.Execute ToolContext.allowAll "x"
            Assert.AreEqual("echo:PRE:x:POST", out)
        }).GetAwaiter().GetResult()

    [<TestMethod>]
    member _.``Disabled annotations are not applied``() =
        let tool = echoTool "search"
        let ann =
            { Annotation.ForTool("search") with
                Status = AnnotationStatus.Disabled
                DescriptionAppend = Some "Should not appear." }
        let result = Annotations.applyToolAnnotations [ ann ] [ tool ]
        Assert.AreEqual("Echoes its input.", result.[0].Description)

    [<TestMethod>]
    member _.``appliesToTool respects base version matching``() =
        let v1 = { echoTool "search" with Version = Some "v1" }
        let annAny = Annotation.ForTool("search")
        let annV1 = { Annotation.ForTool("search") with BaseVersion = Some "v1" }
        let annV9 = { Annotation.ForTool("search") with BaseVersion = Some "v9" }
        Assert.IsTrue(Annotations.appliesToTool annAny v1)
        Assert.IsTrue(Annotations.appliesToTool annV1 v1)
        Assert.IsFalse(Annotations.appliesToTool annV9 v1)

    [<TestMethod>]
    member _.``applyAgentAnnotations appends guidance to constraints and overrides description``() =
        let def = agentDef "writer"
        let ann =
            { Annotation.ForAgent("writer") with
                GuidanceAppend = Some "Always cite sources."
                DescriptionOverride = Some "A careful writer." }
        let result = Annotations.applyAgentAnnotations [ ann ] def
        Assert.AreEqual("A careful writer.", result.Description)
        Assert.IsTrue(result.Prompt.Constraints |> List.contains "Always cite sources.")

    [<TestMethod>]
    member _.``materializeToolVersion writes a new versioned definition file``() =
        let dir = tempDir ()
        let original = Path.Combine(dir, "search.json")
        File.WriteAllText(original,
            """{ "name": "search", "description": "Original.", "mode": "echo" }""")
        let ann =
            { Annotation.ForTool("search") with
                DescriptionAppend = Some "Added guidance."
                Provenance = Some (ToolProvenance.json original) }
        match Annotations.materializeToolVersion (Some (ToolProvenance.json original)) "search" "v2" [ ann ] with
        | Error e -> Assert.Fail(sprintf "expected success, got: %s" e)
        | Ok outPath ->
            Assert.IsTrue(File.Exists outPath)
            StringAssert.Contains(outPath, "search@v2.json")
            let content = File.ReadAllText outPath
            StringAssert.Contains(content, "\"version\"")
            StringAssert.Contains(content, "v2")
            StringAssert.Contains(content, "Added guidance.")
            StringAssert.Contains(content, "echo")

    [<TestMethod>]
    member _.``materializeToolVersion fails for non-json provenance``() =
        match Annotations.materializeToolVersion (Some (ToolProvenance.assembly "/x.dll" "X.Search")) "search" "v2" [] with
        | Ok _ -> Assert.Fail("expected error for assembly provenance")
        | Error _ -> ()

[<TestClass>]
type FileStoreTests() =

    [<TestMethod>]
    member _.``Turn store round-trips via JSONL``() =
        (task {
            let dir = tempDir ()
            let store = FileTurnStore dir :> ITurnStore
            let turn =
                { TurnRecord.Empty with
                    TurnId = "t1"; SessionId = "s1"
                    ToolCalls = [ { Name = "search"; Version = Some "v1"; Input = "q"; Output = "r"; Provenance = Some (ToolProvenance.json "/a.json") } ] }
            do! store.SaveAsync turn
            let! loaded = store.GetAsync "t1"
            Assert.IsTrue(loaded.IsSome)
            Assert.AreEqual(1, loaded.Value.ToolCalls.Length)
            Assert.AreEqual("search", loaded.Value.ToolCalls.[0].Name)
            Assert.AreEqual(Some "v1", loaded.Value.ToolCalls.[0].Version)
        }).GetAwaiter().GetResult()

    [<TestMethod>]
    member _.``Annotation store round-trips, filters, updates status and deletes``() =
        (task {
            let dir = tempDir ()
            let store = FileAnnotationStore dir :> IAnnotationStore
            let a1 = { Annotation.ForTool("search") with DescriptionAppend = Some "x" }
            let a2 = Annotation.ForAgent("writer")
            do! store.SaveAsync a1
            do! store.SaveAsync a2
            let! all = store.GetAllAsync()
            Assert.AreEqual(2, all.Length)
            let! forSearch = store.GetForTargetAsync(AnnotationKind.Tool, "search")
            Assert.AreEqual(1, forSearch.Length)
            Assert.AreEqual("search", forSearch.[0].TargetName)

            let! updated = store.UpdateStatusAsync(a1.Id, AnnotationStatus.Disabled)
            Assert.IsTrue(updated)
            let! reloaded = store.GetForTargetAsync(AnnotationKind.Tool, "search")
            Assert.AreEqual(AnnotationStatus.Disabled, reloaded.[0].Status)

            let! deleted = store.DeleteAsync a1.Id
            Assert.IsTrue(deleted)
            let! remaining = store.GetAllAsync()
            Assert.AreEqual(1, remaining.Length)
        }).GetAwaiter().GetResult()

    [<TestMethod>]
    member _.``Version store round-trips and updates status``() =
        (task {
            let dir = tempDir ()
            let store = FileVersionStore dir :> IVersionStore
            let v = VersionRecord.Create(AnnotationKind.Tool, "search", "v2")
            do! store.SaveAsync v
            let! all = store.GetAllAsync()
            Assert.AreEqual(1, all.Length)
            Assert.AreEqual(VersionStatus.Draft, all.[0].Status)
            let! ok = store.UpdateStatusAsync(v.Id, VersionStatus.Active)
            Assert.IsTrue(ok)
            let! forSearch = store.GetForTargetAsync(AnnotationKind.Tool, "search")
            Assert.AreEqual(VersionStatus.Active, forSearch.[0].Status)
        }).GetAwaiter().GetResult()

/// Same coverage as FileStoreTests but against the ADO.NET (SQLite) backend, proving
/// the feedback stores have full parity across InMemory / File / Database modes.
[<TestClass>]
type DatabaseStoreTests() =

    [<TestMethod>]
    member _.``Turn store round-trips via ADO.NET``() =
        (task {
            let factory = sqliteFactory ()
            let store = AdoTurnStore factory :> ITurnStore
            let turn =
                { TurnRecord.Empty with
                    TurnId = "t1"; SessionId = "s1"
                    ToolCalls = [ { Name = "search"; Version = Some "v1"; Input = "q"; Output = "r"; Provenance = Some (ToolProvenance.json "/a.json") } ] }
            do! store.SaveAsync turn
            let! loaded = store.GetAsync "t1"
            Assert.IsTrue(loaded.IsSome)
            Assert.AreEqual(1, loaded.Value.ToolCalls.Length)
            Assert.AreEqual("search", loaded.Value.ToolCalls.[0].Name)
            Assert.AreEqual(Some "v1", loaded.Value.ToolCalls.[0].Version)
            let! forSession = store.GetForSessionAsync "s1"
            Assert.AreEqual(1, forSession.Length)
        }).GetAwaiter().GetResult()

    [<TestMethod>]
    member _.``Annotation store round-trips, filters, updates status and deletes via ADO.NET``() =
        (task {
            let factory = sqliteFactory ()
            let store = AdoAnnotationStore factory :> IAnnotationStore
            let a1 = { Annotation.ForTool("search") with DescriptionAppend = Some "x" }
            let a2 = Annotation.ForAgent("writer")
            do! store.SaveAsync a1
            do! store.SaveAsync a2
            let! all = store.GetAllAsync()
            Assert.AreEqual(2, all.Length)
            let! forSearch = store.GetForTargetAsync(AnnotationKind.Tool, "search")
            Assert.AreEqual(1, forSearch.Length)
            Assert.AreEqual("search", forSearch.[0].TargetName)

            let! updated = store.UpdateStatusAsync(a1.Id, AnnotationStatus.Disabled)
            Assert.IsTrue(updated)
            let! reloaded = store.GetForTargetAsync(AnnotationKind.Tool, "search")
            Assert.AreEqual(AnnotationStatus.Disabled, reloaded.[0].Status)

            let! deleted = store.DeleteAsync a1.Id
            Assert.IsTrue(deleted)
            let! remaining = store.GetAllAsync()
            Assert.AreEqual(1, remaining.Length)
        }).GetAwaiter().GetResult()

    [<TestMethod>]
    member _.``Suggestion store round-trips and updates via ADO.NET``() =
        (task {
            let factory = sqliteFactory ()
            let store = AdoSuggestionStore factory :> ISuggestionStore
            let s = Suggestion.Improve(AnnotationKind.Tool, "search")
            do! store.SaveAsync s
            let! all = store.GetAllAsync()
            Assert.AreEqual(1, all.Length)
            let! fetched = store.GetAsync s.Id
            Assert.IsTrue(fetched.IsSome)
            let! ok = store.UpdateAsync { s with Status = SuggestionStatus.Confirmed }
            Assert.IsTrue(ok)
            let! after = store.GetAsync s.Id
            Assert.AreEqual(SuggestionStatus.Confirmed, after.Value.Status)
            let! missing = store.UpdateAsync (Suggestion.Improve(AnnotationKind.Tool, "ghost"))
            Assert.IsFalse(missing)
        }).GetAwaiter().GetResult()

    [<TestMethod>]
    member _.``Version store round-trips and updates status via ADO.NET``() =
        (task {
            let factory = sqliteFactory ()
            let store = AdoVersionStore factory :> IVersionStore
            let v = VersionRecord.Create(AnnotationKind.Tool, "search", "v2")
            do! store.SaveAsync v
            let! all = store.GetAllAsync()
            Assert.AreEqual(1, all.Length)
            Assert.AreEqual(VersionStatus.Draft, all.[0].Status)
            let! ok = store.UpdateStatusAsync(v.Id, VersionStatus.Active)
            Assert.IsTrue(ok)
            let! forSearch = store.GetForTargetAsync(AnnotationKind.Tool, "search")
            Assert.AreEqual(VersionStatus.Active, forSearch.[0].Status)
        }).GetAwaiter().GetResult()

    [<TestMethod>]
    member _.``Feedback service end-to-end loop works over Database mode``() =
        (task {
            let factory = sqliteFactory ()
            let svc = FeedbackService.Create(PersistenceMode.Database factory)
            let ann = Annotation.ForTool("search")
            do! (svc.AddAnnotationAsync ann :> Task)
            let! listed = svc.ListAnnotationsAsync()
            Assert.AreEqual(1, listed.Length)
            Assert.AreEqual("search", listed.[0].TargetName)
        }).GetAwaiter().GetResult()

[<TestClass>]
type AnalyzerTests() =

    [<TestMethod>]
    member _.``Negative feedback proposes an active annotation per tool``() =
        (task {
            let turn =
                { TurnRecord.Empty with
                    TurnId = "t1"
                    ToolCalls = [ { Name = "search"; Version = None; Input = "q"; Output = "r"; Provenance = Some (ToolProvenance.json "/a.json") } ] }
            let feedback =
                { Id = Guid.NewGuid(); TurnId = "t1"; SessionId = "s1"; UserId = "u1"
                  Sentiment = FeedbackSentiment.Negative; Comment = Some "too verbose"
                  CreatedAt = DateTimeOffset.UtcNow; Metadata = Map.empty }
            let analyzer = HeuristicFeedbackAnalyzer() :> IFeedbackAnalyzer
            let! proposals = analyzer.AnalyzeAsync turn feedback
            Assert.AreEqual(1, proposals.Length)
            let p = proposals.[0]
            Assert.AreEqual(AnnotationKind.Tool, p.Annotation.Kind)
            Assert.AreEqual("search", p.Annotation.TargetName)
            Assert.AreEqual(AnnotationStatus.Active, p.Annotation.Status)
            StringAssert.StartsWith(p.Annotation.Source, "feedback:")
            StringAssert.Contains(p.Annotation.DescriptionAppend |> Option.defaultValue "", "too verbose")
        }).GetAwaiter().GetResult()

    [<TestMethod>]
    member _.``Negative feedback with no tools proposes an agent annotation``() =
        (task {
            let turn = { TurnRecord.Empty with TurnId = "t1"; AgentName = "writer" }
            let feedback =
                { Id = Guid.NewGuid(); TurnId = "t1"; SessionId = "s1"; UserId = "u1"
                  Sentiment = FeedbackSentiment.Negative; Comment = Some "be friendlier"
                  CreatedAt = DateTimeOffset.UtcNow; Metadata = Map.empty }
            let analyzer = HeuristicFeedbackAnalyzer() :> IFeedbackAnalyzer
            let! proposals = analyzer.AnalyzeAsync turn feedback
            Assert.AreEqual(1, proposals.Length)
            Assert.AreEqual(AnnotationKind.Agent, proposals.[0].Annotation.Kind)
            Assert.AreEqual("writer", proposals.[0].Annotation.TargetName)
            StringAssert.Contains(proposals.[0].Annotation.GuidanceAppend |> Option.defaultValue "", "be friendlier")
        }).GetAwaiter().GetResult()

    [<TestMethod>]
    member _.``Positive feedback proposes nothing``() =
        (task {
            let turn = { TurnRecord.Empty with TurnId = "t1" }
            let feedback =
                { Id = Guid.NewGuid(); TurnId = "t1"; SessionId = "s1"; UserId = "u1"
                  Sentiment = FeedbackSentiment.Positive; Comment = None
                  CreatedAt = DateTimeOffset.UtcNow; Metadata = Map.empty }
            let analyzer = HeuristicFeedbackAnalyzer() :> IFeedbackAnalyzer
            let! proposals = analyzer.AnalyzeAsync turn feedback
            Assert.AreEqual(0, proposals.Length)
        }).GetAwaiter().GetResult()

[<TestClass>]
type FeedbackServiceTests() =

    [<TestMethod>]
    member _.``End-to-end: record turn, submit negative feedback, overlay annotation``() =
        (task {
            let svc = FeedbackService.InMemory()
            let turn =
                { TurnRecord.Empty with
                    TurnId = "t1"; SessionId = "s1"
                    ToolCalls = [ { Name = "search"; Version = None; Input = "q"; Output = "r"; Provenance = None } ] }
            do! svc.RecordTurnAsync turn
            let feedback =
                { Id = Guid.NewGuid(); TurnId = "t1"; SessionId = "s1"; UserId = "u1"
                  Sentiment = FeedbackSentiment.Negative; Comment = Some "be concise"
                  CreatedAt = DateTimeOffset.UtcNow; Metadata = Map.empty }
            let! proposals = svc.SubmitFeedbackAsync feedback
            Assert.AreEqual(1, proposals.Length)

            let! annotations = svc.ListAnnotationsAsync()
            Assert.AreEqual(1, annotations.Length)

            let tool = echoTool "search"
            let! tools = svc.ApplyToolAnnotationsAsync [ tool ]
            Assert.AreEqual(1, tools.Length)
            StringAssert.Contains(tools.[0].Description, "be concise")
        }).GetAwaiter().GetResult()

    [<TestMethod>]
    member _.``Dropping an annotation reverts the tool to its legacy behaviour``() =
        (task {
            let svc = FeedbackService.InMemory()
            let ann = { Annotation.ForTool("search") with DescriptionAppend = Some "Be concise." }
            let! stored = svc.AddAnnotationAsync ann
            let tool = echoTool "search"
            let! adjusted = svc.ApplyToolAnnotationsAsync [ tool ]
            StringAssert.Contains(adjusted.[0].Description, "Be concise.")

            let! dropped = svc.DropAnnotationAsync stored.Id
            Assert.IsTrue(dropped)
            let! reverted = svc.ApplyToolAnnotationsAsync [ tool ]
            Assert.AreEqual("Echoes its input.", reverted.[0].Description)
        }).GetAwaiter().GetResult()

    [<TestMethod>]
    member _.``Promote, confirm and deprecate a tool version``() =
        (task {
            let dir = tempDir ()
            let original = Path.Combine(dir, "search.json")
            File.WriteAllText(original,
                """{ "name": "search", "description": "Original.", "mode": "echo" }""")
            let svc = FeedbackService.InMemory()
            let ann =
                { Annotation.ForTool("search") with
                    DescriptionAppend = Some "Be concise."
                    Provenance = Some (ToolProvenance.json original) }
            let! _ = svc.AddAnnotationAsync ann

            let! version = svc.PromoteAsync(AnnotationKind.Tool, "search")
            Assert.AreEqual(VersionStatus.Draft, version.Status)
            Assert.AreEqual("v2", version.Version)
            Assert.IsTrue(version.Location.IsSome)
            Assert.IsTrue(File.Exists version.Location.Value)

            let! confirmed = svc.ConfirmVersionAsync(version.Id, false)
            Assert.IsTrue(confirmed)
            let! versions = svc.GetVersionsForTargetAsync(AnnotationKind.Tool, "search")
            Assert.AreEqual(VersionStatus.Active, versions.[0].Status)

            let! deprecated = svc.DeprecateVersionAsync version.Id
            Assert.IsTrue(deprecated)
            let! after = svc.GetVersionsForTargetAsync(AnnotationKind.Tool, "search")
            Assert.AreEqual(VersionStatus.Deprecated, after.[0].Status)
        }).GetAwaiter().GetResult()

    [<TestMethod>]
    member _.``Confirm with replaceLegacy deprecates prior active versions``() =
        (task {
            let svc = FeedbackService.InMemory()
            let ann = { Annotation.ForTool("search") with DescriptionAppend = Some "g" }
            let! _ = svc.AddAnnotationAsync ann
            let! v1 = svc.PromoteAsync(AnnotationKind.Tool, "search", version = "v2")
            let! v2 = svc.PromoteAsync(AnnotationKind.Tool, "search", version = "v3")
            let! _ = svc.ConfirmVersionAsync(v1.Id, false)
            let! _ = svc.ConfirmVersionAsync(v2.Id, true)
            let! versions = svc.GetVersionsForTargetAsync(AnnotationKind.Tool, "search")
            let byVersion = versions |> List.map (fun v -> v.Version, v.Status) |> Map.ofList
            Assert.AreEqual(VersionStatus.Deprecated, byVersion.["v2"])
            Assert.AreEqual(VersionStatus.Active, byVersion.["v3"])
        }).GetAwaiter().GetResult()

    [<TestMethod>]
    member _.``SubmitFeedback with unknown turn yields no proposals``() =
        (task {
            let svc = FeedbackService.InMemory()
            let feedback =
                { Id = Guid.NewGuid(); TurnId = "missing"; SessionId = "s1"; UserId = "u1"
                  Sentiment = FeedbackSentiment.Negative; Comment = None
                  CreatedAt = DateTimeOffset.UtcNow; Metadata = Map.empty }
            let! proposals = svc.SubmitFeedbackAsync feedback
            Assert.AreEqual(0, proposals.Length)
        }).GetAwaiter().GetResult()

[<TestClass>]
type ConversationFeedbackTests() =

    [<TestMethod>]
    member _.``Detects negative reaction phrases``() =
        match ConversationFeedback.detect "No, that's wrong, try again please" with
        | Some(FeedbackSentiment.Negative, comment) -> StringAssert.Contains(comment, "wrong")
        | other -> Assert.Fail(sprintf "expected negative, got %A" other)

    [<TestMethod>]
    member _.``Detects positive reaction phrases``() =
        match ConversationFeedback.detect "Perfect, thank you!" with
        | Some(FeedbackSentiment.Positive, _) -> ()
        | other -> Assert.Fail(sprintf "expected positive, got %A" other)

    [<TestMethod>]
    member _.``Negative wins when both signals present``() =
        match ConversationFeedback.detect "thanks but that's not what I asked" with
        | Some(FeedbackSentiment.Negative, _) -> ()
        | other -> Assert.Fail(sprintf "expected negative, got %A" other)

    [<TestMethod>]
    member _.``Neutral text yields no signal``() =
        Assert.IsTrue((ConversationFeedback.detect "Please summarise the third paragraph.").IsNone)
        Assert.IsTrue((ConversationFeedback.detect "").IsNone)

[<TestClass>]
type SuggestionEngineTests() =

    let neg (turnId: string) (sessionId: string) (comment: string) =
        { Id = Guid.NewGuid(); TurnId = turnId; SessionId = sessionId; UserId = "u1"
          Sentiment = FeedbackSentiment.Negative; Comment = Some comment
          CreatedAt = DateTimeOffset.UtcNow; Metadata = Map.empty }

    [<TestMethod>]
    member _.``Aggregates negative feedback across sessions into one tool suggestion``() =
        let turn1 =
            { TurnRecord.Empty with
                TurnId = "t1"; SessionId = "s1"
                ToolCalls = [ { Name = "search"; Version = None; Input = "q"; Output = "r"; Provenance = Some (ToolProvenance.json "/a.json") } ] }
        let turn2 = { turn1 with TurnId = "t2"; SessionId = "s2" }
        let fb = [ neg "t1" "s1" "too verbose"; neg "t2" "s2" "missed the point" ]
        let suggestions = SuggestionEngine.generate [] fb [ turn1; turn2 ]
        Assert.AreEqual(1, suggestions.Length)
        let s = suggestions.[0]
        Assert.AreEqual(AnnotationKind.Tool, s.Kind)
        Assert.AreEqual("search", s.TargetName)
        Assert.AreEqual(2, s.NegativeCount)
        Assert.AreEqual(2, s.SupportingSessions.Length)
        StringAssert.Contains(s.ProposedAnnotation.Value.DescriptionAppend |> Option.defaultValue "", "too verbose")

    [<TestMethod>]
    member _.``No tools used produces an agent suggestion``() =
        let turn = { TurnRecord.Empty with TurnId = "t1"; SessionId = "s1"; AgentName = "writer" }
        let suggestions = SuggestionEngine.generate [] [ neg "t1" "s1" "be friendlier" ] [ turn ]
        Assert.AreEqual(1, suggestions.Length)
        Assert.AreEqual(AnnotationKind.Agent, suggestions.[0].Kind)
        Assert.AreEqual("writer", suggestions.[0].TargetName)

    [<TestMethod>]
    member _.``Existing open suggestion is not duplicated``() =
        let turn =
            { TurnRecord.Empty with
                TurnId = "t1"; SessionId = "s1"
                ToolCalls = [ { Name = "search"; Version = None; Input = "q"; Output = "r"; Provenance = None } ] }
        let existing = { Suggestion.Improve(AnnotationKind.Tool, "search") with Status = SuggestionStatus.Confirmed }
        let suggestions = SuggestionEngine.generate [ existing ] [ neg "t1" "s1" "x" ] [ turn ]
        Assert.AreEqual(0, suggestions.Length)

    [<TestMethod>]
    member _.``Rejected suggestion allows a fresh proposal``() =
        let turn =
            { TurnRecord.Empty with
                TurnId = "t1"; SessionId = "s1"
                ToolCalls = [ { Name = "search"; Version = None; Input = "q"; Output = "r"; Provenance = None } ] }
        let existing = { Suggestion.Improve(AnnotationKind.Tool, "search") with Status = SuggestionStatus.Rejected }
        let suggestions = SuggestionEngine.generate [ existing ] [ neg "t1" "s1" "x" ] [ turn ]
        Assert.AreEqual(1, suggestions.Length)

    [<TestMethod>]
    member _.``Positive feedback drives no suggestions``() =
        let turn =
            { TurnRecord.Empty with
                TurnId = "t1"; SessionId = "s1"
                ToolCalls = [ { Name = "search"; Version = None; Input = "q"; Output = "r"; Provenance = None } ] }
        let pos =
            { Id = Guid.NewGuid(); TurnId = "t1"; SessionId = "s1"; UserId = "u1"
              Sentiment = FeedbackSentiment.Positive; Comment = None
              CreatedAt = DateTimeOffset.UtcNow; Metadata = Map.empty }
        Assert.AreEqual(0, (SuggestionEngine.generate [] [ pos ] [ turn ]).Length)

[<TestClass>]
type SuggestionPipelineTests() =

    [<TestMethod>]
    member _.``Implicit capture persists conversation feedback bound to the prior turn``() =
        (task {
            let svc = FeedbackService.InMemory()
            let turn =
                { TurnRecord.Empty with
                    TurnId = "t1"; SessionId = "s1"
                    ToolCalls = [ { Name = "search"; Version = None; Input = "q"; Output = "r"; Provenance = None } ] }
            do! svc.RecordTurnAsync turn
            let! captured = svc.CaptureImplicitFeedbackAsync("t1", "s1", "u1", "no, that's wrong")
            Assert.IsTrue(captured.IsSome)
            Assert.AreEqual(FeedbackSentiment.Negative, captured.Value.Sentiment)
            Assert.AreEqual(FeedbackSource.Conversation, FeedbackSource.ofFeedback captured.Value)
        }).GetAwaiter().GetResult()

    [<TestMethod>]
    member _.``Implicit capture ignores neutral text and empty turn id``() =
        (task {
            let svc = FeedbackService.InMemory()
            let! none1 = svc.CaptureImplicitFeedbackAsync("t1", "s1", "u1", "please continue")
            Assert.IsTrue(none1.IsNone)
            let! none2 = svc.CaptureImplicitFeedbackAsync("", "s1", "u1", "that's wrong")
            Assert.IsTrue(none2.IsNone)
        }).GetAwaiter().GetResult()

    [<TestMethod>]
    member _.``Generate, confirm and reject suggestions across sessions``() =
        (task {
            let svc = FeedbackService.InMemory()
            // Two turns in different sessions both using the same tool, both with negative feedback.
            let turn1 =
                { TurnRecord.Empty with
                    TurnId = "t1"; SessionId = "s1"
                    ToolCalls = [ { Name = "search"; Version = None; Input = "q"; Output = "r"; Provenance = None } ] }
            let turn2 = { turn1 with TurnId = "t2"; SessionId = "s2" }
            do! svc.RecordTurnAsync turn1
            do! svc.RecordTurnAsync turn2
            let mkFb t s c =
                { Id = Guid.NewGuid(); TurnId = t; SessionId = s; UserId = "u1"
                  Sentiment = FeedbackSentiment.Negative; Comment = Some c
                  CreatedAt = DateTimeOffset.UtcNow; Metadata = Map.empty }
            let! _ = svc.SubmitFeedbackAsync(mkFb "t1" "s1" "too verbose")
            let! _ = svc.SubmitFeedbackAsync(mkFb "t2" "s2" "missed the point")

            let! generated = svc.GenerateSuggestionsAsync()
            Assert.AreEqual(1, generated.Length)
            Assert.AreEqual(2, generated.[0].NegativeCount)

            // Idempotent: a second pass proposes nothing new.
            let! again = svc.GenerateSuggestionsAsync()
            Assert.AreEqual(0, again.Length)

            let sid = generated.[0].Id
            let! confirmed = svc.ConfirmSuggestionAsync sid
            Assert.IsTrue(confirmed)
            let! confirmedList = svc.GetSuggestionsByStatusAsync SuggestionStatus.Confirmed
            Assert.AreEqual(1, confirmedList.Length)

            // Confirming again is a no-op (not in Proposed state).
            let! reconfirm = svc.ConfirmSuggestionAsync sid
            Assert.IsFalse(reconfirm)

            let! rejected = svc.RejectSuggestionAsync sid
            Assert.IsTrue(rejected)
            let! rejectedList = svc.GetSuggestionsByStatusAsync SuggestionStatus.Rejected
            Assert.AreEqual(1, rejectedList.Length)
        }).GetAwaiter().GetResult()

    [<TestMethod>]
    member _.``Confirmed suggestion promotes from its annotation into a materialised version``() =
        (task {
            let dir = tempDir ()
            let original = Path.Combine(dir, "search.json")
            File.WriteAllText(original,
                """{ "name": "search", "description": "Original.", "mode": "echo" }""")
            let svc = FeedbackService.InMemory()
            let ann =
                { Annotation.ForTool("search") with
                    DescriptionAppend = Some "Distilled guidance."
                    Provenance = Some (ToolProvenance.json original) }
            let! version = svc.PromoteFromAnnotationsAsync(AnnotationKind.Tool, "search", [ ann ])
            Assert.AreEqual(VersionStatus.Draft, version.Status)
            Assert.IsTrue(version.Location.IsSome)
            Assert.IsTrue(File.Exists version.Location.Value)
            let content = File.ReadAllText version.Location.Value
            StringAssert.Contains(content, "Distilled guidance.")
        }).GetAwaiter().GetResult()
