namespace Nao.Semantics

open System
open System.Globalization
open System.Text.RegularExpressions
open Nao.Agents

[<Struct; StructuralEquality; StructuralComparison>]
type RdfIri = private RdfIri of string

[<RequireQualifiedAccess>]
module RdfIri =
    let parse value =
        match Uri.TryCreate(value, UriKind.Absolute) with
        | true, _ when not (String.IsNullOrWhiteSpace value) -> RdfIri value
        | _ -> invalidArg (nameof value) "RDF IRI must be absolute."

    let value (RdfIri value) = value

[<Struct; StructuralEquality; StructuralComparison>]
type RdfBlankNodeId = private RdfBlankNodeId of string

[<RequireQualifiedAccess>]
module RdfBlankNodeId =
    let private isLabelCharacter value =
        Char.IsLetterOrDigit value
        || value = '_'
        || value = '-'
        || value = '\u00B7'
        || match Char.GetUnicodeCategory value with
           | UnicodeCategory.NonSpacingMark
           | UnicodeCategory.SpacingCombiningMark
           | UnicodeCategory.ModifierLetter -> true
           | _ -> false

    let parse value =
        if
            String.IsNullOrWhiteSpace value
            || not (Char.IsLetterOrDigit value[0] || value[0] = '_')
            || value[value.Length - 1] = '.'
            || value
               |> Seq.exists (fun character -> character <> '.' && not (isLabelCharacter character))
        then
            invalidArg (nameof value) "RDF blank-node identity is not a valid RDF blank-node label."

        RdfBlankNodeId value

    let value (RdfBlankNodeId value) = value

type RdfLiteral =
    private
        { LexicalForm: string
          Language: string option
          Datatype: RdfIri option }

[<RequireQualifiedAccess>]
module RdfLiteral =
    let private normalizeLanguageTag value =
        if String.IsNullOrWhiteSpace value then
            invalidArg (nameof value) "RDF language tag cannot be blank."

        let subtags = value.Split('-')

        let isAsciiLetter character =
            character >= 'A' && character <= 'Z' || character >= 'a' && character <= 'z'

        let isAsciiLetterOrDigit character =
            isAsciiLetter character || character >= '0' && character <= '9'

        let validFirst = subtags[0].Length <= 8 && Seq.forall isAsciiLetter subtags[0]

        let validRemaining =
            subtags
            |> Seq.skip 1
            |> Seq.forall (fun subtag ->
                subtag.Length >= 1
                && subtag.Length <= 8
                && Seq.forall isAsciiLetterOrDigit subtag)

        if
            not validFirst
            || (subtags[0].Length = 1 && subtags.Length = 1)
            || not validRemaining
        then
            invalidArg (nameof value) $"RDF language tag '{value}' is invalid."

        value.ToLowerInvariant()

    let plain lexicalForm =
        if isNull lexicalForm then
            nullArg (nameof lexicalForm)

        { LexicalForm = lexicalForm
          Language = None
          Datatype = None }

    let language lexicalForm language =
        if isNull lexicalForm then
            nullArg (nameof lexicalForm)

        { LexicalForm = lexicalForm
          Language = Some(normalizeLanguageTag language)
          Datatype = None }

    let typed lexicalForm datatype =
        if isNull lexicalForm then
            nullArg (nameof lexicalForm)

        { LexicalForm = lexicalForm
          Language = None
          Datatype = Some datatype }

    let lexicalForm value = value.LexicalForm
    let languageTag value = value.Language
    let datatype value = value.Datatype

[<RequireQualifiedAccess>]
type RdfResource =
    | Iri of RdfIri
    | BlankNode of RdfBlankNodeId

[<RequireQualifiedAccess>]
type RdfTerm =
    | Resource of RdfResource
    | Literal of RdfLiteral

[<RequireQualifiedAccess>]
type RdfGraphName =
    | Iri of RdfIri
    | BlankNode of RdfBlankNodeId

type RdfSourceProvenance =
    { SourceVersionId: string
      EvidenceAddresses: string list }

type RdfInferenceProvenance =
    { EngineId: string
      EngineVersion: string
      RuleOrAxiomId: string
      PremiseStatementIds: string list }

type RdfAuthorizedUpdateProvenance =
    { OperationId: string
      EndpointId: string
      PermissionDecisionId: Guid
      PriorDatasetRevision: int64 }

[<RequireQualifiedAccess>]
type RdfStatementOrigin =
    | Asserted of RdfSourceProvenance
    | AuthorizedUpdate of RdfAuthorizedUpdateProvenance
    | Inferred of RdfInferenceProvenance

type RdfStatement =
    { Id: string
      Subject: RdfResource
      Predicate: RdfIri
      Object: RdfTerm
      Graph: RdfGraphName option
      Origin: RdfStatementOrigin }

type RdfPrefix =
    private
        { Name: string
          Namespace: RdfIri }

[<RequireQualifiedAccess>]
module RdfPrefix =
    let create name namespaceIri =
        if isNull name || not (Regex.IsMatch(name, "^[A-Za-z][A-Za-z0-9_-]*$")) then
            invalidArg (nameof name) "RDF prefix name is not canonical."

        { Name = name
          Namespace = namespaceIri }

    let name value = value.Name
    let namespaceIri value = value.Namespace

type RdfDataset =
    private
        { Owner: AuthorizationIdentity
          Id: string
          Revision: int64
          Prefixes: Map<string, RdfIri>
          Statements: RdfStatement list }

[<RequireQualifiedAccess>]
module RdfDataset =
    let private canonical name value =
        if String.IsNullOrWhiteSpace value || value <> value.Trim() then
            invalidArg name $"{name} must be canonical and non-blank."

    let private validateOrigin =
        function
        | RdfStatementOrigin.Asserted provenance ->
            canonical (nameof provenance.SourceVersionId) provenance.SourceVersionId

            if List.isEmpty provenance.EvidenceAddresses then
                invalidArg (nameof provenance.EvidenceAddresses) "Asserted RDF statement requires source evidence."

            provenance.EvidenceAddresses
            |> List.iter (canonical (nameof provenance.EvidenceAddresses))
        | RdfStatementOrigin.AuthorizedUpdate provenance ->
            canonical (nameof provenance.OperationId) provenance.OperationId
            canonical (nameof provenance.EndpointId) provenance.EndpointId

            if provenance.PermissionDecisionId = Guid.Empty then
                invalidArg
                    (nameof provenance.PermissionDecisionId)
                    "Authorized RDF update provenance requires a permission decision identity."

            if provenance.PriorDatasetRevision <= 0L then
                invalidArg
                    (nameof provenance.PriorDatasetRevision)
                    "Authorized RDF update provenance requires a positive prior dataset revision."
        | RdfStatementOrigin.Inferred provenance ->
            canonical (nameof provenance.EngineId) provenance.EngineId
            canonical (nameof provenance.EngineVersion) provenance.EngineVersion
            canonical (nameof provenance.RuleOrAxiomId) provenance.RuleOrAxiomId

            if List.isEmpty provenance.PremiseStatementIds then
                invalidArg (nameof provenance.PremiseStatementIds) "Inferred RDF statement requires premises."

            provenance.PremiseStatementIds
            |> List.iter (canonical (nameof provenance.PremiseStatementIds))

    let create
        (owner: AuthorizationIdentity)
        (id: string)
        (revision: int64)
        (prefixes: RdfPrefix list)
        (statements: RdfStatement list)
        =
        canonical (nameof id) id

        if revision <= 0L then
            invalidArg (nameof revision) "RDF dataset revision must be positive."

        let statementIds = statements |> List.map _.Id

        if statementIds |> List.distinct |> List.length <> statementIds.Length then
            invalidArg (nameof statements) "RDF statement identities must be unique within a dataset."

        statements
        |> List.iter (fun (statement: RdfStatement) ->
            canonical (nameof statement.Id) statement.Id
            validateOrigin statement.Origin)

        let prefixMap =
            prefixes
            |> List.map (fun prefix -> RdfPrefix.name prefix, RdfPrefix.namespaceIri prefix)
            |> Map.ofList

        if prefixMap.Count <> prefixes.Length then
            invalidArg (nameof prefixes) "RDF prefix names must be unique."

        { Owner = owner
          Id = id
          Revision = revision
          Prefixes = prefixMap
          Statements = statements }

    let requireOwner authorization dataset =
        if dataset.Owner <> AuthorizationScope.identity authorization then
            raise (UnauthorizedAccessException "RDF dataset access cannot cross authorization boundaries.")

        dataset

    let owner value = value.Owner
    let id value = value.Id
    let revision value = value.Revision
    let prefixes value = value.Prefixes
    let statements value = value.Statements

    let asserted value =
        value.Statements
        |> List.filter (fun statement ->
            match statement.Origin with
            | RdfStatementOrigin.Asserted _
            | RdfStatementOrigin.AuthorizedUpdate _ -> true
            | _ -> false)

    let inferred value =
        value.Statements
        |> List.filter (fun statement ->
            match statement.Origin with
            | RdfStatementOrigin.Inferred _ -> true
            | _ -> false)

type OntologyDescriptor =
    private
        { OntologyIri: RdfIri
          VersionIri: RdfIri
          Imports: Set<RdfIri>
          Compatibility: Map<string, string> }

[<RequireQualifiedAccess>]
module OntologyDescriptor =
    let create ontologyIri versionIri imports compatibility =
        if imports |> Set.contains ontologyIri || imports |> Set.contains versionIri then
            invalidArg (nameof imports) "Ontology cannot import itself or its version IRI."

        if
            compatibility
            |> Map.exists (fun key value ->
                String.IsNullOrWhiteSpace key
                || key <> key.Trim()
                || String.IsNullOrWhiteSpace value
                || value <> value.Trim())
        then
            invalidArg (nameof compatibility) "Ontology compatibility metadata must be canonical."

        { OntologyIri = ontologyIri
          VersionIri = versionIri
          Imports = imports
          Compatibility = compatibility }

    let ontologyIri value = value.OntologyIri
    let versionIri value = value.VersionIri
    let imports value = value.Imports
    let compatibility value = value.Compatibility
