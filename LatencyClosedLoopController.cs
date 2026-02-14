using UnityEngine;

public class LatencyClosedLoopController : MonoBehaviour
{
    [Header("References")]
    public LatencyDatasetLogger telemetry;
    public LatencyPredictorSentis predictor;
    public ScenarioRunner scenario;
    public HoloSpawner spawner;

    [Header("Experiment Mode")]
    public ExperimentMode mode = ExperimentMode.ClosedLoop;

    public enum ControlSignal
    {
        CpuFrameMs,          
        MeasuredLatencyMs   
    }

    [Header("Control signal (what the controller regulates)")]
    public ControlSignal controlSignal = ControlSignal.CpuFrameMs;

    [Header("Target + thresholds (ms)")]
    [Tooltip("For CpuFrameMs, 16.7ms ~ 60 FPS. Start with 16-18.")]
    public float targetMs = 16.7f;

    [Tooltip("Enter ReduceLoad if error > this.")]
    public float enterHighMs = 2.0f;

    [Tooltip("Exit ReduceLoad if error < this.")]
    public float exitLowMs = 0.5f;

    [Tooltip("Do nothing if |error| < deadband.")]
    public float deadbandMs = 1.0f;

    [Header("Prediction smoothing (EMA)")]
    [Range(0.01f, 1.0f)]
    public float emaAlpha = 0.2f;
    private float predSmoothMs = -1f;

    [Header("Quality limits")]
    public int minQuality = 0;
    public int maxQuality = 5;

    [Header("Particles (optional direct control)")]
    public bool enableParticleBudget = true;
    public int particleBudget = 1000;
    public int particleStep = 100;
    public int minParticles = 0;
    public int maxParticles = 1000;

    [Header("Adaptation rate limiting")]
    public float cooldownSeconds = 1.0f;
    private double lastActionTime = -1.0;

    public enum ControlState { Normal, ReduceLoad }

    [Header("Controller State (read-only)")]
    public ControlState state = ControlState.Normal;

    [Header("Loop-closure handling")]
    public bool freezeOnLoopClosure = true;
    public float loopClosureHoldSeconds = 0.75f;
    private double lcHoldUntil = 0.0;

    [Header("Logging bookkeeping")]
    public bool adaptationEnabled = true;
    private int adaptStep = 0;

    void Start()
    {
        if (telemetry == null) telemetry = FindObjectOfType<LatencyDatasetLogger>();
        if (predictor == null) predictor = FindObjectOfType<LatencyPredictorSentis>();
        if (scenario == null) scenario = FindObjectOfType<ScenarioRunner>();
        if (spawner == null) spawner = FindObjectOfType<HoloSpawner>();

        if (enableParticleBudget && spawner != null)
        {
            particleBudget = Mathf.Clamp(particleBudget, minParticles, maxParticles);
            spawner.SetParticleBudget(particleBudget);
        }

        state = ControlState.Normal;
        predSmoothMs = -1f;
        lastActionTime = -1.0;
        adaptStep = 0;
    }

    void Update()
    {
        if (telemetry == null) return;

        // ----- Features (same as training) -----
        float targetCount = telemetry.TargetCount;
        float smoothedFps = telemetry.SmoothedFps;
        float headLinSpeed = telemetry.HeadLinSpeed;
        float headAngSpeedDeg = telemetry.HeadAngSpeedDeg;
        float cpuFrameMs = telemetry.CpuFrameMs;

        // ----- Predict -----
        float predMs = -1f;

        if ((mode == ExperimentMode.PredictionOnly || mode == ExperimentMode.ClosedLoop) &&
            predictor != null && predictor.Ready)
        {
            predictor.PushFrameFeatures(targetCount, smoothedFps, headLinSpeed, headAngSpeedDeg, cpuFrameMs);
            predMs = predictor.PredictedLatencyMs;

            if (!predictor.WindowReady || predMs < 0f)
            {
                predMs = -1f;
                predSmoothMs = -1f;
            }
            else
            {
                if (predSmoothMs < 0f) predSmoothMs = predMs;
                else predSmoothMs = emaAlpha * predMs + (1f - emaAlpha) * predSmoothMs;
            }
        }
        else
        {
            predMs = -1f;
            predSmoothMs = -1f;
        }

        // ----- Measured signals -----
        float measuredLatencyMs = telemetry.MeasuredLatencyMs;
        float measuredControlMs = (controlSignal == ControlSignal.CpuFrameMs)
            ? telemetry.CpuFrameMs
            : telemetry.MeasuredLatencyMs;

        // prediction error is logged vs the chosen control signal
        float errMs = (predMs >= 0f && measuredControlMs >= 0f) ? (measuredControlMs - predMs) : -1f;

        double t = Time.unscaledTimeAsDouble;

        // ----- Loop-closure freeze -----
        if (freezeOnLoopClosure && telemetry.LoopClosureFlag == 1)
            lcHoldUntil = t + loopClosureHoldSeconds;

        bool inLcHold = freezeOnLoopClosure && (t < lcHoldUntil);

        // ----- Cooldown -----
        bool cooldownActive = (lastActionTime >= 0.0) && ((t - lastActionTime) < cooldownSeconds);

        // ----- Control -----
        string action = "none";

        bool canAdapt =
            adaptationEnabled &&
            mode == ExperimentMode.ClosedLoop &&
            predictor != null &&
            predictor.Ready &&
            predictor.WindowReady &&
            scenario != null &&
            !inLcHold &&
            predSmoothMs >= 0f;

        if (canAdapt)
        {
            float controlError = predSmoothMs - targetMs;

            if (Mathf.Abs(controlError) >= deadbandMs)
            {
                // hysteresis transitions
                if (state == ControlState.Normal)
                {
                    if (controlError > enterHighMs) state = ControlState.ReduceLoad;
                }
                else
                {
                    if (controlError < exitLowMs) state = ControlState.Normal;
                }

                if (!cooldownActive)
                {
                    int q = scenario.QualityLevel;

                    if (state == ControlState.ReduceLoad)
                    {
                        if (q > minQuality)
                        {
                            scenario.SetQuality(q - 1);
                            action = "quality_down";
                            lastActionTime = t;
                        }
                        else if (enableParticleBudget && spawner != null && particleBudget > minParticles)
                        {
                            particleBudget = Mathf.Max(minParticles, particleBudget - particleStep);
                            spawner.SetParticleBudget(particleBudget);
                            action = "particles_down";
                            lastActionTime = t;
                        }
                    }
                    else // Normal recovery
                    {
                        if (controlError < -deadbandMs)
                        {
                            if (enableParticleBudget && spawner != null && particleBudget < maxParticles)
                            {
                                particleBudget = Mathf.Min(maxParticles, particleBudget + particleStep);
                                spawner.SetParticleBudget(particleBudget);
                                action = "particles_up";
                                lastActionTime = t;
                            }
                            else if (q < maxQuality)
                            {
                                scenario.SetQuality(q + 1);
                                action = "quality_up";
                                lastActionTime = t;
                            }
                        }
                    }
                }
                else
                {
                    action = "cooldown";
                }
            }
        }
        else if (inLcHold && mode == ExperimentMode.ClosedLoop)
        {
            action = "hold_lc";
        }

        // count only real actions
        if (action == "quality_down" || action == "quality_up" || action == "particles_down" || action == "particles_up")
            adaptStep++;

        // ----- Write to telemetry -----
        telemetry.PredictedLatencyMs = predMs;
        telemetry.PredictedLatencySmoothMs = predSmoothMs;
        telemetry.ControllerState = state.ToString();
        telemetry.CooldownActive = cooldownActive ? 1 : 0;

        telemetry.Action = action;
        telemetry.ParticleBudget = enableParticleBudget ? particleBudget : -1;

        telemetry.AdaptEnabled = (mode == ExperimentMode.ClosedLoop && adaptationEnabled) ? 1 : 0;
        telemetry.AdaptStep = adaptStep;

        // Here AdaptErrorMs is defined against the *control signal* for analysis
        telemetry.AdaptErrorMs = errMs;

       
        _ = measuredLatencyMs;
    }
}
