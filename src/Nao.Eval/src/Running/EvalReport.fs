namespace Nao.Eval

open System

/// Aggregate report of an evaluation run
type EvalReport =
    { /// Name of the evaluation run
      Name: string
      /// When the evaluation was run
      RunAt: DateTimeOffset
      /// Total number of cases evaluated
      TotalCases: int
      /// Number of cases that passed
      Passed: int
      /// Number of cases that failed
      Failed: int
      /// Number of cases with partial scores
      Partial: int
      /// Average score across all cases (0.0 to 1.0)
      AverageScore: float
      /// Average latency in ms
      AverageLatencyMs: float
      /// Individual results
      Results: EvalResult list
      /// Per-tag breakdown
      TagBreakdown: Map<string, TagSummary> }

/// Summary statistics for a specific tag
and TagSummary =
    { Tag: string
      Count: int
      PassRate: float
      AverageScore: float }

module EvalReport =

    /// Generate a report from a list of results
    let fromResults (name: string) (results: EvalResult list) : EvalReport =
        let passed = results |> List.filter (fun r -> r.Verdict = EvalVerdict.Pass) |> List.length
        let failed = results |> List.filter (fun r -> r.Verdict = EvalVerdict.Fail) |> List.length
        let partial = results.Length - passed - failed
        let avgScore =
            if results.IsEmpty then 0.0
            else results |> List.averageBy EvalResult.score
        let avgLatency =
            if results.IsEmpty then 0.0
            else results |> List.averageBy (fun r -> float r.LatencyMs)

        // Build tag breakdown
        let tagBreakdown =
            results
            |> List.collect (fun r ->
                // We need the case's tags - stored in metadata or we infer from evaluator
                // For now, use a simple approach
                [])
            |> ignore

        { Name = name
          RunAt = DateTimeOffset.UtcNow
          TotalCases = results.Length
          Passed = passed
          Failed = failed
          Partial = partial
          AverageScore = avgScore
          AverageLatencyMs = avgLatency
          Results = results
          TagBreakdown = Map.empty }

    /// Generate a report with tag breakdown from cases and results
    let fromCasesAndResults (name: string) (cases: EvalCase list) (results: EvalResult list) : EvalReport =
        let base' = fromResults name results
        let resultMap = results |> List.map (fun r -> r.CaseId, r) |> Map.ofList

        let tagBreakdown =
            cases
            |> List.collect (fun c -> c.Tags |> List.map (fun tag -> (tag, c.Id)))
            |> List.groupBy fst
            |> List.map (fun (tag, pairs) ->
                let caseIds = pairs |> List.map snd
                let tagResults =
                    caseIds |> List.choose (fun id -> Map.tryFind id resultMap)
                let count = tagResults.Length
                let passRate =
                    if count = 0 then 0.0
                    else float (tagResults |> List.filter EvalResult.passed |> List.length) / float count
                let avgScore =
                    if count = 0 then 0.0
                    else tagResults |> List.averageBy EvalResult.score
                (tag, { Tag = tag; Count = count; PassRate = passRate; AverageScore = avgScore }))
            |> Map.ofList

        { base' with TagBreakdown = tagBreakdown }

    /// Format report as a human-readable string
    let format (report: EvalReport) : string =
        let lines = ResizeArray<string>()
        lines.Add(sprintf "=== Evaluation Report: %s ===" report.Name)
        lines.Add(sprintf "Run at: %s" (report.RunAt.ToString("o")))
        lines.Add(sprintf "Total: %d | Passed: %d | Failed: %d | Partial: %d" report.TotalCases report.Passed report.Failed report.Partial)
        lines.Add(sprintf "Average Score: %.2f | Average Latency: %.0fms" report.AverageScore report.AverageLatencyMs)
        lines.Add("")

        if not report.TagBreakdown.IsEmpty then
            lines.Add("--- Tag Breakdown ---")
            for kvp in report.TagBreakdown do
                lines.Add(sprintf "  [%s] %d cases, %.0f%% pass rate, avg score %.2f" kvp.Key kvp.Value.Count (kvp.Value.PassRate * 100.0) kvp.Value.AverageScore)
            lines.Add("")

        lines.Add("--- Details ---")
        for r in report.Results do
            let status =
                match r.Verdict with
                | EvalVerdict.Pass -> "PASS"
                | EvalVerdict.Fail -> "FAIL"
                | EvalVerdict.Partial s -> sprintf "PARTIAL(%.2f)" s
            lines.Add(sprintf "  [%s] %s (%dms) - %s" status r.CaseId r.LatencyMs r.Reason)

        lines |> Seq.toList |> String.concat "\n"
