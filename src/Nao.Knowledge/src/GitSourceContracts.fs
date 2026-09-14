namespace Nao.Knowledge

open System
open System.Threading
open System.Threading.Tasks

[<Struct>]
type GitCommitId = private GitCommitId of string

module GitCommitId =
    let tryParse (value: string) =
        if
            not (isNull value)
            && (value.Length = 40 || value.Length = 64)
            && value
               |> Seq.forall (fun character -> Char.IsDigit character || character >= 'a' && character <= 'f')
        then
            Some(GitCommitId value)
        else
            None

    let parse value =
        tryParse value
        |> Option.defaultWith (fun () -> invalidArg (nameof value) "Git commit ID must be canonical lowercase hex.")

    let value (GitCommitId value) = value

type GitSourceFile =
    { Path: string
      Content: byte array
      MediaType: string option }

type GitSourceSnapshot =
    { CommitId: GitCommitId
      CommittedAt: DateTimeOffset
      Files: GitSourceFile list }

type GitSourceReadRequest =
    { RepositoryUri: Uri
      Reference: string
      PreviousCommit: GitCommitId option
      MaximumBlobBytes: int64
      MaximumItems: int
      Network: SourceNetworkPolicy }

type GitSourceReadResult =
    | Unchanged of GitCommitId
    | Snapshot of GitSourceSnapshot

type GitSourceRepository =
    { ReadAsync: GitSourceReadRequest -> CancellationToken -> Task<GitSourceReadResult> }

type GitSourceConnectorConfig =
    { Reference: string
      Include: string list
      Exclude: string list
      MaximumBlobBytes: int64
      MaximumItems: int
      Repository: GitSourceRepository }
