namespace Nao.Reasoning.Constraints

open System
open System.Text.RegularExpressions

[<RequireQualifiedAccess>]
module ConstraintValidation =
    let private identifier =
        Regex("^[A-Za-z_][A-Za-z0-9_]*$", RegexOptions.CultureInvariant)

    let private canonical name value =
        if String.IsNullOrWhiteSpace value || value <> value.Trim() then
            invalidArg name $"{name} must be canonical and non-blank."

    let private variableName name =
        canonical (nameof name) name

        if not (identifier.IsMatch name) then
            invalidArg (nameof name) "Constraint variable names must be safe identifiers."

    let private digest (value: string) =
        if
            isNull value
            || value.Length <> 64
            || value
               |> Seq.exists (fun character ->
                   not (character >= '0' && character <= '9' || character >= 'a' && character <= 'f'))
        then
            invalidArg (nameof value) "Constraint problem digest must be lowercase SHA-256."

    let private domainSort =
        function
        | ConstraintDomain.Boolean -> ConstraintSort.Boolean
        | ConstraintDomain.IntegerRange _
        | ConstraintDomain.UnboundedInteger -> ConstraintSort.Integer
        | ConstraintDomain.RealRange _
        | ConstraintDomain.UnboundedReal -> ConstraintSort.Real

    let private numeric =
        function
        | ConstraintSort.Integer
        | ConstraintSort.Real -> true
        | ConstraintSort.Boolean -> false

    let rec private infer variables =
        function
        | ConstraintExpression.Value value -> ConstraintValue.sort value
        | ConstraintExpression.Variable name ->
            variables
            |> Map.tryFind name
            |> Option.defaultWith (fun () -> invalidArg (nameof name) $"Undeclared constraint variable '{name}'.")
        | ConstraintExpression.Not value ->
            if infer variables value <> ConstraintSort.Boolean then
                invalidArg (nameof value) "Logical negation requires a Boolean expression."

            ConstraintSort.Boolean
        | ConstraintExpression.And values
        | ConstraintExpression.Or values ->
            if
                List.isEmpty values
                || values
                   |> List.exists (fun value -> infer variables value <> ConstraintSort.Boolean)
            then
                invalidArg (nameof values) "Logical conjunction and disjunction require Boolean operands."

            ConstraintSort.Boolean
        | ConstraintExpression.Implies(left, right) ->
            if
                infer variables left <> ConstraintSort.Boolean
                || infer variables right <> ConstraintSort.Boolean
            then
                invalidArg (nameof left) "Logical implication requires Boolean operands."

            ConstraintSort.Boolean
        | ConstraintExpression.Equal(left, right) ->
            if infer variables left <> infer variables right then
                invalidArg (nameof left) "Equality operands must have identical sorts."

            ConstraintSort.Boolean
        | ConstraintExpression.Distinct values ->
            if List.length values < 2 then
                invalidArg (nameof values) "Distinct requires at least two operands."

            let sorts = values |> List.map (infer variables) |> Set.ofList

            if sorts.Count <> 1 then
                invalidArg (nameof values) "Distinct operands must have identical sorts."

            ConstraintSort.Boolean
        | ConstraintExpression.LessThan(left, right)
        | ConstraintExpression.LessThanOrEqual(left, right)
        | ConstraintExpression.GreaterThan(left, right)
        | ConstraintExpression.GreaterThanOrEqual(left, right) ->
            let leftSort = infer variables left

            if not (numeric leftSort) || infer variables right <> leftSort then
                invalidArg (nameof left) "Ordered comparisons require identical numeric sorts."

            ConstraintSort.Boolean
        | ConstraintExpression.Add values
        | ConstraintExpression.Multiply values ->
            if List.isEmpty values then
                invalidArg (nameof values) "Arithmetic operations require operands."

            let sorts = values |> List.map (infer variables) |> Set.ofList

            if sorts.Count <> 1 || not (numeric (Set.minElement sorts)) then
                invalidArg (nameof values) "Arithmetic operands must have identical numeric sorts."

            Set.minElement sorts
        | ConstraintExpression.Subtract(left, right) ->
            let leftSort = infer variables left

            if not (numeric leftSort) || infer variables right <> leftSort then
                invalidArg (nameof left) "Subtraction requires identical numeric sorts."

            leftSort
        | ConstraintExpression.Negate value ->
            let valueSort = infer variables value

            if not (numeric valueSort) then
                invalidArg (nameof value) "Arithmetic negation requires a numeric operand."

            valueSort

    let sortOf problem expression =
        let variables =
            ConstraintProblem.variables problem
            |> List.map (fun value -> value.Name, domainSort value.Domain)
            |> Map.ofList

        infer variables expression

    let create
        authorization
        (identity: ConstraintProblemIdentity)
        (variables: ConstraintVariable list)
        (assertions: ConstraintAssertion list)
        (objectives: ConstraintObjective list)
        =
        canonical (nameof identity.Id) identity.Id
        canonical (nameof identity.Version) identity.Version
        digest identity.Digest

        variables
        |> List.iter (fun variable ->
            variableName variable.Name

            match variable.Domain with
            | ConstraintDomain.IntegerRange(minimum, maximum) when minimum > maximum ->
                invalidArg (nameof variables) $"Invalid integer domain for '{variable.Name}'."
            | ConstraintDomain.RealRange(minimum, maximum) when ConstraintRational.compare minimum maximum > 0 ->
                invalidArg (nameof variables) $"Invalid real domain for '{variable.Name}'."
            | _ -> ())

        let variableMap =
            variables
            |> List.map (fun value -> value.Name, domainSort value.Domain)
            |> Map.ofList

        if variableMap.Count <> variables.Length then
            invalidArg (nameof variables) "Constraint variable names must be unique."

        let assertionIds = assertions |> List.map _.Id

        if assertionIds.Length <> (assertionIds |> List.distinct |> List.length) then
            invalidArg (nameof assertions) "Constraint assertion identities must be unique."

        assertions
        |> List.iter (fun assertion ->
            canonical (nameof assertion.Id) assertion.Id

            if List.isEmpty assertion.Citations then
                invalidArg (nameof assertions) "Constraint assertions require source citations."

            if infer variableMap assertion.Expression <> ConstraintSort.Boolean then
                invalidArg (nameof assertions) "Constraint assertions must be Boolean.")

        let objectiveIds = objectives |> List.map _.Id

        if objectiveIds.Length <> (objectiveIds |> List.distinct |> List.length) then
            invalidArg (nameof objectives) "Constraint objective identities must be unique."

        objectives
        |> List.iter (fun objective ->
            canonical (nameof objective.Id) objective.Id

            if not (numeric (infer variableMap objective.Expression)) then
                invalidArg (nameof objectives) "Constraint objectives must be numeric.")

        ConstraintProblem.validated authorization identity variables assertions objectives
