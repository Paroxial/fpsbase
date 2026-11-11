namespace FpsBase
{
    // NV_LATENCY_MARKER_TYPE
    public enum ReflexMarker
    {
        SimulationStart = 0,
        SimulationEnd = 1,
        RenderSubmitStart = 2,
        RenderSubmitEnd = 3,
        PresentStart = 4,
        PresentEnd = 5,
        InputSample = 6,
        TriggerFlash = 7,
    }
}
