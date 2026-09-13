namespace Nao.Agents

open System
open System.Threading.Tasks

/// An episode represents a discrete event in the agent's experience
type Episode =
    { Id: string
      Action: string
      Observation: string
      Context: string
      Success: bool
      Importance: float
      Timestamp: DateTimeOffset
      Tags: string list
      Valence: float
      LinkedEpisodes: string list }

/// Query for retrieving episodes
[<RequireQualifiedAccess>]
type EpisodeQuery =
    /// Find episodes similar to a description
    | BySimilarity of description: string * topK: int
    /// Find episodes within a time range
    | ByTimeRange of from': DateTimeOffset * to': DateTimeOffset
    /// Find episodes by tags
    | ByTags of tags: string list
    /// Find recent episodes
    | Recent of count: int
    /// Find episodes related to a specific episode
    | Related of episodeId: string * maxHops: int
    /// Find episodes matching success/failure
    | ByOutcome of success: bool * topK: int

/// Functional episodic memory — stores sequences of experiences.
type EpisodicMemory =
    { RecordAsync: AuthorizationScope -> Episode -> Task<unit>
      QueryAsync: AuthorizationScope -> EpisodeQuery -> Task<Episode list>
      LinkAsync: AuthorizationScope -> string -> string -> Task<unit>
      GetChainAsync: AuthorizationScope -> string -> Task<Episode list>
      SynthesizeAsync: AuthorizationScope -> string -> Task<string list>
      ForgetBelowAsync: AuthorizationScope -> float -> Task<int>
      DeleteOwnerAsync: AuthorizationScope -> Task<Result<int, PlatformFailure>>
      DeleteExpiredAsync: AuthorizationScope -> DateTimeOffset -> Task<Result<int, PlatformFailure>> }
