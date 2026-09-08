using System.Runtime.InteropServices;

namespace DesktopDriverServer.Sap;

/// <summary>
/// Minimal <c>IDispatch</c> late-binding wrapper for a COM object.
///
/// SAP GUI for Windows 8.00 registers only a 32-bit scripting type library
/// (<c>{5EA428A0-F2B8-45E7-99FA-0E994E82B5BC}</c> → <c>sapfewse.ocx</c>, win32 only).
/// A 64-bit process — which DesktopDriverServer is, for UIA — therefore cannot use
/// <c>dynamic</c> or <c>Type.InvokeMember</c> against the <c>GuiApplication</c> object:
/// both resolve members through <c>ITypeInfo</c> and fail with
/// <c>TYPE_E_CANTLOADLIBRARY (0x80029C4A)</c> because there is no 64-bit typelib to load.
///
/// PowerShell's COM adapter avoids this by calling <c>IDispatch::GetIDsOfNames</c> +
/// <c>IDispatch::Invoke</c> directly, never opening the typelib. This class does the
/// same: given a raw COM instance it exposes <see cref="Get"/> / <see cref="Call"/> /
/// <see cref="Set"/>, marshalling arguments as VARIANTs by hand. Object-valued results
/// come back wrapped in another <see cref="Disp"/>; scalars come back as
/// <see cref="string"/> / <see cref="int"/> / <see cref="bool"/> / <c>null</c>.
///
/// Not general-purpose — no by-ref args, no SAFEARRAYs, no named args beyond the
/// property-put value. That covers the entire SAP GUI Scripting surface this driver uses.
/// </summary>
internal sealed class Disp
{
    private const int LOCALE_SYSTEM_DEFAULT = 0x0800;
    private const short DISPATCH_METHOD = 1;
    private const short DISPATCH_PROPERTYGET = 2;
    private const short DISPATCH_PROPERTYPUT = 4;
    private const int DISPID_PROPERTYPUT = -3;

    private static Guid _iidNull = Guid.Empty;

    private readonly object _target;
    private readonly IDispatch _disp;

    public Disp(object target)
    {
        _target = target ?? throw new ArgumentNullException(nameof(target));
        _disp = target as IDispatch
            ?? throw new InvalidOperationException(
                $"COM object of type {target.GetType()} does not implement IDispatch.");
    }

    /// <summary>The underlying COM instance (an <c>__ComObject</c>).</summary>
    public object Raw => _target;

    // ── Public accessors ──────────────────────────────────────────────────────

    /// <summary>Property get / method call whose result is another COM object.</summary>
    public Disp GetObj(string name, params object?[] args)
    {
        var r = InvokeCore(name, DISPATCH_PROPERTYGET | DISPATCH_METHOD, args);
        if (r == null)
            throw new InvalidOperationException($"SAP member '{name}' returned null (expected an object).");
        return new Disp(r);
    }

    /// <summary>Property get / method call whose result is another COM object, or null.</summary>
    public Disp? GetObjOrNull(string name, params object?[] args)
    {
        var r = InvokeCore(name, DISPATCH_PROPERTYGET | DISPATCH_METHOD, args);
        return r == null ? null : new Disp(r);
    }

    /// <summary>Method call whose result is another COM object, or null.</summary>
    public Disp? CallObjOrNull(string name, params object?[] args)
    {
        var r = InvokeCore(name, DISPATCH_METHOD, args);
        return r == null ? null : new Disp(r);
    }

    /// <summary>Scalar property get. Returns string/int/bool/null.</summary>
    public object? Get(string name, params object?[] args)
        => InvokeCore(name, DISPATCH_PROPERTYGET | DISPATCH_METHOD, args);

    public string GetString(string name, params object?[] args)
    {
        try { return InvokeCore(name, DISPATCH_PROPERTYGET | DISPATCH_METHOD, args)?.ToString() ?? ""; }
        catch { return ""; }
    }

    public int GetInt(string name, params object?[] args)
    {
        try
        {
            var v = InvokeCore(name, DISPATCH_PROPERTYGET | DISPATCH_METHOD, args);
            return v == null ? 0 : Convert.ToInt32(v);
        }
        catch { return 0; }
    }

    public bool GetBool(string name, params object?[] args)
    {
        try
        {
            var v = InvokeCore(name, DISPATCH_PROPERTYGET | DISPATCH_METHOD, args);
            return v != null && Convert.ToBoolean(v);
        }
        catch { return false; }
    }

    /// <summary>Method call, result discarded (or scalar returned).</summary>
    public object? Call(string name, params object?[] args)
        => InvokeCore(name, DISPATCH_METHOD, args);

    /// <summary>Property put.</summary>
    public void Set(string name, object? value)
        => InvokeCore(name, DISPATCH_PROPERTYPUT, new[] { value }, isPut: true);

    // ── Core ──────────────────────────────────────────────────────────────────

    private int DispId(string name)
    {
        var names = new[] { name };
        var ids = new int[1];
        int hr = _disp.GetIDsOfNames(ref _iidNull, names, 1, LOCALE_SYSTEM_DEFAULT, ids);
        if (hr < 0)
            throw new MissingMemberException($"SAP scripting object has no member '{name}' (0x{hr:X8}).");
        return ids[0];
    }

    private object? InvokeCore(string name, short flags, object?[] args, bool isPut = false)
    {
        int dispId = DispId(name);
        args ??= Array.Empty<object?>();

        IntPtr variantsPtr = IntPtr.Zero;
        IntPtr namedArgPtr = IntPtr.Zero;
        IntPtr resultPtr = IntPtr.Zero;
        try
        {
            var dp = new DISPPARAMS { cArgs = args.Length, cNamedArgs = 0 };

            if (args.Length > 0)
            {
                variantsPtr = Marshal.AllocCoTaskMem(VariantSize * args.Length);
                // COM expects arguments in reverse order.
                for (int i = 0; i < args.Length; i++)
                {
                    IntPtr slot = variantsPtr + VariantSize * i;
                    Marshal.GetNativeVariantForObject(args[args.Length - 1 - i], slot);
                }
                dp.rgvarg = variantsPtr;
            }

            if (isPut)
            {
                namedArgPtr = Marshal.AllocCoTaskMem(sizeof(int));
                Marshal.WriteInt32(namedArgPtr, DISPID_PROPERTYPUT);
                dp.rgdispidNamedArgs = namedArgPtr;
                dp.cNamedArgs = 1;
            }

            resultPtr = Marshal.AllocCoTaskMem(VariantSize);
            // zero the result VARIANT (vt = VT_EMPTY)
            for (int i = 0; i < VariantSize; i += 8) Marshal.WriteInt64(resultPtr + i, 0);

            int hr = _disp.Invoke(dispId, ref _iidNull, LOCALE_SYSTEM_DEFAULT, flags,
                ref dp, isPut ? IntPtr.Zero : resultPtr, IntPtr.Zero, IntPtr.Zero);

            if (hr < 0)
                throw Marshal.GetExceptionForHR(hr)
                    ?? new COMException($"SAP scripting Invoke('{name}') failed", hr);

            if (isPut) return null;

            object? result = Marshal.GetObjectForNativeVariant(resultPtr);
            return result;
        }
        finally
        {
            if (variantsPtr != IntPtr.Zero)
            {
                for (int i = 0; i < args.Length; i++)
                    VariantClear(variantsPtr + VariantSize * i);
                Marshal.FreeCoTaskMem(variantsPtr);
            }
            if (namedArgPtr != IntPtr.Zero) Marshal.FreeCoTaskMem(namedArgPtr);
            if (resultPtr != IntPtr.Zero)
            {
                VariantClear(resultPtr);
                Marshal.FreeCoTaskMem(resultPtr);
            }
        }
    }

    // ── Interop ───────────────────────────────────────────────────────────────

    // VARIANT is 16 bytes on x86, 24 on x64. DesktopDriverServer is x64-only
    // (see .csproj RuntimeIdentifier), so this is fixed.
    private const int VariantSize = 24;

    [DllImport("oleaut32.dll")]
    private static extern int VariantClear(IntPtr pvarg);

    [StructLayout(LayoutKind.Sequential)]
    private struct DISPPARAMS
    {
        public IntPtr rgvarg;
        public IntPtr rgdispidNamedArgs;
        public int cArgs;
        public int cNamedArgs;
    }

    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown),
     Guid("00020400-0000-0000-C000-000000000046")]
    private interface IDispatch
    {
        // Slots kept to preserve vtable order; GetTypeInfo* are never called.
        void GetTypeInfoCount(out uint pctinfo);
        void GetTypeInfo(uint iTInfo, int lcid, out IntPtr ppTInfo);

        [PreserveSig]
        int GetIDsOfNames(
            ref Guid riid,
            [In, MarshalAs(UnmanagedType.LPArray, ArraySubType = UnmanagedType.LPWStr)] string[] rgszNames,
            int cNames,
            int lcid,
            [Out, MarshalAs(UnmanagedType.LPArray)] int[] rgDispId);

        [PreserveSig]
        int Invoke(
            int dispIdMember,
            ref Guid riid,
            int lcid,
            short wFlags,
            ref DISPPARAMS pDispParams,
            IntPtr pVarResult,
            IntPtr pExcepInfo,
            IntPtr puArgErr);
    }
}
