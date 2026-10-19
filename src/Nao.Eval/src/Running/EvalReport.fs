namespace Nao.Eval

open System
open Nao.Agents

/// Aggregate report of an evaluation run
type EvalReport =
    {
        /// Nao-owned report contract version.
        SchemaVersion: int
        /// Stable evaluation-run identity
        Id: Guid
        /// Retention owner
        Owner: AuthorizationIdentity
        /// Dataset revision evaluated by this report
        DatasetId: Guid
        /// Monotonic dataset revision.
        DatasetRevision: int
        /// Canonical SHA-256 identity of the complete dataset specification.
        DatasetContentSha256: string
        /// Complete executable configuration used for this run.
        Configuration: EvalExecutableConfiguration
        /// Execution mode, seed, fixture identity, and scheduling settings.
        Reproducibility: EvalReproducibility
        /// Name of the evaluation run
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
        TagBreakdown: Map<string, TagSummary>
    }

/// Summary statistics for a specific tag
and TagSummary =
    { Tag: string
      Count: int
      PassRate: float
      AverageScore: float }

module EvalReport =
    [<Literal>]
    let CurrentSchemaVersion = 1

    /// Generate a report from a list of results
    let fromResults (run: EvalRun) (name: string) (results: EvalResult list) : EvalReport =
        let passed =
            results |> List.filter (fun r -> r.Verdict = EvalVerdict.Pass) |> List.length

        let failed =
            results |> List.filter (fun r -> r.Verdict = EvalVerdict.Fail) |> List.length

        let partial = results.Length - passed - failed

        let avgScore =
            if results.IsEmpty then
                0.0
            else
                results |> List.averageBy EvalResult.score

        let avgLatency =
            if results.IsEmpty then
                0.0
            else
                results |> List.averageBy (fun r -> float r.LatencyMs)

        { SchemaVersion = CurrentSchemaVersion
          Id = run.Id
          Owner = run.Owner
          DatasetId = run.DatasetId
          DatasetRevision = run.DatasetRevision
          DatasetContentSha256 = run.DatasetContentSha256
          Configuration = run.Configuration
          Reproducibility = run.Reproducibility
          Name = name
          RunAt = run.StartedAt
          TotalCases = results.Length
          Passed = passed
          Failed = failed
          Partial = partial
          AverageScore = avgScore
          AverageLatencyMs = avgLatency
          Results = results
          TagBreakdown = Map.empty }

    /// Generate a report with tag breakdown from cases and results
    let fromCasesAndResults
        (run: EvalRun)
        (name: string)
        (cases: EvalCase list)
        (results: EvalResult list)
        : EvalReport =
        let base' = fromResults run name results
        let resultMap = results |> List.map (fun r -> r.CaseId, r) |> Map.ofList

        let tagBreakdown =
            cases
            |> List.collect (fun case ->
                case
                |> EvalCase.labels
                |> Seq.map (fun label -> label, EvalCase.id case)
                |> Seq.toList)
            |> List.groupBy fst
            |> List.map (fun (tag, pairs) ->
                let caseIds = pairs |> List.map snd
                let tagResults = caseIds |> List.choose (fun id -> Map.tryFind id resultMap)
                let count = tagResults.Length

                let passRate =
                    if count = 0 then
                        0.0
                    else
                        float (tagResults |> List.filter EvalResult.passed |> List.length) / float count

                let avgScore =
                    if count = 0 then
                        0.0
                    else
                        tagResults |> List.averageBy EvalResult.score

                (tag,
                 { Tag = tag
                   Count = count
                   PassRate = passRate
                   AverageScore = avgScore }))
            |> Map.ofList

        { base' with
            TagBreakdown = tagBreakdown }

    /// Format report as a human-readable string
    let format (report: EvalReport) : string =
        let lines = ResizeArray<string>()
        lines.Add(sprintf "=== Evaluation Report: %s ===" report.Name)
        lines.Add(sprintf "Run at: %s" (report.RunAt.ToString("o")))

        lines.Add(
            sprintf
                "Total: %d | Passed: %d | Failed: %d | Partial: %d"
                report.TotalCases
                report.Passed
                report.Failed
                report.Partial
        )

        lines.Add(sprintf "Average Score: %.2f | Average Latency: %.0fms" report.AverageScore report.AverageLatencyMs)
        lines.Add("")

        if not report.TagBreakdown.IsEmpty then
            lines.Add("--- Tag Breakdown ---")

            for kvp in report.TagBreakdown do
                lines.Add(
                    sprintf
                        "  [%s] %d cases, %.0f%% pass rate, avg score %.2f"
                        kvp.Key
                        kvp.Value.Count
                        (kvp.Value.PassRate * 100.0)
                        kvp.Value.AverageScore
                )

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
