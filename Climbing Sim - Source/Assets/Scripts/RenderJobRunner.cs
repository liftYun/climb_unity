using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Diagnostics;
using UnityEngine;
using UnityEngine.Networking;
using Debug = UnityEngine.Debug;

/// <summary>
/// Coordinates downloading assets, applying them to the scene, triggering the climbing animation, and exporting video.
/// This is a high-level skeleton you can expand with the actual capture & upload implementation.
/// </summary>
public class RenderJobRunner : MonoBehaviour
{
    public static RenderJobRunner Instance { get; private set; }

    [Header("Scene References")]
    public RouteFollower routeFollower;
    public Renderer wallRenderer;
    public Camera captureCamera;
    public string ffmpegPath = "ffmpeg";

    [Header("Debug")]
    public bool autoPlayInEditor;

    readonly Dictionary<string, RenderJobStatus> jobStatuses = new();
    string lastVideoPath;
    Coroutine activeJob;

    public string CurrentState => activeJob == null ? "idle" : "busy";

    void Awake()
    {
        if (Instance != null && Instance != this)
        {
            Destroy(gameObject);
            return;
        }

        Instance = this;
        DontDestroyOnLoad(gameObject);
    }

    void Start()
    {
        if (routeFollower == null)
        {
#if UNITY_2023_1_OR_NEWER
            routeFollower = FindFirstObjectByType<RouteFollower>();
#else
            routeFollower = FindObjectOfType<RouteFollower>();
#endif
        }

        if (wallRenderer == null)
        {
            var cube = GameObject.Find("Cube (2)");
            if (cube != null)
            {
                wallRenderer = cube.GetComponent<Renderer>();
            }
        }
    }

    public void EnqueueJob(RenderJobPayload payload)
    {
        if (activeJob != null)
        {
            Debug.LogWarning("A job is already running. Rejecting new job.");
            jobStatuses[payload.jobId] = RenderJobStatus.Failed(payload.jobId, "busy");
            return;
        }

        jobStatuses[payload.jobId] = RenderJobStatus.Queued(payload.jobId);
        activeJob = StartCoroutine(RunJobCoroutine(payload));
    }

    public RenderJobStatus GetStatus(string jobId)
    {
        if (string.IsNullOrEmpty(jobId) || !jobStatuses.TryGetValue(jobId, out var status))
        {
            return new RenderJobStatus { jobId = jobId ?? "unknown", state = "unknown", message = "no-such-job" };
        }
        return status;
    }

    IEnumerator RunJobCoroutine(RenderJobPayload payload)
    {
        jobStatuses[payload.jobId] = RenderJobStatus.Running(payload.jobId, "downloading");

        HoldEntry[] holds = payload.routeJson;
        Texture2D wallTexture = null;
        if (holds == null || holds.Length == 0)
        {
            FinishJob(payload.jobId, false, "route json missing");
            yield break;
        }

        if (!string.IsNullOrEmpty(payload.textureUrl))
        {
            using UnityWebRequest req = UnityWebRequestTexture.GetTexture(payload.textureUrl);
            yield return req.SendWebRequest();
            if (req.result != UnityWebRequest.Result.Success)
            {
                FinishJob(payload.jobId, false, $"texture download failed: {req.error}");
                yield break;
            }
            wallTexture = DownloadHandlerTexture.GetContent(req);
        }

        jobStatuses[payload.jobId] = RenderJobStatus.Running(payload.jobId, "configuring");
        ApplyWallTexture(wallTexture);

        int sourceWidth = payload.imageWidth > 0 ? payload.imageWidth : 1;
        int sourceHeight = payload.imageHeight > 0 ? payload.imageHeight : 1;
        var holdRoute = new HoldRouteFile
        {
            image_width = sourceWidth,
            image_height = sourceHeight,
            holds = holds
        };
        string routeJson = JsonUtility.ToJson(holdRoute);
        ApplyRoute(routeJson);

        jobStatuses[payload.jobId] = RenderJobStatus.Running(payload.jobId, "rendering");
        yield return PlayAndCapture(payload);
        if (string.IsNullOrEmpty(lastVideoPath) || !File.Exists(lastVideoPath))
        {
            FinishJob(payload.jobId, false, "capture failed");
            yield break;
        }

        jobStatuses[payload.jobId] = RenderJobStatus.Running(payload.jobId, "uploading");
        bool uploadSuccess = true;
        string uploadMessage = "completed";
        yield return UploadResult(payload, (success, message) =>
        {
            uploadSuccess = success;
            uploadMessage = message;
        });
        FinishJob(payload.jobId, uploadSuccess, uploadMessage);
    }

    void ApplyWallTexture(Texture2D texture)
    {
        if (texture == null || wallRenderer == null) return;
        var mat = wallRenderer.material;
        mat.mainTexture = texture;
    }

    void ApplyRoute(string json)
    {
        if (routeFollower == null || string.IsNullOrEmpty(json)) return;
        routeFollower.StopRoute();
        routeFollower.LoadRouteFromJson(json);
        routeFollower.PlayRoute();
    }

    IEnumerator PlayAndCapture(RenderJobPayload payload)
    {
        lastVideoPath = null;
        if (captureCamera == null || routeFollower == null)
        {
            Debug.LogWarning("RenderJobRunner: capture camera or route follower missing.");
            yield return new WaitForSeconds(Mathf.Max(payload.durationPadding, 0f));
            yield break;
        }

        int width = 1920;
        int height = 1080;
        int fps = Mathf.Max(1, payload.fps);

        bool routeFinished = false;
        void OnRouteCompleted() => routeFinished = true;
        routeFollower.RouteCompleted += OnRouteCompleted;

        RenderTexture renderTexture = new RenderTexture(width, height, 24, RenderTextureFormat.ARGB32);
        Texture2D frameTexture = new Texture2D(width, height, TextureFormat.RGBA32, false);
        string outputPath = Path.Combine(Application.temporaryCachePath, $"{payload.jobId}_{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}.mp4");
        Process ffmpegProcess = null;

        try
        {
            ffmpegProcess = StartFfmpegProcess(payload, outputPath, width, height, fps);
            if (ffmpegProcess == null)
            {
                Debug.LogError("RenderJobRunner: Failed to start FFmpeg process.");
                yield break;
            }

            captureCamera.targetTexture = renderTexture;
            RenderTexture.active = renderTexture;

            float padTimer = 0f;
            while (!routeFinished || padTimer < payload.durationPadding)
            {
                yield return new WaitForEndOfFrame();
                captureCamera.Render();
                frameTexture.ReadPixels(new Rect(0, 0, width, height), 0, 0, false);
                frameTexture.Apply(false);

                byte[] frameBytes = frameTexture.GetRawTextureData();
                ffmpegProcess.StandardInput.BaseStream.Write(frameBytes, 0, frameBytes.Length);
                ffmpegProcess.StandardInput.BaseStream.Flush();

                if (routeFinished)
                {
                    padTimer += Time.deltaTime;
                }
            }

            captureCamera.targetTexture = null;
            RenderTexture.active = null;

            ffmpegProcess.StandardInput.Close();
            ffmpegProcess.WaitForExit();
            if (ffmpegProcess.ExitCode != 0)
            {
                Debug.LogError($"RenderJobRunner: FFmpeg exited with code {ffmpegProcess.ExitCode}");
                yield break;
            }

            lastVideoPath = outputPath;
        }
        finally
        {
            routeFollower.RouteCompleted -= OnRouteCompleted;
            if (captureCamera != null)
            {
                captureCamera.targetTexture = null;
            }
            RenderTexture.active = null;
            if (renderTexture != null)
            {
                Destroy(renderTexture);
            }
            if (frameTexture != null)
            {
                Destroy(frameTexture);
            }
            if (ffmpegProcess != null && !ffmpegProcess.HasExited)
            {
                try
                {
                    ffmpegProcess.Kill();
                }
                catch { }
            }
        }
    }

    IEnumerator UploadResult(RenderJobPayload payload, Action<bool, string> callback)
    {
        if (string.IsNullOrEmpty(payload.uploadUrl))
        {
            callback?.Invoke(true, "no upload url provided");
            yield break;
        }

        if (string.IsNullOrEmpty(lastVideoPath) || !File.Exists(lastVideoPath))
        {
            callback?.Invoke(false, "no video to upload");
            yield break;
        }

        byte[] bytes = File.ReadAllBytes(lastVideoPath);
        using UnityWebRequest req = UnityWebRequest.Put(payload.uploadUrl, bytes);
        req.SetRequestHeader("Content-Type", "video/mp4");
        yield return req.SendWebRequest();
        if (req.result != UnityWebRequest.Result.Success)
        {
            callback?.Invoke(false, $"upload failed: {req.error}");
        }
        else
        {
            callback?.Invoke(true, "upload complete");
        }
    }

    Process StartFfmpegProcess(RenderJobPayload payload, string outputPath, int width, int height, int fps)
    {
        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = ffmpegPath,
                Arguments = $"-f rawvideo -pix_fmt rgba -s {width}x{height} -r {fps} -i - -y -an -c:v libx264 \"{outputPath}\"",
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            var process = new Process { StartInfo = startInfo };
            process.Start();
            return process;
        }
        catch (Exception ex)
        {
            Debug.LogError($"RenderJobRunner: Unable to start FFmpeg. {ex.Message}");
            return null;
        }
    }

    void FinishJob(string jobId, bool success, string message)
    {
        jobStatuses[jobId] = new RenderJobStatus
        {
            jobId = jobId,
            state = success ? "completed" : "failed",
            message = message,
            finishedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
        };
        activeJob = null;
    }
}

[Serializable]
public class RenderJobStatus
{
    public string jobId;
    public string state;
    public string message;
    public long finishedAt;

    public static RenderJobStatus Queued(string jobId) => new RenderJobStatus
    {
        jobId = jobId,
        state = "queued",
        message = "waiting"
    };

    public static RenderJobStatus Running(string jobId, string stage) => new RenderJobStatus
    {
        jobId = jobId,
        state = "running",
        message = stage
    };

    public static RenderJobStatus Failed(string jobId, string reason) => new RenderJobStatus
    {
        jobId = jobId,
        state = "failed",
        message = reason
    };
}
