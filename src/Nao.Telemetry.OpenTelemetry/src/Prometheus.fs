namespace Nao.Telemetry.OpenTelemetry

open System
open System.Collections.Concurrent
open System.Globalization
open System.Text
open Nao.Agents

type internal PrometheusRegistry() =
    let counters = ConcurrentDictionary<string * Map<string, string>, float>()
    let histograms = ConcurrentDictionary<string * Map<string, string>, int64 * float>()
    let gauges = ConcurrentDictionary<string * Map<string, string>, float>()

    let updateCounter name labels value =
        counters.AddOrUpdate((name, labels), value, fun _ current -> current + value)
        |> ignore

    let updateHistogram name labels value =
        histograms.AddOrUpdate((name, labels), (1L, value), fun _ (count, sum) -> count + 1L, sum + value)
        |> ignore

    member _.Record(instrument: TelemetryInstrument, labels, value) =
        match TelemetryInstrument.kind instrument with
        | TelemetryInstrumentKind.Counter -> updateCounter (TelemetryInstrument.name instrument) labels value
        | TelemetryInstrumentKind.Histogram -> updateHistogram (TelemetryInstrument.name instrument) labels value
        | TelemetryInstrumentKind.Gauge -> gauges.[(TelemetryInstrument.name instrument, labels)] <- value

    member _.Render() =
        let builder = StringBuilder()

        let escape (value: string) =
            value.Replace("\\", "\\\\").Replace("\n", "\\n").Replace("\"", "\\\"")

        let metricName (value: string) = value.Replace(".", "_")

        let labelsText labels =
            if Map.isEmpty labels then
                ""
            else
                labels
                |> Map.toList
                |> List.map (fun (key, value) -> $"{metricName key}=\"{escape value}\"")
                |> String.concat ","
                |> fun value -> $"{{{value}}}"

        let number (value: float) =
            value.ToString("R", CultureInfo.InvariantCulture)

        counters
        |> Seq.sortBy (fun pair -> pair.Key)
        |> Seq.iter (fun pair ->
            let name, labels = pair.Key

            builder.AppendLine($"{metricName name}_total{labelsText labels} {number pair.Value}")
            |> ignore)

        histograms
        |> Seq.sortBy (fun pair -> pair.Key)
        |> Seq.iter (fun pair ->
            let name, labels = pair.Key
            let count, sum = pair.Value

            builder.AppendLine($"{metricName name}_count{labelsText labels} {count}")
            |> ignore

            builder.AppendLine($"{metricName name}_sum{labelsText labels} {number sum}")
            |> ignore)

        gauges
        |> Seq.sortBy (fun pair -> pair.Key)
        |> Seq.iter (fun pair ->
            let name, labels = pair.Key

            builder.AppendLine($"{metricName name}{labelsText labels} {number pair.Value}")
            |> ignore)

        builder.ToString()
