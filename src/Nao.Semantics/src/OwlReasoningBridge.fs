namespace Nao.Semantics

open System
open System.Text.Json
open Nao.Agents
open Nao.Reasoning

[<RequireQualifiedAccess>]
module OwlReasoningBridge =
    let private profileName =
        function
        | OwlProfile.Owl2EL -> "owl2-el"
        | OwlProfile.Owl2QL -> "owl2-ql"
        | OwlProfile.Owl2RL -> "owl2-rl"
        | OwlProfile.Owl2DL -> "owl2-dl"

    let private operationName =
        function
        | OwlReasoningOperationKind.CheckConsistency -> "consistency"
        | OwlReasoningOperationKind.Classify -> "classification"
        | OwlReasoningOperationKind.Realize -> "realization"
        | OwlReasoningOperationKind.CheckEntailment -> "entailment"

    let semanticMode profile =
        ReasoningSemanticMode.create
            ReasoningWorldAssumption.OpenWorld
            ReasoningNegationSemantics.Classical
            ReasoningMonotonicity.Monotonic
            (Set.singleton "no-unique-name")
            (Map.ofList [ "owl-profile", profileName profile ])

    let private resource =
        function
        | RdfResource.Iri iri -> $"<{RdfIri.value iri}>"
        | RdfResource.BlankNode identifier -> $"_:{RdfBlankNodeId.value identifier}"

    let private term =
        function
        | RdfTerm.Resource value -> resource value
        | RdfTerm.Literal literal ->
            let lexical = JsonSerializer.Serialize(RdfLiteral.lexicalForm literal)

            match RdfLiteral.languageTag literal, RdfLiteral.datatype literal with
            | Some language, None -> $"{lexical}@{language}"
            | None, Some datatype -> $"{lexical}^^<{RdfIri.value datatype}>"
            | None, None -> lexical
            | Some _, Some _ -> invalidOp "RDF literal cannot have both language and datatype."

    let private statementArtifact (statement: RdfStatement) =
        let graph =
            match statement.Graph with
            | None -> ""
            | Some(RdfGraphName.Iri iri) -> $" <{RdfIri.value iri}>"
            | Some(RdfGraphName.BlankNode identifier) -> $" _:{RdfBlankNodeId.value identifier}"

        ReasoningArtifact.create
            "application/n-quads"
            $"{resource statement.Subject} <{RdfIri.value statement.Predicate}> {term statement.Object}{graph} ."

    let private statementCitations (statement: RdfStatement) =
        match statement.Origin with
        | RdfStatementOrigin.Asserted provenance ->
            provenance.EvidenceAddresses
            |> List.map (fun address -> ReasoningCitation.create address provenance.SourceVersionId (Some statement.Id))
        | RdfStatementOrigin.AuthorizedUpdate provenance ->
            [ ReasoningCitation.create
                  $"nao:rdf-update/{Uri.EscapeDataString provenance.OperationId}"
                  $"dataset-revision/{provenance.PriorDatasetRevision + 1L}"
                  (Some statement.Id) ]
        | RdfStatementOrigin.Inferred provenance ->
            provenance.PremiseStatementIds
            |> List.map (fun premiseId ->
                ReasoningCitation.create
                    $"nao:rdf-statement/{Uri.EscapeDataString premiseId}"
                    $"{provenance.EngineId}/{provenance.EngineVersion}"
                    (Some statement.Id))

    let private proof (value: OwlProof) : ReasoningProof =
        let steps =
            value.Steps
            |> List.map (fun step ->
                { Id = step.Id
                  RuleOrAxiomId = step.RuleOrAxiomId
                  PremiseIds = step.PremiseIds
                  Conclusion = statementArtifact step.Conclusion })

        { Steps = steps
          RootStepIds = value.RootStepIds }

    let private evidence (proofValue: OwlProof option) (statements: RdfStatement list) : ReasoningEvidence =
        let citations = statements |> List.collect statementCitations |> List.distinct

        { Proof = proofValue |> Option.map proof
          Explanation = None
          UnsatCore = []
          Citations = citations }

    let private conclusion (statement: RdfStatement) : ReasoningConclusion =
        { Artifact = statementArtifact statement
          Citations = statementCitations statement }

    let private statusArtifact value =
        ReasoningArtifact.create "application/vnd.nao.reasoning-status+text" value

    let private pinnedInputs (provenance: OwlReasoningProvenance) =
        let ontology =
            ReasoningInputIdentity.create
                "owl-ontology"
                provenance.OntologyDatasetId
                (string provenance.OntologyDatasetRevision)
                provenance.OntologyContentDigest

        let configuration =
            ReasoningInputIdentity.create
                "reasoner-configuration"
                provenance.ConfigurationId
                provenance.ConfigurationVersion
                provenance.ConfigurationDigest

        let imports =
            provenance.Imports
            |> Map.toList
            |> List.map (fun (ontologyIri, identity) ->
                ReasoningInputIdentity.create
                    "owl-import"
                    (RdfIri.value ontologyIri)
                    (identity |> OwlImportIdentity.versionIri |> RdfIri.value)
                    (OwlImportIdentity.contentDigest identity))

        ontology :: configuration :: imports

    let private engine (provenance: OwlReasoningProvenance) =
        let capabilities =
            Set.ofList [ profileName provenance.Profile; operationName provenance.Operation ]

        ReasoningEngineIdentity.create
            provenance.EngineId
            provenance.EngineVersion
            provenance.ConfigurationDigest
            ReasoningExecutionMode.RemoteService
            capabilities

    let private proofSteps (value: OwlProof option) =
        match value with
        | Some proofValue -> proofValue.Steps.Length
        | None -> 0

    let private resultMetrics (result: OwlReasoningResult) =
        match result with
        | OwlReasoningResult.Classification statements
        | OwlReasoningResult.Realization statements -> statements.Length, 0
        | OwlReasoningResult.Consistency(OwlConsistencyResult.Inconsistent value) -> 0, proofSteps value
        | OwlReasoningResult.Entailment(OwlEntailmentResult.Entailed value)
        | OwlReasoningResult.Entailment(OwlEntailmentResult.Disentailed value) -> 0, proofSteps value
        | OwlReasoningResult.Consistency OwlConsistencyResult.Consistent
        | OwlReasoningResult.Entailment OwlEntailmentResult.NotEntailed
        | OwlReasoningResult.Entailment(OwlEntailmentResult.Unknown _) -> 0, 0

    let private completed (result: OwlReasoningResult) =
        match result with
        | OwlReasoningResult.Consistency OwlConsistencyResult.Consistent ->
            ReasoningOutcome.Proven(
                [ { Artifact = statusArtifact "consistent"
                    Citations = [] } ],
                ReasoningEvidence.Empty
            )
        | OwlReasoningResult.Consistency(OwlConsistencyResult.Inconsistent proofValue) ->
            ReasoningOutcome.Inconsistent(evidence proofValue [])
        | OwlReasoningResult.Classification statements
        | OwlReasoningResult.Realization statements ->
            ReasoningOutcome.Proven(statements |> List.map conclusion, evidence None statements)
        | OwlReasoningResult.Entailment(OwlEntailmentResult.Entailed proofValue) ->
            ReasoningOutcome.Proven(
                [ { Artifact = statusArtifact "entailed"
                    Citations = [] } ],
                evidence proofValue []
            )
        | OwlReasoningResult.Entailment(OwlEntailmentResult.Disentailed proofValue) ->
            ReasoningOutcome.Disproven(evidence proofValue [])
        | OwlReasoningResult.Entailment OwlEntailmentResult.NotEntailed ->
            ReasoningOutcome.Unknown(ReasoningUnknownReason.NotProven, ReasoningEvidence.Empty)
        | OwlReasoningResult.Entailment(OwlEntailmentResult.Unknown reason) ->
            ReasoningOutcome.Unknown(ReasoningUnknownReason.EngineReturnedUnknown reason, ReasoningEvidence.Empty)

    let private mapLimit resource limit observed =
        ReasoningOutcome.ResourceLimitExceeded(resource, int64 limit, int64 observed)

    let private outcome =
        function
        | OwlReasoningOutcome.Completed result -> completed result
        | OwlReasoningOutcome.Unsupported(operation, detail) ->
            ReasoningOutcome.Unsupported(operationName operation, detail)
        | OwlReasoningOutcome.TimedOut -> ReasoningOutcome.TimedOut
        | OwlReasoningOutcome.InputLimitExceeded(limit, observed) ->
            mapLimit ReasoningResourceLimit.Inputs limit observed
        | OwlReasoningOutcome.OutputLimitExceeded(limit, observed) ->
            mapLimit ReasoningResourceLimit.Conclusions limit observed
        | OwlReasoningOutcome.ProofLimitExceeded(limit, observed) ->
            mapLimit ReasoningResourceLimit.ProofSteps limit observed
        | OwlReasoningOutcome.ResponseSizeLimitExceeded(limit, observed) ->
            mapLimit ReasoningResourceLimit.ResponseBytes limit observed
        | OwlReasoningOutcome.Failed(code, detail) -> ReasoningOutcome.Failed(code, detail)

    let private requirementsIssue (request: ReasoningRequest) (engineIdentity: ReasoningEngineIdentity) =
        let requirements = ReasoningRequest.engineRequirements request

        match ReasoningEngineRequirements.engineId requirements with
        | Some required when required <> ReasoningEngineIdentity.id engineIdentity ->
            Some $"Required engine '{required}' was not used."
        | _ when
            not (
                ReasoningEngineRequirements.allowedExecutionModes requirements
                |> Set.contains (ReasoningEngineIdentity.executionMode engineIdentity)
            )
            ->
            Some "Reasoner execution mode does not satisfy the request."
        | _ ->
            let missing =
                Set.difference
                    (ReasoningEngineRequirements.requiredCapabilities requirements)
                    (ReasoningEngineIdentity.capabilities engineIdentity)

            if Set.isEmpty missing then
                None
            else
                let names = String.concat ", " missing
                Some $"Reasoner is missing required capabilities: {names}."

    let private evidenceProofSteps evidence =
        evidence.Proof
        |> Option.map (fun value -> value.Steps.Length)
        |> Option.defaultValue 0

    let private enforceLimits request mappedOutcome =
        let limits = ReasoningRequest.limits request

        let conclusions, proofSteps =
            match mappedOutcome with
            | ReasoningOutcome.Proven(values, evidence) -> values.Length, evidenceProofSteps evidence
            | ReasoningOutcome.Disproven evidence
            | ReasoningOutcome.Unknown(_, evidence)
            | ReasoningOutcome.Inconsistent evidence -> 0, evidenceProofSteps evidence
            | _ -> 0, 0

        if conclusions > ReasoningLimits.maxConclusions limits then
            ReasoningOutcome.ResourceLimitExceeded(
                ReasoningResourceLimit.Conclusions,
                int64 (ReasoningLimits.maxConclusions limits),
                int64 conclusions
            )
        elif proofSteps > ReasoningLimits.maxProofSteps limits then
            ReasoningOutcome.ResourceLimitExceeded(
                ReasoningResourceLimit.ProofSteps,
                int64 (ReasoningLimits.maxProofSteps limits),
                int64 proofSteps
            )
        elif ReasoningLimits.maxExternalCalls limits < 1 then
            ReasoningOutcome.ResourceLimitExceeded(ReasoningResourceLimit.ExternalCalls, 0L, 1L)
        else
            mappedOutcome

    let adapt (authorization: AuthorizationScope) (request: ReasoningRequest) (execution: OwlReasoningExecution) =
        ReasoningRequest.requireOwner authorization request |> ignore

        let provenance = execution.Provenance
        let expectedMode = semanticMode provenance.Profile
        let expectedInputs = pinnedInputs provenance
        let engineIdentity = engine provenance

        if ReasoningRequest.semanticMode request <> expectedMode then
            invalidArg (nameof request) "Common reasoning semantics do not match the OWL execution."

        if ReasoningRequest.pinnedInputs request <> expectedInputs then
            invalidArg (nameof request) "Common reasoning inputs do not match the OWL execution identities."

        let mappedOutcome =
            match requirementsIssue request engineIdentity with
            | Some issue -> ReasoningOutcome.Unsupported("engine-requirements", issue)
            | None when ReasoningEngineRequirements.requireUnsatCore (ReasoningRequest.engineRequirements request) ->
                ReasoningOutcome.Unsupported("unsat-core", "The ELK service adapter does not expose unsat cores.")
            | None ->
                let mapped = outcome execution.Outcome

                let missingProof =
                    match mapped with
                    | ReasoningOutcome.Proven(_, evidence)
                    | ReasoningOutcome.Disproven evidence
                    | ReasoningOutcome.Unknown(_, evidence)
                    | ReasoningOutcome.Inconsistent evidence -> evidence.Proof.IsNone
                    | _ -> false

                if
                    ReasoningEngineRequirements.requireProof (ReasoningRequest.engineRequirements request)
                    && missingProof
                then
                    ReasoningOutcome.Unsupported("proof", "The completed OWL result did not include a proof.")
                else
                    mapped
            |> enforceLimits request

        let conclusions, proofCount =
            match execution.Outcome with
            | OwlReasoningOutcome.Completed result -> resultMetrics result
            | _ -> 0, 0

        let usage =
            { WallTime = provenance.Duration
              CpuTime = None
              PeakMemoryBytes = None
              Steps = None
              Conclusions = conclusions
              ProofSteps = proofCount
              ResponseBytes = None
              ExternalCalls = Some 1 }

        let audit =
            { TraceId = provenance.ExecutionId
              StartedAt = provenance.StartedAt
              CompletedAt = provenance.StartedAt + provenance.Duration
              CacheHit = provenance.CacheHit }

        let artifacts =
            match mappedOutcome with
            | ReasoningOutcome.Proven(conclusions, evidence) ->
                (conclusions |> List.map _.Artifact) @ (evidence.Explanation |> Option.toList)
            | ReasoningOutcome.Disproven evidence
            | ReasoningOutcome.Unknown(_, evidence)
            | ReasoningOutcome.Inconsistent evidence -> evidence.Explanation |> Option.toList
            | _ -> []

        ReasoningResult.create request engineIdentity mappedOutcome usage audit artifacts
