using System;
using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

/// <summary>
/// Minimal HTTP server that exposes endpoints Unity can use to receive render jobs from Spring.
/// Attach this to a GameObject in the bootstrap scene.
/// </summary>
public class UnityRenderServer : MonoBehaviour
{
    [Header("Network")]
    [Tooltip("Listening port for the local Unity server.")]
    public int port = 8785;

    [Tooltip("Optional shared secret that incoming requests must include in the Authorization header as 'Bearer <token>'.")]
    public string sharedSecret = "";

    [Header("Job Runner")]
    public RenderJobRunner jobRunner;

    HttpListener listener;
    CancellationTokenSource cts;
    readonly ConcurrentQueue<Action> mainThreadActions = new();

    void Awake()
    {
        if (jobRunner == null)
        {
#if UNITY_2023_1_OR_NEWER
            jobRunner = FindFirstObjectByType<RenderJobRunner>();
#else
            jobRunner = FindObjectOfType<RenderJobRunner>();
#endif
        }
    }

    void OnEnable()
    {
        StartServer();
    }

    void OnDisable()
    {
        StopServer();
    }

    void Update()
    {
        while (mainThreadActions.TryDequeue(out var action))
        {
            action?.Invoke();
        }
    }

    void StartServer()
    {
        if (listener != null)
        {
            return;
        }

        listener = new HttpListener();
        RegisterPrefix("localhost");
        RegisterPrefix("127.0.0.1");
        // RegisterPrefix("hongsam-caramel.store");
        try
        {
            listener.Start();
        }
        catch (HttpListenerException ex)
        {
            Debug.LogError($"UnityRenderServer failed to start: {ex.Message}");
            listener = null;
            return;
        }

        cts = new CancellationTokenSource();
        Task.Run(() => ListenLoop(cts.Token));
        Debug.Log($"UnityRenderServer listening on port {port}");
    }

    bool RegisterPrefix(string host)
    {
        if (string.IsNullOrWhiteSpace(host))
        {
            return false;
        }
        string trimmed = host.Trim();
        if (trimmed.Contains("://"))
        {
            trimmed = trimmed.Substring(trimmed.IndexOf("://", StringComparison.Ordinal) + 3);
        }
        string prefix = $"http://{trimmed}:{port}/";
        try
        {
            listener.Prefixes.Add(prefix);
            return true;
        }
        catch (Exception ex)
        {
            Debug.LogWarning($"UnityRenderServer could not register prefix {prefix}: {ex.Message}");
            return false;
        }
    }

    void StopServer()
    {
        cts?.Cancel();
        listener?.Close();
        listener = null;
        cts = null;
    }

    async Task ListenLoop(CancellationToken token)
    {
        while (!token.IsCancellationRequested && listener != null && listener.IsListening)
        {
            HttpListenerContext ctx = null;
            try
            {
                ctx = await listener.GetContextAsync();
            }
            catch when (token.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"UnityRenderServer listener error: {ex}");
            }

            if (ctx == null) continue;
            _ = Task.Run(() => HandleRequest(ctx));
        }
    }

    void HandleRequest(HttpListenerContext ctx)
    {
        try
        {
            var remote = ctx.Request.RemoteEndPoint?.ToString() ?? "unknown";
            Debug.Log($"UnityRenderServer received {ctx.Request.HttpMethod} {ctx.Request.Url.AbsolutePath} from {remote}");
            if (!Authorize(ctx))
            {
                WriteResponse(ctx, HttpStatusCode.Unauthorized, "{\"error\":\"unauthorized\"}");
                return;
            }

            var path = ctx.Request.Url.AbsolutePath.TrimEnd('/');
            switch (ctx.Request.HttpMethod)
            {
                case "GET" when path.Equals("/health", StringComparison.OrdinalIgnoreCase):
                    HandleHealth(ctx);
                    break;
                case "GET" when path.StartsWith("/status", StringComparison.OrdinalIgnoreCase):
                    HandleStatus(ctx);
                    break;
                case "POST" when path.Equals("/render", StringComparison.OrdinalIgnoreCase):
                    HandleRender(ctx);
                    break;
                default:
                    WriteResponse(ctx, HttpStatusCode.NotFound, "{\"error\":\"not-found\"}");
                    break;
            }
        }
        catch (Exception ex)
        {
            Debug.LogError($"UnityRenderServer request error: {ex}");
            if (ctx.Response.OutputStream.CanWrite)
            {
                WriteResponse(ctx, HttpStatusCode.InternalServerError, "{\"error\":\"server-error\"}");
            }
        }
    }

    bool Authorize(HttpListenerContext ctx)
    {
        if (string.IsNullOrEmpty(sharedSecret))
        {
            return true;
        }

        string header = ctx.Request.Headers["Authorization"];
        if (string.IsNullOrEmpty(header))
        {
            return false;
        }

        const string prefix = "Bearer ";
        if (!header.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        string token = header.Substring(prefix.Length).Trim();
        return token == sharedSecret;
    }

    void HandleHealth(HttpListenerContext ctx)
    {
        var status = jobRunner != null ? jobRunner.CurrentState : "unknown";
        WriteResponse(ctx, HttpStatusCode.OK, $"{{\"status\":\"{status}\"}}");
    }

    void HandleStatus(HttpListenerContext ctx)
    {
        if (jobRunner == null)
        {
            WriteResponse(ctx, HttpStatusCode.ServiceUnavailable, "{\"error\":\"job-runner-missing\"}");
            return;
        }

        string jobId = ctx.Request.QueryString["jobId"];
        var jobStatus = jobRunner.GetStatus(jobId);
        WriteResponse(ctx, HttpStatusCode.OK, JsonUtility.ToJson(jobStatus));
    }

    void HandleRender(HttpListenerContext ctx)
    {
        if (jobRunner == null)
        {
            Debug.LogWarning("UnityRenderServer render request rejected: job runner reference missing.");
            WriteResponse(ctx, HttpStatusCode.ServiceUnavailable, "{\"error\":\"job-runner-missing\"}");
            return;
        }

        string body;
        // using (var reader = new StreamReader(ctx.Request.InputStream, ctx.Request.ContentEncoding))
        using (var reader = new StreamReader(ctx.Request.InputStream, Encoding.UTF8))
        {
            body = reader.ReadToEnd();
        }

        string remote = ctx.Request.RemoteEndPoint?.ToString() ?? "unknown";

        if (string.IsNullOrEmpty(body))
        {
            Debug.LogWarning($"UnityRenderServer render request rejected: empty body from {remote}.");
            WriteResponse(ctx, HttpStatusCode.BadRequest, "{\"error\":\"empty-body\"}");
            return;
        }

        RenderJobPayload payload;
        try
        {
            payload = JsonUtility.FromJson<RenderJobPayload>(body);
        }
        catch (Exception ex)
        {
            Debug.LogWarning($"UnityRenderServer render request rejected: invalid json from {remote}. {ex.Message}. Payload: {TruncateForLog(body)}");
            WriteResponse(ctx, HttpStatusCode.BadRequest, $"{{\"error\":\"invalid-json\",\"detail\":\"{ex.Message}\"}}");
            return;
        }

        if (payload == null || string.IsNullOrEmpty(payload.jobId))
        {
            Debug.LogWarning($"UnityRenderServer render request rejected: missing jobId from {remote}. Payload: {TruncateForLog(body)}");
            WriteResponse(ctx, HttpStatusCode.BadRequest, "{\"error\":\"missing-job-id\"}");
            return;
        }
        if (payload.routeJson == null || payload.routeJson.Length == 0)
        {
            Debug.LogWarning($"UnityRenderServer render request rejected: missing routeJson for job {payload.jobId} from {remote}.");
            WriteResponse(ctx, HttpStatusCode.BadRequest, "{\"error\":\"missing-route-json\"}");
            return;
        }

        Debug.Log($"UnityRenderServer accepted render job {payload.jobId} with {payload.routeJson.Length} holds from {remote}.");

        mainThreadActions.Enqueue(() =>
        {
            Debug.Log($"UnityRenderServer enqueue job {payload.jobId}");
            jobRunner.EnqueueJob(payload);
        });

        WriteResponse(ctx, HttpStatusCode.Accepted, $"{{\"jobId\":\"{payload.jobId}\",\"status\":\"queued\"}}");
    }

    void WriteResponse(HttpListenerContext ctx, HttpStatusCode code, string body)
    {
        byte[] bytes = string.IsNullOrEmpty(body) ? Array.Empty<byte>() : Encoding.UTF8.GetBytes(body);
        ctx.Response.StatusCode = (int)code;
        ctx.Response.ContentType = "application/json";
        ctx.Response.ContentEncoding = Encoding.UTF8;
        ctx.Response.ContentLength64 = bytes.Length;
        ctx.Response.OutputStream.Write(bytes, 0, bytes.Length);
        ctx.Response.OutputStream.Close();
    }

    static string TruncateForLog(string text, int maxLength = 512)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        if (text.Length <= maxLength)
        {
            return text;
        }

        return text.Substring(0, maxLength) + "...(truncated)";
    }
}

[Serializable]
public class RenderJobPayload
{
    public string jobId;
    public HoldEntry[] routeJson;
    public int imageWidth;
    public int imageHeight;
    public string textureUrl;
    public string uploadUrl;
    public int width = 1920;
    public int height = 1080;
    public int fps = 30;
    public float durationPadding = 2f;
}
