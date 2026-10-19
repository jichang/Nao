namespace Nao.Eval

open System
open System.Security.Cryptography
open System.Text
open Nao.Agents

[<RequireQualifiedAccess>]
type EvalTurnRole =
    | System
    | User
    | Assistant
    | Tool of toolName: string

type EvalTurn =
    { Id: string
      Role: EvalTurnRole
      Content: string }

[<RequireQualifiedAccess>]
type EvalExpectedTerminalState =
    | Succeeded
    | Failed
    | Cancelled
    | TimedOut
    | Denied
    | LimitExceeded
    | Indeterminate

type EvalExpectedToolCall =
    { Tool: string
      Operation: string
      ArgumentsSha256: string option
      MinimumCount: int
      MaximumCount: int }

type EvalExpectedArtifact =
    { MediaType: string
      ContentSha256: string option
      RequiredMetadata: Map<string, string> }

type EvalExpectedCitation =
    { SourceId: string
      ChunkId: string option }

type EvalExpectedBudgets =
    { MaximumDuration: TimeSpan option
      MaximumInputTokens: int option
      MaximumOutputTokens: int option
      MaximumToolCalls: int option
      MaximumCostUsd: decimal option }

type EvalExpectedBehavior =
    { Answer: string option
      Properties: Map<string, string>
      ToolCalls: EvalExpectedToolCall list
      ForbiddenActions: string list
      Artifacts: EvalExpectedArtifact list
      Citations: EvalExpectedCitation list
      Budgets: EvalExpectedBudgets option
      TerminalStates: Set<EvalExpectedTerminalState> }

[<RequireQualifiedAccess>]
module EvalExpectedBehavior =
    let answer value =
        { Answer = Some value
          Properties = Map.empty
          ToolCalls = []
          ForbiddenActions = []
          Artifacts = []
          Citations = []
          Budgets = None
          TerminalStates = Set.singleton EvalExpectedTerminalState.Succeeded }

    let openEnded =
        { Answer = None
          Properties = Map.empty
          ToolCalls = []
          ForbiddenActions = []
          Artifacts = []
          Citations = []
          Budgets = None
          TerminalStates = Set.singleton EvalExpectedTerminalState.Succeeded }

type EvalReviewApproval =
    { Reviewer: string
      ReviewedAt: DateTimeOffset
      Rationale: string }

[<RequireQualifiedAccess>]
type EvalReviewStatus =
    | Unreviewed
    | Approved of EvalReviewApproval
    | Rejected of reviewer: string * reviewedAt: DateTimeOffset * rationale: string

type EvalSanitizationReview =
    { SanitizedBy: string
      SanitizedAt: DateTimeOffset
      AppliedRules: string list
      Approval: EvalReviewApproval }

[<RequireQualifiedAccess>]
type EvalCaseSource =
    | RepositoryAuthored
    | ProductionDerived of EvalSanitizationReview

type EvalCase =
    private
        { SchemaVersion: int
          Id: string
          Description: string
          Turns: EvalTurn list
          ExpectedBehavior: EvalExpectedBehavior
          Labels: Set<string>
          Metadata: Map<string, string>
          Owner: string
          ReviewStatus: EvalReviewStatus
          ExpiresAt: DateTimeOffset option
          Source: EvalCaseSource }

[<RequireQualifiedAccess>]
module EvalCase =
    [<Literal>]
    let CurrentSchemaVersion = 1

    let private required fieldName value =
        if String.IsNullOrWhiteSpace value || value <> value.Trim() then
            invalidArg fieldName "Value must be non-blank and have no surrounding whitespace."

        value

    let private validateHash fieldName (value: string option) =
        match value with
        | None -> ()
        | Some value when
            value.Length = 64
            && value
               |> Seq.forall (fun character -> Char.IsDigit character || character >= 'a' && character <= 'f')
            ->
            ()
        | Some _ -> invalidArg fieldName "Content hash must be canonical lowercase SHA-256."

    let private validateExpected (expected: EvalExpectedBehavior) =
        expected.Answer
        |> Option.iter (fun value ->
            if isNull value then
                nullArg (nameof expected.Answer))

        expected.Properties
        |> Map.iter (fun key value ->
            required (nameof expected.Properties) key |> ignore

            if isNull value then
                nullArg (nameof expected.Properties))

        for toolCall in expected.ToolCalls do
            required (nameof toolCall.Tool) toolCall.Tool |> ignore
            required (nameof toolCall.Operation) toolCall.Operation |> ignore
            validateHash (nameof toolCall.ArgumentsSha256) toolCall.ArgumentsSha256

            if toolCall.MinimumCount < 0 || toolCall.MaximumCount < toolCall.MinimumCount then
                invalidArg (nameof expected.ToolCalls) "Expected tool-call counts must form a non-negative range."

        expected.ForbiddenActions
        |> List.iter (required (nameof expected.ForbiddenActions) >> ignore)

        for artifact in expected.Artifacts do
            required (nameof artifact.MediaType) artifact.MediaType |> ignore
            validateHash (nameof artifact.ContentSha256) artifact.ContentSha256

        for citation in expected.Citations do
            required (nameof citation.SourceId) citation.SourceId |> ignore
            citation.ChunkId |> Option.iter (required (nameof citation.ChunkId) >> ignore)

        expected.Budgets
        |> Option.iter (fun budgets ->
            match budgets.MaximumDuration with
            | Some value when value <= TimeSpan.Zero ->
                invalidArg (nameof budgets.MaximumDuration) "Evaluation budgets must be positive."
            | _ -> ()

            for name, value in
                [ nameof budgets.MaximumInputTokens, budgets.MaximumInputTokens
                  nameof budgets.MaximumOutputTokens, budgets.MaximumOutputTokens
                  nameof budgets.MaximumToolCalls, budgets.MaximumToolCalls ] do
                match value with
                | Some count when count <= 0 -> invalidArg name "Evaluation budgets must be positive."
                | _ -> ()

            match budgets.MaximumCostUsd with
            | Some value when value <= 0M ->
                invalidArg (nameof budgets.MaximumCostUsd) "Evaluation budgets must be positive."
            | _ -> ())

        if Set.isEmpty expected.TerminalStates then
            invalidArg (nameof expected.TerminalStates) "Expected behavior requires at least one terminal state."

    let private validateReview =
        function
        | EvalReviewStatus.Unreviewed -> ()
        | EvalReviewStatus.Approved approval ->
            required (nameof approval.Reviewer) approval.Reviewer |> ignore
            required (nameof approval.Rationale) approval.Rationale |> ignore
        | EvalReviewStatus.Rejected(reviewer, _, rationale) ->
            required (nameof reviewer) reviewer |> ignore
            required (nameof rationale) rationale |> ignore

    let private validateSource reviewStatus =
        function
        | EvalCaseSource.RepositoryAuthored -> ()
        | EvalCaseSource.ProductionDerived sanitization ->
            required (nameof sanitization.SanitizedBy) sanitization.SanitizedBy |> ignore

            if List.isEmpty sanitization.AppliedRules then
                invalidArg
                    (nameof sanitization.AppliedRules)
                    "Production-derived cases require at least one explicit sanitization rule."

            sanitization.AppliedRules
            |> List.iter (required (nameof sanitization.AppliedRules) >> ignore)

            required (nameof sanitization.Approval.Reviewer) sanitization.Approval.Reviewer
            |> ignore

            required (nameof sanitization.Approval.Rationale) sanitization.Approval.Rationale
            |> ignore

            match reviewStatus with
            | EvalReviewStatus.Approved approval when approval = sanitization.Approval -> ()
            | _ ->
                invalidArg
                    (nameof reviewStatus)
                    "Production-derived cases require the exact approval attached to their sanitization review."

    let create
        owner
        id
        description
        (turns: EvalTurn list)
        (expected: EvalExpectedBehavior)
        (labels: Set<string>)
        (metadata: Map<string, string>)
        reviewStatus
        expiresAt
        source
        =
        required (nameof owner) owner |> ignore
        required (nameof id) id |> ignore

        if isNull description then
            nullArg (nameof description)

        if isNull (box turns) || List.isEmpty turns then
            invalidArg (nameof turns) "Evaluation cases require at least one turn."

        let duplicateTurnIds =
            turns |> List.countBy _.Id |> List.exists (fun (_, count) -> count > 1)

        if duplicateTurnIds then
            invalidArg (nameof turns) "Evaluation turn identities must be unique within a case."

        for turn in turns do
            required (nameof turn.Id) turn.Id |> ignore

            if isNull turn.Content then
                nullArg (nameof turn.Content)

            match turn.Role with
            | EvalTurnRole.Tool toolName -> required (nameof toolName) toolName |> ignore
            | _ -> ()

        labels |> Set.iter (required (nameof labels) >> ignore)

        metadata
        |> Map.iter (fun key value ->
            required (nameof metadata) key |> ignore

            if isNull value then
                nullArg (nameof metadata))

        validateExpected expected
        validateReview reviewStatus
        validateSource reviewStatus source

        { SchemaVersion = CurrentSchemaVersion
          Id = id
          Description = description
          Turns = turns
          ExpectedBehavior = expected
          Labels = labels
          Metadata = metadata
          Owner = owner
          ReviewStatus = reviewStatus
          ExpiresAt = expiresAt
          Source = source }

    let repository id input expected =
        create
            "nao"
            id
            ""
            [ { Id = "input"
                Role = EvalTurnRole.User
                Content = input } ]
            (EvalExpectedBehavior.answer expected)
            Set.empty
            Map.empty
            EvalReviewStatus.Unreviewed
            None
            EvalCaseSource.RepositoryAuthored

    let repositoryOpenEnded id description input =
        create
            "nao"
            id
            description
            [ { Id = "input"
                Role = EvalTurnRole.User
                Content = input } ]
            EvalExpectedBehavior.openEnded
            Set.empty
            Map.empty
            EvalReviewStatus.Unreviewed
            None
            EvalCaseSource.RepositoryAuthored

    let withLabels labels case =
        create
            case.Owner
            case.Id
            case.Description
            case.Turns
            case.ExpectedBehavior
            (Set.ofList labels)
            case.Metadata
            case.ReviewStatus
            case.ExpiresAt
            case.Source

    let withMetadata metadata case =
        create
            case.Owner
            case.Id
            case.Description
            case.Turns
            case.ExpectedBehavior
            case.Labels
            metadata
            case.ReviewStatus
            case.ExpiresAt
            case.Source

    let withDescription description case =
        create
            case.Owner
            case.Id
            description
            case.Turns
            case.ExpectedBehavior
            case.Labels
            case.Metadata
            case.ReviewStatus
            case.ExpiresAt
            case.Source

    let schemaVersion case = case.SchemaVersion
    let id case = case.Id
    let description case = case.Description
    let turns case = case.Turns
    let expectedBehavior case = case.ExpectedBehavior
    let labels case = case.Labels
    let metadata case = case.Metadata
    let owner case = case.Owner
    let reviewStatus case = case.ReviewStatus
    let expiresAt case = case.ExpiresAt
    let source case = case.Source

    let input case =
        case.Turns
        |> List.tryFindBack (fun turn -> turn.Role = EvalTurnRole.User)
        |> Option.map _.Content
        |> Option.defaultWith (fun () -> invalidOp "Evaluation case has no user turn to execute.")

type EvalFixtureStep =
    { Sequence: int
      Adapter: string
      AdapterVersion: string
      Operation: string
      InputSha256: string }

type EvalFixture =
    { Id: string
      Version: string
      Setup: EvalFixtureStep list
      Teardown: EvalFixtureStep list }

type EvalDataset =
    private
        { SchemaVersion: int
          Id: Guid
          Owner: AuthorizationIdentity
          CreatedAt: DateTimeOffset
          Name: string
          Revision: int
          ContentSha256: string
          Cases: EvalCase list
          Fixtures: EvalFixture list }

[<RequireQualifiedAccess>]
module EvalDataset =
    [<Literal>]
    let CurrentSchemaVersion = 1

    let private appendValue (builder: StringBuilder) (value: string) =
        builder.Append(value.Length).Append(':').Append(value).Append('|') |> ignore

    let private appendOptional builder =
        function
        | Some value ->
            appendValue builder "some"
            appendValue builder value
        | None -> appendValue builder "none"

    let private roleKey =
        function
        | EvalTurnRole.System -> "system"
        | EvalTurnRole.User -> "user"
        | EvalTurnRole.Assistant -> "assistant"
        | EvalTurnRole.Tool name -> "tool:" + name

    let private reviewKey =
        function
        | EvalReviewStatus.Unreviewed -> "unreviewed"
        | EvalReviewStatus.Approved approval ->
            String.concat
                "|"
                [ "approved"
                  approval.Reviewer
                  approval.ReviewedAt.ToString("O")
                  approval.Rationale ]
        | EvalReviewStatus.Rejected(reviewer, reviewedAt, rationale) ->
            String.concat "|" [ "rejected"; reviewer; reviewedAt.ToString("O"); rationale ]

    let private sourceKey =
        function
        | EvalCaseSource.RepositoryAuthored -> "repository"
        | EvalCaseSource.ProductionDerived review ->
            String.concat
                "|"
                [ "production"
                  review.SanitizedBy
                  review.SanitizedAt.ToString("O")
                  String.concat "," review.AppliedRules
                  review.Approval.Reviewer
                  review.Approval.ReviewedAt.ToString("O")
                  review.Approval.Rationale ]

    let private terminalKey =
        function
        | EvalExpectedTerminalState.Succeeded -> "succeeded"
        | EvalExpectedTerminalState.Failed -> "failed"
        | EvalExpectedTerminalState.Cancelled -> "cancelled"
        | EvalExpectedTerminalState.TimedOut -> "timed-out"
        | EvalExpectedTerminalState.Denied -> "denied"
        | EvalExpectedTerminalState.LimitExceeded -> "limit-exceeded"
        | EvalExpectedTerminalState.Indeterminate -> "indeterminate"

    let private computeContentHash name revision (cases: EvalCase list) (fixtures: EvalFixture list) =
        let canonical = StringBuilder()
        appendValue canonical (string CurrentSchemaVersion)
        appendValue canonical name
        appendValue canonical (string revision)

        for case in cases do
            appendValue canonical (string (EvalCase.schemaVersion case))
            appendValue canonical (EvalCase.id case)
            appendValue canonical (EvalCase.description case)
            appendValue canonical (EvalCase.owner case)
            appendValue canonical (case |> EvalCase.reviewStatus |> reviewKey)
            appendOptional canonical (case |> EvalCase.expiresAt |> Option.map _.ToString("O"))
            appendValue canonical (case |> EvalCase.source |> sourceKey)

            for turn in EvalCase.turns case do
                appendValue canonical turn.Id
                appendValue canonical (roleKey turn.Role)
                appendValue canonical turn.Content

            let expected = EvalCase.expectedBehavior case
            appendOptional canonical expected.Answer

            for KeyValue(key, value) in expected.Properties do
                appendValue canonical key
                appendValue canonical value

            for toolCall in expected.ToolCalls do
                appendValue canonical toolCall.Tool
                appendValue canonical toolCall.Operation
                appendOptional canonical toolCall.ArgumentsSha256
                appendValue canonical (string toolCall.MinimumCount)
                appendValue canonical (string toolCall.MaximumCount)

            expected.ForbiddenActions |> List.iter (appendValue canonical)

            for artifact in expected.Artifacts do
                appendValue canonical artifact.MediaType
                appendOptional canonical artifact.ContentSha256

                for KeyValue(key, value) in artifact.RequiredMetadata do
                    appendValue canonical key
                    appendValue canonical value

            for citation in expected.Citations do
                appendValue canonical citation.SourceId
                appendOptional canonical citation.ChunkId

            match expected.Budgets with
            | None -> appendValue canonical "no-budgets"
            | Some budgets ->
                appendValue canonical "budgets"
                appendOptional canonical (budgets.MaximumDuration |> Option.map _.Ticks.ToString())
                appendOptional canonical (budgets.MaximumInputTokens |> Option.map string)
                appendOptional canonical (budgets.MaximumOutputTokens |> Option.map string)
                appendOptional canonical (budgets.MaximumToolCalls |> Option.map string)
                appendOptional canonical (budgets.MaximumCostUsd |> Option.map (fun value -> value.ToString("G29")))

            expected.TerminalStates
            |> Seq.map terminalKey
            |> Seq.sort
            |> Seq.iter (appendValue canonical)

            EvalCase.labels case |> Seq.iter (appendValue canonical)

            for KeyValue(key, value) in EvalCase.metadata case do
                appendValue canonical key
                appendValue canonical value

        for fixture: EvalFixture in fixtures do
            appendValue canonical fixture.Id
            appendValue canonical fixture.Version

            for phase, steps in [ "setup", fixture.Setup; "teardown", fixture.Teardown ] do
                appendValue canonical phase

                for step in steps do
                    appendValue canonical (string step.Sequence)
                    appendValue canonical step.Adapter
                    appendValue canonical step.AdapterVersion
                    appendValue canonical step.Operation
                    appendValue canonical step.InputSha256

        canonical
        |> _.ToString()
        |> Encoding.UTF8.GetBytes
        |> SHA256.HashData
        |> Convert.ToHexString
        |> _.ToLowerInvariant()

    let private validateFixtureStep fieldName (step: EvalFixtureStep) =
        if step.Sequence < 0 then
            invalidArg fieldName "Fixture step sequence must be non-negative."

        for value in [ step.Adapter; step.AdapterVersion; step.Operation ] do
            if String.IsNullOrWhiteSpace value || value <> value.Trim() then
                invalidArg fieldName "Fixture identities and operations must be canonical non-blank values."

        if
            step.InputSha256.Length <> 64
            || step.InputSha256
               |> Seq.exists (fun character -> not (Char.IsDigit character || character >= 'a' && character <= 'f'))
        then
            invalidArg fieldName "Fixture input hashes must be canonical lowercase SHA-256."

    let private build
        schemaVersion
        id
        owner
        createdAt
        name
        revision
        expectedHash
        (cases: EvalCase list)
        (fixtures: EvalFixture list)
        =
        if schemaVersion <> CurrentSchemaVersion then
            invalidArg (nameof schemaVersion) "Unsupported evaluation dataset schema version."

        if id = Guid.Empty then
            invalidArg (nameof id) "Evaluation dataset identity cannot be empty."

        if String.IsNullOrWhiteSpace name || name <> name.Trim() then
            invalidArg (nameof name) "Evaluation dataset name must be canonical and non-blank."

        if revision <= 0 then
            invalidArg (nameof revision) "Evaluation dataset revision must be positive."

        if isNull (box cases) || List.isEmpty cases then
            invalidArg (nameof cases) "Evaluation datasets require at least one case."

        if cases |> List.countBy EvalCase.id |> List.exists (fun (_, count) -> count > 1) then
            invalidArg (nameof cases) "Evaluation case identities must be unique."

        if fixtures |> List.countBy _.Id |> List.exists (fun (_, count) -> count > 1) then
            invalidArg (nameof fixtures) "Evaluation fixture identities must be unique."

        for fixture: EvalFixture in fixtures do
            if
                String.IsNullOrWhiteSpace fixture.Id
                || String.IsNullOrWhiteSpace fixture.Version
            then
                invalidArg (nameof fixtures) "Fixture identity and version must be non-blank."

            fixture.Setup |> List.iter (validateFixtureStep (nameof fixture.Setup))
            fixture.Teardown |> List.iter (validateFixtureStep (nameof fixture.Teardown))

            let validateSequence fieldName (steps: EvalFixtureStep list) =
                let sequences = steps |> List.map _.Sequence

                if sequences <> [ 0 .. sequences.Length - 1 ] then
                    invalidArg fieldName "Fixture steps must use contiguous zero-based sequence numbers."

            validateSequence (nameof fixture.Setup) fixture.Setup
            validateSequence (nameof fixture.Teardown) fixture.Teardown

        let contentHash = computeContentHash name revision cases fixtures

        expectedHash
        |> Option.iter (fun expected ->
            if expected <> contentHash then
                invalidArg (nameof expectedHash) "Evaluation dataset content hash does not match its specification.")

        { SchemaVersion = schemaVersion
          Id = id
          Owner = owner
          CreatedAt = createdAt
          Name = name
          Revision = revision
          ContentSha256 = contentHash
          Cases = cases
          Fixtures = fixtures }

    let create owner name revision cases fixtures =
        build CurrentSchemaVersion (Guid.NewGuid()) owner DateTimeOffset.UtcNow name revision None cases fixtures

    let createAt id owner createdAt name revision cases fixtures =
        build CurrentSchemaVersion id owner createdAt name revision None cases fixtures

    let repository owner name cases = create owner name 1 cases []

    let revise createdAt cases fixtures dataset =
        build
            CurrentSchemaVersion
            dataset.Id
            dataset.Owner
            createdAt
            dataset.Name
            (dataset.Revision + 1)
            None
            cases
            fixtures

    let restore schemaVersion id owner createdAt name revision contentSha256 cases fixtures =
        build schemaVersion id owner createdAt name revision (Some contentSha256) cases fixtures

    let validate dataset =
        restore
            dataset.SchemaVersion
            dataset.Id
            dataset.Owner
            dataset.CreatedAt
            dataset.Name
            dataset.Revision
            dataset.ContentSha256
            dataset.Cases
            dataset.Fixtures
        |> ignore

    let schemaVersion dataset = dataset.SchemaVersion
    let id dataset = dataset.Id
    let owner dataset = dataset.Owner
    let createdAt dataset = dataset.CreatedAt
    let name dataset = dataset.Name
    let revision dataset = dataset.Revision
    let contentSha256 dataset = dataset.ContentSha256
    let cases dataset = dataset.Cases
    let fixtures dataset = dataset.Fixtures
