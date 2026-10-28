using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace EmiliosSoundboard
{
    /// <summary>
    /// Optional Rust renderer for the animated starfield. It is probed once; if the DLL is
    /// missing, the wrong architecture, or returns an error, <see cref="SpaceField"/> keeps
    /// using the managed GDI+ path for the rest of the session.
    /// </summary>
    internal static class NativeStarRenderer
    {
        [StructLayout(LayoutKind.Sequential)]
        internal struct Star
        {
            public float X;
            public float Y;
            public float Radius;
            public float R;
            public float G;
            public float B;
            public float Alpha;
            public float Halo;
        }

        private const string Library = "emilios_native";
        private static int state; // 0 = unprobed, 1 = available, 2 = unavailable
        private static bool loggedFailure;

        [DllImport(Library, CallingConvention = CallingConvention.Cdecl, EntryPoint = "es_native_version")]
        private static extern uint NativeVersion();

        [DllImport(Library, CallingConvention = CallingConvention.Cdecl, EntryPoint = "es_render_starfield")]
        private static extern int NativeRenderStarfield(IntPtr pixels, int width, int height,
                                                        int stridePixels, IntPtr stars, int count);

        public static bool IsAvailable
        {
            get
            {
                if (state == 0) Probe();
                return state == 1;
            }
        }

        private static void Probe()
        {
            if (string.Equals(Environment.GetEnvironmentVariable("EMILIOS_DISABLE_NATIVE"), "1",
                              StringComparison.OrdinalIgnoreCase))
            {
                state = 2;
                Log.Info("native", "native acceleration disabled by EMILIOS_DISABLE_NATIVE");
                return;
            }

            try
            {
                state = NativeVersion() >= 1 ? 1 : 2;
                if (state == 1) Log.Debug("native", "Rust starfield renderer loaded");
            }
            catch (Exception ex)
            {
                state = 2;
                if (!loggedFailure)
                {
                    loggedFailure = true;
                    Log.Info("native", "Rust starfield renderer unavailable; using the managed fallback: " + ex.Message);
                }
            }
        }

        /// <summary>
        /// Renders the commands into a 32bpp premultiplied ARGB bitmap. Returns false on any
        /// native/argument failure so the caller can draw the managed version instead.
        /// </summary>
        public static bool TryRender(Bitmap target, Star[] stars, int count)
        {
            if (!IsAvailable || target == null || stars == null || count <= 0 || count > stars.Length)
                return false;

            BitmapData data = null;
            GCHandle pinned = default(GCHandle);
            try
            {
                data = target.LockBits(new Rectangle(0, 0, target.Width, target.Height),
                                       ImageLockMode.WriteOnly, PixelFormat.Format32bppPArgb);
                pinned = GCHandle.Alloc(stars, GCHandleType.Pinned);
                int result = NativeRenderStarfield(data.Scan0, target.Width, target.Height,
                                                   data.Stride / 4, pinned.AddrOfPinnedObject(), count);
                if (result != 0)
                {
                    MarkUnavailable("native renderer returned error " + result);
                    return false;
                }
                return true;
            }
            catch (Exception ex)
            {
                MarkUnavailable(ex.Message);
                return false;
            }
            finally
            {
                if (pinned.IsAllocated) pinned.Free();
                if (data != null)
                {
                    try { target.UnlockBits(data); } catch { }
                }
            }
        }

        private static void MarkUnavailable(string reason)
        {
            state = 2;
            if (!loggedFailure)
            {
                loggedFailure = true;
                Log.Warn("native", "Rust starfield renderer stopped; falling back to managed drawing: " + reason);
            }
        }
    }
}
