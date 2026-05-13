using System;

namespace PrintSpoolerGuardian
{
    public enum PrinterConnectionType
    {
        Usb,
        Shared
    }

    public class PrinterConfig
    {
        public string Name { get; set; }
        public string PortName { get; set; }
        public PrinterConnectionType ConnectionType { get; set; }
        public bool IsUsb => ConnectionType == PrinterConnectionType.Usb;
        public bool Enabled { get; set; } = true;
    }

    public class PrintJobInfo
    {
        public string JobId { get; set; }
        public string PrinterName { get; set; }
        public string DocumentName { get; set; }
        public string Status { get; set; }
        public uint JobStatus { get; set; }
        public string Owner { get; set; }
        public DateTime? SubmittedAt { get; set; }
        public long Size { get; set; }
        public int TotalPages { get; set; }
        public int PagesPrinted { get; set; }

        public bool IsError => (JobStatus & 0x00000002) != 0;
        public bool IsPaperOut => (JobStatus & 0x00000010) != 0;
        public bool IsNotPrinted => (JobStatus & 0x00000020) != 0;
        public bool IsBlocked => (JobStatus & 0x00000100) != 0;
        public bool IsDeleting => (JobStatus & 0x00000400) != 0;
        public bool IsOffline => (JobStatus & 0x00001000) != 0;
        public bool IsPaperProblem => (JobStatus & 0x00002000) != 0;
        public bool IsOutputBinFull => (JobStatus & 0x00004000) != 0;
        public bool IsUserIntervention => (JobStatus & 0x00008000) != 0;
        public bool IsTimeout => (JobStatus & 0x00010000) != 0;
        public bool IsQueued => (JobStatus & 0x00020000) != 0;
        public bool IsPrinting => (JobStatus & 0x00040000) != 0;
        public bool IsProcessing => (JobStatus & 0x00080000) != 0;
        public bool IsInitializing => (JobStatus & 0x00100000) != 0;
        public bool IsWarmup => (JobStatus & 0x00200000) != 0;
        public bool IsTonerLow => (JobStatus & 0x00400000) != 0;
        public bool IsNoToner => (JobStatus & 0x00800000) != 0;
        public bool IsPagePunt => (JobStatus & 0x01000000) != 0;
        public bool IsServerUnknown => (JobStatus & 0x02000000) != 0;
        public bool IsPowerSave => (JobStatus & 0x04000000) != 0;

        public bool IsProblematic =>
            IsError || IsPaperOut || IsNotPrinted || IsBlocked ||
            IsPaperProblem || IsUserIntervention || IsTimeout || IsOffline;
    }

    public class MonitorStatus
    {
        public string Status { get; set; } = "Idle";
        public DateTime LastCheckTime { get; set; }
        public int ActiveProblemJobs { get; set; }
        public int CleanedStaleFiles { get; set; }
        public int RecoveriesThisHour { get; set; }
        public string LastRecoveryAction { get; set; }
        public DateTime? LastRecoveryTime { get; set; }
        public bool IsPaused { get; set; }
    }
}