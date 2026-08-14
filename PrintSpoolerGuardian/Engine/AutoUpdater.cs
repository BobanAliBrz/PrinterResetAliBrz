using System;

namespace PrintSpoolerGuardian
{
    /// <summary>
    /// Placeholder for the legacy Windows 7 build. Automatic downloads are disabled:
    /// GitHub now requires modern TLS and certificate support that cannot be assumed
    /// on unpatched Windows 7. Install newer releases with their installer instead.
    /// </summary>
    public class AutoUpdater
    {
        public bool IsEnabled { get { return false; } }
        public string LastCheckResult { get { return "Manual installer updates"; } }
        public DateTime LastCheckTime { get { return DateTime.MinValue; } }
        public void Start() { Logger.Info("Auto-update disabled for the Windows 7 inbox-runtime build."); }
        public void Stop() { }
    }
}
