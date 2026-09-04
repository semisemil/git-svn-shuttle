namespace GitSvnShuttle.Vsix;

internal enum BusyExecutionKind
{
    Completed,
    Cancelled,
    Failed,
    Skipped,
}

internal sealed class BusyExecutionResult
{
    private BusyExecutionResult(BusyExecutionKind kind, string message)
    {
        Kind = kind;
        Message = message;
    }

    public BusyExecutionKind Kind { get; }
    public string Message { get; }

    public static BusyExecutionResult Completed() => new BusyExecutionResult(BusyExecutionKind.Completed, string.Empty);
    public static BusyExecutionResult Cancelled() => new BusyExecutionResult(BusyExecutionKind.Cancelled, "사용자가 작업을 취소했습니다.");
    public static BusyExecutionResult Failed(string message) => new BusyExecutionResult(BusyExecutionKind.Failed, message);
    public static BusyExecutionResult Skipped() => new BusyExecutionResult(BusyExecutionKind.Skipped, "작업을 시작하지 않았습니다.");
}
