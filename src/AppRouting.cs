using System;
using System.Runtime.InteropServices;

namespace VolumeControl
{
    // Undocumented WinRT factory behind Settings > Sound > Volume mixer's per-app output.
    // The vtable matches builds 21390 and later; earlier builds use a different interface, so they are refused.
    [ComImport, Guid("ab3d4648-e242-459f-b02f-541c70306324"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IAudioPolicyConfigFactory
    {
        // IInspectable
        [PreserveSig] int GetIids(); [PreserveSig] int GetRuntimeClassName(); [PreserveSig] int GetTrustLevel();
        // Methods this app does not call; they only hold vtable slots.
        [PreserveSig] int Slot01(); [PreserveSig] int Slot02(); [PreserveSig] int Slot03(); [PreserveSig] int Slot04();
        [PreserveSig] int Slot05(); [PreserveSig] int Slot06(); [PreserveSig] int Slot07(); [PreserveSig] int Slot08();
        [PreserveSig] int Slot09(); [PreserveSig] int Slot10(); [PreserveSig] int Slot11(); [PreserveSig] int Slot12();
        [PreserveSig] int Slot13(); [PreserveSig] int Slot14(); [PreserveSig] int Slot15(); [PreserveSig] int Slot16();
        [PreserveSig] int Slot17(); [PreserveSig] int Slot18(); [PreserveSig] int Slot19();
        [PreserveSig] int SetPersistedDefaultAudioEndpoint(uint processId, int flow, int role, IntPtr deviceId);
    }

    internal sealed class AppRouting
    {
        private const int MinimumBuild = 21390, Render = 0;
        private const string ClassName = "Windows.Media.Internal.AudioPolicyConfig";
        private const string DevicePrefix = @"\\?\SWD#MMDEVAPI#";
        private const string DeviceSuffix = "#{e6327cad-dcec-4949-ae8a-991e976a79d2}";

        [DllImport("combase", CharSet = CharSet.Unicode)]
        private static extern int WindowsCreateString(string text, int length, out IntPtr hstring);

        [DllImport("combase")]
        private static extern int WindowsDeleteString(IntPtr hstring);

        [DllImport("combase")]
        private static extern int RoGetActivationFactory(IntPtr classId, ref Guid iid, out IntPtr factory);

        private readonly IAudioPolicyConfigFactory _factory;

        public bool Available => _factory != null;

        /// <summary>Must be created on the engine's MTA thread.</summary>
        public AppRouting()
        {
            if (Environment.OSVersion.Version.Build < MinimumBuild) return;
            try
            {
                WindowsCreateString(ClassName, ClassName.Length, out IntPtr name);
                try
                {
                    var iid = typeof(IAudioPolicyConfigFactory).GUID;
                    if (RoGetActivationFactory(name, ref iid, out IntPtr raw) != 0) return;
                    try { _factory = (IAudioPolicyConfigFactory)Marshal.GetObjectForIUnknown(raw); }
                    finally { Marshal.Release(raw); }
                }
                finally { WindowsDeleteString(name); }
            }
            catch (Exception) { _factory = null; }
        }

        /// <param name="endpointId">Device to use, or null to follow the Windows default again.</param>
        public bool Set(uint processId, string endpointId)
        {
            if (_factory == null) return false;
            IntPtr device = IntPtr.Zero;
            try
            {
                if (endpointId != null)
                {
                    string full = DevicePrefix + endpointId + DeviceSuffix;
                    WindowsCreateString(full, full.Length, out device);
                }
                // Console and multimedia are the two roles the Windows per-app setting changes.
                int console = _factory.SetPersistedDefaultAudioEndpoint(processId, Render, 0, device);
                int media = _factory.SetPersistedDefaultAudioEndpoint(processId, Render, 1, device);
                return console == 0 && media == 0;
            }
            finally
            {
                if (device != IntPtr.Zero) WindowsDeleteString(device);
            }
        }
    }
}
