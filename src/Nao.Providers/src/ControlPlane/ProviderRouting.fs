namespace Nao.Providers

open System
open System.Security.Cryptography
open System.Text
open System.Threading.Tasks
open Nao.Agents

[<RequireQualifiedAccess>]
type ProviderRoutingStrategy =
    | StaticPriority of endpointIds: string list
    | DeterministicWeighted of endpointWeights: Map<string, decimal>

type ProviderCostLimit =
    private
        { Currency: string
          MaximumInputPerMillionTokens: decimal
          MaximumOutputPerMillionTokens: decimal }

[<RequireQualifiedAccess>]
module ProviderCostLimit =
    let create currency maximumInputPerMillionTokens maximumOutputPerMillionTokens =
        if
            String.IsNullOrWhiteSpace currency
            || currency <> currency.Trim().ToUpperInvariant()
        then
            invalidArg (nameof currency) "Routing cost currency must be an uppercase canonical code."

        if maximumInputPerMillionTokens < 0m || maximumOutputPerMillionTokens < 0m then
            invalidArg (nameof maximumInputPerMillionTokens) "Routing cost limits cannot be negative."

        { Currency = currency
          MaximumInputPerMillionTokens = maximumInputPerMillionTokens
          MaximumOutputPerMillionTokens = maximumOutputPerMillionTokens }

type ProviderRoutingRequest =
    private
        { PolicyVersion: string
          SelectionKey: string
          Requirements: ProviderRequirements
          AllowedQualityTiers: Set<ProviderQualityTier>
          AllowedLatencyClasses: Set<ProviderLatencyClass>
          AllowedPrivacyClasses: Set<ProviderPrivacyClass>
          AllowedResidencyRegions: Set<string>
          CostLimit: ProviderCostLimit option
          Strategy: ProviderRoutingStrategy }

[<RequireQualifiedAccess>]
module ProviderRoutingRequest =
    let create
        policyVersion
        selectionKey
        requirements
        allowedQualityTiers
        allowedLatencyClasses
        allowedPrivacyClasses
        allowedResidencyRegions
        costLimit
        strategy
        =
        if String.IsNullOrWhiteSpace policyVersion || policyVersion <> policyVersion.Trim() then
            invalidArg (nameof policyVersion) "Routing policy version must be canonical and non-blank."

        if String.IsNullOrWhiteSpace selectionKey then
            invalidArg (nameof selectionKey) "Deterministic routing requires a non-blank selection key."

        if Set.isEmpty allowedQualityTiers then
            invalidArg (nameof allowedQualityTiers) "At least one quality tier must be allowed."

        if Set.isEmpty allowedLatencyClasses then
            invalidArg (nameof allowedLatencyClasses) "At least one latency class must be allowed."

        if Set.isEmpty allowedPrivacyClasses then
            invalidArg (nameof allowedPrivacyClasses) "At least one privacy class must be allowed."

        if Set.isEmpty allowedResidencyRegions then
            invalidArg (nameof allowedResidencyRegions) "At least one residency region must be allowed."

        allowedResidencyRegions
        |> Set.iter (fun region ->
            if String.IsNullOrWhiteSpace region || region <> region.Trim() then
                invalidArg (nameof allowedResidencyRegions) "Residency regions must be canonical and non-blank.")

        match strategy with
        | ProviderRoutingStrategy.StaticPriority endpointIds ->
            if endpointIds.IsEmpty || endpointIds |> List.exists String.IsNullOrWhiteSpace then
                invalidArg (nameof strategy) "Static routing requires canonical endpoint identities."

            if endpointIds.Length <> (endpointIds |> Set.ofList |> Set.count) then
                invalidArg (nameof strategy) "Static routing endpoint identities must be unique."
        | ProviderRoutingStrategy.DeterministicWeighted weights ->
            if Map.isEmpty weights then
                invalidArg (nameof strategy) "Weighted routing requires at least one endpoint weight."

            weights
            |> Map.iter (fun endpointId weight ->
                if String.IsNullOrWhiteSpace endpointId || weight <= 0m then
                    invalidArg (nameof strategy) "Weighted routing requires canonical endpoints and positive weights.")

        { PolicyVersion = policyVersion
          SelectionKey = selectionKey
          Requirements = requirements
          AllowedQualityTiers = allowedQualityTiers
          AllowedLatencyClasses = allowedLatencyClasses
          AllowedPrivacyClasses = allowedPrivacyClasses
          AllowedResidencyRegions = allowedResidencyRegions
          CostLimit = costLimit
          Strategy = strategy }

[<RequireQualifiedAccess>]
type ProviderRoutingExclusion =
    | Compatibility of ProviderCompatibilityFailure list
    | QualityTier of ProviderQualityTier
    | LatencyClass of ProviderLatencyClass
    | PrivacyClass of ProviderPrivacyClass
    | ResidencyRegions of Set<string>
    | PriceCurrency of string
    | InputPrice of decimal
    | OutputPrice of decimal
    | NotConfiguredByPolicy

type ProviderRoutingCandidateEvidence =
    { EndpointId: string
      ModelId: string
      Exclusions: ProviderRoutingExclusion list }

type ProviderRoutingDecision =
    { PolicyVersion: string
      SelectedEndpointId: string option
      SelectedModelId: string option
      OrderedEligibleEndpointIds: string list
      Candidates: ProviderRoutingCandidateEvidence list }

type ProviderRoutingAudit =
    { Id: Guid
      Timestamp: DateTimeOffset
      Decision: ProviderRoutingDecision }

[<RequireQualifiedAccess>]
type ProviderHedgingPolicy =
    | Disabled
    | Bounded of maximumAdditionalRequests: int * delay: TimeSpan * maximumIncrementalCost: decimal

type ProviderHedgePlan =
    { PrimaryEndpointId: string option
      HedgeEndpointIds: string list
      Delay: TimeSpan
      MaximumIncrementalCost: decimal }

[<RequireQualifiedAccess>]
module ProviderHedgePlan =
    let create
        (policy: ProviderHedgingPolicy)
        (estimatedCostByEndpoint: Map<string, decimal>)
        (decision: ProviderRoutingDecision)
        =
        estimatedCostByEndpoint
        |> Map.iter (fun endpointId cost ->
            if cost < 0m then
                invalidArg
                    (nameof estimatedCostByEndpoint)
                    $"Estimated hedge cost for endpoint '{endpointId}' cannot be negative.")

        match policy with
        | ProviderHedgingPolicy.Disabled ->
            { PrimaryEndpointId = decision.SelectedEndpointId
              HedgeEndpointIds = []
              Delay = TimeSpan.Zero
              MaximumIncrementalCost = 0m }
        | ProviderHedgingPolicy.Bounded(maximumAdditionalRequests, delay, maximumIncrementalCost) ->
            if maximumAdditionalRequests <= 0 then
                invalidArg (nameof policy) "Hedging must permit at least one additional request."

            if delay < TimeSpan.Zero then
                invalidArg (nameof policy) "Hedging delay cannot be negative."

            if maximumIncrementalCost < 0m then
                invalidArg (nameof policy) "Hedging cost budget cannot be negative."

            let hedges, _ =
                decision.OrderedEligibleEndpointIds
                |> List.skip (if decision.SelectedEndpointId.IsSome then 1 else 0)
                |> List.fold
                    (fun ((selected: string list), cost) endpointId ->
                        match Map.tryFind endpointId estimatedCostByEndpoint with
                        | Some endpointCost when
                            selected.Length < maximumAdditionalRequests
                            && cost + endpointCost <= maximumIncrementalCost
                            ->
                            selected @ [ endpointId ], cost + endpointCost
                        | _ -> selected, cost)
                    ([], 0m)

            { PrimaryEndpointId = decision.SelectedEndpointId
              HedgeEndpointIds = hedges
              Delay = delay
              MaximumIncrementalCost = maximumIncrementalCost }

[<RequireQualifiedAccess>]
module ProviderRouter =
    let private policyExclusion (request: ProviderRoutingRequest) endpointId =
        match request.Strategy with
        | ProviderRoutingStrategy.StaticPriority endpointIds when not (List.contains endpointId endpointIds) ->
            [ ProviderRoutingExclusion.NotConfiguredByPolicy ]
        | ProviderRoutingStrategy.DeterministicWeighted weights when not (Map.containsKey endpointId weights) ->
            [ ProviderRoutingExclusion.NotConfiguredByPolicy ]
        | _ -> []

    let private constraintExclusions (request: ProviderRoutingRequest) observation =
        let model = ProviderCapabilityObservation.model observation
        let price = ProviderModelMetadata.price model

        [ if not (Set.contains (ProviderModelMetadata.qualityTier model) request.AllowedQualityTiers) then
              ProviderRoutingExclusion.QualityTier(ProviderModelMetadata.qualityTier model)

          if not (Set.contains (ProviderModelMetadata.latencyClass model) request.AllowedLatencyClasses) then
              ProviderRoutingExclusion.LatencyClass(ProviderModelMetadata.latencyClass model)

          if not (Set.contains (ProviderModelMetadata.privacyClass model) request.AllowedPrivacyClasses) then
              ProviderRoutingExclusion.PrivacyClass(ProviderModelMetadata.privacyClass model)

          if not (Set.isSubset (ProviderModelMetadata.residencyRegions model) request.AllowedResidencyRegions) then
              ProviderRoutingExclusion.ResidencyRegions(ProviderModelMetadata.residencyRegions model)

          match request.CostLimit with
          | Some limit when ProviderModelPrice.currency price <> limit.Currency ->
              ProviderRoutingExclusion.PriceCurrency(ProviderModelPrice.currency price)
          | Some limit ->
              if ProviderModelPrice.inputPerMillionTokens price > limit.MaximumInputPerMillionTokens then
                  ProviderRoutingExclusion.InputPrice(ProviderModelPrice.inputPerMillionTokens price)

              if ProviderModelPrice.outputPerMillionTokens price > limit.MaximumOutputPerMillionTokens then
                  ProviderRoutingExclusion.OutputPrice(ProviderModelPrice.outputPerMillionTokens price)
          | None -> () ]

    let private evidence (request: ProviderRoutingRequest) observation =
        let compatibility = ProviderCompatibility.evaluate request.Requirements observation

        { EndpointId = ProviderCapabilityObservation.endpointId observation
          ModelId =
            observation
            |> ProviderCapabilityObservation.model
            |> ProviderModelMetadata.modelId
          Exclusions =
            [ if not compatibility.IsEmpty then
                  ProviderRoutingExclusion.Compatibility compatibility
              yield! constraintExclusions request observation
              yield! policyExclusion request (ProviderCapabilityObservation.endpointId observation) ] }

    let private weightedScore (request: ProviderRoutingRequest) (weights: Map<string, decimal>) endpointId modelId =
        let material =
            $"{request.PolicyVersion}\u001f{request.SelectionKey}\u001f{endpointId}\u001f{modelId}"

        let digest = SHA256.HashData(Encoding.UTF8.GetBytes material)
        let sample = BitConverter.ToUInt64(digest, 0)
        let unit = (float sample + 1.0) / (float UInt64.MaxValue + 2.0)
        -Math.Log(unit) / float weights.[endpointId]

    let route (request: ProviderRoutingRequest) observations =
        let duplicateEndpoint =
            observations
            |> List.groupBy ProviderCapabilityObservation.endpointId
            |> List.tryFind (fun (_, values) -> values.Length > 1)

        duplicateEndpoint
        |> Option.iter (fun (endpointId, _) ->
            invalidArg (nameof observations) $"Routing candidates contain duplicate endpoint '{endpointId}'.")

        let candidates =
            observations
            |> List.map (fun observation -> observation, evidence request observation)

        let eligible =
            candidates |> List.filter (fun (_, candidate) -> candidate.Exclusions.IsEmpty)

        let ordered =
            match request.Strategy with
            | ProviderRoutingStrategy.StaticPriority endpointIds ->
                let priority =
                    endpointIds |> List.mapi (fun index endpointId -> endpointId, index) |> Map

                eligible
                |> List.sortBy (fun (observation, _) -> priority.[ProviderCapabilityObservation.endpointId observation])
            | ProviderRoutingStrategy.DeterministicWeighted weights ->
                eligible
                |> List.sortBy (fun (observation, candidate) ->
                    weightedScore request weights candidate.EndpointId candidate.ModelId,
                    ProviderCapabilityObservation.endpointId observation)

        let selected = ordered |> List.tryHead |> Option.map snd

        { PolicyVersion = request.PolicyVersion
          SelectedEndpointId = selected |> Option.map _.EndpointId
          SelectedModelId = selected |> Option.map _.ModelId
          OrderedEligibleEndpointIds = ordered |> List.map (snd >> _.EndpointId)
          Candidates = candidates |> List.map snd |> List.sortBy _.EndpointId }

    let routeAndRecordAsync
        (clock: unit -> DateTimeOffset)
        (recordAuditAsync: ProviderRoutingAudit -> Task)
        request
        observations
        =
        task {
            let decision = route request observations

            let audit =
                { Id = Guid.NewGuid()
                  Timestamp = clock ()
                  Decision = decision }

            do! recordAuditAsync audit
            return decision
        }
