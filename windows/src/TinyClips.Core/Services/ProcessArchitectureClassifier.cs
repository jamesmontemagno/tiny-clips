// This dependency-free source is also compiled by Collect-Diagnostics.ps1 on Windows PowerShell.
namespace TinyClips.Core.Services
{
    public static class ProcessArchitectureClassifier
    {
        public static string Machine(ushort machine)
        {
            switch (machine)
            {
                case 0x014c: return "x86";
                case 0x8664: return "x64";
                case 0xAA64: return "ARM64";
                case 0x01c4: return "ARM";
                case 0xA641: return "ARM64EC";
                case 0xA64E: return "ARM64X";
                default: return "unknown";
            }
        }

        public static string Classify(ushort nativeMachine, ushort wowMachine, ushort? processMachine)
        {
            if (Machine(nativeMachine) == "unknown") { return "unknown"; }
            var target = processMachine ?? (wowMachine == 0 ? (ushort?)null : wowMachine);
            if (!target.HasValue || Machine(target.Value) == "unknown") { return "unknown"; }
            if (target == 0xA641 || target == 0xA64E) { return "hybrid"; }
            if (processMachine.HasValue && wowMachine != 0 && processMachine != wowMachine) { return "ambiguous"; }
            if (target == nativeMachine) { return "native"; }
            // ARM64EC final images can report the x64 ABI too; machine codes alone do not prove
            // that every part of an x64-compatible process is running under emulation.
            if (nativeMachine == 0xAA64 && target == 0x8664) { return "emulated-or-hybrid"; }
            if (nativeMachine == 0xAA64 && target == 0x014c) { return "emulated"; }
            if (nativeMachine == 0x8664 && target == 0x014c) { return "wow64"; }
            return "unknown";
        }
    }
}
