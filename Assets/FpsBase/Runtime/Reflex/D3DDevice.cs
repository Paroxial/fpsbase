using System;
using System.Runtime.InteropServices;

namespace FpsBase
{
    // Recovers the ID3D11Device / ID3D12Device behind a native texture pointer, the only graphics
    // device handle Unity hands to C# without a native plugin.
    public static class D3DDevice
    {
        private const int ReleaseSlot = 2;
        private const int D3D11GetDeviceSlot = 3;
        private const int D3D12GetDeviceSlot = 7;

        private static readonly Guid D3D12DeviceIid = new Guid("189819f1-1db6-4b57-be54-1821339b85f7");

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate void D3D11GetDevice(IntPtr self, out IntPtr device);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int D3D12GetDevice(IntPtr self, ref Guid iid, out IntPtr device);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate uint ComRelease(IntPtr self);

        public static IntPtr FromD3D11Resource(IntPtr resource)
        {
            if (resource == IntPtr.Zero)
            {
                return IntPtr.Zero;
            }

            GetMethod<D3D11GetDevice>(resource, D3D11GetDeviceSlot)(resource, out IntPtr device);
            ReleaseReference(device);
            return device;
        }

        public static IntPtr FromD3D12Resource(IntPtr resource)
        {
            if (resource == IntPtr.Zero)
            {
                return IntPtr.Zero;
            }

            Guid iid = D3D12DeviceIid;
            int result = GetMethod<D3D12GetDevice>(resource, D3D12GetDeviceSlot)(resource, ref iid, out IntPtr device);
            if (result < 0)
            {
                return IntPtr.Zero;
            }

            ReleaseReference(device);
            return device;
        }

        // GetDevice adds a reference. The engine owns the device for the whole session, so the raw
        // pointer stays valid after dropping that reference.
        private static void ReleaseReference(IntPtr comObject)
        {
            if (comObject != IntPtr.Zero)
            {
                GetMethod<ComRelease>(comObject, ReleaseSlot)(comObject);
            }
        }

        private static T GetMethod<T>(IntPtr comObject, int slot) where T : Delegate
        {
            IntPtr vtable = Marshal.ReadIntPtr(comObject);
            return Marshal.GetDelegateForFunctionPointer<T>(Marshal.ReadIntPtr(vtable, slot * IntPtr.Size));
        }
    }
}
