using Microsoft.ML.OnnxRuntime;

namespace Fotobox.Services.Detection.Acceleration;

/// <summary>
/// Die einzige Stelle, die <see cref="SessionOptions"/> und <see cref="InferenceSession"/> baut: CPU (Standard) oder ein
/// Windows-ML-Gerät (GPU/NPU). Ein Gerät ohne ONNX-Runtime-Handle (Test-Gerät) scheitert hier mit <see cref="InvalidOperationException"/>.
/// </summary>
internal static class OnnxSessionFactory
{
    /// <summary>Optionen wie bisher für die CPU; für ein Gerät zusätzlich der angehängte Ausführungsanbieter.</summary>
    public static SessionOptions CreateOptions(AcceleratorDevice? device)
    {
        var options = new SessionOptions
        {
            IntraOpNumThreads = 2,
            InterOpNumThreads = 1,
            GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
        };
        try
        {
            // Die ORT-Threads schlafen zwischen den Analysen (im Leerlauf 5/s), statt aktiv zu warten: spart CPU, Ergebnisse bleiben gleich.
            options.AddSessionConfigEntry("session.intra_op.allow_spinning", "0");
            options.AddSessionConfigEntry("session.inter_op.allow_spinning", "0");

            if (device is { IsCpu: false })
            {
                var handle = device.Handle ?? throw new InvalidOperationException($"Das Gerät „{device.Name}“ ist nicht als ONNX-Runtime-Gerät verfügbar.");
                if (string.Equals(device.EpName, AcceleratorDevice.DirectMlEpName, StringComparison.Ordinal))
                {
                    // Empfehlung von ONNX Runtime für DirectML.
                    options.ExecutionMode = ExecutionMode.ORT_SEQUENTIAL;
                    options.EnableMemoryPattern = false;
                }
                options.AppendExecutionProvider(OrtEnv.Instance(), [handle], new Dictionary<string, string>());
            }
            return options;
        }
        catch
        {
            options.Dispose();
            throw;
        }
    }

    /// <summary>Erzeugt die Session; <paramref name="device"/> null oder CPU = Prozessor.</summary>
    public static InferenceSession Create(string modelPath, AcceleratorDevice? device)
    {
        using var options = CreateOptions(device);
        return new InferenceSession(modelPath, options);
    }
}
