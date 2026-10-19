namespace Nao.Eval.Evaluators

open System
open System.Threading.Tasks
open Nao.Eval

[<RequireQualifiedAccess>]
type JudgeAggregationPolicy =
    | Unanimous
    | Majority
    | MinimumAverageScore of minimum: float

module HighRiskEvaluation =
    let create policy deterministicEvaluators judges =
        if List.isEmpty deterministicEvaluators then
            invalidArg (nameof deterministicEvaluators) "High-risk evaluation requires deterministic validators."

        if
            deterministicEvaluators
            |> List.exists (fun evaluator -> evaluator.Authority <> EvaluatorAuthority.Deterministic)
        then
            invalidArg (nameof deterministicEvaluators) "Deterministic validators must declare deterministic authority."

        if List.isEmpty judges then
            invalidArg (nameof judges) "High-risk evaluation requires at least one judge."

        if
            judges
            |> List.exists (fun evaluator -> evaluator.Authority <> EvaluatorAuthority.Probabilistic)
        then
            invalidArg (nameof judges) "High-risk judges must declare probabilistic authority."

        match policy with
        | JudgeAggregationPolicy.MinimumAverageScore minimum when minimum < 0.0 || minimum > 1.0 ->
            invalidArg (nameof policy) "Minimum average judge score must be between zero and one."
        | _ -> ()

        let identities evaluators =
            evaluators
            |> List.map (fun evaluator ->
                String.concat
                    ":"
                    [ EvalComponentIdentity.name evaluator.Identity
                      EvalComponentIdentity.version evaluator.Identity
                      EvalComponentIdentity.configurationSha256 evaluator.Identity ])
            |> String.concat ","

        let configuration =
            sprintf
                "policy=%A|deterministic=%s|judges=%s"
                policy
                (identities deterministicEvaluators)
                (identities judges)

        Evaluator.probabilistic
            "HighRiskEvaluation"
            "1"
            configuration
            true
            "Any deterministic failure blocks release before judges run; judge rationales remain untrusted evidence."
            (fun context case actual ->
                task {
                    let! deterministic =
                        deterministicEvaluators
                        |> List.map (fun evaluator -> evaluator.EvaluateAsync context case actual)
                        |> Task.WhenAll

                    let deterministicFailures =
                        deterministic |> Array.filter (fst >> _.Passed >> not) |> Array.map snd

                    if not (Array.isEmpty deterministicFailures) then
                        return
                            EvalVerdict.Fail,
                            "Deterministic validation failed: " + String.concat "; " deterministicFailures
                    else
                        let! assessments =
                            judges
                            |> List.map (fun evaluator -> evaluator.EvaluateAsync context case actual)
                            |> Task.WhenAll

                        let scores = assessments |> Array.map (fst >> _.Score)
                        let passed = assessments |> Array.filter (fst >> _.Passed) |> Array.length

                        let accepted =
                            match policy with
                            | JudgeAggregationPolicy.Unanimous -> passed = assessments.Length
                            | JudgeAggregationPolicy.Majority -> passed * 2 > assessments.Length
                            | JudgeAggregationPolicy.MinimumAverageScore minimum -> Array.average scores >= minimum

                        let summary =
                            assessments
                            |> Array.mapi (fun index (verdict, rationale) ->
                                sprintf "judge-%d=%A (%s)" (index + 1) verdict rationale)
                            |> String.concat "; "

                        if accepted then
                            return EvalVerdict.Pass, "Untrusted judge evidence accepted: " + summary
                        else
                            return EvalVerdict.Fail, "Untrusted judge evidence rejected: " + summary
                })

type HumanJudgeLabel =
    { CaseId: string
      HumanPassed: bool
      JudgePassed: bool
      Repetition: int
      Position: int
      ResponseLength: int
      JudgeModelFamily: string
      CandidateModelFamily: string
      IsSelfEvaluation: bool }

type JudgeCalibrationReport =
    { Cases: int
      Accuracy: float
      CohenKappa: float
      Stability: float
      PositionBias: float
      VerbosityBias: float
      ModelFamilyPassRates: Map<string, float>
      SelfPreferenceBias: float }

[<RequireQualifiedAccess>]
module JudgeCalibration =
    let private rate values =
        if List.isEmpty values then
            0.0
        else
            values |> List.averageBy (fun value -> if value then 1.0 else 0.0)

    let private difference left right =
        if List.isEmpty left || List.isEmpty right then
            0.0
        else
            abs (rate left - rate right)

    let measure observations =
        if List.isEmpty observations then
            invalidArg (nameof observations) "Judge calibration requires human-labeled observations."

        if
            observations
            |> List.exists (fun item ->
                String.IsNullOrWhiteSpace item.CaseId
                || item.Repetition < 0
                || item.Position < 0
                || item.ResponseLength < 0
                || String.IsNullOrWhiteSpace item.JudgeModelFamily
                || String.IsNullOrWhiteSpace item.CandidateModelFamily)
        then
            invalidArg (nameof observations) "Judge calibration observations contain invalid identities or dimensions."

        let caseLabels =
            observations
            |> List.groupBy _.CaseId
            |> List.map (fun (caseId, items) ->
                let humanLabels = items |> List.map _.HumanPassed |> List.distinct

                if humanLabels.Length <> 1 then
                    invalidArg (nameof observations) (sprintf "Case '%s' has inconsistent human labels." caseId)

                caseId, humanLabels.Head, items)

        let agreement =
            observations
            |> List.averageBy (fun item -> if item.HumanPassed = item.JudgePassed then 1.0 else 0.0)

        let humanPassRate = observations |> List.map _.HumanPassed |> rate
        let judgePassRate = observations |> List.map _.JudgePassed |> rate

        let expectedAgreement =
            humanPassRate * judgePassRate + (1.0 - humanPassRate) * (1.0 - judgePassRate)

        let kappa =
            if expectedAgreement = 1.0 then
                if agreement = 1.0 then 1.0 else 0.0
            else
                (agreement - expectedAgreement) / (1.0 - expectedAgreement)

        let stability =
            caseLabels
            |> List.averageBy (fun (_, _, items) ->
                let majority = items |> List.map _.JudgePassed |> rate
                max majority (1.0 - majority))

        let positions = observations |> List.map _.Position |> List.distinct |> List.sort

        let positionBias =
            match positions with
            | first :: _ when positions.Length > 1 ->
                difference
                    (observations
                     |> List.filter (fun item -> item.Position = first)
                     |> List.map _.JudgePassed)
                    (observations
                     |> List.filter (fun item -> item.Position <> first)
                     |> List.map _.JudgePassed)
            | _ -> 0.0

        let orderedLengths = observations |> List.map _.ResponseLength |> List.sort
        let medianLength = orderedLengths.[orderedLengths.Length / 2]

        let verbosityBias =
            difference
                (observations
                 |> List.filter (fun item -> item.ResponseLength <= medianLength)
                 |> List.map _.JudgePassed)
                (observations
                 |> List.filter (fun item -> item.ResponseLength > medianLength)
                 |> List.map _.JudgePassed)

        let modelFamilyPassRates =
            observations
            |> List.groupBy _.CandidateModelFamily
            |> List.map (fun (family, items) -> family, (items |> List.map _.JudgePassed |> rate))
            |> Map.ofList

        let selfPreferenceBias =
            difference
                (observations |> List.filter _.IsSelfEvaluation |> List.map _.JudgePassed)
                (observations
                 |> List.filter (fun item -> not item.IsSelfEvaluation)
                 |> List.map _.JudgePassed)

        { Cases = caseLabels.Length
          Accuracy = agreement
          CohenKappa = kappa
          Stability = stability
          PositionBias = positionBias
          VerbosityBias = verbosityBias
          ModelFamilyPassRates = modelFamilyPassRates
          SelfPreferenceBias = selfPreferenceBias }

type JudgeUpgradeThresholds =
    { MinimumCases: int
      MinimumAccuracy: float
      MinimumKappa: float
      MinimumStability: float
      MaximumBias: float
      MaximumAccuracyRegression: float }

[<RequireQualifiedAccess>]
module JudgeUpgradeQualification =
    let qualify thresholds prior candidate =
        if thresholds.MinimumCases <= 0 then
            invalidArg (nameof thresholds) "Judge upgrade qualification requires a positive minimum case count."

        [ if candidate.Cases < thresholds.MinimumCases then
              yield sprintf "case count %d is below %d" candidate.Cases thresholds.MinimumCases

          if candidate.Accuracy < thresholds.MinimumAccuracy then
              yield sprintf "accuracy %.4f is below %.4f" candidate.Accuracy thresholds.MinimumAccuracy

          if candidate.CohenKappa < thresholds.MinimumKappa then
              yield sprintf "kappa %.4f is below %.4f" candidate.CohenKappa thresholds.MinimumKappa

          if candidate.Stability < thresholds.MinimumStability then
              yield sprintf "stability %.4f is below %.4f" candidate.Stability thresholds.MinimumStability

          for name, bias in
              [ "position", candidate.PositionBias
                "verbosity", candidate.VerbosityBias
                "self-preference", candidate.SelfPreferenceBias ] do
              if bias > thresholds.MaximumBias then
                  yield sprintf "%s bias %.4f exceeds %.4f" name bias thresholds.MaximumBias

          if prior.Accuracy - candidate.Accuracy > thresholds.MaximumAccuracyRegression then
              yield
                  sprintf
                      "accuracy regression %.4f exceeds %.4f"
                      (prior.Accuracy - candidate.Accuracy)
                      thresholds.MaximumAccuracyRegression ]
