using System;
using System.Collections.Generic;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace EmiliosSoundboard
{
    /// <summary>
    /// Lists the Windows playback devices and resolves the stored device name to the index the
    /// audio backend needs.
    /// </summary>
    /// <remarks>
    /// Uses the same waveOut family the player already uses, so the device list matches exactly what
    /// playback can address. The winmm names are truncated to 31 characters by Windows; that is what
    /// gets stored, and <see cref="AudioOutputPicker.ResolveIndex"/> copes with either direction of
    /// truncation.
    /// </remarks>
    internal static class AudioOutput
    {
        public sealed class Device
        {
            public int Index;
            public string Name;
            public override string ToString() { return Name; }
        }

        private static readonly object sync = new object();
        private static List<Device> cache;
        private static long cacheTicks;
        private static string cachedDefaultName;
        private static long cachedDefaultTicks;

        // Windows devices are plug-and-play, so a cached list must expire. Old builds cached the
        // list forever and could not see a USB headset or VB-CABLE installed after launch.
        private static readonly long DeviceCacheTtlTicks = TimeSpan.FromSeconds(8).Ticks;

        /// <summary>Playback devices, in the order the audio backend reports them.</summary>
        public static List<Device> Devices(bool refresh = false)
        {
            lock (sync)
            {
                long now = DateTime.UtcNow.Ticks;
                if (cache != null && !refresh &&
                    (now - cacheTicks < DeviceCacheTtlTicks || cache.Count == 0))
                    return cache;

                var list = new List<Device>();
                try
                {
                    int count = WaveOut.DeviceCount;
                    for (int i = 0; i < count; i++)
                    {
                        string name = "Output device " + (i + 1);
                        try
                        {
                            var caps = WaveOut.GetCapabilities(i);
                            if (!string.IsNullOrWhiteSpace(caps.ProductName)) name = caps.ProductName.Trim();
                        }
                        catch (Exception ex)
                        {
                            Log.Debug("audio", "could not read the name of output device " + i + ": " + ex.Message);
                        }
                        list.Add(new Device { Index = i, Name = name });
                    }

                    if (list.Count == 0)
                        Log.Warn("audio", "Windows reported no playback devices; sounds will fall back to the system default.");
                    else
                        Log.Debug("audio", list.Count + " playback device(s): " + string.Join(" | ", list.ConvertAll(d => d.Name).ToArray()));
                }
                catch (Exception ex)
                {
                    Log.Error("audio", "listing playback devices failed", ex);
                }

                cache = list;
                cacheTicks = now;
                if (list.Count == 0) cachedDefaultName = null;
                return cache;
            }
        }

        public static List<string> Names(bool refresh = false)
        {
            return Devices(refresh).ConvertAll(d => d.Name);
        }

        /// <summary>Device index for a stored name, or -1 for the system default.</summary>
        public static int Resolve(string storedName)
        {
            List<string> names = Names(false);
            int index = AudioOutputPicker.ResolveIndex(storedName, names);

            if (index < 0 && !string.IsNullOrWhiteSpace(storedName) &&
                !string.Equals(storedName.Trim(), AudioOutputPicker.SystemDefault, StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(storedName.Trim(), AudioOutputPicker.FollowBoard, StringComparison.OrdinalIgnoreCase))
            {
                // Stored device is gone (unplugged headset, renamed driver). Play through the default
                // instead of failing, but say so once so it shows up in a bug report.
                Log.Warn("audio", "output device \"" + storedName + "\" is not available; using the system default.");
            }

            return index;
        }

        /// <summary>
        /// True when the stored device is a real endpoint other than the Windows default, so a clip
        /// routed there also needs a second local playback to stay audible on the user's speakers.
        /// </summary>
        public static bool NeedsLocalMonitor(string storedName)
        {
            if (string.IsNullOrWhiteSpace(storedName)) return false;
            if (string.Equals(storedName.Trim(), AudioOutputPicker.SystemDefault, StringComparison.OrdinalIgnoreCase)) return false;

            int index = Resolve(storedName);
            if (index < 0) return false; // Missing/unresolvable device already falls back to the default.

            string selected = Describe(index);
            string windowsDefault = DefaultDeviceName();
            if (string.IsNullOrWhiteSpace(windowsDefault)) return true;
            return !NamesReferToSameDevice(selected, windowsDefault);
        }

        /// <summary>
        /// Friendly name of the current Windows default output, cached briefly because this is
        /// queried on every card press while local monitoring is enabled.
        /// </summary>
        public static string DefaultDeviceName()
        {
            lock (sync)
            {
                long now = DateTime.UtcNow.Ticks;
                if (cachedDefaultName != null && now - cachedDefaultTicks < DeviceCacheTtlTicks)
                    return cachedDefaultName;
            }

            string name = null;
            try
            {
                using (var enumerator = new MMDeviceEnumerator())
                using (var defaultOutput = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia))
                    name = defaultOutput.FriendlyName ?? "";
            }
            catch (Exception ex)
            {
                // The winmm list and the WASAPI endpoint list can disagree. Treat an unknown
                // default as "not the same device" so the local copy is never silently lost.
                Log.Debug("audio", "could not read the Windows default output name: " + ex.Message);
                return null;
            }

            lock (sync)
            {
                cachedDefaultName = name;
                cachedDefaultTicks = DateTime.UtcNow.Ticks;
            }
            return name;
        }

        /// <summary>Forces both the playback-device and default-endpoint caches to refresh.</summary>
        public static void Invalidate()
        {
            lock (sync)
            {
                cache = null;
                cachedDefaultName = null;
            }
        }

        /// <summary>
        /// Compares a winmm device name (truncated to 31 characters) with a full Windows endpoint
        /// name. Accepts either direction of truncation.
        /// </summary>
        private static bool NamesReferToSameDevice(string a, string b)
        {
            a = (a ?? "").Trim();
            b = (b ?? "").Trim();
            if (a.Length == 0 || b.Length == 0) return false;
            return string.Equals(a, b, StringComparison.OrdinalIgnoreCase) ||
                   (a.Length >= 24 && b.StartsWith(a, StringComparison.OrdinalIgnoreCase)) ||
                   (b.Length >= 24 && a.StartsWith(b, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>Name for an index, falling back to a readable description.</summary>
        public static string Describe(int index)
        {
            var devices = Devices(false);
            if (index >= 0 && index < devices.Count) return devices[index].Name;
            return AudioOutputPicker.SystemDefault;
        }

        /// <summary>Forces the next call to re-read the device list and default endpoint.</summary>
        public static void Refresh()
        {
            Invalidate();
            Devices(false);
        }
    }
}
