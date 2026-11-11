namespace FpsBase
{
    public readonly struct ReflexLatency
    {
        public ReflexLatency(float simulationToGpuEndMs, float gpuRenderMs)
        {
            SimulationToGpuEndMs = simulationToGpuEndMs;
            GpuRenderMs = gpuRenderMs;
        }

        // Start of simulation until the GPU finishes the frame. Excludes display scan-out.
        public float SimulationToGpuEndMs { get; }

        // GPU busy time for the frame, idle gaps excluded.
        public float GpuRenderMs { get; }
    }
}
