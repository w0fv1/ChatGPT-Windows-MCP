namespace ChatGPTWindowsMcp;

internal enum RuntimeState
{
    Stopped,
    Starting,
    Running,
    Stopping,
    Faulted
}

internal sealed record RuntimeHealthSnapshot(
    RuntimeState State,
    bool McpReady,
    bool? TunnelReady,
    string Detail,
    DateTimeOffset? ObservedAt);

/// <summary>
/// Serializes health observations. A generation fences callbacks from a previous
/// run so a late probe/Exited callback cannot revive or fault a replacement run.
/// Running means the supervisor is active, not that a ChatGPT session works.
/// </summary>
internal sealed class RuntimeHealthState
{
    private readonly object _gate = new();
    private long _generation;
    private RuntimeHealthSnapshot _snapshot =
        new(RuntimeState.Stopped, false, null, "服务未启动。", null);

    public RuntimeHealthSnapshot Snapshot
    {
        get { lock (_gate) return _snapshot; }
    }

    public long Generation
    {
        get { lock (_gate) return _generation; }
    }

    public long BeginStart()
    {
        lock (_gate)
        {
            _generation++;
            _snapshot = new(RuntimeState.Starting, false, null, "正在启动本地服务。", null);
            return _generation;
        }
    }

    public bool Observe(long generation, TunnelHealthObservation observation)
    {
        lock (_gate)
        {
            if (generation != _generation ||
                _snapshot.State is not (RuntimeState.Starting or RuntimeState.Running))
                return false;

            _snapshot = _snapshot with
            {
                McpReady = observation.McpPortOpen,
                TunnelReady = observation.TunnelReady,
                Detail = observation.Detail,
                ObservedAt = observation.ObservedAt
            };
            return true;
        }
    }

    public bool MarkRunning(long generation)
    {
        lock (_gate)
        {
            if (generation != _generation || _snapshot.State != RuntimeState.Starting)
                return false;
            _snapshot = _snapshot with { State = RuntimeState.Running };
            return true;
        }
    }

    public bool Fail(long generation, string detail)
    {
        lock (_gate)
        {
            if (generation != _generation ||
                _snapshot.State is not (RuntimeState.Starting or RuntimeState.Running))
                return false;
            _snapshot = new(RuntimeState.Faulted, false, false, detail, DateTimeOffset.UtcNow);
            return true;
        }
    }

    public void BeginStop()
    {
        lock (_gate)
        {
            _generation++;
            _snapshot = new(RuntimeState.Stopping, false, null, "正在停止本地服务。", null);
        }
    }

    public void MarkStopped()
    {
        lock (_gate)
            _snapshot = new(RuntimeState.Stopped, false, null, "服务已停止。", null);
    }

    public static string RunningTitle(RuntimeHealthSnapshot snapshot) =>
        !snapshot.McpReady || snapshot.TunnelReady == false
            ? "● 本地连接异常"
            : snapshot.TunnelReady == true
                ? "● 本地端口与隧道就绪检查通过"
                : "● 进程运行中，隧道健康状态未验证";
}
