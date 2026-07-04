namespace Nao.Agents

open System
open System.Threading.Tasks

/// An episode represents a discrete event in the agent's experience
type Episode =
    { Id: string
      /// What happened
      Action: string
      /// The outcome/observation
      Observation: string
      /// Context at the time (e.g., what task was being performed)
      Context: string
      /// Whether the outcome was successful
      Success: bool
      /// Relevance/importance score
      Importance: float
      /// When the episode occurred
      Timestamp: DateTimeOffset
      /// Tags for categorization
      Tags: string list
      /// Emotional valence: positive=reward, negative=punishment
      Valence: float
      /// Linked episode IDs (causal chain)
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

/// Interface for episodic memory — stores sequences of experiences
type IEpisodicMemory =
    /// Record a new episode
    abstract member RecordAsync: Episode -> Task<unit>
    /// Query episodes
    abstract member QueryAsync: EpisodeQuery -> Task<Episode list>
    /// Link two episodes (causal or temporal relationship)
    abstract member LinkAsync: fromId: string -> toId: string -> Task<unit>
    /// Get the full episode chain starting from a given episode
    abstract member GetChainAsync: episodeId: string -> Task<Episode list>
    /// Compute lessons learned from similar episodes (pattern recognition)
    abstract member SynthesizeAsync: context: string -> Task<string list>
    /// Forget episodes below importance threshold
    abstract member ForgetBelowAsync: importanceThreshold: float -> Task<int>
