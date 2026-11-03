#if ANDROID
using System.Text.Json;
using System.Text.Json.Serialization;
using Android.Runtime;
using RadioApp.Core.Services;
using RadioApp.Maui.Services;

namespace RadioApp.Maui.Platforms.Android;

/// <summary>
/// Userspace Tailscale bridge backed by gomobile Go AAR (tsnet.Tsnet_).
/// Coexists with Android VpnService apps (Happ / VLESS / Xray) via HTTPS DERP + local 127.0.0.1 loopback proxy.
/// </summary>
public sealed class AndroidTsnetBridge : ITailscaleBridge, IDisposable
{
    private const string DefaultHostname = "radiowork-android";

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly SemaphoreSlim _gate = new(1, 1);

    private IntPtr _classHandle;
    private IntPtr _accountInstance;
    private IntPtr _authKeyInstance;
    private string _startedAuthKey = "";
    private bool _authKeyRunning;
    private bool _disposed;

    public bool IsSupported => true;
    public bool IsConnected { get; private set; }
    public bool IsBusy { get; private set; }
    public string State { get; private set; } = "NotStarted";
    public string? LoginName { get; private set; }
    public string? AuthUrl { get; private set; }
    public string? ActiveForwardUrl { get; private set; }
    public string? TargetDisplay { get; private set; }
    public string? LastError { get; private set; }
    public IReadOnlyList<TailscalePeer> Peers { get; private set; } = [];

    public event Action? StateChanged;

    public bool HasSavedSession(string? savedAuthKey)
    {
        if (!string.IsNullOrWhiteSpace(savedAuthKey))
            return true;

        var stateFile = Path.Combine(GetStateDir("tsnet-account"), "tailscaled.state");
        return File.Exists(stateFile) && new FileInfo(stateFile).Length > 0;
    }

    public async Task<string?> TryAutoConnectAsync(string serverBaseUrl, string? savedAuthKey, CancellationToken ct = default)
    {
        if (!HasSavedSession(savedAuthKey))
            return null;

        if (!string.IsNullOrWhiteSpace(savedAuthKey))
            return await ConnectWithAuthKeyAsync(savedAuthKey, serverBaseUrl, ct);

        await _gate.WaitAsync(ct);
        try
        {
            IsBusy = true;
            LastError = null;
            NotifyStateChanged();

            return await Task.Run(async () =>
            {
                var snap = InvokeAccountJni("begin");
                ApplyAccountSnapshot(snap);

                for (int i = 0; i < 30 && !snap.Running && snap.State != "NeedsLogin"; i++)
                {
                    ct.ThrowIfCancellationRequested();
                    await Task.Delay(500, ct);
                    snap = InvokeAccountJni("status");
                    ApplyAccountSnapshot(snap);
                }

                if (!snap.Running)
                    return null;

                var (host, port) = ParseTargetHostAndPort(serverBaseUrl);
                var fwdUrl = InvokeStartForwarderJni(_accountInstance, host, port);
                ActiveForwardUrl = fwdUrl;
                UpdateTargetDisplay(host, port);
                return fwdUrl;
            }, ct);
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            State = "Error";
            return null;
        }
        finally
        {
            IsBusy = false;
            _gate.Release();
            NotifyStateChanged();
        }
    }

    public async Task<string?> BeginInteractiveLoginAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            IsBusy = true;
            LastError = null;
            State = "Starting";
            NotifyStateChanged();

            return await Task.Run(async () =>
            {
                var snap = InvokeAccountJni("begin");
                ApplyAccountSnapshot(snap);

                for (int i = 0; i < 20 && !snap.Running && string.IsNullOrEmpty(snap.AuthUrl); i++)
                {
                    ct.ThrowIfCancellationRequested();
                    await Task.Delay(400, ct);
                    var cmd = snap.State is "NeedsLogin" or "NoState" or "Stopped" or "" ? "begin" : "status";
                    snap = InvokeAccountJni(cmd);
                    ApplyAccountSnapshot(snap);
                }

                return snap.AuthUrl;
            }, ct);
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            State = "Error";
            return null;
        }
        finally
        {
            IsBusy = false;
            _gate.Release();
            NotifyStateChanged();
        }
    }

    public async Task<string?> PollAndForwardIfRunningAsync(string serverBaseUrl, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            return await Task.Run(() =>
            {
                if (_authKeyRunning && _authKeyInstance != IntPtr.Zero)
                {
                    var (kHost, kPort) = ParseTargetHostAndPort(serverBaseUrl);
                    var kFwd = InvokeStartForwarderJni(_authKeyInstance, kHost, kPort);
                    ActiveForwardUrl = kFwd;
                    IsConnected = true;
                    State = "Running";
                    UpdateTargetDisplay(kHost, kPort);
                    return kFwd;
                }

                if (_accountInstance == IntPtr.Zero && !HasSavedSession(null))
                    return null;

                var snap = InvokeAccountJni(_accountInstance == IntPtr.Zero ? "begin" : "status");
                ApplyAccountSnapshot(snap);

                if (!snap.Running)
                    return null;

                var (host, port) = ParseTargetHostAndPort(serverBaseUrl);
                var fwdUrl = InvokeStartForwarderJni(_accountInstance, host, port);
                ActiveForwardUrl = fwdUrl;
                LastError = null;
                UpdateTargetDisplay(host, port);
                return fwdUrl;
            }, ct);
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            return null;
        }
        finally
        {
            _gate.Release();
            NotifyStateChanged();
        }
    }

    public async Task<string?> ConnectWithAuthKeyAsync(string authKey, string serverBaseUrl, CancellationToken ct = default)
    {
        var cleanKey = authKey.Trim();
        if (string.IsNullOrEmpty(cleanKey))
            throw new ArgumentException("Укажите Tailscale Auth Key (tskey-auth-...).");

        await _gate.WaitAsync(ct);
        try
        {
            IsBusy = true;
            LastError = null;
            State = "Starting";
            NotifyStateChanged();

            return await Task.Run(() =>
            {
                EnsureTsnetClassLoaded();

                if (!_authKeyRunning || _authKeyInstance == IntPtr.Zero || _startedAuthKey != cleanKey)
                {
                    StopInstanceQuietly(ref _authKeyInstance);
                    _authKeyInstance = CreateTsnetInstance();
                    var stateDir = GetStateDir("tsnet-authkey");

                    var mid = JNIEnv.GetMethodID(
                        _classHandle,
                        "start",
                        "(Ljava/lang/String;Ljava/lang/String;Ljava/lang/String;Ljava/lang/String;J)V");
                    if (mid == IntPtr.Zero)
                        throw new MissingMethodException("tsnet.Tsnet_.start not found");

                    var jAuth = JNIEnv.NewString(cleanKey);
                    var jHost = JNIEnv.NewString(DefaultHostname);
                    var jCtrl = JNIEnv.NewString("");
                    var jDir = JNIEnv.NewString(stateDir);
                    try
                    {
                        JNIEnv.CallVoidMethod(
                            _authKeyInstance,
                            mid,
                            new JValue(jAuth),
                            new JValue(jHost),
                            new JValue(jCtrl),
                            new JValue(jDir),
                            new JValue(0L));
                        ThrowIfJniException("tsnet start");
                    }
                    finally
                    {
                        JNIEnv.DeleteLocalRef(jAuth);
                        JNIEnv.DeleteLocalRef(jHost);
                        JNIEnv.DeleteLocalRef(jCtrl);
                        JNIEnv.DeleteLocalRef(jDir);
                    }

                    _startedAuthKey = cleanKey;
                    _authKeyRunning = true;
                }

                var (targetHost, targetPort) = ParseTargetHostAndPort(serverBaseUrl);
                var fwdUrl = InvokeStartForwarderJni(_authKeyInstance, targetHost, targetPort);
                IsConnected = true;
                State = "Running";
                AuthUrl = null;
                LoginName = "AuthKey";
                ActiveForwardUrl = fwdUrl;
                UpdateTargetDisplay(targetHost, targetPort);
                return fwdUrl;
            }, ct);
        }
        catch (Exception ex)
        {
            _authKeyRunning = false;
            IsConnected = false;
            ActiveForwardUrl = null;
            LastError = ex.Message;
            State = "Error";
            return null;
        }
        finally
        {
            IsBusy = false;
            _gate.Release();
            NotifyStateChanged();
        }
    }

    public async Task LogoutAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            IsBusy = true;
            NotifyStateChanged();

            await Task.Run(() =>
            {
                if (_accountInstance != IntPtr.Zero)
                {
                    try { _ = InvokeAccountJni("logout"); } catch { }
                    StopInstanceQuietly(ref _accountInstance);
                }

                if (_authKeyInstance != IntPtr.Zero)
                {
                    StopInstanceQuietly(ref _authKeyInstance);
                }

                _authKeyRunning = false;
                _startedAuthKey = "";

                var accountDir = GetStateDir("tsnet-account");
                try
                {
                    if (Directory.Exists(accountDir))
                        Directory.Delete(accountDir, recursive: true);
                }
                catch { }
            }, ct);

            IsConnected = false;
            State = "NotStarted";
            LoginName = null;
            AuthUrl = null;
            ActiveForwardUrl = null;
            TargetDisplay = null;
            LastError = null;
            Peers = [];
        }
        finally
        {
            IsBusy = false;
            _gate.Release();
            NotifyStateChanged();
        }
    }

    private AccountSnapshotDto InvokeAccountJni(string command)
    {
        EnsureTsnetClassLoaded();
        if (_accountInstance == IntPtr.Zero)
            _accountInstance = CreateTsnetInstance();

        var mid = JNIEnv.GetMethodID(
            _classHandle,
            "account",
            "(Ljava/lang/String;Ljava/lang/String;)Ljava/lang/String;");
        if (mid == IntPtr.Zero)
            throw new MissingMethodException("tsnet.Tsnet_.account not found");

        var payloadJson = JsonSerializer.Serialize(new
        {
            Hostname = DefaultHostname,
            StateDir = GetStateDir("tsnet-account")
        });

        var jCmd = JNIEnv.NewString(command);
        var jPayload = JNIEnv.NewString(payloadJson);
        try
        {
            var jRes = JNIEnv.CallObjectMethod(_accountInstance, mid, new JValue(jCmd), new JValue(jPayload));
            ThrowIfJniException($"tsnet account({command})");
            if (jRes == IntPtr.Zero)
                return new AccountSnapshotDto();

            string raw;
            try
            {
                raw = JNIEnv.GetString(jRes, JniHandleOwnership.DoNotTransfer) ?? "{}";
            }
            finally
            {
                JNIEnv.DeleteLocalRef(jRes);
            }

            return JsonSerializer.Deserialize<AccountSnapshotDto>(raw, JsonOpts) ?? new AccountSnapshotDto();
        }
        finally
        {
            JNIEnv.DeleteLocalRef(jCmd);
            JNIEnv.DeleteLocalRef(jPayload);
        }
    }

    private string InvokeStartForwarderJni(IntPtr instance, string host, int port)
    {
        if (instance == IntPtr.Zero)
            throw new InvalidOperationException("Tailscale instance is not initialized.");

        var mid = JNIEnv.GetMethodID(
            _classHandle,
            "startForwarder",
            "(Ljava/lang/String;J)Ljava/lang/String;");
        if (mid == IntPtr.Zero)
            throw new MissingMethodException("tsnet.Tsnet_.startForwarder not found");

        var jHost = JNIEnv.NewString(host);
        try
        {
            var jUrl = JNIEnv.CallObjectMethod(instance, mid, new JValue(jHost), new JValue((long)port));
            ThrowIfJniException($"tsnet startForwarder({host}:{port})");
            if (jUrl == IntPtr.Zero)
                throw new IOException("tsnet startForwarder returned null");

            try
            {
                var url = JNIEnv.GetString(jUrl, JniHandleOwnership.DoNotTransfer);
                if (string.IsNullOrWhiteSpace(url))
                    throw new IOException("tsnet startForwarder returned empty URL");
                return url;
            }
            finally
            {
                JNIEnv.DeleteLocalRef(jUrl);
            }
        }
        finally
        {
            JNIEnv.DeleteLocalRef(jHost);
        }
    }

    private void ApplyAccountSnapshot(AccountSnapshotDto snap)
    {
        IsConnected = snap.Running;
        State = string.IsNullOrWhiteSpace(snap.State) ? "Unknown" : snap.State;
        AuthUrl = string.IsNullOrWhiteSpace(snap.AuthUrl) ? null : snap.AuthUrl;
        LoginName = string.IsNullOrWhiteSpace(snap.Login) ? null : snap.Login;
        Peers = snap.Peers?
            .Select(p => new TailscalePeer(p.Id ?? "", p.Name ?? "", p.Dns ?? "", p.Ip ?? "", p.Online))
            .ToList() ?? [];

        if (!snap.Running)
            ActiveForwardUrl = null;
    }

    private void UpdateTargetDisplay(string host, int port)
    {
        var matched = Peers.FirstOrDefault(p =>
            string.Equals(p.Name, host, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(p.Dns, host, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(p.Ip, host, StringComparison.OrdinalIgnoreCase));

        TargetDisplay = matched is not null && !string.IsNullOrEmpty(matched.Ip)
            ? $"{matched.Name} ({matched.Ip}:{port})"
            : $"{host}:{port}";
    }

    private static (string Host, int Port) ParseTargetHostAndPort(string serverBaseUrl)
    {
        var normalized = UserRadioStore.NormalizeServerUrl(serverBaseUrl);
        if (Uri.TryCreate(normalized, UriKind.Absolute, out var uri) &&
            !string.IsNullOrWhiteSpace(uri.Host) &&
            uri.Host != "127.0.0.1" &&
            uri.Host != "localhost")
        {
            int port = uri.IsDefaultPort ? 5480 : uri.Port;
            return (uri.Host, port);
        }

        return ("mks68i7rtx", 5480);
    }

    private static string GetStateDir(string folderName)
    {
        var appCtx = global::Android.App.Application.Context;
        var root = appCtx.NoBackupFilesDir?.AbsolutePath
                   ?? appCtx.FilesDir?.AbsolutePath
                   ?? FileSystem.AppDataDirectory;
        var dir = Path.Combine(root, folderName);
        Directory.CreateDirectory(dir);
        return dir;
    }

    private void EnsureTsnetClassLoaded()
    {
        if (_classHandle != IntPtr.Zero)
            return;

        var appCtx = global::Android.App.Application.Context;
        var loader = appCtx.ClassLoader
                     ?? Java.Lang.Class.FromType(typeof(Java.Lang.Object)).ClassLoader
                     ?? throw new PlatformNotSupportedException("Android ClassLoader unavailable.");

        Java.Lang.Class? tsnetClass;
        try
        {
            tsnetClass = Java.Lang.Class.ForName("tsnet.Tsnet_", true, loader);
        }
        catch (Java.Lang.ClassNotFoundException)
        {
            tsnetClass = null;
        }

        if (tsnetClass is null)
            throw new PlatformNotSupportedException("Вшитый модуль tsnet.aar не найден в сборке APK.");

        var globalRef = JNIEnv.NewGlobalRef(tsnetClass.Handle);
        if (globalRef == IntPtr.Zero)
            throw new PlatformNotSupportedException("JNI NewGlobalRef(tsnet.Tsnet_) failed.");

        _classHandle = globalRef;
    }

    private IntPtr CreateTsnetInstance()
    {
        var ctor = JNIEnv.GetMethodID(_classHandle, "<init>", "()V");
        if (ctor == IntPtr.Zero)
            throw new MissingMethodException("tsnet.Tsnet_::<init> not found");

        var local = JNIEnv.NewObject(_classHandle, ctor);
        ThrowIfJniException("new tsnet.Tsnet_()");
        if (local == IntPtr.Zero)
            throw new InvalidOperationException("Failed to instantiate tsnet.Tsnet_");

        var global = JNIEnv.NewGlobalRef(local);
        JNIEnv.DeleteLocalRef(local);
        return global;
    }

    private void StopInstanceQuietly(ref IntPtr instance)
    {
        if (instance == IntPtr.Zero)
            return;

        try
        {
            if (_classHandle != IntPtr.Zero)
            {
                var mid = JNIEnv.GetMethodID(_classHandle, "stop", "()V");
                if (mid != IntPtr.Zero)
                {
                    JNIEnv.CallVoidMethod(instance, mid);
                    if (JNIEnv.ExceptionOccurred() != IntPtr.Zero)
                        JNIEnv.ExceptionClear();
                }
            }
        }
        catch { }
        finally
        {
            JNIEnv.DeleteGlobalRef(instance);
            instance = IntPtr.Zero;
        }
    }

    private static void ThrowIfJniException(string context)
    {
        var exHandle = JNIEnv.ExceptionOccurred();
        if (exHandle == IntPtr.Zero)
            return;

        JNIEnv.ExceptionClear();
        using var throwable = Java.Lang.Object.GetObject<Java.Lang.Throwable>(exHandle, JniHandleOwnership.TransferLocalRef);
        var msg = throwable?.Message ?? throwable?.ToString() ?? context;
        throw new InvalidOperationException($"{context}: {msg}");
    }

    private void NotifyStateChanged()
    {
        try { StateChanged?.Invoke(); } catch { }
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;

        StopInstanceQuietly(ref _accountInstance);
        StopInstanceQuietly(ref _authKeyInstance);
        if (_classHandle != IntPtr.Zero)
        {
            JNIEnv.DeleteGlobalRef(_classHandle);
            _classHandle = IntPtr.Zero;
        }
        _gate.Dispose();
    }

    private sealed class AccountSnapshotDto
    {
        [JsonPropertyName("running")]
        public bool Running { get; set; }

        [JsonPropertyName("state")]
        public string? State { get; set; }

        [JsonPropertyName("authUrl")]
        public string? AuthUrl { get; set; }

        [JsonPropertyName("login")]
        public string? Login { get; set; }

        [JsonPropertyName("peers")]
        public List<PeerDto>? Peers { get; set; }
    }

    private sealed class PeerDto
    {
        [JsonPropertyName("id")]
        public string? Id { get; set; }

        [JsonPropertyName("name")]
        public string? Name { get; set; }

        [JsonPropertyName("dns")]
        public string? Dns { get; set; }

        [JsonPropertyName("ip")]
        public string? Ip { get; set; }

        [JsonPropertyName("online")]
        public bool Online { get; set; }
    }
}
#endif
