namespace Nao.Assistant

open System
open Nao.Agents

/// Small self-contained utility tools that need no workspace or network access.
module UtilityTools =

    let dateTime: Tool =
        Tool.Create("get_datetime", "Get the current date and time.",
            fun _ -> task {
                return DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss zzz")
            })

    let calculator: Tool =
        { Tool.Create("calculator", "Evaluate a simple math expression. Input: JSON {\"expression\":\"2 + 3\"} (format 'a op b').",
            fun input -> task {
                try
                    let a = parseArgs input
                    let parts = (a.StringOrRaw "expression").Trim().Split(' ')
                    if parts.Length = 3 then
                        let x = Double.Parse(parts.[0])
                        let y = Double.Parse(parts.[2])
                        let result =
                            match parts.[1] with
                            | "+" -> x + y | "-" -> x - y
                            | "*" -> x * y | "/" -> if y <> 0.0 then x / y else Double.NaN
                            | _ -> Double.NaN
                        if Double.IsNaN result then return json {| error = "Invalid expression" |}
                        else return json {| result = result |}
                    else
                        return json {| error = "Expected format: 'a op b'" |}
                with ex ->
                    return json {| error = ex.Message |}
            }) with
            Schema = [ reqParam "expression" "string" "Math expression in the form 'a op b' (op is + - * /)." ] }
