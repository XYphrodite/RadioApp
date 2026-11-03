namespace RadioApp.Maui.Services;

public sealed record TailscalePeer(
    string Id,
    string Name,
    string Dns,
    string Ip,
    bool Online);

public interface ITailscaleBridge
{
    bool IsSupported { get; }
    bool IsConnected { get; }
    bool IsBusy { get; }
    string State { get; }
    string? LoginName { get; }
    string? AuthUrl { get; }
    string? ActiveForwardUrl { get; }
    string? TargetDisplay { get; }
    string? LastError { get; }
    IReadOnlyList<TailscalePeer> Peers { get; }

    event Action? StateChanged;

    bool HasSavedSession(string? savedAuthKey);
    Task<string?> TryAutoConnectAsync(string serverBaseUrl, string? savedAuthKey, CancellationToken ct = default);
    Task<string?> BeginInteractiveLoginAsync(CancellationToken ct = default);
    Task<string?> PollAndForwardIfRunningAsync(string serverBaseUrl, CancellationToken ct = default);
    Task<string?> ConnectWithAuthKeyAsync(string authKey, string serverBaseUrl, CancellationToken ct = default);
    Task LogoutAsync(CancellationToken ct = default);
}

public sealed class NoOpTailscaleBridge : ITailscaleBridge
{
    public bool IsSupported => false;
    public bool IsConnected => false;
    public bool IsBusy => false;
    public string State => "Unsupported";
    public string? LoginName => null;
    public string? AuthUrl => null;
    public string? ActiveForwardUrl => null;
    public string? TargetDisplay => null;
    public string? LastError => null;
    public IReadOnlyList<TailscalePeer> Peers => [];

    public event Action? StateChanged
    {
        add { }
        remove { }
    }

    public bool HasSavedSession(string? savedAuthKey) => false;

    public Task<string?> TryAutoConnectAsync(string serverBaseUrl, string? savedAuthKey, CancellationToken ct = default) =>
        Task.FromResult<string?>(null);

    public Task<string?> BeginInteractiveLoginAsync(CancellationToken ct = default) =>
        Task.FromResult<string?>(null);

    public Task<string?> PollAndForwardIfRunningAsync(string serverBaseUrl, CancellationToken ct = default) =>
        Task.FromResult<string?>(null);

    public Task<string?> ConnectWithAuthKeyAsync(string authKey, string serverBaseUrl, CancellationToken ct = default) =>
        Task.FromResult<string?>(null);

    public Task LogoutAsync(CancellationToken ct = default) => Task.CompletedTask;
}
