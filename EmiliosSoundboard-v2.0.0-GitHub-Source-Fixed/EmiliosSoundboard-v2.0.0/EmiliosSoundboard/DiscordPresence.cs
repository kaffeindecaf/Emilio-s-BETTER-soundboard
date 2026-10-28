using System;
using DiscordRPC;

namespace EmiliosSoundboard
{
    /// <summary>Best-effort Discord Rich Presence. It does not require or store a user token.</summary>
    /// <remarks>
    /// Discord being closed is normal, so a failure must never affect the soundboard. The client
    /// is also retried on a cooldown, which means starting Discord after the app connects works
    /// without restarting the soundboard.
    /// </remarks>
    internal sealed class DiscordPresence : IDisposable
    {
        private const string ApplicationId = "1564710814374436884";
        private const double RetrySeconds = 30.0;

        private DiscordRpcClient client;
        private bool connected;
        private bool disposed;
        private DateTime nextRetryUtc = DateTime.MinValue;
        private bool loggedFirstFailure;

        public DiscordPresence()
        {
            TryConnect();
            if (connected) SetPresence(false);
        }

        public void Update(bool coolMode)
        {
            if (disposed) return;
            if (!connected)
            {
                TryConnect();
                if (!connected) return;
            }
            SetPresence(coolMode);
        }

        /// <summary>Cheap periodic retry hook, called from the UI tick.</summary>
        public void Tick()
        {
            if (disposed || connected) return;
            if (DateTime.UtcNow < nextRetryUtc) return;
            TryConnect();
            if (connected) SetPresence(false);
        }

        private void TryConnect()
        {
            if (disposed || connected) return;

            try
            {
                if (client != null) { try { client.Dispose(); } catch { } }
                client = new DiscordRpcClient(ApplicationId);
                client.Initialize();
                connected = client.IsInitialized;
                if (connected)
                {
                    loggedFirstFailure = false;
                    Log.Debug("discord", "rich presence connected");
                }
                else
                {
                    nextRetryUtc = DateTime.UtcNow.AddSeconds(RetrySeconds);
                }
            }
            catch (Exception ex)
            {
                connected = false;
                if (client != null) { try { client.Dispose(); } catch { } }
                client = null;
                nextRetryUtc = DateTime.UtcNow.AddSeconds(RetrySeconds);
                if (!loggedFirstFailure)
                {
                    loggedFirstFailure = true;
                    Log.Info("discord", "rich presence unavailable (is the Discord desktop app running?)");
                }
                else
                {
                    Log.Debug("discord", "rich presence retry failed: " + ex.Message);
                }
            }
        }

        private void SetPresence(bool coolMode)
        {
            try
            {
                if (client == null || !client.IsInitialized) { connected = false; return; }
                client.SetPresence(new RichPresence
                {
                    Details = coolMode ? "Exploring Rainbow Road" : "Using the soundboard",
                    State = "Emilio's Soundboard",
                    Timestamps = Timestamps.Now
                });
            }
            catch (Exception ex)
            {
                // Discord may be closed or Rich Presence may be disabled in its settings.
                Log.Debug("discord", "presence update failed: " + ex.Message);
                connected = false;
                if (client != null) { try { client.Dispose(); } catch { } }
                client = null;
                nextRetryUtc = DateTime.UtcNow.AddSeconds(RetrySeconds);
            }
        }

        public void Dispose()
        {
            disposed = true;
            connected = false;
            try { client?.ClearPresence(); } catch { }
            try { client?.Dispose(); } catch { }
            client = null;
        }
    }
}
