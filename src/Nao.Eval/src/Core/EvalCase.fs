namespace Nao.Eval

open System

/// A single evaluation test case
type EvalCase =
    { /// Unique identifier for this case
      Id: string
      /// Human-readable description
      Description: string
      /// The input to send to the agent
      Input: string
      /// Expected output or reference answer (used by some evaluators)
      Expected: string option
      /// Tags for categorization and filtering
      Tags: string list
      /// Additional metadata for evaluators
      Metadata: Map<string, string> }

module EvalCase =

    /// Create a simple eval case with input and expected output
    let create id input expected =
        { Id = id
          Description = ""
          Input = input
          Expected = Some expected
          Tags = []
          Metadata = Map.empty }

    /// Create an open-ended eval case (no single expected answer)
    let openEnded id description input =
        { Id = id
          Description = description
          Input = input
          Expected = None
          Tags = []
          Metadata = Map.empty }

    /// Add tags to a case
    let withTags tags case = { case with Tags = tags }

    /// Add metadata to a case
    let withMetadata metadata case = { case with Metadata = metadata }

    /// Add description to a case
    let withDescription desc case = { case with Description = desc }

/// A dataset is a named collection of eval cases
type EvalDataset =
    { Name: string
      Cases: EvalCase list }

module EvalDataset =

    let create name cases = { Name = name; Cases = cases }
