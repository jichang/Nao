namespace Nao.Persistence

/// Selects which durable backend the persistence factories should produce. The host
/// turns this single knob to choose between the two storage categories: file system
/// or database.
[<RequireQualifiedAccess>]
type PersistenceMode =
    /// FileSystem-backed implementations rooted at the given directory.
    | File of baseDir: string
    /// Provider-agnostic ADO.NET implementations using the supplied connection factory.
    | Database of factory: DbConnectionFactory
