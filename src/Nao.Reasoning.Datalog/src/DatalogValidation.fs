namespace Nao.Reasoning.Datalog

open System
open System.Text.RegularExpressions

[<RequireQualifiedAccess>]
module DatalogValidation =
    let private identifier =
        Regex("^[A-Za-z_][A-Za-z0-9_]*$", RegexOptions.CultureInvariant)

    let private canonical name value =
        if
            String.IsNullOrWhiteSpace value
            || value <> value.Trim()
            || not (identifier.IsMatch value)
        then
            invalidArg name $"{name} must be a canonical Datalog identifier."

    let private canonicalText name value =
        if String.IsNullOrWhiteSpace value || value <> value.Trim() then
            invalidArg name $"{name} must be canonical and non-blank."

    let private digest (value: string) =
        if
            isNull value
            || value.Length <> 64
            || value
               |> Seq.exists (fun character ->
                   not (character >= '0' && character <= '9' || character >= 'a' && character <= 'f'))
        then
            invalidArg (nameof value) "Datalog rule-set digest must be lowercase SHA-256."

    let private variables (atom: DatalogAtom) =
        atom.Terms
        |> List.choose (function
            | DatalogTerm.Variable name -> Some name
            | DatalogTerm.Constant _ -> None)
        |> Set.ofList

    let private validateAtom
        (relations: Map<string, DatalogRelation>)
        (variableTypes: Map<string, DatalogType>)
        (atom: DatalogAtom)
        =
        let relation =
            relations
            |> Map.tryFind atom.Relation
            |> Option.defaultWith (fun () -> invalidArg (nameof atom) $"Undeclared relation '{atom.Relation}'.")

        if atom.Terms.Length <> relation.Columns.Length then
            invalidArg (nameof atom) $"Relation '{atom.Relation}' arity does not match its declaration."

        (variableTypes, List.zip atom.Terms relation.Columns)
        ||> List.fold (fun types (term, column) ->
            match term with
            | DatalogTerm.Constant value ->
                DatalogValue.validate value

                if DatalogValue.valueType value <> column.Type then
                    invalidArg (nameof atom) $"Constant type does not match '{atom.Relation}.{column.Name}'."

                types
            | DatalogTerm.Variable name ->
                canonical (nameof atom) name

                match Map.tryFind name types with
                | Some existing when existing <> column.Type ->
                    invalidArg (nameof atom) $"Variable '{name}' is used with incompatible types."
                | _ -> Map.add name column.Type types)

    let private pathExists (dependencies: Map<string, Set<string>>) start target =
        let rec visit seen current =
            if current = target then
                true
            elif Set.contains current seen then
                false
            else
                dependencies
                |> Map.tryFind current
                |> Option.defaultValue Set.empty
                |> Set.exists (visit (Set.add current seen))

        visit Set.empty start

    let create
        authorization
        (identity: DatalogRuleSetIdentity)
        (relations: DatalogRelation list)
        (facts: DatalogFact list)
        (rules: DatalogRule list)
        (query: DatalogQuery)
        =
        canonicalText (nameof identity.Id) identity.Id
        canonicalText (nameof identity.Version) identity.Version
        digest identity.Digest

        if List.isEmpty relations then
            invalidArg (nameof relations) "Datalog program requires relation declarations."

        relations
        |> List.iter (fun relation ->
            canonical (nameof relation.Name) relation.Name

            if List.isEmpty relation.Columns then
                invalidArg (nameof relation.Columns) "Datalog relations require at least one column."

            relation.Columns
            |> List.iter (fun column -> canonical (nameof column.Name) column.Name)

            if
                relation.Columns |> List.map _.Name |> List.distinct |> List.length
                <> relation.Columns.Length
            then
                invalidArg (nameof relation.Columns) $"Relation '{relation.Name}' column names must be unique.")

        let relationMap =
            relations |> List.map (fun value -> value.Name, value) |> Map.ofList

        if relationMap.Count <> relations.Length then
            invalidArg (nameof relations) "Datalog relation names must be unique."

        let factIds = facts |> List.map _.Id

        if factIds.Length <> (factIds |> List.distinct |> List.length) then
            invalidArg (nameof facts) "Datalog fact identities must be unique."

        facts
        |> List.iter (fun fact ->
            canonicalText (nameof fact.Id) fact.Id

            if List.isEmpty fact.Citations then
                invalidArg (nameof facts) "Datalog facts require source citations."

            let relation =
                relationMap
                |> Map.tryFind fact.Relation
                |> Option.defaultWith (fun () ->
                    invalidArg (nameof facts) $"Undeclared fact relation '{fact.Relation}'.")

            if relation.Kind = DatalogRelationKind.Derived then
                invalidArg (nameof facts) "Derived Datalog relations cannot contain source facts."

            if fact.Values.Length <> relation.Columns.Length then
                invalidArg (nameof facts) $"Fact arity does not match relation '{fact.Relation}'."

            List.zip fact.Values relation.Columns
            |> List.iter (fun (value, column) ->
                DatalogValue.validate value

                if DatalogValue.valueType value <> column.Type then
                    invalidArg (nameof facts) $"Fact type does not match '{fact.Relation}.{column.Name}'."))

        let ruleIds = rules |> List.map _.Id

        if ruleIds.Length <> (ruleIds |> List.distinct |> List.length) then
            invalidArg (nameof rules) "Datalog rule identities must be unique."

        let dependencies =
            rules
            |> List.fold
                (fun result rule ->
                    canonicalText (nameof rule.Id) rule.Id

                    if List.isEmpty rule.Citations then
                        invalidArg (nameof rules) "Datalog rules require source citations."

                    if List.isEmpty rule.Body then
                        invalidArg (nameof rules) "Datalog rules require a body."

                    let headRelation =
                        relationMap
                        |> Map.tryFind rule.Head.Relation
                        |> Option.defaultWith (fun () ->
                            invalidArg (nameof rules) $"Undeclared head relation '{rule.Head.Relation}'.")

                    if headRelation.Kind <> DatalogRelationKind.Derived then
                        invalidArg (nameof rules) "Rule heads must target derived relations."

                    let mutable types = validateAtom relationMap Map.empty rule.Head

                    for literal in rule.Body do
                        let atom =
                            match literal with
                            | DatalogLiteral.Positive atom
                            | DatalogLiteral.Negative atom -> atom

                        types <- validateAtom relationMap types atom

                    let positiveVariables =
                        rule.Body
                        |> List.choose (function
                            | DatalogLiteral.Positive atom -> Some(variables atom)
                            | DatalogLiteral.Negative _ -> None)
                        |> List.fold Set.union Set.empty

                    if not (Set.isSubset (variables rule.Head) positiveVariables) then
                        invalidArg (nameof rules) "Every rule-head variable must be bound by a positive body atom."

                    rule.Body
                    |> List.iter (function
                        | DatalogLiteral.Negative atom when not (Set.isSubset (variables atom) positiveVariables) ->
                            invalidArg (nameof rules) "Every negated variable must be bound by a positive body atom."
                        | _ -> ())

                    rule.Body
                    |> List.fold
                        (fun state literal ->
                            let atom =
                                match literal with
                                | DatalogLiteral.Positive atom
                                | DatalogLiteral.Negative atom -> atom

                            let existing = Map.tryFind rule.Head.Relation state |> Option.defaultValue Set.empty
                            Map.add rule.Head.Relation (Set.add atom.Relation existing) state)
                        result)
                Map.empty

        rules
        |> List.iter (fun rule ->
            rule.Body
            |> List.iter (function
                | DatalogLiteral.Negative atom when pathExists dependencies atom.Relation rule.Head.Relation ->
                    invalidArg (nameof rules) "Datalog negation must be stratified; negative recursion is forbidden."
                | _ -> ()))

        let queryTypes = validateAtom relationMap Map.empty query.Atom
        let queryVariables = variables query.Atom

        if
            query.OutputVariables |> List.distinct |> List.length
            <> query.OutputVariables.Length
        then
            invalidArg (nameof query) "Datalog query output variables must be unique."

        query.OutputVariables
        |> List.iter (fun name ->
            canonical (nameof query) name

            if not (Set.contains name queryVariables) || not (Map.containsKey name queryTypes) then
                invalidArg (nameof query) $"Query output variable '{name}' is not bound by the query atom.")

        DatalogProgram.validated authorization identity relations facts rules query
