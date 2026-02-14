using System;
using System.IO;
using System.Text;
using UnityEngine;
using System.Collections;
using System.Diagnostics;

public class LatencyDatasetLogger : MonoBehaviour
{
    [Header("References")]
    public Transform headTransform;
    public Transform worldLockedRoot;
    public HoloSpawner spawner;
    public ScenarioRunner scenario;

    // Prevent double stop/flush on quit/destroy
    private bool stopped = false;

    // ---------------- Live telemetry ----------------
    public float CpuFrameMs { get; private set; }
    public float SmoothedFps { get; private set; }
    public float HeadLinSpeed { get; private set; }
    public float HeadAngSpeedDeg { get; private set; }

    public int TargetCount { get; private set; }
    public int QualityLevel { get; private set; }
    public int ActiveParticlesCount { get; private set; }

    // ---------------- Measured latency proxy ----------------
    public float MeasuredLatencyMs { get; private set; } = -1f;

    public double PoseSampleTime { get; private set; } = -1.0;
    public double SubmitTime { get; private set; } = -1.0;

    // ---------------- Loop-closure proxy (pose discontinuity) ----------------
    public int LoopClosureFlag { get; private set; } = 0;
    public float LoopClosureTransJump { get; private set; } = 0f;
    public float LoopClosureRotJumpDeg { get; private set; } = 0f;

    [Header("Loop-closure thresholds")]
    [Tooltip("We only consider loop-closure when the user is mostly still.")]
    public float lcHeadStillLinSpeed = 0.05f;   // m/s
    public float lcHeadStillAngSpeed = 5.0f;    // deg/s

    [Tooltip("Pose jump thresholds indicating relocalization correction (tune for HoloLens).")]
    public float lcPoseJumpTrans = 0.01f;      
    public float lcPoseJumpRotDeg = 1.0f;      

    [Header("Loop-closure debounce")]
    [Tooltip("Minimum time between loop-closure flags to avoid spamming during one correction event.")]
    public float lcDebounceSeconds = 1.0f;
    private double lcLastTime = -999.0;

    public bool logLoopClosureDiagnostics = true;

    public int HeadStillFlag { get; private set; } = 0;
    public float HeadPoseJumpTransRaw { get; private set; } = 0f;
    public float HeadPoseJumpRotRawDeg { get; private set; } = 0f;

    public int HasWorldLockedRoot { get; private set; } = 0;
    public float WlrTransJumpRaw { get; private set; } = 0f;
    public float WlrRotJumpDegRaw { get; private set; } = 0f;

    // ---------------- Closed-loop hooks (controller writes) ----------------
    [Header("Closed-loop values (written by controller)")]
    public float PredictedLatencyMs = -1f;
    public float PredictedLatencySmoothMs = -1f;
    public string ControllerState = "NA";
    public int CooldownActive = 0;

    public string Action = "none";
    public int ParticleBudget = -1;
    public int AdaptEnabled = 0;
    public int AdaptStep = 0;
    public float AdaptErrorMs = -1f;

    // ---------------- Logging ----------------
    [Header("Logging")]
    public bool loggingEnabled = false;
    public bool autoStartLogging = false;

    public string filePrefix = "latency_dataset";
    public int writeEveryNFrames = 60;
    public int maxBufferedLines = 600;
    public bool includeDebugTimestamps = false;

    private StreamWriter writer;
    private readonly StringBuilder sb = new StringBuilder(1024);
    private string filePath;

    private Quaternion lastRot;
    private Vector3 lastPos;
    private float smoothedFps;

    // For pose-discontinuity detection
    private Vector3 lastHeadPosForJump;
    private Quaternion lastHeadRotForJump;

    
    private Vector3 lastWlrPos;
    private Quaternion lastWlrRot;

    // Buffered lines
    private readonly StringBuilder batch = new StringBuilder(1024 * 64);
    private int bufferedLines = 0;
    private int frameCounter = 0;

    // Stopwatch timing for measured latency
    private static readonly double TickToMs = 1000.0 / Stopwatch.Frequency;
    private long poseSampleTick = 0;
    private long submitTick = 0;

    public string CurrentFilePath => filePath;

    IEnumerator Start()
    {
        // Let one frame pass so LateUpdate runs at least once
        yield return null;

        EnsureHeadTransform();

        if (headTransform != null)
        {
            lastRot = headTransform.rotation;
            lastPos = headTransform.position;

            lastHeadPosForJump = headTransform.position;
            lastHeadRotForJump = headTransform.rotation;
        }

        smoothedFps = 0f;

        if (worldLockedRoot != null)
        {
            lastWlrPos = worldLockedRoot.position;
            lastWlrRot = worldLockedRoot.rotation;
            HasWorldLockedRoot = 1;
        }
        else
        {
            HasWorldLockedRoot = 0;
        }

        if (autoStartLogging) StartLogging();
    }

    private void EnsureHeadTransform()
    {
        if (headTransform != null) return;

        var cam = Camera.main;
        if (cam != null)
        {
            headTransform = cam.transform;
            return;
        }

        var cams = Camera.allCameras;
        if (cams != null && cams.Length > 0 && cams[0] != null)
            headTransform = cams[0].transform;
    }

    public void StartLogging()
    {
        if (writer != null) return;

        Directory.CreateDirectory(Application.persistentDataPath);

        filePath = Path.Combine(Application.persistentDataPath,
            $"{filePrefix}_{DateTime.Now:yyyyMMdd_HHmmss}.csv");

        writer = new StreamWriter(filePath, false, Encoding.UTF8);

        var header = new StringBuilder(1800);
        header.Append("t_unity,frame_idx,")
              .Append("cpu_frame_ms,smoothed_fps,")
              .Append("scenario_mode,target_count,quality_level,")
              .Append("active_objects,active_renderers,active_particles_count,")
              .Append("head_lin_speed,head_ang_speed_deg,")
              .Append("measured_latency_ms,")
              .Append("loop_closure,lc_trans_jump,lc_rot_jump_deg,")
              .Append("pred_latency_ms,pred_latency_smooth_ms,controller_state,cooldown_active,")
              .Append("adapt_enabled,adapt_step,adapt_error_ms,action,particle_budget");

        if (logLoopClosureDiagnostics)
        {
            header.Append(",head_still,head_pose_jump_trans,head_pose_jump_rot_deg");
            header.Append(",has_world_locked_root,wlr_trans_jump_raw,wlr_rot_jump_raw_deg");
        }

        if (includeDebugTimestamps)
            header.Append(",t_pose_sample,t_submit");

        writer.WriteLine(header.ToString());
        writer.Flush();

        loggingEnabled = true;
        frameCounter = 0;
        bufferedLines = 0;
        batch.Clear();

        
        lcLastTime = -999.0;

        UnityEngine.Debug.Log("Logging started: " + filePath);
    }

    public void StopLogging()
    {
        loggingEnabled = false;
        FlushBatch();

        if (writer != null)
        {
            writer.Flush();
            writer.Close();
            writer = null;
        }

        UnityEngine.Debug.Log("Logging stopped.");
    }

    void Update()
    {
        EnsureHeadTransform();
        if (headTransform == null) return;

        // Use ONE consistent timebase
        double t = Time.unscaledTimeAsDouble;
        float dt = Mathf.Max(Time.unscaledDeltaTime, 1e-6f);

        // Pose sample tick (high-res monotonic) for latency proxy
        poseSampleTick = Stopwatch.GetTimestamp();
        PoseSampleTime = t;

        // CPU frame proxy
        float cpuFrameMs = dt * 1000f;

        // Smoothed FPS
        float instFps = 1f / dt;
        smoothedFps = Mathf.Lerp(smoothedFps, instFps, 0.05f);

        // Head motion
        var rot = headTransform.rotation;
        var pos = headTransform.position;

        float linSpeed = (pos - lastPos).magnitude / dt;
        float angSpeed = Quaternion.Angle(lastRot, rot) / dt;

        lastRot = rot;
        lastPos = pos;

        // Scene stats
        int ao = spawner != null ? spawner.ActiveCount : -1;
        int ar = spawner != null ? spawner.CountActiveRenderers() : -1;
        int apCount = spawner != null ? spawner.CountActiveParticles() : -1;

        // Scenario info
        TargetCount = scenario != null ? scenario.CurrentTargetCount : -1;
        QualityLevel = scenario != null ? scenario.QualityLevel : -1;
        ActiveParticlesCount = apCount;

        // Expose live values
        CpuFrameMs = cpuFrameMs;
        SmoothedFps = smoothedFps;
        HeadLinSpeed = linSpeed;
        HeadAngSpeedDeg = angSpeed;

        // ---------------- Loop-closure proxy (pose discontinuity while still) ----------------
        float poseJumpTrans = (pos - lastHeadPosForJump).magnitude;
        float poseJumpRotDeg = Quaternion.Angle(lastHeadRotForJump, rot);

        lastHeadPosForJump = pos;
        lastHeadRotForJump = rot;

        bool headStill = linSpeed < lcHeadStillLinSpeed && angSpeed < lcHeadStillAngSpeed;
        HeadStillFlag = headStill ? 1 : 0;

        HeadPoseJumpTransRaw = poseJumpTrans;
        HeadPoseJumpRotRawDeg = poseJumpRotDeg;

        bool poseJump = poseJumpTrans > lcPoseJumpTrans || poseJumpRotDeg > lcPoseJumpRotDeg;

        double now = t; 
        bool rawLc = headStill && poseJump;

        if (rawLc && (now - lcLastTime) >= lcDebounceSeconds)
        {
            LoopClosureFlag = 1;
            lcLastTime = now;
        }
        else
        {
            LoopClosureFlag = 0;
        }

        LoopClosureTransJump = (LoopClosureFlag == 1) ? poseJumpTrans : 0f;
        LoopClosureRotJumpDeg = (LoopClosureFlag == 1) ? poseJumpRotDeg : 0f;

        
        WlrTransJumpRaw = 0f;
        WlrRotJumpDegRaw = 0f;

        if (worldLockedRoot != null)
        {
            HasWorldLockedRoot = 1;

            var wPos = worldLockedRoot.position;
            var wRot = worldLockedRoot.rotation;

            WlrTransJumpRaw = (wPos - lastWlrPos).magnitude;
            WlrRotJumpDegRaw = Quaternion.Angle(lastWlrRot, wRot);

            lastWlrPos = wPos;
            lastWlrRot = wRot;
        }
        else
        {
            HasWorldLockedRoot = 0;
        }

        // ---------------- Buffered logging ----------------
        if (loggingEnabled && writer != null)
        {
            sb.Clear();

            sb.Append(t.ToString("F6")).Append(',');
            sb.Append(Time.frameCount).Append(',');

            sb.Append(cpuFrameMs.ToString("F3")).Append(',');
            sb.Append(smoothedFps.ToString("F2")).Append(',');

            sb.Append(scenario != null ? scenario.mode.ToString() : "NA").Append(',');
            sb.Append(TargetCount).Append(',');
            sb.Append(QualityLevel).Append(',');

            sb.Append(ao).Append(',');
            sb.Append(ar).Append(',');
            sb.Append(ActiveParticlesCount).Append(',');

            sb.Append(linSpeed.ToString("F3")).Append(',');
            sb.Append(angSpeed.ToString("F2")).Append(',');

            sb.Append(MeasuredLatencyMs.ToString("F4")).Append(',');

            sb.Append(LoopClosureFlag).Append(',');
            sb.Append(LoopClosureTransJump.ToString("F4")).Append(',');
            sb.Append(LoopClosureRotJumpDeg.ToString("F3")).Append(',');

            sb.Append(PredictedLatencyMs.ToString("F3")).Append(',');
            sb.Append(PredictedLatencySmoothMs.ToString("F3")).Append(',');
            sb.Append((ControllerState ?? "NA")).Append(',');
            sb.Append(CooldownActive).Append(',');

            sb.Append(AdaptEnabled).Append(',');
            sb.Append(AdaptStep).Append(',');
            sb.Append(AdaptErrorMs.ToString("F3")).Append(',');

            sb.Append(Action ?? "none").Append(',');
            sb.Append(ParticleBudget);

            if (logLoopClosureDiagnostics)
            {
                sb.Append(',');
                sb.Append(HeadStillFlag).Append(',');
                sb.Append(HeadPoseJumpTransRaw.ToString("F5")).Append(',');
                sb.Append(HeadPoseJumpRotRawDeg.ToString("F4")).Append(',');

                sb.Append(HasWorldLockedRoot).Append(',');
                sb.Append(WlrTransJumpRaw.ToString("F5")).Append(',');
                sb.Append(WlrRotJumpDegRaw.ToString("F4"));
            }

            if (includeDebugTimestamps)
            {
                sb.Append(',');
                sb.Append(PoseSampleTime.ToString("F6")).Append(',');
                sb.Append(SubmitTime.ToString("F6"));
            }

            batch.AppendLine(sb.ToString());
            bufferedLines++;
            frameCounter++;

            if (frameCounter % writeEveryNFrames == 0 || bufferedLines >= maxBufferedLines)
                FlushBatch();
        }
    }

    void LateUpdate()
    {
        submitTick = Stopwatch.GetTimestamp();
        SubmitTime = Time.unscaledTimeAsDouble;

        // Measured latency using Stopwatch (pose sample -> LateUpdate)
        MeasuredLatencyMs = (float)((submitTick - poseSampleTick) * TickToMs);
    }

    private void FlushBatch()
    {
        if (writer == null) return;
        if (bufferedLines <= 0) return;

        try
        {
            writer.Write(batch.ToString());
            writer.Flush();
        }
        catch (Exception e)
        {
            UnityEngine.Debug.LogError("Failed to flush log batch: " + e.Message);
        }
        finally
        {
            batch.Clear();
            bufferedLines = 0;
        }
    }

    void OnApplicationPause(bool pause)
    {
        if (pause)
        {
            FlushBatch();
            writer?.Flush();
        }
    }

    private void SafeStop()
    {
        if (stopped) return;
        stopped = true;
        StopLogging();
    }

    void OnApplicationQuit() { SafeStop(); }
    void OnDestroy() { SafeStop(); }
}
