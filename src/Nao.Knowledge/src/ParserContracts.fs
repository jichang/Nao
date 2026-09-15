namespace Nao.Knowledge

open System
open System.Threading
open System.Threading.Tasks
open Nao.Agents

type ParserSignature = { Offset: int; Bytes: byte array }

type KnowledgeParserDescriptor =
    { Parser: ComponentVersion
      MediaTypes: Set<string>
      Signatures: ParserSignature list }

[<RequireQualifiedAccess>]
type ParsedContentTrust = | UntrustedSource

type ParsedContentUnit =
    { Kind: ContentUnitKind
      Text: string
      Location: SourceLocation
      Metadata: Map<string, string> }

type ParsedKnowledgeDocument =
    { Units: ParsedContentUnit list
      Language: string option
      RequiresOcr: bool
      Trust: ParsedContentTrust }

[<RequireQualifiedAccess>]
type ParserRejectionKind =
    | Malformed
    | Encrypted
    | Unsupported
    | Suspicious
    | RequiresOcr

type ParserRejection =
    { Kind: ParserRejectionKind
      Message: string }

type KnowledgeParser =
    { Descriptor: KnowledgeParserDescriptor
      ParseAsync:
          AuthorizationScope
              -> byte array
              -> string option
              -> CancellationToken
              -> Task<Result<ParsedKnowledgeDocument, ParserRejection>> }

type ParserLimits =
    { MaximumInputBytes: int64
      MaximumOutputCharacters: int
      MaximumUnits: int
      MaximumExpansionRatio: float
      Timeout: TimeSpan }

type LanguageDetector = { Detect: string -> string option }

type ParserRequest =
    { Authorization: AuthorizationScope
      DocumentVersionId: KnowledgeDocumentVersionId
      ContentHash: ContentHash
      MediaType: string
      FileName: string option
      Content: byte array
      Lifecycle: KnowledgeLifecycle
      RequestedAt: DateTimeOffset }

type QuarantinedParse =
    { DocumentVersionId: KnowledgeDocumentVersionId
      ContentHash: ContentHash
      Parser: ComponentVersion option
      Kind: ParserRejectionKind
      Message: string
      QuarantinedAt: DateTimeOffset }

[<RequireQualifiedAccess>]
type ParseOutcome =
    | Parsed of parser: ComponentVersion * document: ParsedKnowledgeDocument
    | Quarantined of QuarantinedParse

type ParserCatalog =
    { Discover: string -> byte array -> KnowledgeParser option }

type ParserService =
    { ParseAsync: ParserRequest -> CancellationToken -> Task<ParseOutcome> }
