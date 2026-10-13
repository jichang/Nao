namespace Nao.Knowledge

open System
open System.Diagnostics
open System.Threading
open System.Threading.Tasks
open Nao.Agents
open Nao.Protocols

type KnowledgeGenerationAdapterDescriptor =
    private
        { Component: ComponentVersion
          ConfigurationHash: ContentHash }

[<RequireQualifiedAccess>]
module KnowledgeGenerationAdapterDescriptor =
    let create adapterComponent configurationHash =
        if isNull (box adapterComponent) then
            nullArg (nameof adapterComponent)

        { Component = adapterComponent
          ConfigurationHash = configurationHash }

    let adapterComponent value = value.Component
    let configurationHash value = value.ConfigurationHash

type KnowledgeGroundedGenerationBudget =
    private
        { MaximumDuration: TimeSpan
          MaximumTokens: int
          MaximumRepairAttempts: int }

[<RequireQualifiedAccess>]
module KnowledgeGroundedGenerationBudget =
    let create maximumDuration maximumTokens maximumRepairAttempts =
        if maximumDuration <= TimeSpan.Zero then
            invalidArg (nameof maximumDuration) "Grounded generation duration must be positive."

        if maximumTokens <= 0 then
            invalidArg (nameof maximumTokens) "Grounded generation token budget must be positive."

        if maximumRepairAttempts < 0 then
            invalidArg (nameof maximumRepairAttempts) "Grounded generation repair attempts cannot be negative."

        { MaximumDuration = maximumDuration
          MaximumTokens = maximumTokens
          MaximumRepairAttempts = maximumRepairAttempts }

    let maximumDuration value = value.MaximumDuration
    let maximumTokens value = value.MaximumTokens
    let maximumRepairAttempts value = value.MaximumRepairAttempts

type KnowledgeGroundedGenerationUsage =
    private
        { ProviderCalls: int
          Tokens: int
          Duration: TimeSpan }

[<RequireQualifiedAccess>]
module KnowledgeGroundedGenerationUsage =
    let providerCalls value = value.ProviderCalls
    let tokens value = value.Tokens
    let duration value = value.Duration

[<RequireQualifiedAccess>]
type KnowledgeGroundedGenerationFailure =
    | ProtocolViolation of ResponseParseError
    | MissingTokenUsage
    | TokenBudgetExceeded of maximum: int * observed: int
    | InvalidProtocolCardinality of observed: int

type KnowledgeGroundedGenerationExecution =
    private
        { Adapter: KnowledgeGenerationAdapterDescriptor
          Protocol: ResponseProtocolDescriptor
          Context: KnowledgeAssembledContext
          Result: KnowledgeGroundedGenerationResult
          Usage: KnowledgeGroundedGenerationUsage }

[<RequireQualifiedAccess>]
module KnowledgeGroundedGenerationExecution =
    let adapter value = value.Adapter
    let protocol value = value.Protocol
    let context value = value.Context
    let result value = value.Result
    let usage value = value.Usage

[<RequireQualifiedAccess>]
type KnowledgeGroundedGenerationAdapterResult =
    | Generated of KnowledgeGroundedGenerationExecution
    | Rejected of KnowledgeGroundedGenerationFailure list * KnowledgeGroundedGenerationUsage

type KnowledgeGroundedGenerationAdapter =
    { Descriptor: KnowledgeGenerationAdapterDescriptor
      GenerateAsync:
          AuthorizationScope
              -> CorrelationContext
              -> KnowledgeAssembledContext
              -> string
              -> CancellationToken
              -> Task<KnowledgeGroundedGenerationAdapterResult> }

[<RequireQualifiedAccess>]
module KnowledgeGroundedGenerationAdapter =
    let private contextAuthorization (context: KnowledgeAssembledContext) =
        context
        |> KnowledgeAssembledContext.reranking
        |> KnowledgeReranking.request
        |> KnowledgeRerankRequest.retrievalRequest
        |> KnowledgeRetrievalRequest.authorization

    let create
        (descriptor: KnowledgeGenerationAdapterDescriptor)
        (provider: LlmProvider)
        (options: CompletionOptions)
        (protocol: ResponseProtocol<KnowledgeGroundedGenerationResult>)
        (budget: KnowledgeGroundedGenerationBudget)
        =
        if isNull (box descriptor) then
            nullArg (nameof descriptor)

        if isNull (box provider) then
            nullArg (nameof provider)

        if isNull (box protocol) then
            nullArg (nameof protocol)

        if isNull (box budget) then
            nullArg (nameof budget)

        let generateAsync
            (authorization: AuthorizationScope)
            (correlation: CorrelationContext)
            (context: KnowledgeAssembledContext)
            (question: string)
            (cancellationToken: CancellationToken)
            =
            task {
                if isNull (box authorization) then
                    nullArg (nameof authorization)

                if isNull (box context) then
                    nullArg (nameof context)

                if String.IsNullOrWhiteSpace question || question <> question.Trim() then
                    invalidArg (nameof question) "Grounded generation question must be non-blank and trimmed."

                if authorization <> contextAuthorization context then
                    raise (
                        UnauthorizedAccessException(
                            "Grounded generation authorization must match the assembled context."
                        )
                    )

                use timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken)
                timeout.CancelAfter(KnowledgeGroundedGenerationBudget.maximumDuration budget)
                let timer = Stopwatch.StartNew()

                let systemPrompt = ResponseProtocol.promptInstructions protocol

                let initialConversation =
                    [ { Role = Role.System
                        Content = systemPrompt }
                      { Role = Role.User
                        Content = question + "\n\nGrounding context:\n" + KnowledgeAssembledContext.text context } ]

                let mutable conversation = initialConversation
                let mutable providerCalls = 0
                let mutable tokens = 0
                let mutable repairAttempts = 0

                let mutable outcome
                    : Result<KnowledgeGroundedGenerationResult, KnowledgeGroundedGenerationFailure list> option =
                    None

                try
                    while outcome.IsNone do
                        let! completion = provider.CompleteAsync correlation conversation options timeout.Token

                        providerCalls <- providerCalls + 1

                        match completion.TokensUsed with
                        | None -> outcome <- Some(Error [ KnowledgeGroundedGenerationFailure.MissingTokenUsage ])
                        | Some used ->
                            tokens <- tokens + used
                            let maximumTokens = KnowledgeGroundedGenerationBudget.maximumTokens budget

                            if tokens > maximumTokens then
                                outcome <-
                                    Some(
                                        Error
                                            [ KnowledgeGroundedGenerationFailure.TokenBudgetExceeded(
                                                  maximumTokens,
                                                  tokens
                                              ) ]
                                    )
                            else
                                match protocol.Parse completion.Content with
                                | Ok [ result ] -> outcome <- Some(Ok result)
                                | Ok results ->
                                    outcome <-
                                        Some(
                                            Error
                                                [ KnowledgeGroundedGenerationFailure.InvalidProtocolCardinality
                                                      results.Length ]
                                        )
                                | Error parseError when
                                    repairAttempts < KnowledgeGroundedGenerationBudget.maximumRepairAttempts budget
                                    ->
                                    repairAttempts <- repairAttempts + 1

                                    conversation <-
                                        conversation
                                        @ [ { Role = Role.Assistant
                                              Content = completion.Content }
                                            { Role = Role.User
                                              Content = protocol.BuildRepairMessage parseError } ]
                                | Error parseError ->
                                    outcome <-
                                        Some(Error [ KnowledgeGroundedGenerationFailure.ProtocolViolation parseError ])

                    timer.Stop()

                    let usage =
                        { ProviderCalls = providerCalls
                          Tokens = tokens
                          Duration = timer.Elapsed }

                    match outcome.Value with
                    | Ok result ->
                        return
                            KnowledgeGroundedGenerationAdapterResult.Generated
                                { Adapter = descriptor
                                  Protocol = protocol.Descriptor
                                  Context = context
                                  Result = result
                                  Usage = usage }
                    | Error failures -> return KnowledgeGroundedGenerationAdapterResult.Rejected(failures, usage)
                with :? OperationCanceledException when
                    timeout.IsCancellationRequested && not cancellationToken.IsCancellationRequested ->
                    return raise (TimeoutException("Grounded generation exceeded its duration budget."))
            }

        { Descriptor = descriptor
          GenerateAsync = generateAsync }
