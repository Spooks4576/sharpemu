// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;

namespace SharpEmu.Libs.Fmod;

// FMOD::System platform NIDs imported by Ghostrunner 2, recovered from retail
// libfmod.prx. Setters honour the FMOD success contract (zero FMOD_RESULT);
// getters populate their out-parameters with a 5.1/48 kHz description
// (the format GR2 configures via setSoftwareFormat).
// Set SHARPEMU_LOG_FMOD=1 to trace every call to stderr.
public static class FmodPlatformExports
{
    private const int FmodOk = (int)OrbisGen2Result.ORBIS_GEN2_OK;
    private const int FmodErrInvalidParam = 31;
    private const int FmodErrUnsupported = 68;

    private const int DefaultSampleRate = 48000;
    private const int SpeakerMode5Point1 = 6; // FMOD_SPEAKERMODE_5POINT1 — matches the format GR2 requests
    private const int Surround51RawSpeakers = 6;

    private static readonly bool LoggingEnabled =
        string.Equals(Environment.GetEnvironmentVariable("SHARPEMU_LOG_FMOD"), "1", StringComparison.Ordinal);

    // Trace the call and set the FMOD_RESULT return value in one step.
    private static int Done(CpuContext ctx, string signature, int result)
    {
        if (LoggingEnabled)
        {
            Console.Error.WriteLine($"[LOADER][TRACE] fmod.{signature} -> {result}");
        }

        return ctx.SetReturn(result);
    }

    // System V: 7th+ integer arguments live just above the return address.
    private static ulong ReadStackArg(CpuContext ctx, int index) =>
        ctx.TryReadUInt64(ctx[CpuRegister.Rsp] + sizeof(ulong) + ((ulong)index * sizeof(ulong)), out var value)
            ? value
            : 0;

    // Every export below is a synthetic label for an uncatalogued NID recovered
    // from libfmod.prx (the Unknown* convention); the NID is authoritative, so
    // SHEM006 (name not in ps5_names.txt) does not apply.
    #pragma warning disable SHEM006

    // System::setThreadAttributes(threadType, affinity, priority, stackKiB).
    [SysAbiExport(Nid = "tPFAXasYRR4", ExportName = "sharpemu_fmod_system_set_thread_attributes", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libfmod")]
    public static int FmodSystemSetThreadAttributes(CpuContext ctx)
    {
        var threadType = unchecked((uint)ctx[CpuRegister.Rdi]);
        var result = threadType <= 12 ? FmodOk : FmodErrInvalidParam;
        return Done(
            ctx,
            $"System::setThreadAttributes(type={threadType}, affinity=0x{ctx[CpuRegister.Rsi]:X}, priority={(int)ctx[CpuRegister.Rdx]}, stack={ctx[CpuRegister.Rcx]})",
            result);
    }

    // Retail body is `return 68` (FMOD_ERR_UNSUPPORTED); the game tolerates it.
    [SysAbiExport(Nid = "BgCoL-FZz0Y", ExportName = "sharpemu_fmod_unsupported", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libfmod")]
    public static int FmodUnsupported(CpuContext ctx) => Done(ctx, "[unsupported]", FmodErrUnsupported);

    // System::setFileSystem(...).
    [SysAbiExport(Nid = "VS1Vg5yOLH0", ExportName = "sharpemu_fmod_system_set_filesystem", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libfmod")]
    public static int FmodSystemSetFileSystem(CpuContext ctx) => Done(ctx, "System::setFileSystem", FmodOk);

    // System pre-init file-system/callback installer.
    [SysAbiExport(Nid = "zKjgSnqVqf4", ExportName = "sharpemu_fmod_system_attach_filesystem", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libfmod")]
    public static int FmodSystemAttachFileSystem(CpuContext ctx) => Done(ctx, "System::attachFileSystem", FmodOk);

    // System::setOutput(FMOD_OUTPUTTYPE).
    [SysAbiExport(Nid = "yYh3lwMFxd8", ExportName = "sharpemu_fmod_system_set_output", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libfmod")]
    public static int FmodSystemSetOutput(CpuContext ctx) =>
        Done(ctx, $"System::setOutput(output={(int)ctx[CpuRegister.Rsi]})", FmodOk);

    // System::getSoftwareFormat(*sampleRate, *speakerMode, *numRawSpeakers).
    [SysAbiExport(Nid = "A1mb1i8hsRw", ExportName = "sharpemu_fmod_system_get_software_format", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libfmod")]
    public static int FmodSystemGetSoftwareFormat(CpuContext ctx)
    {
        var sampleRatePtr = ctx[CpuRegister.Rsi];
        var speakerModePtr = ctx[CpuRegister.Rdx];
        var rawSpeakersPtr = ctx[CpuRegister.Rcx];

        if (sampleRatePtr != 0)
        {
            ctx.TryWriteInt32(sampleRatePtr, DefaultSampleRate);
        }

        if (speakerModePtr != 0)
        {
            ctx.TryWriteInt32(speakerModePtr, SpeakerMode5Point1);
        }

        if (rawSpeakersPtr != 0)
        {
            ctx.TryWriteInt32(rawSpeakersPtr, Surround51RawSpeakers);
        }

        return Done(
            ctx,
            $"System::getSoftwareFormat(rate={DefaultSampleRate}, mode={SpeakerMode5Point1}, rawSpeakers={Surround51RawSpeakers})",
            FmodOk);
    }

    // System::getDriverInfo(id, name, nameLen, *guid, *systemRate, *speakerMode, *speakerModeChannels).
    [SysAbiExport(Nid = "mHhZZyzhOvw", ExportName = "sharpemu_fmod_system_get_driver_info", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libfmod")]
    public static int FmodSystemGetDriverInfo(CpuContext ctx)
    {
        var driverId = unchecked((int)ctx[CpuRegister.Rsi]);
        var namePtr = ctx[CpuRegister.Rdx];
        var nameLen = unchecked((int)ctx[CpuRegister.Rcx]);
        var guidPtr = ctx[CpuRegister.R8];
        var systemRatePtr = ctx[CpuRegister.R9];
        var speakerModePtr = ReadStackArg(ctx, 0);
        var speakerModeChannelsPtr = ReadStackArg(ctx, 1);

        if (namePtr != 0 && nameLen >= 1)
        {
            Span<byte> terminator = stackalloc byte[1];
            terminator[0] = 0;
            ctx.Memory.TryWrite(namePtr, terminator);
        }

        if (guidPtr != 0)
        {
            Span<byte> guid = stackalloc byte[16];
            guid.Clear();
            ctx.Memory.TryWrite(guidPtr, guid);
        }

        if (systemRatePtr != 0)
        {
            ctx.TryWriteInt32(systemRatePtr, DefaultSampleRate);
        }

        if (speakerModePtr != 0)
        {
            ctx.TryWriteInt32(speakerModePtr, SpeakerMode5Point1);
        }

        if (speakerModeChannelsPtr != 0)
        {
            ctx.TryWriteInt32(speakerModeChannelsPtr, Surround51RawSpeakers);
        }

        return Done(
            ctx,
            $"System::getDriverInfo(id={driverId}, rate={DefaultSampleRate}, mode={SpeakerMode5Point1}, chans={Surround51RawSpeakers})",
            FmodOk);
    }

    // System::setSoftwareFormat(sampleRate, speakerMode, numRawSpeakers).
    [SysAbiExport(Nid = "MPBXfoxd+eo", ExportName = "sharpemu_fmod_system_set_software_format", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libfmod")]
    public static int FmodSystemSetSoftwareFormat(CpuContext ctx) =>
        Done(
            ctx,
            $"System::setSoftwareFormat(rate={(int)ctx[CpuRegister.Rsi]}, mode={(int)ctx[CpuRegister.Rdx]}, rawSpeakers={(int)ctx[CpuRegister.Rcx]})",
            FmodOk);

    // System::setSoftwareChannels(numSoftwareChannels).
    [SysAbiExport(Nid = "qDkZUgzj3Cs", ExportName = "sharpemu_fmod_system_set_software_channels", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libfmod")]
    public static int FmodSystemSetSoftwareChannels(CpuContext ctx) =>
        Done(ctx, $"System::setSoftwareChannels(count={(int)ctx[CpuRegister.Rsi]})", FmodOk);

    // System::setAdvancedSettings(*FMOD_ADVANCEDSETTINGS).
    [SysAbiExport(Nid = "RcRmUkp-AOo", ExportName = "sharpemu_fmod_system_set_advanced_settings", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libfmod")]
    public static int FmodSystemSetAdvancedSettings(CpuContext ctx) =>
        Done(ctx, $"System::setAdvancedSettings(0x{ctx[CpuRegister.Rsi]:X})", FmodOk);

    // Imported by Ghostrunner 2 but not exported by libfmod.prx; success stubs
    // pending identification of the module that provides them.
    [SysAbiExport(Nid = "MjQ5oH6b620", ExportName = "sharpemu_fmod_platform_unresolved_0", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libfmod")]
    public static int FmodPlatformUnresolved0(CpuContext ctx) => Done(ctx, "[unresolved MjQ5oH6b620]", FmodOk);

    [SysAbiExport(Nid = "7hd4bRJuLMg", ExportName = "sharpemu_fmod_platform_unresolved_1", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libfmod")]
    public static int FmodPlatformUnresolved1(CpuContext ctx) => Done(ctx, "[unresolved 7hd4bRJuLMg]", FmodOk);

    [SysAbiExport(Nid = "DlcM6wdwWJc", ExportName = "sharpemu_fmod_platform_unresolved_2", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libfmod")]
    public static int FmodPlatformUnresolved2(CpuContext ctx) => Done(ctx, "[unresolved DlcM6wdwWJc]", FmodOk);

    [SysAbiExport(Nid = "iILsKSo5Syg", ExportName = "sharpemu_fmod_platform_unresolved_3", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libfmod")]
    public static int FmodPlatformUnresolved3(CpuContext ctx) => Done(ctx, "[unresolved iILsKSo5Syg]", FmodOk);
    #pragma warning restore SHEM006
}
