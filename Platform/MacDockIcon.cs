using System;
using System.IO;
using System.Runtime.InteropServices;
using Avalonia.Platform;

namespace SuperDev;

/// <summary>
/// Shows the app icon in the Dock while Super Dev is open.
/// The packaged app also carries the same icon in its bundle.
/// </summary>
internal static class MacDockIcon
{
    public static void Apply()
    {
        if (!OperatingSystem.IsMacOS())
            return;

        try
        {
            using var stream = AssetLoader.Open(new Uri("avares://Super Dev/Assets/AppIcon.icns"));
            using var memory = new MemoryStream();
            stream.CopyTo(memory);
            Show(memory.ToArray());
        }
        catch (Exception)
        {
            // A missing native image must not stop startup. The packaged
            // bundle still shows Assets/AppIcon.icns.
        }
    }

    public static void ApplyPng(byte[] png) => Show(png);

    private static void Show(byte[] bytes)
    {
        if (!OperatingSystem.IsMacOS() || bytes.Length == 0)
            return;

        try
        {

            var dataClass = objc_getClass("NSData");
            var imageClass = objc_getClass("NSImage");
            var appClass = objc_getClass("NSApplication");
            if (dataClass == 0 || imageClass == 0 || appClass == 0)
                return;

            var pinned = GCHandle.Alloc(bytes, GCHandleType.Pinned);
            try
            {
                var data = Send(
                    dataClass,
                    sel_registerName("dataWithBytes:length:"),
                    pinned.AddrOfPinnedObject(),
                    bytes.Length);
                if (data == 0)
                    return;

                var image = Send(imageClass, sel_registerName("alloc"));
                image = Send(image, sel_registerName("initWithData:"), data);
                if (image == 0)
                    return;

                var app = Send(appClass, sel_registerName("sharedApplication"));
                if (app == 0)
                    return;

                Send(app, sel_registerName("setApplicationIconImage:"), image);
            }
            finally
            {
                pinned.Free();
            }
        }
        catch (Exception)
        {
            // A missing native image must not stop startup.
        }
    }

    const string ObjC = "/usr/lib/libobjc.A.dylib";

    [DllImport(ObjC)]
    static extern nint objc_getClass(string name);

    [DllImport(ObjC)]
    static extern nint sel_registerName(string name);

    [DllImport(ObjC, EntryPoint = "objc_msgSend")]
    static extern nint Send(nint receiver, nint selector);

    [DllImport(ObjC, EntryPoint = "objc_msgSend")]
    static extern nint Send(nint receiver, nint selector, nint arg);

    [DllImport(ObjC, EntryPoint = "objc_msgSend")]
    static extern nint Send(nint receiver, nint selector, nint arg, nint arg2);
}
