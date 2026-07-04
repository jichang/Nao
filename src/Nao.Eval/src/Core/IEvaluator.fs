namespace Nao.Eval

open System.Threading.Tasks

/// Interface for evaluating agent outputs against expectations
type IEvaluator =
    /// A name identifying this evaluator
    abstract member Name: string
    /// Evaluate the agent's output for a given case
    abstract member EvaluateAsync: EvalCase -> string -> Task<EvalVerdict * string>
