using System;
using TMPro;
using UnityEngine;

public class UIStatusController : MonoBehaviour
{
    [Header("UI")]
    public TMP_Text statusText;

    [Header("Optional references")]
    public ScenarioRunner scenarioRunner;
    public LatencyDatasetLogger logger;

    private bool loggingOn = false;
    private string scenarioName = "Idle";

    public void SetLoggingOn()
    {
        loggingOn = true;
        UpdateText("Logging started");
    }

    public void SetLoggingOff()
    {
        loggingOn = false;
        UpdateText("Logging stopped");
    }

    public void OnScenarioIdle() => SetScenario("Idle");
    public void OnScenarioRamp() => SetScenario("Ramp");
    public void OnScenarioBursts() => SetScenario("Bursts");
    public void OnScenarioRandomWalk() => SetScenario("RandomWalk");

    private void SetScenario(string name)
    {
        scenarioName = name;
        UpdateText($"Scenario set to {name}");
    }

    public void UpdateText(string message)
    {
        if (statusText == null) return;

        // Optionally also read from actual components for correctness
        string scen = scenarioRunner != null ? scenarioRunner.mode.ToString() : scenarioName;
        bool log = logger != null ? logger.loggingEnabled : loggingOn;

        statusText.text =
            $"Status: {message}\n" +
            $"Logging: {(log ? "ON" : "OFF")} | Scenario: {scen}\n" +
            $"Time: {DateTime.Now:HH:mm:ss}";
    }
}
