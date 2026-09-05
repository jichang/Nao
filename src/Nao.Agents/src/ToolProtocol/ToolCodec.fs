namespace Nao.Agents

/// Typed transport codec for one side of a tool contract.
/// The schema is authored documentation; encode and decode are the sole authority for the wire representation.
type ToolCodec<'Value> =
    { Schema: string
      Encode: 'Value -> Result<string, string>
      Decode: string -> Result<'Value, string> }

[<RequireQualifiedAccess>]
module ToolCodec =
    /// Creates a codec from an explicit schema and caller-owned transport functions.
    let create schema encode decode =
        { Schema = schema
          Encode = encode
          Decode = decode }

    /// Identity codec for tools whose transport and domain value are both plain text.
    let text = create "string" Ok Ok
