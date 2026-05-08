// Phase 12.J.E.9 — purpose-built Squid.Tentacle.exe shim with a fixed
// Win32 VERSIONINFO ProductVersion stamp. NEVER intended to run as a
// service; only to exist on disk so the .ps1's
// `(Get-Item $tentacleExe).VersionInfo.ProductVersion` short-circuit
// check has a target file with a known version stamp.
//
// If you somehow run this binary directly: it prints a one-liner and
// exits 0. The real Squid.Tentacle.exe is a different binary altogether
// (Squid.Tentacle assembly in /src/) — this shim has nothing to do with it
// beyond the filename collision-by-design.
Console.WriteLine("Squid.WindowsTentacleE2E.VersionStampedShim — version-stamped shim for the upgrade E4.h short-circuit test. This binary does nothing functional; it exists only to provide a Squid.Tentacle.exe with a known ProductVersion for `Get-Item.VersionInfo` to inspect.");
return 0;
