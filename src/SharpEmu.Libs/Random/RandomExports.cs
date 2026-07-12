// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using System.Security.Cryptography;

namespace SharpEmu.Libs.Random;

public static class RandomExports
{
    private const int SceRandomErrorInvalid = unchecked((int)0x817C0002);
    private const ulong MaximumRandomLength = 64;

    [SysAbiExport(
        Nid = "PI7jIZj4pcE",
        ExportName = "sceRandomGetRandomNumber",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceRandom")]
    public static int RandomGetRandomNumber(CpuContext ctx)
    {
        var bufferAddress = ctx[CpuRegister.Rdi];
        var length = ctx[CpuRegister.Rsi];

        if (bufferAddress == 0 || length == 0 || length > MaximumRandomLength)
        {
            return ctx.SetReturn(SceRandomErrorInvalid);
        }

        var bytes = new byte[length];
        RandomNumberGenerator.Fill(bytes);

        return ctx.Memory.TryWrite(bufferAddress, bytes)
            ? ctx.SetReturn(0)
            : ctx.SetReturn((int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
    }
}
