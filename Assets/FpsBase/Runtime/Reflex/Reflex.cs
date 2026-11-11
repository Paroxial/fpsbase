using System;
using System.Runtime.InteropServices;
using AOT;
using UnityEngine;
using UnityEngine.PlayerLoop;
using UnityEngine.Rendering;

namespace FpsBase
{
    // Drives NVIDIA Reflex Low Latency from the player loop. Each frame:
    //
    //   PostLateUpdate, first             SIMULATION_END
    //   before FinishFrameRendering       RENDERSUBMIT_START                 (render thread)
    //   before PresentAfterDraw           RENDERSUBMIT_END, PRESENT_START    (render thread)
    //   after PresentAfterDraw            PRESENT_END                        (render thread)
    //   PostLateUpdate, last              Sleep, next frame ID, SIMULATION_START
    //
    // The sleep sits at the end of the loop rather than the start because Unity pumps OS messages
    // between player loops: input that arrives while the driver holds the CPU still reaches the
    // very next frame. Render-thread markers ride command buffers so they land next to the real
    // D3D submission and Present instead of where the main thread happens to be.
    public static class Reflex
    {
        private const int NvidiaVendorId = 0x10DE;
        private const string NotInitialized = "Initializes before the first scene loads.";

        private struct SimulationEndMarker { }
        private struct RenderSubmitStartMarker { }
        private struct PresentStartMarker { }
        private struct PresentEndMarker { }
        private struct FrameBoundary { }

        private delegate void RenderEventCallback(int eventId, IntPtr data);

        private static readonly RenderEventCallback RenderEvent = OnRenderEvent;
        private static readonly LatencyFrameReport[] Reports = new LatencyFrameReport[NvApi.FrameReportCount];

        private static IntPtr device;
        private static IntPtr renderEventFunction;
        private static CommandBuffer renderCommands;
        private static ulong frameId;
        private static ReflexMode mode;
        private static float frameRateLimit;

        public static bool IsAvailable { get; private set; }

        public static string UnavailableReason { get; private set; } = NotInitialized;

        public static ReflexMode Mode
        {
            get => mode;
            set
            {
                mode = value;
                ApplySleepMode(device);
            }
        }

        // Caps the frame rate inside the driver's sleep, which adds less latency than
        // Application.targetFrameRate. 0 removes the cap.
        public static float FrameRateLimit
        {
            get => frameRateLimit;
            set
            {
                frameRateLimit = Mathf.Max(0f, value);
                ApplySleepMode(device);
            }
        }

        // The driver can override the requested mode, e.g. from the NVIDIA Control Panel.
        public static bool DriverLowLatencyActive =>
            IsAvailable && NvApi.GetSleepStatus(device, out bool active) == NvApi.Ok && active;

        // For markers raised during simulation, such as InputSample or TriggerFlash on a click.
        public static void SetMarker(ReflexMarker marker)
        {
            if (IsAvailable)
            {
                NvApi.SetLatencyMarker(device, frameId, marker);
            }
        }

        // Averages the driver's last 64 frame reports. Needs roughly 90 frames before data appears.
        public static bool TryGetLatency(out ReflexLatency latency)
        {
            latency = default;
            if (!IsAvailable || NvApi.GetLatency(device, Reports) != NvApi.Ok)
            {
                return false;
            }

            double totalUs = 0;
            double gpuUs = 0;
            int frames = 0;
            for (int i = 0; i < Reports.Length; i++)
            {
                ref LatencyFrameReport report = ref Reports[i];
                if (report.SimulationStartTime == 0 || report.GpuRenderEndTime <= report.SimulationStartTime)
                {
                    continue;
                }

                totalUs += report.GpuRenderEndTime - report.SimulationStartTime;
                gpuUs += report.GpuActiveRenderTimeUs;
                frames++;
            }

            if (frames == 0)
            {
                return false;
            }

            latency = new ReflexLatency((float)(totalUs / frames / 1000.0), (float)(gpuUs / frames / 1000.0));
            return true;
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetState()
        {
            IsAvailable = false;
            UnavailableReason = NotInitialized;
            device = IntPtr.Zero;
            frameId = 0;
            mode = ReflexMode.On;
            frameRateLimit = 0f;
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void Initialize()
        {
            string failure = AcquireDevice(out IntPtr candidate);
            if (failure == null)
            {
                int status = ApplySleepMode(candidate);
                if (status != NvApi.Ok)
                {
                    failure = $"Driver rejects Reflex with {NvApi.Describe(status)}.";
                }
            }

            if (failure != null)
            {
                UnavailableReason = failure;
                Debug.Log("Reflex unavailable. " + failure);
                return;
            }

            device = candidate;
            IsAvailable = true;
            UnavailableReason = null;
            renderEventFunction = Marshal.GetFunctionPointerForDelegate(RenderEvent);
            renderCommands = new CommandBuffer { name = "Reflex Latency Markers" };
            frameId = 1;
            NvApi.SetLatencyMarker(device, frameId, ReflexMarker.SimulationStart);

            PlayerLoopUtility.AddFirst(typeof(PostLateUpdate), typeof(SimulationEndMarker), MarkSimulationEnd);
            PlayerLoopUtility.AddBefore(typeof(PostLateUpdate.FinishFrameRendering), typeof(RenderSubmitStartMarker), QueueRenderSubmitStart);
            PlayerLoopUtility.AddBefore(typeof(PostLateUpdate.PresentAfterDraw), typeof(PresentStartMarker), QueuePresentStart);
            PlayerLoopUtility.AddAfter(typeof(PostLateUpdate.PresentAfterDraw), typeof(PresentEndMarker), QueuePresentEnd);
            PlayerLoopUtility.AddLast(typeof(PostLateUpdate), typeof(FrameBoundary), SleepUntilNextFrame);
            Application.quitting += Shutdown;
            Debug.Log($"Reflex ready on {SystemInfo.graphicsDeviceName} ({SystemInfo.graphicsDeviceType}).");
        }

        private static string AcquireDevice(out IntPtr result)
        {
            result = IntPtr.Zero;
            if (SystemInfo.graphicsDeviceVendorID != NvidiaVendorId)
            {
                return $"Requires an NVIDIA GPU, running on {SystemInfo.graphicsDeviceName}.";
            }

            GraphicsDeviceType api = SystemInfo.graphicsDeviceType;
            if (api != GraphicsDeviceType.Direct3D11 && api != GraphicsDeviceType.Direct3D12)
            {
                return $"Requires Direct3D 11 or 12, running on {api}.";
            }

            int status = NvApi.Initialize();
            if (status != NvApi.Ok)
            {
                return $"NVAPI fails to initialize with {NvApi.Describe(status)}.";
            }

            IntPtr texture = Texture2D.whiteTexture.GetNativeTexturePtr();
            result = api == GraphicsDeviceType.Direct3D11
                ? D3DDevice.FromD3D11Resource(texture)
                : D3DDevice.FromD3D12Resource(texture);
            return result == IntPtr.Zero ? "Fails to resolve the Direct3D device." : null;
        }

        private static int ApplySleepMode(IntPtr target)
        {
            if (target == IntPtr.Zero)
            {
                return NvApi.NoImplementation;
            }

            uint intervalUs = frameRateLimit > 0f ? (uint)Mathf.RoundToInt(1_000_000f / frameRateLimit) : 0u;
            return NvApi.SetSleepMode(target, mode != ReflexMode.Off, mode == ReflexMode.OnAndBoost, intervalUs);
        }

        private static void MarkSimulationEnd()
        {
            NvApi.SetLatencyMarker(device, frameId, ReflexMarker.SimulationEnd);
        }

        private static void QueueRenderSubmitStart()
        {
            renderCommands.Clear();
            AddRenderThreadMarker(ReflexMarker.RenderSubmitStart);
            Graphics.ExecuteCommandBuffer(renderCommands);
        }

        private static void QueuePresentStart()
        {
            renderCommands.Clear();
            AddRenderThreadMarker(ReflexMarker.RenderSubmitEnd);
            AddRenderThreadMarker(ReflexMarker.PresentStart);
            Graphics.ExecuteCommandBuffer(renderCommands);
        }

        private static void QueuePresentEnd()
        {
            renderCommands.Clear();
            AddRenderThreadMarker(ReflexMarker.PresentEnd);
            Graphics.ExecuteCommandBuffer(renderCommands);
        }

        // The frame ID travels as the event's data pointer, so the render thread, which trails the
        // main thread by up to a frame, tags each marker with the frame that queued it.
        private static void AddRenderThreadMarker(ReflexMarker marker)
        {
            renderCommands.IssuePluginEventAndData(renderEventFunction, (int)marker, new IntPtr((long)frameId));
        }

        [MonoPInvokeCallback(typeof(RenderEventCallback))]
        private static void OnRenderEvent(int eventId, IntPtr data)
        {
            NvApi.SetLatencyMarker(device, (ulong)data.ToInt64(), (ReflexMarker)eventId);
        }

        private static void SleepUntilNextFrame()
        {
            NvApi.Sleep(device);
            frameId++;
            NvApi.SetLatencyMarker(device, frameId, ReflexMarker.SimulationStart);
        }

        // Leaves the device pointer in place: render-thread markers queued this frame still need it.
        private static void Shutdown()
        {
            Application.quitting -= Shutdown;
            PlayerLoopUtility.Remove(typeof(SimulationEndMarker));
            PlayerLoopUtility.Remove(typeof(RenderSubmitStartMarker));
            PlayerLoopUtility.Remove(typeof(PresentStartMarker));
            PlayerLoopUtility.Remove(typeof(PresentEndMarker));
            PlayerLoopUtility.Remove(typeof(FrameBoundary));
            NvApi.SetSleepMode(device, false, false, 0);
            IsAvailable = false;
            UnavailableReason = "Shuts down with the player.";
            renderCommands.Release();
            renderCommands = null;
        }
    }
}
