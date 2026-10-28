using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml;
using System.Xml.Serialization;

namespace EmiliosSoundboard
{
    /// <summary>
    /// Owns the %LOCALAPPDATA%\EmiliosSoundboard folder: loading/saving the library and copying
    /// audio and images in and out. Only files inside the app's own folders are ever deleted.
    /// </summary>
    /// <remarks>
    /// Deliberately free of System.Drawing so it can live in the cross-platform core assembly and
    /// be unit-tested on any OS. Image decoding is supplied by the front end through
    /// <see cref="ImageImporter"/>.
    /// </remarks>
    public sealed class DataStore
    {
        public string Root { get; private set; }
        public string SoundsDir { get; private set; }
        public string ImagesDir { get; private set; }
        public string ConfigDir { get; private set; }
        public string ConfigPath { get; private set; }

        /// <summary>The previous good library file, written by <see cref="Save"/>.</summary>
        public string BackupPath { get { return ConfigPath + ".bak"; } }

        public LibraryData Data { get; private set; }

        private static readonly XmlSerializer librarySerializer = new XmlSerializer(typeof(LibraryData));
        private static readonly object serializerSync = new object();
        private readonly object saveSync = new object();

        /// <summary>Set when the saved library was unreadable and a fresh one was started.</summary>
        public string LoadWarning { get; private set; }

        public string LastSaveError { get; private set; }

        /// <summary>True when the normal LocalAppData folder was unusable and a temp folder was used.</summary>
        public bool UsingFallbackFolder { get; private set; }

        /// <summary>Why the fallback folder was needed; empty when the normal folder is in use.</summary>
        public string FallbackReason { get; private set; }

        /// <summary>
        /// Optional image pipeline. When null, imported images are copied through unchanged
        /// instead of being re-encoded and downscaled.
        /// </summary>
        public IImageImporter ImageImporter { get; set; }

        public DataStore()
            : this(DefaultRoot())
        {
        }

        private static string DefaultRoot()
        {
            string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (string.IsNullOrWhiteSpace(local)) local = Path.GetTempPath();
            return Path.Combine(local, "EmiliosSoundboard");
        }

        /// <summary>Creates a store rooted at an explicit folder. Used by tests.</summary>
        public DataStore(string root)
        {
            if (string.IsNullOrWhiteSpace(root))
                throw new ArgumentException("A storage folder is required.", "root");

            SetRoot(root);
            try
            {
                EnsureFolders();
            }
            catch (Exception ex)
            {
                // A locked/read-only profile or a redirected OneDrive folder must not stop the
                // app from opening. Fall back to the user's temp folder and explain why.
                string fallback = Path.Combine(Path.GetTempPath(), "EmiliosSoundboard");
                SetRoot(fallback);
                try
                {
                    EnsureFolders();
                    UsingFallbackFolder = true;
                    FallbackReason = "The normal data folder could not be created (" + ex.Message +
                                     "), so this session uses:\n" + fallback;
                    Log.Error("storage", "data folder unavailable; using " + fallback, ex);
                }
                catch
                {
                    // If even temp is unusable there is nothing safe left to try.
                    throw;
                }
            }

            Data = new LibraryData();
        }

        private void SetRoot(string root)
        {
            Root = root;
            SoundsDir = Path.Combine(Root, "Sounds");
            ImagesDir = Path.Combine(Root, "Backgrounds");
            ConfigDir = Path.Combine(Root, "Config");
            ConfigPath = Path.Combine(ConfigDir, "library.xml");
        }

        private void EnsureFolders()
        {
            Directory.CreateDirectory(SoundsDir);
            Directory.CreateDirectory(ImagesDir);
            Directory.CreateDirectory(ConfigDir);
        }

        // ------------------------------------------------------------------ load / save

        public void Load()
        {
            LoadWarning = null;
            LibraryData loaded = null;
            Exception primaryError = null;

            if (File.Exists(ConfigPath))
            {
                try
                {
                    loaded = ReadLibrary(ConfigPath);
                }
                catch (Exception ex)
                {
                    primaryError = ex;
                    Log.Error("storage", "library.xml could not be read", ex);
                }
            }

            // Recover from the last good copy before giving up. The old code only kept a
            // timestamped damaged file, which turned a single write glitch into an empty board.
            bool recovered = false;
            if (loaded == null && File.Exists(BackupPath))
            {
                try
                {
                    loaded = ReadLibrary(BackupPath);
                    recovered = loaded != null;
                }
                catch (Exception ex)
                {
                    Log.Error("storage", "library.xml.bak could not be read either", ex);
                }
            }

            if (loaded == null && primaryError != null)
            {
                string backup = ArchiveCorruptCopy();
                LoadWarning = "Your saved sound list could not be read, so the app started with an empty list." +
                              (backup != null ? "\n\nA copy of the damaged file was kept here:\n" + backup : "") +
                              "\n\nYour imported audio files are still in:\n" + SoundsDir;

                Log.Error("storage", "library.xml could not be read" +
                                     (backup != null ? "; damaged copy kept at " + backup : ""), null);
            }
            else if (recovered)
            {
                bool mainWasPresent = File.Exists(ConfigPath);
                string backup = mainWasPresent ? ArchiveCorruptCopy() : null;
                LoadWarning = (mainWasPresent
                                  ? "The main sound list was damaged, so the last good backup was loaded."
                                  : "The main sound list was missing, so the last good backup was loaded.") +
                              (backup != null ? "\n\nA copy of the damaged file was kept here:\n" + backup : "") +
                              "\n\nCheck your sounds, then save once to repair the main file.";

                Log.Warn("storage", (mainWasPresent ? "library.xml was damaged" : "library.xml was missing") +
                                    "; recovered the last good backup" +
                                    (backup != null ? " (damaged copy: " + backup + ")" : ""));
            }

            Data = loaded ?? new LibraryData();
            Sanitize();
        }

        private static LibraryData ReadLibrary(string path)
        {
            // DTDs are never part of this format. Refusing them avoids external-entity tricks
            // if a library file is ever copied from somewhere untrusted.
            var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null };
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            using (var reader = XmlReader.Create(fs, settings))
            {
                lock (serializerSync)
                {
                    return librarySerializer.Deserialize(reader) as LibraryData;
                }
            }
        }

        private string ArchiveCorruptCopy()
        {
            try
            {
                string backup = Path.Combine(ConfigDir, "library.corrupt-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".xml");
                File.Copy(ConfigPath, backup, true);
                return backup;
            }
            catch
            {
                return null;
            }
        }

        private void Sanitize()
        {
            if (Data.Settings == null) Data.Settings = new AppSettings();
            Data.Settings.Intensity = Math.Max(0, Math.Min(100, Data.Settings.Intensity));
            if (string.IsNullOrWhiteSpace(Data.Settings.WindowResolution)) Data.Settings.WindowResolution = "Unlocked";
            if (Data.Settings.SoundOutputDeviceId == null) Data.Settings.SoundOutputDeviceId = "";
            if (Data.Sounds == null) Data.Sounds = new List<SoundItem>();

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var clean = new List<SoundItem>();

            foreach (var s in Data.Sounds)
            {
                if (s == null) continue;

                if (string.IsNullOrWhiteSpace(s.Id) || !seen.Add(s.Id))
                {
                    s.Id = Guid.NewGuid().ToString("N");
                    seen.Add(s.Id);
                }

                s.Name = string.IsNullOrWhiteSpace(s.Name) ? "Untitled" : s.Name.Trim();
                s.AudioFile = SafeFileName(s.AudioFile);
                s.ImageFile = SafeFileName(s.ImageFile);

                if (s.AudioFile.Length == 0)
                    continue; // nothing to play and nothing to repair

                s.AudioMissing = !File.Exists(Path.Combine(SoundsDir, s.AudioFile));

                if (s.ImageFile.Length > 0 && !File.Exists(Path.Combine(ImagesDir, s.ImageFile)))
                    s.ImageFile = "";

                clean.Add(s);
            }

            Data.Sounds = clean;
        }

        private static string SafeFileName(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return "";
            string name = Path.GetFileName(value.Trim());
            return name ?? "";
        }

        public bool Save()
        {
            lock (saveSync)
            {
                LastSaveError = null;
                string tmp = ConfigPath + ".tmp";

                for (int attempt = 0; attempt < 2; attempt++)
                {
                    try
                    {
                        Directory.CreateDirectory(ConfigDir);

                        var settings = new XmlWriterSettings { Indent = true, Encoding = new System.Text.UTF8Encoding(false) };
                        using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
                        using (var writer = XmlWriter.Create(fs, settings))
                        {
                            lock (serializerSync)
                            {
                                librarySerializer.Serialize(writer, Data);
                            }
                        }

                        if (File.Exists(ConfigPath))
                        {
                            try
                            {
                                File.Replace(tmp, ConfigPath, BackupPath);
                            }
                            catch (Exception replaceError) when (
                                replaceError is IOException || replaceError is UnauthorizedAccessException ||
                                replaceError is PlatformNotSupportedException || replaceError is NotSupportedException)
                            {
                                // Some sync folders, FAT volumes and lock-down policies reject
                                // File.Replace even though ordinary file moves work. Keep the same
                                // recovery guarantee with a copy + move fallback.
                                Log.Warn("storage", "atomic replace was unavailable; using a copy fallback", replaceError);
                                File.Copy(ConfigPath, BackupPath, true);
                                File.Delete(ConfigPath);
                                File.Move(tmp, ConfigPath);
                            }
                        }
                        else
                        {
                            File.Move(tmp, ConfigPath);
                        }

                        return true;
                    }
                    catch (Exception ex)
                    {
                        LastSaveError = ex.Message;
                        if (attempt == 0 && (ex is IOException || ex is UnauthorizedAccessException))
                        {
                            // A virus scanner or cloud-sync client can hold the file for a moment.
                            System.Threading.Thread.Sleep(120);
                            continue;
                        }

                        Log.Error("storage", "saving library.xml failed: " + ex.Message, ex);
                        return false;
                    }
                    finally
                    {
                        try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
                    }
                }

                return false;
            }
        }

        // ------------------------------------------------------------------ paths and names

        public string AudioPath(SoundItem item)
        {
            return Path.Combine(SoundsDir, SafeFileName(item.AudioFile));
        }

        public string ImagePath(SoundItem item)
        {
            if (string.IsNullOrEmpty(item.ImageFile)) return null;
            return Path.Combine(ImagesDir, SafeFileName(item.ImageFile));
        }

        /// <summary>Returns a name that no other sound uses (adds " (2)", " (3)"... if needed).</summary>
        public string UniqueName(string desired, SoundItem exclude)
        {
            string baseName = string.IsNullOrWhiteSpace(desired) ? "Untitled" : desired.Trim();
            if (baseName.Length > 60) baseName = baseName.Substring(0, 60).Trim();

            string candidate = baseName;
            int n = 2;
            while (Data.Sounds.Any(s => !ReferenceEquals(s, exclude) &&
                                        string.Equals(s.Name, candidate, StringComparison.OrdinalIgnoreCase)))
            {
                candidate = baseName + " (" + n + ")";
                n++;
            }
            return candidate;
        }

        // ------------------------------------------------------------------ importing

        /// <summary>Copies the audio (and optional image) into the app folder. Safe to call from a background thread.</summary>
        public ImportResult ImportNew(string audioSource, string name, string imageSource)
        {
            var result = new ImportResult();
            var item = new SoundItem { Name = name };

            string ext = Path.GetExtension(audioSource);
            ext = string.IsNullOrEmpty(ext) ? ".audio" : ext.ToLowerInvariant();
            string audioName = Guid.NewGuid().ToString("N") + ext;
            File.Copy(audioSource, Path.Combine(SoundsDir, audioName), false);
            item.AudioFile = audioName;

            if (!string.IsNullOrEmpty(imageSource))
            {
                try
                {
                    item.ImageFile = ImportImage(imageSource);
                }
                catch (Exception ex)
                {
                    item.ImageFile = "";
                    result.Warning = "The sound was added, but the background image could not be used: " + ex.Message;
                }
            }

            result.Item = item;
            return result;
        }

        /// <summary>
        /// Copies an image into the Backgrounds folder and returns the new file name.
        /// The front end's <see cref="IImageImporter"/> does the decoding/resizing; without one the
        /// file is copied through untouched so the library stays usable.
        /// </summary>
        public string ImportImage(string sourcePath)
        {
            if (ImageImporter != null)
                return ImageImporter.Import(sourcePath, ImagesDir);

            string ext = Path.GetExtension(sourcePath);
            if (string.IsNullOrEmpty(ext)) ext = ".img";
            string passthrough = NewImageName(ext);
            File.Copy(sourcePath, Path.Combine(ImagesDir, passthrough), false);
            return passthrough;
        }

        /// <summary>Builds a fresh, collision-free name for a stored background image.</summary>
        public static string NewImageName(string extension)
        {
            string ext = string.IsNullOrWhiteSpace(extension) ? ".png" : extension.ToLowerInvariant();
            if (!ext.StartsWith(".")) ext = "." + ext;
            return "bg_" + Guid.NewGuid().ToString("N") + ext;
        }

        // ------------------------------------------------------------------ deleting

        /// <summary>Removes the sound from the library and deletes only its own managed files.</summary>
        /// <returns>true if every file was removed (or was already gone).</returns>
        public bool RemoveSound(SoundItem item)
        {
            Data.Sounds.Remove(item);
            bool ok = DeleteManagedFile(SoundsDir, item.AudioFile, true);
            ok &= DeleteImageFile(item.ImageFile);
            return ok;
        }

        public bool DeleteImageFile(string fileName)
        {
            return DeleteManagedFile(ImagesDir, fileName, false);
        }

        private bool DeleteManagedFile(string dir, string fileName, bool isAudio)
        {
            if (string.IsNullOrWhiteSpace(fileName)) return true;

            // Must be a bare file name; anything with folders is refused.
            if (!string.Equals(Path.GetFileName(fileName), fileName, StringComparison.Ordinal)) return false;

            // Never delete a file another sound still uses.
            bool stillUsed = Data.Sounds.Any(s => string.Equals(
                isAudio ? s.AudioFile : s.ImageFile, fileName, StringComparison.OrdinalIgnoreCase));
            if (stillUsed) return true;

            try
            {
                string dirFull = Path.GetFullPath(dir).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
                string full = Path.GetFullPath(Path.Combine(dir, fileName));
                if (!full.StartsWith(dirFull, StringComparison.OrdinalIgnoreCase)) return false;

                if (File.Exists(full)) File.Delete(full);
                return true;
            }
            catch
            {
                return false;
            }
        }

        // ------------------------------------------------------------------ info

        public long GetStorageBytes()
        {
            return DirSize(SoundsDir) + DirSize(ImagesDir);
        }

        private static long DirSize(string dir)
        {
            try
            {
                return new DirectoryInfo(dir).EnumerateFiles().Sum(f => f.Length);
            }
            catch
            {
                return 0;
            }
        }

        public static string FormatBytes(long bytes)
        {
            if (bytes < 1024) return bytes + " B";
            if (bytes < 1024 * 1024) return (bytes / 1024.0).ToString("0.#") + " KB";
            if (bytes < 1024L * 1024 * 1024) return (bytes / (1024.0 * 1024.0)).ToString("0.#") + " MB";
            return (bytes / (1024.0 * 1024.0 * 1024.0)).ToString("0.##") + " GB";
        }
    }
}
