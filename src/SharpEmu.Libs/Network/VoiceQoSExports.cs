// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System;
using SharpEmu.HLE;

namespace SharpEmu.Libs.Network;

public static class VoiceQoSExports
{
    [SysAbiExport(
        Nid = "U8IfNl6-Css",
        ExportName = "sceVoiceQoSInit",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceVoiceQoS")]
    public static int VoiceQoSInit(CpuContext ctx)
    {
        TraceVoiceQoS("init", ctx[CpuRegister.Rdi], ctx[CpuRegister.Rsi]);
        ctx[CpuRegister.Rax] = 0;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    private static void TraceVoiceQoS(string operation, ulong arg0, ulong arg1)
    {
        if (!string.Equals(Environment.GetEnvironmentVariable("SHARPEMU_LOG_VOICE_QOS"), "1", StringComparison.Ordinal))
        {
            return;
        }

        Console.Error.WriteLine(
            $"[LOADER][TRACE] voiceqos.{operation} arg0=0x{arg0:X16} arg1=0x{arg1:X16}");
    }
}
