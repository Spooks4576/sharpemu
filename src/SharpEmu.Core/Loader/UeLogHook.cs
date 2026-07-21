// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.Core.Loader;
using SharpEmu.Core.Memory;

namespace SharpEmu.Core.Diagnostics;

/// <summary>
/// Title-agnostic tap for Unreal Engine's log output. UE routes every UE_LOG
/// through one variadic formatter with a stable prologue, so a byte-pattern scan
/// finds it without a per-game address. We detour its entry through an int3
/// trampoline; the SIGTRAP handler reads the pristine args, formats the line,
/// filters, prints, then resumes. Enabled with SHARPEMU_UE_LOG=1 (=verbose keeps
/// non-prose lines too).
/// </summary>
public static class UeLogHook
{
    // Full prologue of the UE_LOG variadic formatter — matching the whole shape
    // (fixed push set + large frame for the 512-wide buffer + the SysV varargs
    // xmm-spill idiom) keeps it specific to the log formatter, not every variadic
    // function. 0xFF = wildcard. The match start is the entry, so the first 6
    // bytes (push rbp; mov rbp,rsp; push r15) are stolen for the detour.
    //   55 48 89 E5 41 57 41 56 41 55 41 54 53 | 48 81 EC ?? ?? 00 00
    //   | 48 89 FB 84 C0 74 ?? | C5 F8 29 85
    private static readonly byte[] Pattern =
    [
        0x55, 0x48, 0x89, 0xE5, 0x41, 0x57, 0x41, 0x56, 0x41, 0x55, 0x41, 0x54, 0x53,
        0x48, 0x81, 0xEC, 0xFF, 0xFF, 0x00, 0x00,
        0x48, 0x89, 0xFB, 0x84, 0xC0, 0x74, 0xFF,
        0xC5, 0xF8, 0x29, 0x85,
    ];

    private static readonly byte[] PatternWildcard =
    [
        0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
        0, 0, 0, 1, 1, 0, 0,
        0, 0, 0, 0, 0, 0, 1,
        0, 0, 0, 0,
    ];

    private const int StolenBytes = 6;

    public static bool IsEnabled =>
        Environment.GetEnvironmentVariable("SHARPEMU_UE_LOG") is { Length: > 0 } v &&
        !string.Equals(v, "0", StringComparison.Ordinal);

    public static bool Verbose =>
        string.Equals(Environment.GetEnvironmentVariable("SHARPEMU_UE_LOG"), "verbose", StringComparison.OrdinalIgnoreCase);

    /// <summary>Trap (int3) address -> address to resume the real formatter at.</summary>
    private static readonly Dictionary<ulong, ulong> _trapResume = new();
    private static readonly object _gate = new();

    public static bool TryGetTrapResume(ulong trapAddress, out ulong resumeAddress)
    {
        lock (_gate)
        {
            return _trapResume.TryGetValue(trapAddress, out resumeAddress);
        }
    }

    public delegate bool ReadGuest(ulong address, Span<byte> destination);

    /// <summary>
    /// Called from the SIGTRAP handler when a UE log formatter trap fires. All
    /// arguments are the pristine formatter args (nothing has run yet). Reads the
    /// UTF-16 format + varargs, renders the line, applies the prose filter, prints.
    /// </summary>
    public static void HandleTrap(
        ulong formatPtr,
        ulong rdx, ulong rcx, ulong r8, ulong r9,
        ulong rsp,
        ReadOnlySpan<ulong> xmm,
        ReadGuest read)
    {
        try
        {
            var format = ReadUtf16(read, formatPtr, 2048);
            if (format is null)
            {
                return;
            }

            Span<ulong> gp = [rdx, rcx, r8, r9];
            var line = Render(format, gp, xmm, rsp, read);
            if (line.Length == 0 || (!Verbose && !LooksLikeProse(line)))
            {
                return;
            }

            Console.Error.WriteLine($"[UELOG] {line}");
        }
        catch
        {
            // Never let a diagnostic tap destabilise the guest signal handler.
        }
    }

    private static string Render(string format, ReadOnlySpan<ulong> gpSpan, ReadOnlySpan<ulong> xmmSpan, ulong rsp, ReadGuest read)
    {
        // Copy out of the ref-like spans so the arg cursors (local functions)
        // can capture them.
        var gp = gpSpan.ToArray();
        var xmm = xmmSpan.ToArray();
        var sb = new System.Text.StringBuilder(format.Length + 32);
        var gpIndex = 0;   // rdx,rcx,r8,r9 then stack
        var fpIndex = 0;   // xmm0..7
        var stack = rsp + 8; // first stack overflow arg (past the return address)

        ulong NextGp()
        {
            if (gpIndex < gp.Length)
            {
                return gp[gpIndex++];
            }

            gpIndex++;
            var b = new byte[8];
            var v = read(stack, b) ? BinaryPrimitives.ReadUInt64LittleEndian(b) : 0;
            stack += 8;
            return v;
        }

        double NextFp()
        {
            if (fpIndex < xmm.Length)
            {
                return BitConverter.UInt64BitsToDouble(xmm[fpIndex++]);
            }

            return BitConverter.UInt64BitsToDouble(NextGp());
        }

        for (var i = 0; i < format.Length; i++)
        {
            var c = format[i];
            if (c != '%')
            {
                sb.Append(c);
                continue;
            }

            // Consume flags/width/precision/length modifiers up to the conversion.
            var j = i + 1;
            while (j < format.Length && "-+ #0123456789.*lhLwqjzt".IndexOf(format[j]) >= 0)
            {
                j++;
            }

            if (j >= format.Length)
            {
                sb.Append(c);
                break;
            }

            var conv = format[j];
            switch (conv)
            {
                case '%': sb.Append('%'); break;
                case 'd': case 'i': sb.Append((long)NextGp()); break;
                case 'u': sb.Append(NextGp()); break;
                case 'x': sb.Append(NextGp().ToString("x")); break;
                case 'X': sb.Append(NextGp().ToString("X")); break;
                case 'p': sb.Append("0x").Append(NextGp().ToString("X")); break;
                case 'c': sb.Append((char)(NextGp() & 0xFFFF)); break;
                case 'f': case 'F': case 'g': case 'G': case 'e': case 'E':
                    sb.Append(NextFp().ToString("0.###")); break;
                case 's': case 'S':
                {
                    var ptr = NextGp();
                    // UE TCHAR is UTF-16; %hs/%s-of-ansi is rare — try wide first.
                    var s = ReadUtf16(read, ptr, 4096) ?? string.Empty;
                    sb.Append(s);
                    break;
                }
                default:
                    sb.Append('%').Append(conv);
                    break;
            }

            i = j;
        }

        return sb.ToString().TrimEnd('\r', '\n', ' ', '\t');
    }

    private static string? ReadUtf16(ReadGuest read, ulong address, int maxChars)
    {
        if (address is 0 or < 0x1000)
        {
            return null;
        }

        var sb = new System.Text.StringBuilder(64);
        Span<byte> buf = stackalloc byte[2];
        for (var i = 0; i < maxChars; i++)
        {
            if (!read(address + (ulong)(i * 2), buf))
            {
                break;
            }

            var ch = (char)(buf[0] | (buf[1] << 8));
            if (ch == '\0')
            {
                break;
            }

            sb.Append(ch);
        }

        return sb.ToString();
    }

    // A prose line: has a run of letters and at least one space between words —
    // filters out terse/numeric/hex-only spam so only sentence-style logs show.
    private static bool LooksLikeProse(string s)
    {
        if (s.Length < 12)
        {
            return false;
        }

        int letters = 0, spaces = 0, words = 0;
        var inWord = false;
        foreach (var c in s)
        {
            if (char.IsLetter(c))
            {
                letters++;
                if (!inWord) { words++; inWord = true; }
            }
            else
            {
                if (c == ' ') { spaces++; }
                inWord = false;
            }
        }

        return words >= 3 && spaces >= 2 && letters >= s.Length / 2;
    }

    /// <summary>
    /// Scans the executable image for the UE log formatter and installs detours.
    /// Safe to call once per module load; only the main executable is scanned.
    /// </summary>
    public static void TryInstall(
        IVirtualMemory virtualMemory,
        ulong imageBase,
        IReadOnlyList<ProgramHeader> programHeaders)
    {
        if (!IsEnabled)
        {
            return;
        }

        // Scan the first executable Load segment (UE code lives in the main .text).
        foreach (var ph in programHeaders)
        {
            if (ph.HeaderType != ProgramHeaderType.Load ||
                (ph.Flags & ProgramHeaderFlags.Execute) == 0 ||
                ph.MemorySize == 0)
            {
                continue;
            }

            var segmentBase = imageBase + ph.VirtualAddress;
            var size = ph.MemorySize > int.MaxValue ? int.MaxValue : (ulong)ph.MemorySize;
            InstallInSegment(virtualMemory, segmentBase, size);
            return;
        }
    }

    private static void InstallInSegment(IVirtualMemory virtualMemory, ulong segmentBase, ulong size)
    {
        // Read the segment in one shot for scanning.
        var bytes = new byte[size];
        if (!virtualMemory.TryRead(segmentBase, bytes))
        {
            // Fall back to a bounded chunked read if the full range is not mapped.
            var read = 0;
            const int chunk = 0x10000;
            while (read < bytes.Length)
            {
                var len = Math.Min(chunk, bytes.Length - read);
                if (!virtualMemory.TryRead(segmentBase + (ulong)read, bytes.AsSpan(read, len)))
                {
                    break;
                }

                read += len;
            }

            if (read == 0)
            {
                Console.Error.WriteLine("[LOADER][UELOG] Skipped: could not read code segment.");
                return;
            }
        }

        var installed = 0;
        for (var i = 0; i + Pattern.Length <= bytes.Length; i++)
        {
            if (!MatchesAt(bytes, i, Pattern, PatternWildcard))
            {
                continue;
            }

            var fnAddress = segmentBase + (ulong)i;
            if (TryInstallDetour(virtualMemory, fnAddress, bytes.AsSpan(i, StolenBytes)))
            {
                installed++;
            }
        }

        Console.Error.WriteLine(
            installed > 0
                ? $"[LOADER][UELOG] Installed UE log tap on {installed} formatter(s)."
                : "[LOADER][UELOG] No UE log formatter pattern matched in code segment.");
    }

    private static bool MatchesAt(byte[] data, int offset, byte[] pattern, byte[] wildcard)
    {
        for (var j = 0; j < pattern.Length; j++)
        {
            if (wildcard[j] == 0 && data[offset + j] != pattern[j])
            {
                return false;
            }
        }

        return true;
    }

    // Import-stub region is far from the image, so the trampoline must sit within
    // ±2GB of the formatter for a rel32 jmp. Search just above the image.
    private const ulong TrampolineSearchBase = 0x0000_0008_2000_0000UL;

    private static bool TryInstallDetour(IVirtualMemory virtualMemory, ulong fnAddress, ReadOnlySpan<byte> stolen)
    {
        // Map a nearby RWX page for [int3][stolen prologue][jmp back].
        if (!TryMapNearbyPage(virtualMemory, fnAddress, out var page))
        {
            Console.Error.WriteLine($"[LOADER][UELOG] FAILED to map trampoline near 0x{fnAddress:X}.");
            return false;
        }

        var stealLen = stolen.Length;
        var trapAddress = page;                 // int3 lands here
        var stolenAddress = page + 1;           // relocated prologue
        var jmpBackAddress = stolenAddress + (ulong)stealLen;
        var resumeAddress = fnAddress + (ulong)stealLen;

        // rel32 for `jmp resume` from the trampoline's trailing jmp.
        var jmpBackRel = checked((int)((long)resumeAddress - (long)(jmpBackAddress + 5)));

        Span<byte> tramp = stackalloc byte[1 + 6 + 5];
        tramp = tramp[..(1 + stealLen + 5)];
        tramp[0] = 0xCC;                        // int3 — first thing, args pristine
        stolen.CopyTo(tramp[1..]);              // original position-independent prologue
        tramp[1 + stealLen] = 0xE9;             // jmp rel32
        BinaryPrimitives.WriteInt32LittleEndian(tramp[(2 + stealLen)..], jmpBackRel);
        if (!virtualMemory.TryWrite(page, tramp))
        {
            Console.Error.WriteLine($"[LOADER][UELOG] FAILED to write trampoline at 0x{page:X}.");
            return false;
        }

        // Overwrite the formatter entry with `jmp trampoline` (rel32) + nop padding.
        var entryRel = checked((int)((long)trapAddress - (long)(fnAddress + 5)));
        Span<byte> detour = stackalloc byte[6];
        detour = detour[..stealLen];
        detour.Fill(0x90);
        detour[0] = 0xE9;
        BinaryPrimitives.WriteInt32LittleEndian(detour[1..], entryRel);
        if (!virtualMemory.TryWrite(fnAddress, detour))
        {
            Console.Error.WriteLine($"[LOADER][UELOG] FAILED to detour formatter at 0x{fnAddress:X}.");
            return false;
        }

        // int3 traps with RIP pointing *past* the int3 — i.e. at stolenAddress,
        // which is exactly where the relocated prologue lives. Key on that so the
        // handler recognises the trap and resumes there without touching RIP.
        lock (_gate)
        {
            _trapResume[stolenAddress] = stolenAddress;
        }

        return true;
    }

    private static bool TryMapNearbyPage(IVirtualMemory virtualMemory, ulong near, out ulong mappedBase)
    {
        var page = new byte[0x1000];
        // Walk upward in 16MB steps, staying within rel32 range of `near`.
        for (var i = 0; i < 64; i++)
        {
            var candidate = TrampolineSearchBase + ((ulong)i * 0x0100_0000UL);
            if ((long)candidate - (long)near is > int.MaxValue or < int.MinValue)
            {
                continue;
            }

            if (IsRangeMapped(virtualMemory, candidate, 0x1000))
            {
                continue;
            }

            try
            {
                virtualMemory.Map(
                    candidate,
                    0x1000,
                    fileOffset: 0,
                    page,
                    ProgramHeaderFlags.Read | ProgramHeaderFlags.Write | ProgramHeaderFlags.Execute);
                mappedBase = candidate;
                return true;
            }
            catch (InvalidOperationException)
            {
            }
        }

        mappedBase = 0;
        return false;
    }

    private static bool IsRangeMapped(IVirtualMemory virtualMemory, ulong start, ulong size)
    {
        var end = start + size;
        foreach (var region in virtualMemory.SnapshotRegions())
        {
            var regionStart = region.VirtualAddress;
            var regionEnd = regionStart + region.MemorySize;
            if (start < regionEnd && end > regionStart)
            {
                return true;
            }
        }

        return false;
    }
}
