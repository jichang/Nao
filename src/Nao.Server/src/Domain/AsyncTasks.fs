namespace Nao.Assistant

/// Status string constants used on `TaskDto.Status` and on the grain-backed `TaskRef`.
/// The actual async-task execution now lives in the Orleans layer
/// (SessionTaskGrain / ITaskExecutor); only these shared status labels remain here so
/// the UI can classify a task without depending on the runtime assembly.
module AsyncTasks =

    [<RequireQualifiedAccess>]
    module Status =
        [<Literal>]
        let Pending = "pending"
        [<Literal>]
        let Running = "running"
        [<Literal>]
        let Completed = "completed"
        [<Literal>]
        let Failed = "failed"
        [<Literal>]
        let Cancelled = "cancelled"
