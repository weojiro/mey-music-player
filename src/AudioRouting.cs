using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;

namespace TaskbarPlayerProto;

// Windows' output device per app (Settings > Sound > Volume mixer > an app > Output device), set from code. It isn't a
// public API; the Settings app and EarTrumpet use the same one. Windows keeps the choice per program file.
static class AppRouting
{
    [ComImport, Guid("ab3d4648-e242-459f-b02f-541c70306324"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IFactory
    {
        // IInspectable, then 19 methods MMP doesn't use
        void GetIids(); void GetRuntimeClassName(); void GetTrustLevel();
        void M1(); void M2(); void M3(); void M4(); void M5(); void M6(); void M7(); void M8(); void M9(); void M10();
        void M11(); void M12(); void M13(); void M14(); void M15(); void M16(); void M17(); void M18(); void M19();
        [PreserveSig] int SetPersistedDefaultAudioEndpoint(uint pid, DataFlow flow, Role role, IntPtr deviceId);
        [PreserveSig] int GetPersistedDefaultAudioEndpoint(uint pid, DataFlow flow, Role role, out IntPtr deviceId);
    }

    // the same interface on Windows 10 before 21H2
    [ComImport, Guid("2a59116d-6c4f-45e0-a74f-707e3fef9258"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IFactoryOld
    {
        void GetIids(); void GetRuntimeClassName(); void GetTrustLevel();
        void M1(); void M2(); void M3(); void M4(); void M5(); void M6(); void M7(); void M8(); void M9(); void M10();
        void M11(); void M12(); void M13(); void M14(); void M15(); void M16(); void M17(); void M18(); void M19();
        [PreserveSig] int SetPersistedDefaultAudioEndpoint(uint pid, DataFlow flow, Role role, IntPtr deviceId);
        [PreserveSig] int GetPersistedDefaultAudioEndpoint(uint pid, DataFlow flow, Role role, out IntPtr deviceId);
    }

    [DllImport("combase.dll")] static extern int RoGetActivationFactory(IntPtr cls, ref Guid iid, out IntPtr factory);
    [DllImport("combase.dll")] static extern int WindowsCreateString([MarshalAs(UnmanagedType.LPWStr)] string s, int length, out IntPtr h);
    [DllImport("combase.dll")] static extern int WindowsDeleteString(IntPtr h);
    [DllImport("combase.dll")] static extern IntPtr WindowsGetStringRawBuffer(IntPtr h, out uint length);

    const string Prefix = @"\\?\SWD#MMDEVAPI#", RenderSuffix = "#{e6327cad-dcec-4949-ae8a-991e976a79d2}";
    static object? _factory;
    static bool _tried;

    static object? Factory()
    {
        if (_tried) return _factory;
        _tried = true;
        const string cls = "Windows.Media.Internal.AudioPolicyConfig";
        WindowsCreateString(cls, cls.Length, out var h);
        try
        {
            foreach (var type in new[] { typeof(IFactory), typeof(IFactoryOld) })
            {
                var iid = type.GUID;
                if (RoGetActivationFactory(h, ref iid, out var p) == 0 && p != IntPtr.Zero)
                {
                    _factory = Marshal.GetObjectForIUnknown(p);
                    Marshal.Release(p);
                    return _factory;
                }
            }
        }
        finally { WindowsDeleteString(h); }
        return null;
    }

    public static bool Available => Factory() != null;

    // The device the app's program file is sent to: "" for the default device, null when it can't be read
    public static string? Get(int pid)
    {
        IntPtr h;
        int hr = Factory() switch
        {
            IFactory f => f.GetPersistedDefaultAudioEndpoint((uint)pid, DataFlow.Render, Role.Multimedia, out h),
            IFactoryOld f => f.GetPersistedDefaultAudioEndpoint((uint)pid, DataFlow.Render, Role.Multimedia, out h),
            _ => Unavailable(out h)
        };
        if (hr != 0) return null;
        if (h == IntPtr.Zero) return "";
        string s = Marshal.PtrToStringUni(WindowsGetStringRawBuffer(h, out _)) ?? "";
        WindowsDeleteString(h);
        return s;
    }

    static int Unavailable(out IntPtr h) { h = IntPtr.Zero; return -1; }

    // Sends the app to a device (an MMDevice id, or the long form Get returns); "" sends it back to the default
    public static bool Set(int pid, string deviceId)
    {
        IntPtr h = IntPtr.Zero;
        if (deviceId.Length > 0)
        {
            string full = deviceId.StartsWith(@"\\?\") ? deviceId : Prefix + deviceId + RenderSuffix;
            WindowsCreateString(full, full.Length, out h);
        }
        try
        {
            bool ok = true;
            foreach (var role in new[] { Role.Console, Role.Multimedia })
            {
                int hr = Factory() switch
                {
                    IFactory f => f.SetPersistedDefaultAudioEndpoint((uint)pid, DataFlow.Render, role, h),
                    IFactoryOld f => f.SetPersistedDefaultAudioEndpoint((uint)pid, DataFlow.Render, role, h),
                    _ => -1
                };
                ok &= hr == 0;
            }
            return ok;
        }
        finally { if (h != IntPtr.Zero) WindowsDeleteString(h); }
    }

    // true when a device id (either form) is the given MMDevice
    public static bool Is(string? deviceId, string mmDeviceId) =>
        deviceId != null && deviceId.Contains(mmDeviceId, StringComparison.OrdinalIgnoreCase);
}

// The default sound devices. VB-CABLE's setup makes its cable the default; MMP puts the user's own devices back.
static class DefaultDevices
{
    [ComImport, Guid("870af99c-171d-4f9e-af0d-e63df40c2bc9")] class PolicyConfigClient { }

    [ComImport, Guid("f8679f50-850a-41cf-9c72-430f290290c8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IPolicyConfig
    {
        void GetMixFormat(); void GetDeviceFormat(); void ResetDeviceFormat(); void SetDeviceFormat(); void GetProcessingPeriod();
        void SetProcessingPeriod(); void GetShareMode(); void SetShareMode(); void GetPropertyValue(); void SetPropertyValue();
        [PreserveSig] int SetDefaultEndpoint([MarshalAs(UnmanagedType.LPWStr)] string id, Role role);
    }

    static readonly DataFlow[] Flows = { DataFlow.Render, DataFlow.Capture };
    static readonly Role[] Roles = { Role.Console, Role.Multimedia, Role.Communications };

    public static string?[] Save()
    {
        var ids = new List<string?>();
        using var en = new MMDeviceEnumerator();
        foreach (var flow in Flows)
            foreach (var role in Roles)
            {
                try { ids.Add(en.HasDefaultAudioEndpoint(flow, role) ? en.GetDefaultAudioEndpoint(flow, role).ID : null); }
                catch { ids.Add(null); }
            }
        return ids.ToArray();
    }

    public static void Restore(string?[] saved)
    {
        try
        {
            using var en = new MMDeviceEnumerator();
            var policy = (IPolicyConfig)new PolicyConfigClient();
            int i = 0;
            foreach (var flow in Flows)
                foreach (var role in Roles)
                {
                    string? id = saved[i++];
                    if (id == null) continue;
                    string? now = en.HasDefaultAudioEndpoint(flow, role) ? en.GetDefaultAudioEndpoint(flow, role).ID : null;
                    if (now != id) policy.SetDefaultEndpoint(id, role);
                }
        }
        catch { }
    }
}

// One program's sound (with its child processes) wherever it plays, even on VB-CABLE. Windows 10 build 20348 and later.
static class ProcessLoopback
{
    [StructLayout(LayoutKind.Sequential)] struct Params { public int Type; public int Pid; public int Mode; }
    [StructLayout(LayoutKind.Sequential)] struct PropVariant { public ushort Vt; ushort _r1, _r2, _r3; public int Size; public IntPtr Data; }

    [ComImport, Guid("41D949AB-9862-444A-80F6-C261334DA5EB"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IHandler { void ActivateCompleted(IOperation op); }
    [ComImport, Guid("72A22D78-CDE4-431D-B8CC-843A71199B6D"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IOperation { void GetActivateResult(out int hr, [MarshalAs(UnmanagedType.IUnknown)] out object iface); }
    [ComImport, Guid("94ea2b94-e9cc-49e0-c0ff-ee64ca8f5b90"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAgileObject { }

    sealed class Handler : IHandler, IAgileObject
    {
        public readonly ManualResetEventSlim Done = new();
        public object? Result;
        public int Hr;
        public void ActivateCompleted(IOperation op)
        {
            try { op.GetActivateResult(out Hr, out var o); Result = o; }
            catch (Exception ex) { Hr = ex.HResult; }
            Done.Set();
        }
    }

    [DllImport("Mmdevapi.dll", ExactSpelling = true)]
    static extern int ActivateAudioInterfaceAsync([MarshalAs(UnmanagedType.LPWStr)] string path, [MarshalAs(UnmanagedType.LPStruct)] Guid riid,
        IntPtr activationParams, IHandler handler, out IOperation op);

    public static bool Supported => Environment.OSVersion.Version.Build >= 20348;

    // null when Windows can't (older Windows, the process is gone)
    public static AudioClient? Activate(int pid)
    {
        if (!Supported) return null;
        IntPtr p = Marshal.AllocHGlobal(Marshal.SizeOf<Params>());
        IntPtr pv = Marshal.AllocHGlobal(Marshal.SizeOf<PropVariant>());
        try
        {
            Marshal.StructureToPtr(new Params { Type = 1, Pid = pid, Mode = 0 }, p, false);     // 1: process loopback, 0: with its children
            Marshal.StructureToPtr(new PropVariant { Vt = 65, Size = Marshal.SizeOf<Params>(), Data = p }, pv, false);     // VT_BLOB
            var handler = new Handler();
            if (ActivateAudioInterfaceAsync(@"VAD\Process_Loopback", typeof(IAudioClient).GUID, pv, handler, out _) != 0) return null;
            if (!handler.Done.Wait(3000) || handler.Hr != 0 || handler.Result is not IAudioClient client) return null;
            return new AudioClient(client);
        }
        catch { return null; }
        finally { Marshal.FreeHGlobal(p); Marshal.FreeHGlobal(pv); }
    }
}
