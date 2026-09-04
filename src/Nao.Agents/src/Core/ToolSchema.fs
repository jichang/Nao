namespace Nao.Agents

/// Explicit transport schemas advertised during tool discovery and prompt construction.
type ToolSchema = { Input: string; Output: string }

[<RequireQualifiedAccess>]
module ToolSchema =
    let create input output = { Input = input; Output = output }
