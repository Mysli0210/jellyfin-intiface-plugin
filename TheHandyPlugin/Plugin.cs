using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.TheHandy.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using MediaBrowser.Common.Net;
using Newtonsoft.Json.Linq;

namespace Jellyfin.TheHandy
{
    /// <summary>
    /// Main plugin class — Intiface WebSocket backend
    /// - Uses per-request override headers X-Intiface-WS / X-Intiface-User / X-Intiface-Pass
    /// - Reads .funscript file contents and sends base64 payload to Intiface endpoint
    /// - Implements reconnect/backoff, heartbeat, basic Buttplug/Intiface-compatible message shapes
    /// </summary>
    public class TheHandyPlugin : BasePlugin<PluginConfiguration>, IHasWebPages
    {
        public TheHandyPlugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer,
            IHttpClientFactory httpClientFactory,
            ILogger<TheHandyPlugin> logger)
            : base(applicationPaths, xmlSerializer)
        {
            Instance = this;
            _httpClientFactory = httpClientFactory;
            _logger = logger;
            _states = new Dictionary<string, TheHandySessionState>();
            _wsLock = new SemaphoreSlim(1, 1);
            _connectionBackoffState = new ConnectionBackoffState();
            _pendingRequestHeaders = new ConcurrentDictionary<string, (string url, string? user, string? pass)>();
        }

        public static TheHandyPlugin? Instance { get; private set; }
        public override Guid Id => Guid.Parse("5dfbd8b1-7439-4f3a-ac9f-69525d349d48");
        public override string Name => "Intiface";

        private readonly ILogger<TheHandyPlugin> _logger;
        private readonly IHttpClientFactory _httpClientFactory;

        private readonly Dictionary<string, TheHandySessionState> _states;

        // WebSocket instance & synchronization
        private ClientWebSocket? _websocket;
        private readonly SemaphoreSlim _wsLock;
        private CancellationTokenSource? _recvCts;
        private readonly ConnectionBackoffState _connectionBackoffState;

        // Temporary storage for per-request headers (keyed by request id). Controller stores them here then calls plugin endpoints.
        private readonly ConcurrentDictionary<string, (string url, string? user, string? pass)> _pendingRequestHeaders;

        public IEnumerable<PluginPageInfo> GetPages()
        {
            return new[]
            {
                new PluginPageInfo
                {
                    Name = this.Name,
                    EmbeddedResourcePath = string.Format(CultureInfo.InvariantCulture, "{0}.Configuration.configPage.html", this.GetType().Namespace)
                }
            };
        }

        // Public playback hook entry. If HttpContext is available we read X-Intiface- headers from it.
        public async Task HandleEvent(PlaybackProgressEventArgs eventArgs, PlaybackChange change, HttpContext? ctx = null)
        {
            _logger.LogDebug("HandleEvent: {path} change={change}", eventArgs.MediaInfo.Path, change);

            if (!_states.ContainsKey(eventArgs.MediaInfo.Path))
            {
                _states.Add(eventArgs.MediaInfo.Path, new TheHandySessionState());
            }

            var state = _states[eventArgs.MediaInfo.Path];

            if (state.state == State.NewVideo)
            {
                state.FunscriptPath = Path.ChangeExtension(eventArgs.MediaInfo.Path, ".funscript");
            }

            if (state.state == State.NewVideo)
            {
                if (File.Exists(state.FunscriptPath))
                {
                    _logger.LogInformation("Funscript found: {file}", state.FunscriptPath);
                    state.state = State.UploadingScript;

                    string scriptContents;
                    try
                    {
                        scriptContents = File.ReadAllText(state.FunscriptPath, Encoding.UTF8);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Failed reading funscript {file}", state.FunscriptPath);
                        state.state = State.NewVideo;
                        return;
                    }

                    state.FunscriptURL = "";
                    state.state = State.UploadedScript;

                    var connInfo = GetConnectionInfoFromContext(ctx);
                    await EnsureWebsocketConnected(connInfo).ConfigureAwait(false);

                    if (_websocket != null && _websocket.State == WebSocketState.Open)
                    {
                        await SendLoadScriptToIntiface(state, scriptContents).ConfigureAwait(false);
                        await UpdateServerTime(state).ConfigureAwait(false);
                    }
                }
            }

            if (state.state == State.UploadedScript || state.state == State.SyncStarted || state.state == State.Playing || state.state == State.Paused)
            {
                if (change == PlaybackChange.PlaybackStart && (state.state == State.Paused || state.state == State.SyncStarted || state.state == State.UploadedScript))
                {
                    var videoTime = TimeSpan.FromTicks((long)eventArgs.PlaybackPositionTicks);
                    var connInfo = GetConnectionInfoFromContext(ctx);

                    await EnsureWebsocketConnected(connInfo).ConfigureAwait(false);

                    if (_websocket != null && _websocket.State == WebSocketState.Open)
                    {
                        await SendPlayToIntiface(state, videoTime).ConfigureAwait(false);
                        state.state = State.Playing;
                    }
                    else
                    {
                        _logger.LogWarning("Not connected — cannot send Play");
                    }
                }
                else if (change == PlaybackChange.PlaybackStop && state.state == State.Playing)
                {
                    var connInfo = GetConnectionInfoFromContext(ctx);
                    await EnsureWebsocketConnected(connInfo).ConfigureAwait(false);

                    if (_websocket != null && _websocket.State == WebSocketState.Open)
                    {
                        await SendPauseToIntiface(state).ConfigureAwait(false);
                        state.state = State.Paused;
                    }
                }
            }
        }

        // Controller will call this to register per-request headers for next operations.
        // requestId is an opaque key created by controller (e.g., GUID).
        public void RegisterPendingRequestHeaders(string requestId, string url, string? user, string? pass)
        {
            if (string.IsNullOrWhiteSpace(url)) return;
            _pendingRequestHeaders[requestId] = (url, user, pass);
            // expire in 30s to avoid memory leak
            _ = Task.Run(async () =>
            {
                await Task.Delay(TimeSpan.FromSeconds(30)).ConfigureAwait(false);
                _pendingRequestHeaders.TryRemove(requestId, out _);
            });
        }

        // Extract connection info: priority is per-request pending headers if we know request-id via header; else inspect ctx headers directly; fallback to Configuration.ConnectionKey.
        private (string? url, string? user, string? pass) GetConnectionInfoFromContext(HttpContext? ctx)
        {
            if (ctx == null)
            {
                return (Configuration.ConnectionKey, null, null);
            }

            // If controller provided a pending-request id header, use the registered values
            if (ctx.Request.Headers.TryGetValue("X-Intiface-Request-Id", out var rid))
            {
                var key = rid.ToString();
                if (!string.IsNullOrEmpty(key) && _pendingRequestHeaders.TryRemove(key, out var tuple))
                {
                    return (tuple.url, tuple.user, tuple.pass);
                }
            }

            // Else take direct override headers (best-effort)
            ctx.Request.Headers.TryGetValue("X-Intiface-WS", out var ws);
            ctx.Request.Headers.TryGetValue("X-Intiface-User", out var usr);
            ctx.Request.Headers.TryGetValue("X-Intiface-Pass", out var p);

            var url = !StringValuesIsNullOrWhiteSpace(ws) ? ws.ToString() : Configuration.ConnectionKey;
            var user = !StringValuesIsNullOrWhiteSpace(usr) ? usr.ToString() : null;
            var pass = !StringValuesIsNullOrWhiteSpace(p) ? p.ToString() : null;

            return (url, user, pass);
        }

        private bool StringValuesIsNullOrWhiteSpace(Microsoft.Extensions.Primitives.StringValues v)
        {
            if (v.Count == 0) return true;
            foreach (var s in v)
            {
                if (!string.IsNullOrWhiteSpace(s)) return false;
            }
            return true;
        }

        // Ensure connected; supports reconnect/backoff and per-connection Authorization header
        private async Task EnsureWebsocketConnected((string? url, string? user, string? pass) connInfo)
        {
            var url = connInfo.url?.Trim();
            if (string.IsNullOrWhiteSpace(url))
            {
                _logger.LogWarning("No websocket url configured");
                return;
            }

            await _wsLock.WaitAsync().ConfigureAwait(false);
            try
            {
                // If existing websocket is open and same endpoint, keep it. (We do not do per-request separate connections for performance)
                if (_websocket != null && _websocket.State == WebSocketState.Open)
                {
                    return;
                }

                // Dispose old
                try
                {
                    _recvCts?.Cancel();
                    _websocket?.Abort();
                    _websocket?.Dispose();
                }
                catch { }

                // Build new websocket and attach headers if basic auth requested
                _websocket = new ClientWebSocket();
                if (!string.IsNullOrEmpty(connInfo.user))
                {
                    var basic = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{connInfo.user}:{connInfo.pass ?? ""}"));
                    _websocket.Options.SetRequestHeader("Authorization", "Basic " + basic);
                }

                _recvCts = new CancellationTokenSource();

                // Attempt connect with backoff loop
                _connectionBackoffState.ResetIfNeeded(url);
                var attempt = 0;
                while (!_recvCts.IsCancellationRequested)
                {
                    attempt++;
                    try
                    {
                        var uri = new Uri(url);
                        await _websocket.ConnectAsync(uri, CancellationToken.None).ConfigureAwait(false);
                        _logger.LogInformation("Connected to {url}", url);

                        // Reset backoff on success
                        _connectionBackoffState.OnSuccess();

                        // start_receive & heartbeat loops
                        _ = Task.Run(() => ReceiveLoop(_websocket, _recvCts.Token));
                        _ = Task.Run(() => HeartbeatLoop(_websocket, _recvCts.Token));
                        return;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Connect attempt {attempt} to {url} failed", attempt, url);
                        var delay = _connectionBackoffState.NextBackoff();
                        _logger.LogInformation("Reconnecting in {delayMs}ms", (int)delay.TotalMilliseconds);
                        await Task.Delay(delay).ConfigureAwait(false);

                        // recreate websocket instance for next attempt
                        try
                        {
                            _websocket?.Dispose();
                        }
                        catch { }
                        _websocket = new ClientWebSocket();
                        if (!string.IsNullOrEmpty(connInfo.user))
                        {
                            var basic = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{connInfo.user}:{connInfo.pass ?? ""}"));
                            _websocket.Options.SetRequestHeader("Authorization", "Basic " + basic);
                        }
                    }
                }
            }
            finally
            {
                _wsLock.Release();
            }
        }

        // Simple heartbeat loop (sends ping-style messages periodically). Many servers expect ping/pong at transport-level; we send an application-level ping as JSON to encourage keepalive.
        private async Task HeartbeatLoop(ClientWebSocket ws, CancellationToken ct)
        {
            try
            {
                while (!ct.IsCancellationRequested && ws.State == WebSocketState.Open)
                {
                    var ping = new JObject { ["type"] = "Ping", ["ts"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() }.ToString();
                    await SendStringAsync(ping).ConfigureAwait(false);
                    await Task.Delay(TimeSpan.FromSeconds(10), ct).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                _logger.LogError(ex, "HeartbeatLoop error");
            }
        }

        private async Task ReceiveLoop(ClientWebSocket ws, CancellationToken ct)
        {
            var buffer = new byte[8192];
            try
            {
                while (!ct.IsCancellationRequested && ws.State == WebSocketState.Open)
                {
                    var ms = new MemoryStream();
                    WebSocketReceiveResult? result = null;
                    do
                    {
                        result = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), ct).ConfigureAwait(false);
                        if (result.MessageType == WebSocketMessageType.Close)
                        {
                            _logger.LogInformation("Websocket closed by remote");
                            try
                            {
                                await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "closing", CancellationToken.None).ConfigureAwait(false);
                            }
                            catch { }
                            return;
                        }
                        ms.Write(buffer, 0, result.Count);
                    } while (!result.EndOfMessage);

                    ms.Seek(0, SeekOrigin.Begin);
                    var text = Encoding.UTF8.GetString(ms.ToArray());
                    _logger.LogDebug("WS RX: {text}", text);

                    // Attempt to parse as JSON and act on common messages
                    try
                    {
                        var j = JObject.Parse(text);
                        var t = j["type"]?.Value<string>();
                        if (t == "Pong")
                        {
                            _logger.LogDebug("Pong received");
                        }
                        else if (t == "ServerInfo")
                        {
                            _logger.LogInformation("Remote ServerInfo: {info}", j.ToString());
                        }
                        else
                        {
                            _logger.LogDebug("Unhandled message type {type}", t);
                        }
                    }
                    catch (Exception parseEx)
                    {
                        _logger.LogDebug(parseEx, "Failed to parse incoming WS message");
                    }
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                _logger.LogError(ex, "ReceiveLoop error");
            }
        }

        private async Task SendStringAsync(string payload)
        {
            if (_websocket == null || _websocket.State != WebSocketState.Open)
            {
                _logger.LogWarning("Send attempted while websocket not open");
                return;
            }

            var bytes = Encoding.UTF8.GetBytes(payload);
            var seg = new ArraySegment<byte>(bytes);
            try
            {
                await _websocket.SendAsync(seg, WebSocketMessageType.Text, true, CancellationToken.None).ConfigureAwait(false);
                _logger.LogDebug("WS TX: {payload}", payload);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error sending websocket payload");
            }
        }

        // Centralized message names — change here to match exact Intiface/central message names if necessary
        private const string MsgLoadScript = "Intiface.LoadScript"; // common namespace style
        private const string MsgPlay = "Intiface.Play";
        private const string MsgPause = "Intiface.Pause";

        // Send script contents as base64 inside a LoadScript message
        private async Task SendLoadScriptToIntiface(TheHandySessionState state, string scriptContents)
        {
            var scriptB64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(scriptContents));
            var obj = new JObject
            {
                ["type"] = MsgLoadScript,
                ["name"] = Path.GetFileName(state.FunscriptPath),
                ["data_base64"] = scriptB64
            };
            await SendStringAsync(obj.ToString(Newtonsoft.Json.Formatting.None)).ConfigureAwait(false);
        }

        private async Task SendPlayToIntiface(TheHandySessionState state, TimeSpan videoTime)
        {
            var obj = new JObject
            {
                ["type"] = MsgPlay,
                ["serverTime"] = ((long)DateTimeToJavaTimeStamp(GetServerTime(state))).ToString(),
                ["time"] = ((long)videoTime.TotalMilliseconds).ToString()
            };
            await SendStringAsync(obj.ToString(Newtonsoft.Json.Formatting.None)).ConfigureAwait(false);
        }

        private async Task SendPauseToIntiface(TheHandySessionState state)
        {
            var obj = new JObject
            {
                ["type"] = MsgPause
            };
            await SendStringAsync(obj.ToString(Newtonsoft.Json.Formatting.None)).ConfigureAwait(false);
        }

        private DateTime GetServerTime(TheHandySessionState state)
        {
            return DateTime.Now + state.timeSyncAverageOffset + state.timeSyncInitialOffset;
        }

        private static DateTime JavaTimeStampToDateTime(double javaTimeStamp)
        {
            DateTime dateTime = new DateTime(1970, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc);
            dateTime = dateTime.AddMilliseconds(javaTimeStamp).ToLocalTime();
            return dateTime;
        }

        private static double DateTimeToJavaTimeStamp(DateTime datetime)
        {
            DateTime javadateTime = new DateTime(1970, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc);
            return (datetime.ToUniversalTime() - javadateTime).TotalMilliseconds;
        }

        private async Task UpdateServerTime(TheHandySessionState CurrentSessionState)
        {
            if (string.IsNullOrWhiteSpace(Configuration.TimeServerUri))
            {
                CurrentSessionState.state = State.SyncStarted;
                return;
            }

            try
            {
                var sendTime = DateTime.Now;
                var url = Configuration.TimeServerUri;

                var httpRequestMessage = new System.Net.Http.HttpRequestMessage(System.Net.Http.HttpMethod.Get, url);

                using var response = await _httpClientFactory
                            .CreateClient(NamedClient.Default)
                            .SendAsync(httpRequestMessage)
                            .ConfigureAwait(false);
                response.EnsureSuccessStatusCode();
                var jsonString = await response.Content.ReadAsStringAsync();

                JObject responseJson = JObject.Parse(jsonString);

                var now = DateTime.Now;

                var receiveTime = now;

                var rtd = receiveTime - sendTime;

                var serverTimeInUnix = responseJson["serverTime"].Value<double>();
                var serverTime = JavaTimeStampToDateTime(serverTimeInUnix);

                var estimatedServerTimeNow = serverTime + rtd / 2;

                TimeSpan offset = TimeSpan.Zero;

                if (CurrentSessionState.timeSyncMessage == 0)
                {
                    CurrentSessionState.timeSyncInitialOffset = estimatedServerTimeNow - now;
                }
                else
                {
                    offset = estimatedServerTimeNow - receiveTime - CurrentSessionState.timeSyncInitialOffset;
                    CurrentSessionState.timeSyncAverageOffset = (CurrentSessionState.timeSyncAggregatedOffset + offset) / CurrentSessionState.timeSyncMessage;
                    CurrentSessionState.timeSyncAggregatedOffset = CurrentSessionState.timeSyncAggregatedOffset + offset;
                }
                CurrentSessionState.timeSyncMessage++;
                if (CurrentSessionState.timeSyncMessage < 30)
                {
                    await UpdateServerTime(CurrentSessionState);
                }
                else
                {
                    _logger.LogInformation("Server Time Sync Done for {file}", Path.GetFileName(CurrentSessionState.FunscriptPath));
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "UpdateServerTime failed");
                CurrentSessionState.state = State.SyncStarted;
            }
        }

        // Small helper types

        private class ConnectionBackoffState
        {
            private readonly TimeSpan _initial = TimeSpan.FromMilliseconds(500);
            private readonly TimeSpan _max = TimeSpan.FromSeconds(30);
            private readonly Random _rng = new Random();
            private int _attempts = 0;
            private string? _currentTarget;

            public void ResetIfNeeded(string url)
            {
                if (_currentTarget != url)
                {
                    _attempts = 0;
                    _currentTarget = url;
                }
            }

            public TimeSpan NextBackoff()
            {
                _attempts++;
                var exp = Math.Min((double)(_initial.TotalMilliseconds * Math.Pow(2, _attempts - 1)), _max.TotalMilliseconds);
                // add jitter +/-20%
                var jitter = 1.0 + (_rng.NextDouble() * 0.4 - 0.2);
                return TimeSpan.FromMilliseconds(Math.Max(100, exp * jitter));
            }

            public void OnSuccess()
            {
                _attempts = 0;
            }
        }
    }
}