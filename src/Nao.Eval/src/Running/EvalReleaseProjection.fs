namespace Nao.Eval

open System

[<RequireQualifiedAccess>]
module EvalReleaseProjection =
    let private sampleStandardDeviation values =
        match values with
        | []
        | [ _ ] -> 0m
        | values ->
            let decimalValues = values |> List.map decimal
            let mean = decimalValues |> List.average

            decimalValues
            |> List.sumBy (fun value -> pown (value - mean) 2)
            |> fun sum -> sum / decimal (decimalValues.Length - 1)
            |> float
            |> sqrt
            |> decimal

    let fromReport additionalMetrics (report: EvalReport) =
        if report.TotalCases <= 0 || report.TotalCases <> report.Results.Length then
            invalidArg (nameof report) "Release projection requires a complete non-empty evaluation report."

        let scores = report.Results |> List.map EvalResult.score
        let latencies = report.Results |> List.map (fun result -> float result.LatencyMs)
        let successful = report.Results |> List.filter EvalResult.passed |> List.length

        let builtInMetrics =
            [ EvalReleaseMetricKey.overall EvalReleaseMetric.Quality,
              EvalMetricMeasurement.create
                  (decimal report.AverageScore)
                  report.TotalCases
                  None
                  (Some(sampleStandardDeviation scores))
              EvalReleaseMetricKey.overall EvalReleaseMetric.SuccessRate,
              EvalMetricMeasurement.proportion successful report.TotalCases
              EvalReleaseMetricKey.overall EvalReleaseMetric.LatencyMilliseconds,
              EvalMetricMeasurement.create
                  (decimal report.AverageLatencyMs)
                  report.TotalCases
                  None
                  (Some(sampleStandardDeviation latencies)) ]

        let scenarioMetrics =
            report.TagBreakdown
            |> Map.toList
            |> List.map (fun (tag, summary) ->
                let successes = int (Math.Round(summary.PassRate * float summary.Count))

                EvalReleaseMetricKey.segment EvalSegmentDimension.Scenario tag EvalReleaseMetric.SuccessRate,
                EvalMetricMeasurement.proportion successes summary.Count)

        let failures =
            report.Results
            |> List.choose (fun result ->
                match result.Outcome with
                | EvalResultOutcome.Succeeded -> None
                | EvalResultOutcome.CorrectnessFailure ->
                    Some
                        { CaseId = result.CaseId
                          Classification = EvalFailureClass.Correctness
                          Reason = result.Reason }
                | EvalResultOutcome.InfrastructureFailure ->
                    Some
                        { CaseId = result.CaseId
                          Classification = EvalFailureClass.Infrastructure
                          Reason = result.Reason })

        EvalReleaseSnapshot.create
            report.Id
            report.DatasetId
            report.DatasetRevision
            report.DatasetContentSha256
            report.Configuration
            (builtInMetrics @ scenarioMetrics @ additionalMetrics)
            failures
            report.RunAt
