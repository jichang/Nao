namespace Nao.Semantics.Rdf

open System
open System.Threading
open System.Threading.Tasks
open Nao.Semantics

type ElkReasonerServiceRequest =
    { Profile: OwlProfile
      Assumptions: OwlSemanticAssumptions
      Ontology: OwlOntologyIdentity
      Configuration: OwlReasonerConfiguration
      Operation: OwlReasoningOperation
      Statements: RdfStatement list
      Budget: OwlReasoningBudget }

type ElkReasonerServiceResponse =
    { EngineId: string
      EngineVersion: string
      ConfigurationDigest: string
      OntologyContentDigest: string
      ResponseBytes: int
      Outcome: OwlReasoningOutcome }

type ElkReasonerTransport =
    private
        { ExecuteAsync: ElkReasonerServiceRequest -> CancellationToken -> Task<ElkReasonerServiceResponse> }

[<RequireQualifiedAccess>]
module ElkReasonerTransport =
    let create executeAsync = { ExecuteAsync = executeAsync }

[<RequireQualifiedAccess>]
module ElkReasonerEndpoint =
    let private operations =
        Set.ofList
            [ OwlReasoningOperationKind.CheckConsistency
              OwlReasoningOperationKind.Classify
              OwlReasoningOperationKind.Realize
              OwlReasoningOperationKind.CheckEntailment ]

    let private proof =
        function
        | OwlConsistencyResult.Inconsistent value -> value
        | OwlConsistencyResult.Consistent -> None

    let private resultProofs =
        function
        | OwlReasoningResult.Consistency consistency -> proof consistency |> Option.toList
        | OwlReasoningResult.Entailment(OwlEntailmentResult.Entailed value) -> value |> Option.toList
        | OwlReasoningResult.Entailment(OwlEntailmentResult.Disentailed value) -> value |> Option.toList
        | OwlReasoningResult.Entailment _
        | OwlReasoningResult.Classification _
        | OwlReasoningResult.Realization _ -> []

    let private resultStatements =
        function
        | OwlReasoningResult.Classification statements
        | OwlReasoningResult.Realization statements -> statements
        | OwlReasoningResult.Consistency _
        | OwlReasoningResult.Entailment _ -> []

    let private resultMatches operation result =
        match operation, result with
        | OwlReasoningOperation.CheckConsistency, OwlReasoningResult.Consistency _
        | OwlReasoningOperation.Classify, OwlReasoningResult.Classification _
        | OwlReasoningOperation.Realize, OwlReasoningResult.Realization _
        | OwlReasoningOperation.CheckEntailment _, OwlReasoningResult.Entailment _ -> true
        | _ -> false

    let private validInference engineId engineVersion statement =
        match statement.Origin with
        | RdfStatementOrigin.Inferred provenance ->
            provenance.EngineId = engineId && provenance.EngineVersion = engineVersion
        | _ -> false

    let private validateResult capabilities request result =
        let budget = OwlReasoningRequest.budget request
        let operation = OwlReasoningRequest.operation request
        let statements = resultStatements result
        let proofs = resultProofs result
        let proofSteps = proofs |> List.sumBy (fun value -> value.Steps.Length)
        let engineId = OwlReasonerCapabilities.engineId capabilities
        let engineVersion = OwlReasonerCapabilities.engineVersion capabilities

        if not (resultMatches operation result) then
            OwlReasoningOutcome.Failed("invalid_response", "Reasoner result does not match the requested operation.")
        elif statements.Length > OwlReasoningBudget.maxOutputStatements budget then
            OwlReasoningOutcome.OutputLimitExceeded(OwlReasoningBudget.maxOutputStatements budget, statements.Length)
        elif proofSteps > OwlReasoningBudget.maxProofSteps budget then
            OwlReasoningOutcome.ProofLimitExceeded(OwlReasoningBudget.maxProofSteps budget, proofSteps)
        elif
            OwlReasonerCapabilities.proofCapability capabilities = OwlProofCapability.None
            && not (List.isEmpty proofs)
        then
            OwlReasoningOutcome.Failed("invalid_response", "Reasoner returned proof data it declared unsupported.")
        elif statements |> List.exists (validInference engineId engineVersion >> not) then
            OwlReasoningOutcome.Failed(
                "invalid_response",
                "Every materialized conclusion must carry matching inferred-statement provenance."
            )
        elif
            proofs
            |> List.exists (fun value ->
                let stepIds = value.Steps |> List.map _.Id
                let known = stepIds |> Set.ofList

                stepIds.Length <> known.Count
                || value.RootStepIds |> List.exists (fun root -> not (Set.contains root known))
                || value.Steps
                   |> List.exists (fun step ->
                       String.IsNullOrWhiteSpace step.Id
                       || String.IsNullOrWhiteSpace step.RuleOrAxiomId
                       || not (validInference engineId engineVersion step.Conclusion)))
        then
            OwlReasoningOutcome.Failed("invalid_response", "Reasoner returned a malformed derivation graph.")
        else
            OwlReasoningOutcome.Completed result

    let private provenance capabilities request startedAt duration cacheHit =
        let ontology = OwlReasoningRequest.ontology request
        let configuration = OwlReasoningRequest.configuration request

        { ExecutionId = Guid.NewGuid()
          EngineId = OwlReasonerCapabilities.engineId capabilities
          EngineVersion = OwlReasonerCapabilities.engineVersion capabilities
          Profile = OwlReasoningRequest.profile request
          Assumptions = OwlReasoningRequest.assumptions request
          Operation = OwlReasoningRequest.operation request |> OwlReasoningOperation.kind
          ConfigurationId = OwlReasonerConfiguration.id configuration
          ConfigurationVersion = OwlReasonerConfiguration.version configuration
          ConfigurationDigest = OwlReasonerConfiguration.digest configuration
          OntologyDatasetId = OwlOntologyIdentity.datasetId ontology
          OntologyDatasetRevision = OwlOntologyIdentity.datasetRevision ontology
          OntologyVersionIri = OwlOntologyIdentity.versionIri ontology
          OntologyContentDigest = OwlOntologyIdentity.contentDigest ontology
          Imports = OwlOntologyIdentity.imports ontology
          StartedAt = startedAt
          Duration = duration
          CacheHit = cacheHit }

    let create
        engineVersion
        proofCapability
        (timeProvider: TimeProvider)
        (cache: OwlReasoningCache)
        (transport: ElkReasonerTransport)
        =
        if String.IsNullOrWhiteSpace engineVersion || engineVersion <> engineVersion.Trim() then
            invalidArg (nameof engineVersion) "ELK reasoner version must be canonical and non-blank."

        let capabilities =
            OwlReasonerCapabilities.create
                "elk"
                engineVersion
                (Set.singleton OwlProfile.Owl2EL)
                operations
                proofCapability
                OwlReasonerExecutionMode.RemoteService

        let executeAsync authorization dataset request cancellationToken =
            task {
                let startedAt = timeProvider.GetUtcNow()
                let timestamp = timeProvider.GetTimestamp()
                RdfDataset.requireOwner authorization dataset |> ignore

                let ontology = OwlReasoningRequest.ontology request
                let budget = OwlReasoningRequest.budget request
                let operation = OwlReasoningRequest.operation request
                let operationKind = OwlReasoningOperation.kind operation

                let finish cacheHit outcome =
                    { Provenance =
                        provenance capabilities request startedAt (timeProvider.GetElapsedTime timestamp) cacheHit
                      Outcome = outcome }

                if OwlReasoningRequest.profile request <> OwlProfile.Owl2EL then
                    return
                        finish
                            false
                            (OwlReasoningOutcome.Unsupported(operationKind, "ELK supports the OWL 2 EL profile only."))
                elif not (OwlReasonerCapabilities.operations capabilities |> Set.contains operationKind) then
                    return
                        finish
                            false
                            (OwlReasoningOutcome.Unsupported(
                                operationKind,
                                "Operation is not supported by this ELK adapter."
                            ))
                elif
                    RdfDataset.id dataset <> OwlOntologyIdentity.datasetId ontology
                    || RdfDataset.revision dataset <> OwlOntologyIdentity.datasetRevision ontology
                then
                    return
                        finish
                            false
                            (OwlReasoningOutcome.Failed(
                                "ontology_identity_mismatch",
                                "Dataset identity or revision does not match the immutable ontology identity."
                            ))
                else
                    let statements = RdfDataset.asserted dataset
                    let observedInput = statements.Length

                    if observedInput > OwlReasoningBudget.maxInputStatements budget then
                        return
                            finish
                                false
                                (OwlReasoningOutcome.InputLimitExceeded(
                                    OwlReasoningBudget.maxInputStatements budget,
                                    observedInput
                                ))
                    else
                        let cacheKey = OwlReasoningCacheKey.create capabilities request
                        let! cached = cache.TryGetAsync cacheKey cancellationToken

                        match cached with
                        | Some result -> return finish true (validateResult capabilities request result)
                        | None ->
                            use timeout = CancellationTokenSource.CreateLinkedTokenSource cancellationToken
                            timeout.CancelAfter(OwlReasoningBudget.timeout budget)

                            let serviceRequest =
                                { Profile = OwlReasoningRequest.profile request
                                  Assumptions = OwlReasoningRequest.assumptions request
                                  Ontology = ontology
                                  Configuration = OwlReasoningRequest.configuration request
                                  Operation = operation
                                  Statements = statements
                                  Budget = budget }

                            try
                                let! response = transport.ExecuteAsync serviceRequest timeout.Token
                                let configuration = OwlReasoningRequest.configuration request

                                let outcome =
                                    if
                                        response.EngineId <> OwlReasonerCapabilities.engineId capabilities
                                        || response.EngineVersion <> OwlReasonerCapabilities.engineVersion capabilities
                                        || response.ConfigurationDigest <> OwlReasonerConfiguration.digest configuration
                                        || response.OntologyContentDigest <> OwlOntologyIdentity.contentDigest ontology
                                    then
                                        OwlReasoningOutcome.Failed(
                                            "identity_mismatch",
                                            "Reasoner response identity does not match the immutable request."
                                        )
                                    elif response.ResponseBytes < 0 then
                                        OwlReasoningOutcome.Failed(
                                            "invalid_response",
                                            "Reasoner response byte count cannot be negative."
                                        )
                                    elif response.ResponseBytes > OwlReasoningBudget.maxResponseBytes budget then
                                        OwlReasoningOutcome.ResponseSizeLimitExceeded(
                                            OwlReasoningBudget.maxResponseBytes budget,
                                            response.ResponseBytes
                                        )
                                    else
                                        match response.Outcome with
                                        | OwlReasoningOutcome.Completed result ->
                                            validateResult capabilities request result
                                        | value -> value

                                match outcome with
                                | OwlReasoningOutcome.Completed result ->
                                    do! cache.StoreAsync cacheKey result cancellationToken
                                | _ -> ()

                                return finish false outcome
                            with :? OperationCanceledException when not cancellationToken.IsCancellationRequested ->
                                return finish false OwlReasoningOutcome.TimedOut
            }

        { Capabilities = capabilities
          ExecuteAsync = executeAsync }
