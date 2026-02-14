using UnityEngine;

public class ScenarioRunner : MonoBehaviour
{
    public enum ScenarioMode { Idle, Ramp, Bursts, RandomWalk }

    [Header("References")]
    public HoloSpawner spawner;

    [Header("Scenario Settings")]
    public ScenarioMode mode = ScenarioMode.Idle;
    public int minCount = 10;
    public int maxCount = 400;

    [Header("Ramp")]
    public float rampStepSeconds = 1.0f;
    public int rampStep = 20;

    [Header("Bursts")]
    public float burstEverySeconds = 10f;
    public float burstDurationSeconds = 2f;
    public int burstAddCount = 150;

    [Header("Random Walk")]
    public float randomStepSeconds = 1.0f;
    public int randomStepMaxDelta = 40;

    // ================= QUALITY CONTROL =================

    [Header("Quality control (now affects workload)")]
    [Range(0, 5)]
    [SerializeField]
    private int qualityLevel = 0;

    public int QualityLevel => qualityLevel;

    [Tooltip("Maps quality level -> target count multiplier (0..5).")]
    public float[] qualityCountMultiplier =
        { 0.60f, 0.75f, 0.90f, 1.00f, 1.10f, 1.25f };

    [Tooltip("If true, quality also scales particle budget.")]
    public bool qualityAffectsParticles = false;

    [Tooltip("Particle budget at quality level 3 (neutral).")]
    public int baseParticleBudget = 1000;

    public float[] qualityParticleMultiplier =
        { 0.50f, 0.70f, 0.85f, 1.00f, 1.15f, 1.35f };

    

    public int CurrentTargetCount { get; private set; }

  
    public bool externalQualityOverride = false;
    public int externalQualityLevel = 0;

    private float tNext;
    private float tBurstStart = -999f;
    private int baseCount;

    void Start()
    {
        CurrentTargetCount = minCount;
        baseCount = CurrentTargetCount;

        ApplyCountWithQuality(CurrentTargetCount);
        tNext = Time.realtimeSinceStartup + 1f;
    }

    void Update()
    {
        if (externalQualityOverride)
        {
            qualityLevel = Mathf.Clamp(externalQualityLevel, 0, 5);
        }

        float now = Time.realtimeSinceStartup;

        switch (mode)
        {
            case ScenarioMode.Idle:
                break;

            case ScenarioMode.Ramp:
                if (now >= tNext)
                {
                    CurrentTargetCount += rampStep;
                    if (CurrentTargetCount > maxCount) CurrentTargetCount = minCount;
                    ApplyCountWithQuality(CurrentTargetCount);
                    tNext = now + rampStepSeconds;
                }
                break;

            case ScenarioMode.Bursts:
                if (now >= tNext)
                {
                    baseCount += rampStep;
                    if (baseCount > maxCount) baseCount = minCount;
                    tNext = now + rampStepSeconds;
                }

                if (now - tBurstStart >= burstEverySeconds)
                    tBurstStart = now;

                bool inBurst = (now - tBurstStart) <= burstDurationSeconds;
                CurrentTargetCount = baseCount + (inBurst ? burstAddCount : 0);
                ApplyCountWithQuality(CurrentTargetCount);
                break;

            case ScenarioMode.RandomWalk:
                if (now >= tNext)
                {
                    int delta = Random.Range(-randomStepMaxDelta, randomStepMaxDelta + 1);
                    CurrentTargetCount =
                        Mathf.Clamp(CurrentTargetCount + delta, minCount, maxCount);
                    ApplyCountWithQuality(CurrentTargetCount);
                    tNext = now + randomStepSeconds;
                }
                break;
        }
    }

    private void ApplyCountWithQuality(int rawCount)
    {
        if (spawner == null) return;

        int q = Mathf.Clamp(qualityLevel, 0, 5);

        float mult = (qualityCountMultiplier != null && qualityCountMultiplier.Length >= 6)
            ? qualityCountMultiplier[q]
            : 1.0f;

        int scaled =
            Mathf.Clamp(Mathf.RoundToInt(rawCount * mult), minCount, maxCount);

        spawner.SetCount(scaled);

        if (qualityAffectsParticles)
        {
            float pm = (qualityParticleMultiplier != null && qualityParticleMultiplier.Length >= 6)
                ? qualityParticleMultiplier[q]
                : 1.0f;

            int pb =
                Mathf.Clamp(Mathf.RoundToInt(baseParticleBudget * pm), 0, baseParticleBudget * 2);

            spawner.SetParticleBudget(pb);
        }
    }

    // ===== API used by the closed-loop controller =====
    public void SetQuality(int q)
    {
        qualityLevel = Mathf.Clamp(q, 0, 5);
        ApplyCountWithQuality(CurrentTargetCount);
    }

    // UI helpers
    public void SetIdle() => mode = ScenarioMode.Idle;
    public void SetRamp() => mode = ScenarioMode.Ramp;
    public void SetBursts() => mode = ScenarioMode.Bursts;
    public void SetRandomWalk() => mode = ScenarioMode.RandomWalk;
}
