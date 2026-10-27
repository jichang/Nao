#nowarn "3511"

namespace Nao.Knowledge

open System
open System.Threading
open System.Threading.Tasks
open Nao.Agents

module KnowledgeParsingService =
    let private quarantine (request: ParserRequest) parser kind message now =
        ParseOutcome.Quarantined
            { DocumentVersionId = request.DocumentVersionId
              ContentHash = request.ContentHash
              Parser = parser
              Kind = kind
              Message = SecretRedaction.redact message
              QuarantinedAt = now }

    let private invalidUnit inputLength (unit: ParsedContentUnit) =
        isNull unit.Text
        || isNull (box unit.Metadata)
        || unit.Location.ByteOffset < 0L
        || unit.Location.ByteLength < 0L
        || unit.Location.ByteOffset > inputLength
        || unit.Location.ByteLength > inputLength - unit.Location.ByteOffset
        || unit.Location.CharacterOffset < 0
        || unit.Location.CharacterLength < 0
        || (unit.Location.Page |> Option.exists (fun page -> page <= 0))
        || (unit.Location.LineStart |> Option.exists (fun line -> line <= 0))
        || (unit.Location.LineEnd |> Option.exists (fun line -> line <= 0))

    let private privilegedMetadata =
        Set.ofList [ "authority"; "instruction.priority"; "role"; "system" ]

    let private attemptsPrivilegeElevation (unit: ParsedContentUnit) =
        unit.Metadata
        |> Map.exists (fun key _ -> privilegedMetadata.Contains(key.ToLowerInvariant()))

    let create registry limits languageDetector clock =
        if limits.MaximumInputBytes <= 0L then
            invalidArg (nameof limits.MaximumInputBytes) "Maximum parser input bytes must be positive."

        if limits.MaximumOutputCharacters <= 0 || limits.MaximumUnits <= 0 then
            invalidArg (nameof limits) "Parser output and unit limits must be positive."

        if
            not (Double.IsFinite limits.MaximumExpansionRatio)
            || limits.MaximumExpansionRatio <= 0.0
        then
            invalidArg (nameof limits.MaximumExpansionRatio) "Parser expansion ratio must be finite and positive."

        if limits.Timeout <= TimeSpan.Zero then
            invalidArg (nameof limits.Timeout) "Parser timeout must be positive."

        { ParseAsync =
            fun request cancellationToken ->
                task {
                    cancellationToken.ThrowIfCancellationRequested()

                    if isNull request.Content then
                        nullArg (nameof request.Content)

                    if ContentHash.computeBytes request.Content <> request.ContentHash then
                        invalidArg (nameof request) "Parser request content hash does not match its bytes."

                    if int64 request.Content.Length > limits.MaximumInputBytes then
                        return
                            quarantine
                                request
                                None
                                ParserRejectionKind.Suspicious
                                "Parser input exceeds the configured byte limit."
                                (clock ())
                    else
                        match registry.Discover request.MediaType request.Content with
                        | None ->
                            return
                                quarantine
                                    request
                                    None
                                    ParserRejectionKind.Unsupported
                                    "No parser supports the content media type or signature."
                                    (clock ())
                        | Some parser ->
                            use timeout = CancellationTokenSource.CreateLinkedTokenSource cancellationToken

                            timeout.CancelAfter limits.Timeout

                            try
                                let! result =
                                    parser.ParseAsync
                                        request.Authorization
                                        request.Content
                                        request.FileName
                                        timeout.Token

                                match result with
                                | Error rejection ->
                                    return
                                        quarantine
                                            request
                                            (Some parser.Descriptor.Parser)
                                            rejection.Kind
                                            rejection.Message
                                            (clock ())
                                | Ok document ->
                                    let invalidShape =
                                        isNull (box document.Units)
                                        || document.Units |> List.exists (invalidUnit (int64 request.Content.Length))

                                    if invalidShape then
                                        return
                                            quarantine
                                                request
                                                (Some parser.Descriptor.Parser)
                                                ParserRejectionKind.Suspicious
                                                "Parser returned invalid source locations or text."
                                                (clock ())
                                    elif document.RequiresOcr then
                                        return
                                            quarantine
                                                request
                                                (Some parser.Descriptor.Parser)
                                                ParserRejectionKind.RequiresOcr
                                                "Document requires OCR before text extraction."
                                                (clock ())
                                    else
                                        let language =
                                            document.Language
                                            |> Option.orElseWith (fun () ->
                                                languageDetector
                                                |> Option.bind (fun detector ->
                                                    document.Units
                                                    |> List.map _.Text
                                                    |> String.concat "\n"
                                                    |> detector.Detect))

                                        let document = { document with Language = language }

                                        let invalidLanguage =
                                            language
                                            |> Option.exists (fun value ->
                                                String.IsNullOrWhiteSpace value || value <> value.Trim())

                                        let elevatesPrivilege = document.Units |> List.exists attemptsPrivilegeElevation

                                        let outputCharacters =
                                            document.Units |> List.sumBy (fun unit -> int64 unit.Text.Length)

                                        let expansionRatio =
                                            float outputCharacters / float (max 1 request.Content.Length)

                                        if invalidLanguage || elevatesPrivilege then
                                            return
                                                quarantine
                                                    request
                                                    (Some parser.Descriptor.Parser)
                                                    ParserRejectionKind.Suspicious
                                                    "Parser returned invalid language or privileged metadata."
                                                    (clock ())
                                        elif
                                            document.Units.Length > limits.MaximumUnits
                                            || outputCharacters > int64 limits.MaximumOutputCharacters
                                            || expansionRatio > limits.MaximumExpansionRatio
                                        then
                                            return
                                                quarantine
                                                    request
                                                    (Some parser.Descriptor.Parser)
                                                    ParserRejectionKind.Suspicious
                                                    "Parser output exceeds configured bounds."
                                                    (clock ())
                                        elif document.Trust <> ParsedContentTrust.UntrustedSource then
                                            return
                                                quarantine
                                                    request
                                                    (Some parser.Descriptor.Parser)
                                                    ParserRejectionKind.Suspicious
                                                    "Parser attempted to elevate source content trust."
                                                    (clock ())
                                        else
                                            return ParseOutcome.Parsed(parser.Descriptor.Parser, document)
                            with :? OperationCanceledException when not cancellationToken.IsCancellationRequested ->
                                return
                                    quarantine
                                        request
                                        (Some parser.Descriptor.Parser)
                                        ParserRejectionKind.Suspicious
                                        "Parser exceeded its execution timeout."
                                        (clock ())
                } }
