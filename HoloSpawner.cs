using System.Collections.Generic;
using UnityEngine;

public class HoloSpawner : MonoBehaviour
{
    [Header("Prefab + layout")]
    public GameObject holoPrefab;
    public Transform anchor;         // where the grid sits (in front of user)
    public float spacing = 0.12f;
    public int columns = 10;

    private readonly List<GameObject> spawned = new List<GameObject>();

    // -------- Particle budget state (closed-loop) --------
    [Header("Particle budget (closed-loop)")]
    [Tooltip("If true, particle budget is distributed over all ParticleSystems under spawned objects.")]
    public bool particleBudgetEnabled = true;

    [Tooltip("Default total emission rate across all ParticleSystems (particles/sec).")]
    public int defaultParticleBudget = 1000;

    // Current applied budget (total emission rate across all systems)
    public int CurrentParticleBudget { get; private set; } = -1;

    // Cache all particle systems currently spawned (rebuilt on SetCount)
    private readonly List<ParticleSystem> particleSystemsCache = new List<ParticleSystem>(256);

    public int ActiveCount => spawned.Count;

    void Start()
    {
       
        if (particleBudgetEnabled)
        {
            SetParticleBudget(defaultParticleBudget);
        }
    }

    public void SetCount(int targetCount)
    {
        targetCount = Mathf.Max(0, targetCount);

        // Add
        while (spawned.Count < targetCount)
        {
            var go = Instantiate(holoPrefab, anchor);
            go.transform.localPosition = GridPos(spawned.Count);
            spawned.Add(go);
        }

        // Remove
        while (spawned.Count > targetCount)
        {
            var last = spawned[spawned.Count - 1];
            spawned.RemoveAt(spawned.Count - 1);
            Destroy(last);
        }

        // Rebuild cache because the hierarchy changed
        RebuildParticleCache();

        // Re-apply current budget so newly spawned objects follow control
        if (particleBudgetEnabled && CurrentParticleBudget >= 0)
        {
            ApplyParticleBudget(CurrentParticleBudget);
        }
    }

    private Vector3 GridPos(int i)
    {
        int row = i / columns;
        int col = i % columns;

        // Center grid roughly
        float x = (col - columns * 0.5f) * spacing;
        float y = -(row * spacing) * 0.6f - 0.2f;
        float z = 0f;

        return new Vector3(x, y, z);
    }

    public int CountActiveRenderers()
    {
        int c = 0;
        foreach (var go in spawned)
        {
            if (go == null) continue;
            c += go.GetComponentsInChildren<Renderer>(true).Length;
        }
        return c;
    }
    
    public int CountActiveParticles()
    {
        // Return cached count if available (fast)
        if (particleSystemsCache != null && particleSystemsCache.Count > 0)
            return particleSystemsCache.Count;

        // Fallback if cache not built yet
        int c = 0;
        foreach (var go in spawned)
        {
            if (go == null) continue;
            c += go.GetComponentsInChildren<ParticleSystem>(true).Length;
        }
        return c;
    }

   
    /// Sets a global particle "budget" by distributing emission rate across all active ParticleSystems.
    /// Interprets budget as "total emission rate per second" across all systems.
    public void SetParticleBudget(int totalRatePerSecond)
    {
        totalRatePerSecond = Mathf.Max(0, totalRatePerSecond);
        CurrentParticleBudget = totalRatePerSecond;

        if (!particleBudgetEnabled) return;

        // Ensure cache is up-to-date
        if (particleSystemsCache.Count == 0)
            RebuildParticleCache();

        ApplyParticleBudget(totalRatePerSecond);
    }

   
    /// Apply the particle budget to cached particle systems.
    private void ApplyParticleBudget(int totalRatePerSecond)
    {
        if (particleSystemsCache.Count == 0) return;

        // Distribute equally across systems
        float perSystem = (float)totalRatePerSecond / particleSystemsCache.Count;

        for (int i = 0; i < particleSystemsCache.Count; i++)
        {
            var ps = particleSystemsCache[i];
            if (ps == null) continue;

            var emission = ps.emission;

            // Disable emission completely if budget is zero
            emission.enabled = (perSystem > 0f);

            // Use constant emission
            var rot = emission.rateOverTime;
            rot.mode = ParticleSystemCurveMode.Constant;
            rot.constant = perSystem;
            emission.rateOverTime = rot;

            // Optional: set maxParticles based on rate (prevents runaway)
            var main = ps.main;
            int maxP = Mathf.Clamp(Mathf.CeilToInt(perSystem * 2f), 0, 5000);
            main.maxParticles = maxP;
        }
    }

   
    public int GetParticleBudget()
    {
        if (particleSystemsCache.Count == 0)
            RebuildParticleCache();

        int total = 0;

        for (int i = 0; i < particleSystemsCache.Count; i++)
        {
            var ps = particleSystemsCache[i];
            if (ps == null) continue;

            var emission = ps.emission;
            var rate = emission.rateOverTime;

            float val = 0f;
            if (rate.mode == ParticleSystemCurveMode.Constant)
                val = rate.constant;
            else
                val = rate.constantMax;

            total += Mathf.RoundToInt(Mathf.Max(0f, val));
        }

        return total;
    }

 
    private void RebuildParticleCache()
    {
        particleSystemsCache.Clear();

        for (int i = 0; i < spawned.Count; i++)
        {
            var go = spawned[i];
            if (go == null) continue;

            // Collect all ParticleSystems in this spawned object
            var systems = go.GetComponentsInChildren<ParticleSystem>(true);
            if (systems == null) continue;

            for (int k = 0; k < systems.Length; k++)
            {
                if (systems[k] != null)
                    particleSystemsCache.Add(systems[k]);
            }
        }
    }
}
