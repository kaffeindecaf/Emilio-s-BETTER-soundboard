using System;
using System.Collections.Generic;
using System.IO;
using NAudio.Wave;

namespace EmiliosSoundboard
{
    public sealed class PlaybackEndedEventArgs : EventArgs
    {
        public string Id { get; private set; }
        public Exception Error { get; private set; }

        public PlaybackEndedEventArgs(string id, Exception error)
        {
            Id = id;
            Error = error;
        }
    }

    /// <summary>
    /// Plays one sound at a time, once from start to finish. Starting a new sound silently replaces
    /// the previous one. The Ended event fires only when a sound finishes by itself (or fails).
    /// </summary>
    /// <remarks>
    /// Playback is deliberately defensive: a named device can disappear, a driver can reject the
    /// requested format, and waveOut itself can be unavailable. The player tries the requested
    /// device, then the Windows default, then WASAPI shared mode, and finally a plain WAV reader.
    /// Small files are also cached in memory so repeated clicks do not depend on disk latency.
    /// </remarks>
    public sealed class AudioPlayer : IDisposable
    {
        private sealed class Session
        {
            public string Id;
            public IWavePlayer Output;
            public WaveStream Reader;
            public AudioFileReader FileReader;
            public EventHandler<StoppedEventArgs> Handler;
            public Func<long> GetPosition;
            public TimeSpan Total;
        }

        private readonly object sync = new object();
        private readonly AudioFileCache cache = new AudioFileCache();
        private Session current;
        private bool disposed;

        public event EventHandler<PlaybackEndedEventArgs> Ended;

        public bool IsPlaying { get { lock (sync) return current != null; } }

        /// <summary>True when the requested device failed and a default/WASAPI backend was used.</summary>
        public bool OutputFellBack { get; private set; }

        private float volume = 1f;
        public float Volume
        {
            get { return volume; }
            set
            {
                volume = Math.Max(0f, Math.Min(1f, value));
                Session s;
                lock (sync) s = current;
                if (s == null) return;

                try { if (s.FileReader != null) s.FileReader.Volume = volume; } catch { }
                try { if (s.Output != null) s.Output.Volume = volume; } catch { }
            }
        }

        public string CurrentId
        {
            get { lock (sync) return current == null ? null : current.Id; }
        }

        public TimeSpan Duration
        {
            get { lock (sync) return current == null ? TimeSpan.Zero : current.Total; }
        }

        public TimeSpan Position
        {
            get
            {
                Session s;
                lock (sync) s = current;
                if (s == null) return TimeSpan.Zero;

                try
                {
                    double bytesPerSecond = s.Reader.WaveFormat.AverageBytesPerSecond;
                    if (bytesPerSecond <= 0) return TimeSpan.Zero;
                    long position = s.GetPosition == null ? 0 : s.GetPosition();
                    var pos = TimeSpan.FromSeconds(Math.Max(0, position / bytesPerSecond));
                    return pos > s.Total ? s.Total : pos;
                }
                catch
                {
                    return TimeSpan.Zero;
                }
            }
        }

        /// <summary>Checks that a file can be opened as audio and returns its length.</summary>
        public static bool TryProbe(string path, out TimeSpan duration, out string error)
        {
            duration = TimeSpan.Zero;
            error = null;
            try
            {
                var info = new FileInfo(path);
                if (!info.Exists) { error = "The file could not be found."; return false; }
                if (info.Length == 0) { error = "The file is empty."; return false; }

                try
                {
                    using (var reader = new AudioFileReader(path))
                        duration = reader.TotalTime;
                }
                catch when (string.Equals(Path.GetExtension(path), ".wav", StringComparison.OrdinalIgnoreCase))
                {
                    // AudioFileReader normally handles WAV, but a malformed/streaming WAV header
                    // can make its duration calculation throw. The raw reader still plays it.
                    using (var reader = new WaveFileReader(path))
                        duration = reader.TotalTime;
                }

                if (duration <= TimeSpan.Zero) { error = "The file does not contain any audio."; return false; }
                return true;
            }
            catch (Exception ex)
            {
                error = "It is not a supported audio file, or it is damaged. (" + ex.Message + ")";
                return false;
            }
        }

        /// <summary>
        /// Starts playing from the beginning on the named output device (empty = system default).
        /// Throws if every available backend fails.
        /// </summary>
        public void Play(string id, string path, string deviceName = null)
        {
            if (disposed) return;
            StopSilently();
            OutputFellBack = false;

            int deviceIndex = AudioOutput.Resolve(deviceName);
            string deviceLabel = AudioOutput.Describe(deviceIndex);
            Exception lastError = null;
            Session session;

            // 1. The device the user chose (or the system default when no device was chosen).
            if (TryStart(id, path, deviceIndex, false, out session, out lastError))
            {
                Commit(session, path, deviceLabel);
                return;
            }

            // 2. The Windows default if a named endpoint refused to open.
            if (deviceIndex != AudioOutputPicker.DefaultIndex)
            {
                Log.Warn("audio", "could not open output device \"" + deviceLabel + "\"; retrying on the system default", lastError);
                AudioOutput.Invalidate();
                if (TryStart(id, path, AudioOutputPicker.DefaultIndex, false, out session, out lastError))
                {
                    OutputFellBack = true;
                    Commit(session, path, AudioOutputPicker.SystemDefault);
                    return;
                }
            }

            // 3. WASAPI shared mode. This exists on modern Windows even when the legacy waveOut
            //    mapper/driver is missing or broken.
            Log.Warn("audio", "waveOut could not start playback; falling back to WASAPI", lastError);
            if (TryStart(id, path, AudioOutputPicker.DefaultIndex, true, out session, out lastError))
            {
                OutputFellBack = true;
                Commit(session, path, AudioOutputPicker.SystemDefault + " (WASAPI)");
                return;
            }

            Log.Error("audio", "playing \"" + Path.GetFileName(path) + "\" failed on every output backend", lastError);
            if (lastError != null)
                throw new InvalidOperationException("Could not play this sound: " + lastError.Message, lastError);
            throw new InvalidOperationException("Could not play this sound: no output device is available.");
        }

        private void Commit(Session session, string path, string deviceLabel)
        {
            Log.Debug("audio", "playing \"" + Path.GetFileName(path) + "\" on " + deviceLabel +
                               " [" + session.Reader.GetType().Name + "]");
        }

        private bool TryStart(string id, string path, int deviceIndex, bool wasapi,
                              out Session session, out Exception error)
        {
            session = null;
            error = null;

            WaveStream reader = null;
            AudioFileReader fileReader = null;
            IWavePlayer output = null;
            try
            {
                reader = OpenReader(path, out fileReader);

                if (wasapi)
                {
                    // Parameterless WasapiOut uses the default render endpoint in shared mode.
                    output = new WasapiOut();
                }
                else
                {
                    output = new WaveOutEvent { DeviceNumber = deviceIndex };
                }

                if (fileReader != null) fileReader.Volume = volume;

                var created = new Session
                {
                    Id = id,
                    Output = output,
                    Reader = reader,
                    FileReader = fileReader,
                    Total = reader.TotalTime
                };
                created.Handler = (sender, e) => OnStopped(created, e);
                created.GetPosition = CreatePositionReader(output);
                output.PlaybackStopped += created.Handler;
                output.Init(reader);
                // Some backends reject a volume change until Initialize has completed. A
                // failed volume change is cosmetic, so never let it abort playback.
                try { output.Volume = volume; } catch { }

                // Publish the session before Play so a very short clip cannot finish before the
                // End event knows which session it belongs to.
                lock (sync) current = created;
                try
                {
                    output.Play();
                }
                catch
                {
                    lock (sync) if (ReferenceEquals(current, created)) current = null;
                    throw;
                }

                session = created;
                return true;
            }
            catch (Exception ex)
            {
                error = ex;
                if (output != null)
                {
                    try { if (session != null) output.PlaybackStopped -= session.Handler; } catch { }
                    try { output.Dispose(); } catch { }
                }
                if (reader != null) { try { reader.Dispose(); } catch { } }
                return false;
            }
        }

        private static Func<long> CreatePositionReader(IWavePlayer output)
        {
            var waveOut = output as WaveOutEvent;
            if (waveOut != null) return () => waveOut.GetPosition();
            var wasapi = output as WasapiOut;
            if (wasapi != null) return () => wasapi.GetPosition();
            return null;
        }

        private WaveStream OpenReader(string path, out AudioFileReader fileReader)
        {
            fileReader = null;

            // Reuse a cached byte buffer for small clips. This removes disk I/O from the click
            // path and also avoids holding a lock on the user's original/imported file. NAudio's
            // AudioFileReader only accepts a path, so stream-based readers are used for the
            // formats that support them; everything else quietly falls back to the path.
            byte[] cached = cache.TryGet(path);
            if (cached != null)
            {
                try
                {
                    WaveStream cachedReader = TryOpenCachedReader(path, cached);
                    if (cachedReader != null) return cachedReader;
                }
                catch (Exception ex)
                {
                    Log.Debug("audio", "in-memory reader unavailable for \"" + Path.GetFileName(path) +
                                       "\"; using the file path: " + ex.Message);
                }
            }

            try
            {
                fileReader = new AudioFileReader(path);
                return fileReader;
            }
            catch (Exception ex)
            {
                if (string.Equals(Path.GetExtension(path), ".wav", StringComparison.OrdinalIgnoreCase))
                {
                    Log.Warn("audio", "AudioFileReader could not open \"" + Path.GetFileName(path) +
                                      "\"; trying the raw WAV reader", ex);
                    return new WaveFileReader(path);
                }
                throw;
            }
        }

        private static WaveStream TryOpenCachedReader(string path, byte[] bytes)
        {
            string extension = Path.GetExtension(path) ?? "";
            if (string.Equals(extension, ".wav", StringComparison.OrdinalIgnoreCase))
                return new WaveFileReader(new MemoryStream(bytes, false));
            if (string.Equals(extension, ".mp3", StringComparison.OrdinalIgnoreCase))
                return new Mp3FileReader(new MemoryStream(bytes, false));
            return null;
        }

        /// <summary>Stops the current sound without raising Ended.</summary>
        public void Stop()
        {
            StopSilently();
        }

        private void StopSilently()
        {
            Session s;
            lock (sync)
            {
                s = current;
                current = null;
            }
            if (s != null) DisposeSession(s);
        }

        private void OnStopped(Session session, StoppedEventArgs e)
        {
            // Ignore late events from sessions that were already replaced or stopped.
            lock (sync)
            {
                if (!ReferenceEquals(current, session)) return;
                current = null;
            }

            string id = session.Id;
            if (e.Exception != null)
                Log.Error("audio", "playback of \"" + id + "\" stopped with an error", e.Exception);

            DisposeSession(session);

            var handler = Ended;
            if (handler != null) handler(this, new PlaybackEndedEventArgs(id, e.Exception));
        }

        private static void DisposeSession(Session s)
        {
            if (s == null) return;
            try { if (s.Output != null && s.Handler != null) s.Output.PlaybackStopped -= s.Handler; } catch { }
            try { if (s.Output != null) s.Output.Dispose(); } catch { }
            try { if (s.Reader != null) s.Reader.Dispose(); } catch { }
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            StopSilently();
            cache.Clear();
        }
    }

    /// <summary>
    /// A tiny bounded LRU cache for short sound files. It is intentionally conservative: large
    /// files stay on disk, and any read failure simply falls back to the normal reader.
    /// </summary>
    internal sealed class AudioFileCache
    {
        private sealed class Entry
        {
            public byte[] Bytes;
            public long WriteTicks;
            public long LastUsed;
        }

        private const long MaxFileBytes = 8L * 1024 * 1024;
        private const long MaxTotalBytes = 64L * 1024 * 1024;

        private readonly object sync = new object();
        private readonly Dictionary<string, Entry> entries = new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);
        private long totalBytes;
        private long clock;

        public byte[] TryGet(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return null;

            FileInfo info;
            try
            {
                info = new FileInfo(path);
                if (!info.Exists || info.Length <= 0 || info.Length > MaxFileBytes) return null;
            }
            catch
            {
                return null;
            }

            string key = path;
            lock (sync)
            {
                Entry existing;
                if (entries.TryGetValue(key, out existing))
                {
                    if (existing.WriteTicks == info.LastWriteTimeUtc.Ticks)
                    {
                        existing.LastUsed = ++clock;
                        return existing.Bytes;
                    }
                    totalBytes -= existing.Bytes.Length;
                    entries.Remove(key);
                }
            }

            byte[] bytes;
            try
            {
                bytes = ReadAllBytesShared(path, (int)info.Length);
            }
            catch
            {
                return null;
            }

            lock (sync)
            {
                Entry entry;
                if (entries.TryGetValue(key, out entry))
                {
                    entry.Bytes = bytes;
                    entry.WriteTicks = info.LastWriteTimeUtc.Ticks;
                    entry.LastUsed = ++clock;
                }
                else
                {
                    entries[key] = new Entry
                    {
                        Bytes = bytes,
                        WriteTicks = info.LastWriteTimeUtc.Ticks,
                        LastUsed = ++clock
                    };
                    totalBytes += bytes.Length;
                }

                while (totalBytes > MaxTotalBytes && entries.Count > 1)
                {
                    string oldestKey = null;
                    long oldestUse = long.MaxValue;
                    foreach (var pair in entries)
                    {
                        if (pair.Value.LastUsed < oldestUse)
                        {
                            oldestUse = pair.Value.LastUsed;
                            oldestKey = pair.Key;
                        }
                    }
                    if (oldestKey == null) break;
                    totalBytes -= entries[oldestKey].Bytes.Length;
                    entries.Remove(oldestKey);
                }
            }

            return bytes;
        }

        private static byte[] ReadAllBytesShared(string path, int length)
        {
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                var bytes = new byte[length];
                int offset = 0;
                while (offset < bytes.Length)
                {
                    int read = fs.Read(bytes, offset, bytes.Length - offset);
                    if (read <= 0) break;
                    offset += read;
                }
                if (offset == bytes.Length) return bytes;
                if (offset == 0) return new byte[0];
                var shorter = new byte[offset];
                Array.Copy(bytes, shorter, offset);
                return shorter;
            }
        }

        public void Clear()
        {
            lock (sync)
            {
                entries.Clear();
                totalBytes = 0;
            }
        }
    }
}
