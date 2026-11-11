using System.Runtime.InteropServices;

namespace FpsBase
{
    // NV_LATENCY_RESULT_PARAMS_V1::FrameReport. Timestamps are driver microseconds; zero means the
    // frame never reported that stage.
    [StructLayout(LayoutKind.Sequential, Size = 240)]
    public struct LatencyFrameReport
    {
        public ulong FrameId;
        public ulong InputSampleTime;
        public ulong SimulationStartTime;
        public ulong SimulationEndTime;
        public ulong RenderSubmitStartTime;
        public ulong RenderSubmitEndTime;
        public ulong PresentStartTime;
        public ulong PresentEndTime;
        public ulong DriverStartTime;
        public ulong DriverEndTime;
        public ulong OsRenderQueueStartTime;
        public ulong OsRenderQueueEndTime;
        public ulong GpuRenderStartTime;
        public ulong GpuRenderEndTime;
        public uint GpuActiveRenderTimeUs;
        public uint GpuFrameTimeUs;
        public ulong CameraConstructedTime;
        public uint CrossAdapterCopyTimeUs;
        public uint AiFrameTimeUs;
    }
}
