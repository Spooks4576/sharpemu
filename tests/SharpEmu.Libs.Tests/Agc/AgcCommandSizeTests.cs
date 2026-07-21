// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using SharpEmu.Libs.Agc;
using Xunit;

namespace SharpEmu.Libs.Tests.Agc;

public sealed class AgcCommandSizeTests
{
    [Theory]
    [InlineData(0u, 16u)]
    [InlineData(1u, 20u)]
    [InlineData(2u, 24u)]
    [InlineData(64u, 272u)]
    public void CbSetShRegisterRangeDirectGetSize_IncludesMarkerAndPacket(uint valueCount, uint expectedSize)
    {
        var ctx = new CpuContext(new FakeCpuMemory(0x1_0000_0000, 0x1000), Generation.Gen5);
        ctx[CpuRegister.Rdi] = valueCount;
        ctx[CpuRegister.Rdx] = 0xDEAD_BEEF;

        var result = AgcExports.CbSetShRegisterRangeDirectGetSize(ctx);

        Assert.Equal(unchecked((int)expectedSize), result);
        Assert.Equal(expectedSize, ctx[CpuRegister.Rax]);
    }
}
