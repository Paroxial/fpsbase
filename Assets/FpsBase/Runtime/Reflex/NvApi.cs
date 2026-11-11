using System;
using System.Runtime.InteropServices;

namespace FpsBase
{
    // Binds the Reflex entry points straight out of the driver's nvapi64.dll by interface ID, so the
    // project ships no NVIDIA binaries. IDs and struct layouts follow github.com/NVIDIA/nvapi
    // (nvapi_interface.h, nvapi.h under pack 8). Every struct's reserved tail is covered by Size and
    // zeroed by initobj, which NVAPI requires.
    public static unsafe class NvApi
    {
        public const int Ok = 0;
        public const int LibraryNotFound = -2;
        public const int NoImplementation = -3;
        public const int FrameReportCount = 64;

        private const uint InitializeId = 0x0150E828;
        private const uint SetSleepModeId = 0xAC1CA9E0;
        private const uint SleepId = 0x852CD1D2;
        private const uint GetSleepStatusId = 0xAEF96CA1;
        private const uint SetLatencyMarkerId = 0xD9984C05;
        private const uint GetLatencyId = 0x1A587F9C;

        private const int FrameReportSize = 240;
        private const int FrameReportOffset = 8;

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int InitializeFunction();

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int DeviceFunction(IntPtr device);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int DeviceParamsFunction(IntPtr device, void* parameters);

        private static DeviceParamsFunction setSleepMode;
        private static DeviceFunction sleep;
        private static DeviceParamsFunction getSleepStatus;
        private static DeviceParamsFunction setLatencyMarker;
        private static DeviceParamsFunction getLatency;
        private static IntPtr latencyResult;

        [DllImport("nvapi64", EntryPoint = "nvapi_QueryInterface", CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr QueryInterface(uint id);

        public static int Initialize()
        {
            if (sleep != null)
            {
                return Ok;
            }

            IntPtr initialize;
            try
            {
                initialize = QueryInterface(InitializeId);
            }
            catch (Exception e) when (e is DllNotFoundException || e is EntryPointNotFoundException || e is BadImageFormatException)
            {
                return LibraryNotFound;
            }

            if (initialize == IntPtr.Zero)
            {
                return NoImplementation;
            }

            int status = Marshal.GetDelegateForFunctionPointer<InitializeFunction>(initialize)();
            if (status != Ok)
            {
                return status;
            }

            setSleepMode = Bind<DeviceParamsFunction>(SetSleepModeId);
            getSleepStatus = Bind<DeviceParamsFunction>(GetSleepStatusId);
            setLatencyMarker = Bind<DeviceParamsFunction>(SetLatencyMarkerId);
            getLatency = Bind<DeviceParamsFunction>(GetLatencyId);
            DeviceFunction sleepFunction = Bind<DeviceFunction>(SleepId);
            if (setSleepMode == null || getSleepStatus == null || setLatencyMarker == null || getLatency == null || sleepFunction == null)
            {
                return NoImplementation;
            }

            latencyResult = Marshal.AllocHGlobal(sizeof(LatencyResultParams));
            sleep = sleepFunction;
            return Ok;
        }

        public static int SetSleepMode(IntPtr device, bool lowLatency, bool boost, uint minimumIntervalUs)
        {
            var parameters = new SetSleepModeParams
            {
                Version = VersionOf<SetSleepModeParams>(),
                LowLatencyMode = lowLatency ? (byte)1 : (byte)0,
                LowLatencyBoost = boost ? (byte)1 : (byte)0,
                MinimumIntervalUs = minimumIntervalUs,
            };
            return setSleepMode(device, &parameters);
        }

        public static int Sleep(IntPtr device)
        {
            return sleep(device);
        }

        public static int GetSleepStatus(IntPtr device, out bool lowLatencyActive)
        {
            var parameters = new SleepStatusParams { Version = VersionOf<SleepStatusParams>() };
            int status = getSleepStatus(device, &parameters);
            lowLatencyActive = status == Ok && parameters.LowLatencyMode != 0;
            return status;
        }

        // Safe to call from the render thread: the params live on the caller's stack.
        public static int SetLatencyMarker(IntPtr device, ulong frameId, ReflexMarker marker)
        {
            var parameters = new LatencyMarkerParams
            {
                Version = VersionOf<LatencyMarkerParams>(),
                FrameId = frameId,
                MarkerType = (int)marker,
            };
            return setLatencyMarker(device, &parameters);
        }

        // Main thread only: reuses one unmanaged result buffer. The newest frame lands at the end.
        public static int GetLatency(IntPtr device, LatencyFrameReport[] reports)
        {
            var result = (LatencyResultParams*)latencyResult;
            *result = new LatencyResultParams { Version = VersionOf<LatencyResultParams>() };
            int status = getLatency(device, result);
            if (status != Ok)
            {
                return status;
            }

            var source = (LatencyFrameReport*)((byte*)result + FrameReportOffset);
            int count = Math.Min(reports.Length, FrameReportCount);
            for (int i = 0; i < count; i++)
            {
                reports[i] = source[i];
            }
            return Ok;
        }

        public static string Describe(int status)
        {
            switch (status)
            {
                case Ok: return "NVAPI_OK";
                case -1: return "NVAPI_ERROR";
                case LibraryNotFound: return "NVAPI_LIBRARY_NOT_FOUND";
                case NoImplementation: return "NVAPI_NO_IMPLEMENTATION";
                case -4: return "NVAPI_API_NOT_INITIALIZED";
                case -5: return "NVAPI_INVALID_ARGUMENT";
                case -6: return "NVAPI_NVIDIA_DEVICE_NOT_FOUND";
                case -9: return "NVAPI_INCOMPATIBLE_STRUCT_VERSION";
                case -14: return "NVAPI_INVALID_POINTER";
                case -104: return "NVAPI_NOT_SUPPORTED";
                default: return "NVAPI status " + status;
            }
        }

        private static T Bind<T>(uint id) where T : Delegate
        {
            IntPtr function = QueryInterface(id);
            return function == IntPtr.Zero ? null : Marshal.GetDelegateForFunctionPointer<T>(function);
        }

        private static uint VersionOf<T>() where T : unmanaged
        {
            return (uint)sizeof(T) | (1u << 16);
        }

        // NV_SET_SLEEP_MODE_PARAMS_V1
        [StructLayout(LayoutKind.Sequential, Size = 44)]
        private struct SetSleepModeParams
        {
            public uint Version;
            public byte LowLatencyMode;
            public byte LowLatencyBoost;
            public uint MinimumIntervalUs;
            public byte UseMarkersToOptimize;
            public byte UseMinQueueTime;
        }

        // NV_GET_SLEEP_STATUS_PARAMS_V1
        [StructLayout(LayoutKind.Sequential, Size = 136)]
        private struct SleepStatusParams
        {
            public uint Version;
            public byte LowLatencyMode;
            public byte FullscreenVrr;
            public byte ControlPanelVsyncOn;
            public uint SleepIntervalUs;
        }

        // NV_LATENCY_MARKER_PARAMS_V1
        [StructLayout(LayoutKind.Sequential, Size = 88)]
        private struct LatencyMarkerParams
        {
            public uint Version;
            public ulong FrameId;
            public int MarkerType;
        }

        // NV_LATENCY_RESULT_PARAMS_V1: version, then 64 frame reports aligned to 8, then 32 reserved bytes.
        [StructLayout(LayoutKind.Explicit, Size = FrameReportOffset + FrameReportCount * FrameReportSize + 32)]
        private struct LatencyResultParams
        {
            [FieldOffset(0)] public uint Version;
        }
    }
}
