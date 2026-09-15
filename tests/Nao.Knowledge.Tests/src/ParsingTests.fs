namespace Nao.Knowledge.Tests

open System
open System.Text
open System.Threading
open System.Threading.Tasks
open Microsoft.VisualStudio.TestTools.UnitTesting
open Nao.Knowledge
open Nao.Agents

[<TestClass; TestCategory("Security"); TestCategory("FailClosed")>]
type ParsingTests() =
    let now = DateTimeOffset(2026, 9, 14, 0, 0, 0, TimeSpan.Zero)

    let authorization =
        SecurityPrincipal.create (TenantId.parse "parser-tenant") (UserId.parse "parser-user") []
        |> fun principal -> AuthorizationScope.tryCreate principal None (WorkspaceId.parse "parser-workspace") None
        |> Option.get

    let documentVersionId contentHash =
        KnowledgeDocumentVersionId.derive (KnowledgeDocumentId.parse "parser-fixture") 1 contentHash

    let request mediaType bytes =
        let hash = ContentHash.computeBytes bytes

        { Authorization = authorization
          DocumentVersionId = documentVersionId hash
          ContentHash = hash
          MediaType = mediaType
          FileName = Some "fixture.txt"
          Content = bytes
          Lifecycle = KnowledgeLifecycle.active None false
          RequestedAt = now }

    let limits =
        { MaximumInputBytes = 4096L
          MaximumOutputCharacters = 4096
          MaximumUnits = 100
          MaximumExpansionRatio = 4.0
          Timeout = TimeSpan.FromSeconds 1L }

    let parse service request =
        service.ParseAsync request CancellationToken.None |> _.GetAwaiter().GetResult()

    [<TestMethod>]
    member _.``Registry prefers matching signature over declared media type``() =
        let parser name mediaTypes signatures =
            { Descriptor =
                { Parser = ComponentVersion.create name "1"
                  MediaTypes = Set.ofList mediaTypes
                  Signatures = signatures }
              ParseAsync =
                fun _ _ _ _ ->
                    Task.FromResult(
                        Ok
                            { Units = []
                              Language = None
                              RequiresOcr = false
                              Trust = ParsedContentTrust.UntrustedSource }
                    ) }

        let declared = parser "declared" [ "text/plain" ] []

        let signature =
            parser
                "signature"
                [ "application/pdf" ]
                [ { Offset = 0
                    Bytes = Encoding.ASCII.GetBytes "%PDF-" } ]

        let registry = ParserRegistry.create [ declared; signature ]
        let discovered = registry.Discover "text/plain" (Encoding.ASCII.GetBytes "%PDF-1.7")

        Assert.IsTrue(discovered.IsSome)
        Assert.AreEqual(ComponentVersion.create "signature" "1", discovered.Value.Descriptor.Parser)

    [<TestMethod>]
    member _.``Plain text preserves source spans while normalizing output``() =
        let source = "Cafe\u0301\r\nline two\r\n\r\nsecond"
        let bytes = Encoding.UTF8.GetBytes source

        let service =
            KnowledgeParsingService.create (ParserRegistry.create [ PlainTextParser.create () ]) limits None (fun () ->
                now)

        match parse service (request "text/plain; charset=utf-8" bytes) with
        | ParseOutcome.Parsed(parser, document) ->
            Assert.AreEqual(ComponentVersion.create "nao.parser.text" "1", parser)
            Assert.AreEqual(ParsedContentTrust.UntrustedSource, document.Trust)
            Assert.AreEqual(2, document.Units.Length)

            let first = document.Units.[0]
            Assert.AreEqual("Café\nline two", first.Text)
            Assert.AreEqual(0L, first.Location.ByteOffset)
            Assert.AreEqual(int64 (Encoding.UTF8.GetByteCount "Cafe\u0301\r\nline two"), first.Location.ByteLength)
            Assert.AreEqual(0, first.Location.CharacterOffset)
            Assert.AreEqual("Cafe\u0301\r\nline two".Length, first.Location.CharacterLength)
            Assert.AreEqual(Some 1, first.Location.LineStart)
            Assert.AreEqual(Some 2, first.Location.LineEnd)
            Assert.AreEqual("untrusted", first.Metadata.["content.trust"])
        | outcome -> Assert.Fail($"Expected parsed text, got {outcome}.")

    [<TestMethod>]
    member _.``Malformed UTF8 and unsupported media types are quarantined``() =
        let service =
            KnowledgeParsingService.create (ParserRegistry.create [ PlainTextParser.create () ]) limits None (fun () ->
                now)

        let malformed = parse service (request "text/plain" [| 0xffuy; 0xfeuy |])
        let unsupported = parse service (request "application/octet-stream" [| 1uy; 2uy |])

        match malformed with
        | ParseOutcome.Quarantined item ->
            Assert.AreEqual(ParserRejectionKind.Malformed, item.Kind)
            Assert.IsTrue(item.Parser.IsSome)
        | _ -> Assert.Fail "Expected malformed text quarantine."

        match unsupported with
        | ParseOutcome.Quarantined item ->
            Assert.AreEqual(ParserRejectionKind.Unsupported, item.Kind)
            Assert.IsTrue(item.Parser.IsNone)
        | _ -> Assert.Fail "Expected unsupported-content quarantine."

    [<TestMethod>]
    member _.``Oversized input and expanded output fail before release``() =
        let textParser = PlainTextParser.create ()

        let expandedParser =
            { textParser with
                ParseAsync =
                    fun _ _ _ _ ->
                        Task.FromResult(
                            Ok
                                { Units =
                                    [ { Kind = ContentUnitKind.Paragraph
                                        Text = String.replicate 20 "x"
                                        Location = SourceLocation.create 0L 1L 0 1 None None None None
                                        Metadata = Map.empty } ]
                                  Language = None
                                  RequiresOcr = false
                                  Trust = ParsedContentTrust.UntrustedSource }
                        ) }

        let strict =
            { limits with
                MaximumInputBytes = 2L
                MaximumOutputCharacters = 10
                MaximumExpansionRatio = 2.0 }

        let inputService =
            KnowledgeParsingService.create (ParserRegistry.create [ textParser ]) strict None (fun () -> now)

        let outputService =
            KnowledgeParsingService.create (ParserRegistry.create [ expandedParser ]) strict None (fun () -> now)

        match parse inputService (request "text/plain" (Encoding.UTF8.GetBytes "abc")) with
        | ParseOutcome.Quarantined item ->
            Assert.AreEqual(ParserRejectionKind.Suspicious, item.Kind)
            Assert.IsTrue(item.Parser.IsNone)
        | _ -> Assert.Fail "Expected oversized input quarantine."

        match parse outputService (request "text/plain" [| byte 'x' |]) with
        | ParseOutcome.Quarantined item ->
            Assert.AreEqual(ParserRejectionKind.Suspicious, item.Kind)
            Assert.IsTrue(item.Parser.IsSome)
        | _ -> Assert.Fail "Expected expanded output quarantine."

    [<TestMethod>]
    member _.``Parser timeout is quarantined while caller cancellation propagates``() =
        let parser =
            { PlainTextParser.create () with
                ParseAsync =
                    fun _ _ _ cancellationToken ->
                        task {
                            do! Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken)

                            return
                                Ok
                                    { Units = []
                                      Language = None
                                      RequiresOcr = false
                                      Trust = ParsedContentTrust.UntrustedSource }
                        } }

        let service =
            KnowledgeParsingService.create
                (ParserRegistry.create [ parser ])
                { limits with
                    Timeout = TimeSpan.FromMilliseconds 20.0 }
                None
                (fun () -> now)

        match parse service (request "text/plain" [| byte 'x' |]) with
        | ParseOutcome.Quarantined item ->
            Assert.AreEqual(ParserRejectionKind.Suspicious, item.Kind)
            StringAssert.Contains(item.Message, "timeout")
        | _ -> Assert.Fail "Expected parser-timeout quarantine."

        use cancellation = new CancellationTokenSource()
        cancellation.Cancel()

        Assert.ThrowsExactly<OperationCanceledException>(fun () ->
            service.ParseAsync (request "text/plain" [| byte 'x' |]) cancellation.Token
            |> _.GetAwaiter().GetResult()
            |> ignore)
        |> ignore
