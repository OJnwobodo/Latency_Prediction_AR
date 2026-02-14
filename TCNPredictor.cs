using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using Unity.Sentis;
using LatencyPrediction;


namespace LatencyPrediction
{
    [System.Serializable]
    public class ScalerParams
    {
        public float[] mean;
        public float[] scale;
        public string[] feature_cols;
        public int window;
    }
}



public class TCNPredictor : MonoBehaviour
{
    [Header("Sentis Model Asset")]
    public ModelAsset modelAsset;                 
    public BackendType backend = BackendType.CPU;  
    [Header("Scaler JSON (place in Assets/StreamingAssets)")]
    public string scalerJsonFileName = "tcn_scaler_params.json";

    [Header("I/O names (from your model Inspector)")]
    public string inputName = "input_seq";
    public string outputName = "pred_latency";

    private const int F = 5; // features

    private ScalerParams scaler;
    private Worker worker;
    

    private Queue<float[]> windowBuffer;
    public float PredictedLatencyMs { get; private set; }

    public bool Ready => windowBuffer != null && scaler != null && windowBuffer.Count >= scaler.window;

    void Start()
    {
        // Load scaler JSON
        string scalerPath = Path.Combine(Application.streamingAssetsPath, scalerJsonFileName);
        if (!File.Exists(scalerPath))
        {
            Debug.LogError("Scaler JSON not found: " + scalerPath);
            enabled = false;
            return;
        }

        scaler = JsonUtility.FromJson<ScalerParams>(File.ReadAllText(scalerPath));
        if (scaler.mean == null || scaler.scale == null || scaler.mean.Length != F || scaler.scale.Length != F)
        {
            Debug.LogError("Scaler params invalid. mean/scale must be length 5.");
            enabled = false;
            return;
        }

        windowBuffer = new Queue<float[]>(scaler.window);

        // Load model and create worker (Sentis 2.x style)
        Model model = ModelLoader.Load(modelAsset);
        worker = new Worker(model, backend);

        Debug.Log($"TCNPredictor initialized. Window={scaler.window}, backend={backend}");
    }

    void OnDestroy()
    {
        worker?.Dispose();
    }

    // Call once per frame with RAW values (not normalized)
    public void PushFrameFeatures(float targetCount, float smoothedFps, float headLinSpeed, float headAngSpeedDeg, float cpuFrameMs)
    {
        // Normalize: (x - mean) / scale in the SAME order used in training
        float[] x = new float[F];
        x[0] = (targetCount - scaler.mean[0]) / scaler.scale[0];
        x[1] = (smoothedFps - scaler.mean[1]) / scaler.scale[1];
        x[2] = (headLinSpeed - scaler.mean[2]) / scaler.scale[2];
        x[3] = (headAngSpeedDeg - scaler.mean[3]) / scaler.scale[3];
        x[4] = (cpuFrameMs - scaler.mean[4]) / scaler.scale[4];

        if (windowBuffer.Count == scaler.window) windowBuffer.Dequeue();
        windowBuffer.Enqueue(x);

        if (windowBuffer.Count < scaler.window)
        {
            PredictedLatencyMs = cpuFrameMs; // fallback until buffer is full
            return;
        }

        RunInference();
    }

    private void RunInference()
    {
        int W = scaler.window;

        // Input tensor: [1, W, F]
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

        var output = worker.PeekOutput(outputName) as Tensor<float>;
        if (output == null)
        {
            Debug.LogError("PeekOutput returned null or non-float tensor. Check outputName and model output type.");
            input.Dispose();
            return;
        }

        // output is scalar for batch=1
        PredictedLatencyMs = output[0];

        input.Dispose();
        output.Dispose();
    }
}
