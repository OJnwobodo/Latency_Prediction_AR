using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using UnityEngine;
using Unity.Sentis;

[Serializable]
public class ScalerParams
{
    // Feature scaler
    public float[] mean;
    public float[] scale;
    public string[] feature_cols;
    public int window;

    public float y_mean;
    public float y_scale;
}

public class LatencyPredictorSentis : MonoBehaviour
{
    [Header("Sentis Model")]
    public ModelAsset onnxModel;                 
    public BackendType backend = BackendType.CPU;

    [Header("Scaler JSON (StreamingAssets)")]
    public string scalerJsonFileName = "tcn_scaler_params.json";

    [Header("Model I/O")]
    public string inputName = "input_seq";
    public string outputName = "pred_latency";

    [Header("Prediction sanity bounds (ms)")]
    public float minValidMs = 0.1f;
    public float maxValidMs = 200.0f;

    [Header("If true, inverse-scale output using y_mean/y_scale (if present in JSON)")]
    public bool inverseScaleOutput = true;

    
    private const int F = 5;

    private Model runtimeModel;
    private Worker worker;
    private ScalerParams scaler;
    private Queue<float[]> windowBuffer;

    public bool Ready { get; private set; } = false;         // model + scaler loaded
    public bool WindowReady { get; private set; } = false;   // windowBuffer filled

    // Output
    public float PredictedLatencyMs { get; private set; } = -1f;

    void Start()
    {
        Ready = false;
        WindowReady = false;
        PredictedLatencyMs = -1f;

        // --- Validate model ---
        if (onnxModel == null)
        {
            Debug.LogError("ONNX ModelAsset is NOT assigned. Assign your Sentis ModelAsset to onnxModel.");
            enabled = false;
            return;
        }

        // --- Load scaler JSON ---
        string path = Path.Combine(Application.streamingAssetsPath, scalerJsonFileName);
        if (!File.Exists(path))
        {
            Debug.LogError("Scaler JSON not found: " + path);
            enabled = false;
            return;
        }

        scaler = JsonUtility.FromJson<ScalerParams>(File.ReadAllText(path));
        if (scaler == null || scaler.mean == null || scaler.scale == null)
        {
            Debug.LogError("Scaler JSON parse failed or missing mean/scale.");
            enabled = false;
            return;
        }

        if (scaler.mean.Length != F || scaler.scale.Length != F || scaler.window <= 0)
        {
            Debug.LogError($"Scaler params mismatch. Expected mean/scale length {F} and window>0. " +
                           $"Got mean={scaler.mean.Length}, scale={scaler.scale.Length}, window={scaler.window}");
            enabled = false;
            return;
        }

        // Guard against zeros in scale
        for (int i = 0; i < F; i++)
        {
            if (Mathf.Approximately(scaler.scale[i], 0f))
            {
                Debug.LogError($"Scaler scale[{i}] is 0. This would divide by zero.");
                enabled = false;
                return;
            }
        }

        windowBuffer = new Queue<float[]>(scaler.window);

        // --- Load Sentis model and create worker ---
        runtimeModel = ModelLoader.Load(onnxModel);
        worker = new Worker(runtimeModel, backend);

        Ready = true;

        
        if (inverseScaleOutput)
        {
            // If y_scale is 0, we will auto-disable inverse scaling at runtime
            if (Mathf.Approximately(scaler.y_scale, 0f))
            {
                Debug.LogWarning("inverseScaleOutput is ON but scaler JSON has y_scale=0 (or missing). " +
                                 "PredictedLatencyMs will be used as-is (no inverse scaling).");
            }
        }
    }

    void OnDestroy()
    {
        worker?.Dispose();
        worker = null;
    }


    /// Push one frame of features. When enough frames collected (window), runs inference.
    public void PushFrameFeatures(
     float targetCount,
     float smoothedFps,
     float headLinSpeed,
     float headAngSpeedDeg,
     float cpuFrameMs)
    {
        if (!Ready || worker == null || scaler == null)
            return;

        // --- Normalize features ---
        float[] x = new float[F];
        x[0] = (targetCount - scaler.mean[0]) / scaler.scale[0];
        x[1] = (smoothedFps - scaler.mean[1]) / scaler.scale[1];
        x[2] = (headLinSpeed - scaler.mean[2]) / scaler.scale[2];
        x[3] = (headAngSpeedDeg - scaler.mean[3]) / scaler.scale[3];
        x[4] = (cpuFrameMs - scaler.mean[4]) / scaler.scale[4];

        // --- Maintain sliding window ---
        if (windowBuffer.Count == scaler.window)
            windowBuffer.Dequeue();

        windowBuffer.Enqueue(x);

        // --- WARM-UP GATING (THIS IS THE KEY PART) ---
        if (windowBuffer.Count < scaler.window)
        {
            WindowReady = false;
            PredictedLatencyMs = -1f;   
            return;
        }

        // --- Window is now valid ---
        WindowReady = true;
        RunInference(fallbackCpuFrameMs: cpuFrameMs);
    }


    private void RunInference(float fallbackCpuFrameMs)
    {
        int W = scaler.window;

        var input = new Tensor<float>(new TensorShape(1, W, F));

        int i = 0;
        foreach (var row in windowBuffer)
        {
            for (int j = 0; j < F; j++)
                input[0, i, j] = row[j];
            i++;
        }

        worker.SetInput(inputName, input);
        worker.Schedule();

        Tensor outTensor = worker.PeekOutput(outputName);
        if (outTensor == null)
        {
            Debug.LogError("PeekOutput returned null. Check outputName matches the model output.");
            PredictedLatencyMs = -1f;
            input.Dispose();
            return;
        }

        if (!TryReadFirstFloat(outTensor, out float yRaw))
        {
            Debug.LogError("Could not read output tensor to CPU. Check Sentis version / output type.");
            PredictedLatencyMs = -1f;
            input.Dispose();
            return;
        }

      
        float yMs = yRaw;
        if (inverseScaleOutput && !Mathf.Approximately(scaler.y_scale, 0f))
        {
            // y_ms = y_norm * y_scale + y_mean
            yMs = yRaw * scaler.y_scale + scaler.y_mean;
        }

        if (!IsValidMs(yMs))
        {
           
            PredictedLatencyMs = fallbackCpuFrameMs;
        }
        else
        {
            PredictedLatencyMs = yMs;
        }

        input.Dispose();
        
    }

    private bool IsValidMs(float v)
    {
        if (float.IsNaN(v) || float.IsInfinity(v)) return false;
        if (v < minValidMs) return false;
        if (v > maxValidMs) return false;
        return true;
    }


    private bool TryReadFirstFloat(Tensor t, out float value)
    {
        value = 0f;
        try
        {
            
            MethodInfo mReadback = t.GetType().GetMethod("ReadbackAndClone", BindingFlags.Public | BindingFlags.Instance);
            if (mReadback != null)
            {
                object cpuObj = mReadback.Invoke(t, null);
                var cpuTensor = cpuObj as Tensor;
                if (cpuTensor != null)
                {
                    try
                    {
                        var cpuF = cpuTensor as Tensor<float>;
                        if (cpuF != null)
                        {
                            value = cpuF[0];
                            return true;
                        }

                        if (TryIndexTensorAsFloat(cpuTensor, out value))
                            return true;
                    }
                    finally
                    {
                        cpuTensor.Dispose();
                    }
                }
            }

            
            var tf = t as Tensor<float>;
            if (tf != null)
            {
                value = tf[0];
                return true;
            }

            if (TryIndexTensorAsFloat(t, out value))
                return true;

            return false;
        }
        catch
        {
            return false;
        }
    }

    private bool TryIndexTensorAsFloat(Tensor anyTensor, out float value)
    {
        value = 0f;
        try
        {
            var tf = anyTensor as Tensor<float>;
            if (tf != null)
            {
                value = tf[0];
                return true;
            }
            return false;
        }
        catch
        {
            return false;
        }
    }
}
