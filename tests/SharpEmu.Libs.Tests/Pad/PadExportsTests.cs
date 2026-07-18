// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using SharpEmu.Libs.Pad;
using Xunit;

namespace SharpEmu.Libs.Tests.Pad;

public sealed class PadExportsTests
{
    private const ulong Base = 0x1_0000_0000;
    private const int InvalidHandle = unchecked((int)0x80920003);

    private readonly FakeCpuMemory _memory = new(Base, 0x1000);
    private readonly CpuContext _ctx;

    public PadExportsTests()
    {
        _ctx = new CpuContext(_memory, Generation.Gen5);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 0)]
    [InlineData(2, InvalidHandle)]
    [InlineData(-1, InvalidHandle)]
    public void SetTiltCorrectionState_ValidatesHandle(int handle, int expected)
    {
        _ctx[CpuRegister.Rdi] = unchecked((ulong)handle);
        Assert.Equal(expected, PadExports.PadSetTiltCorrectionState(_ctx));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void GetTriggerEffectState_ReturnsNeutralState(int handle)
    {
        var stateAddress = Base + 0x100;
        Assert.True(_memory.TryWrite(stateAddress, Enumerable.Repeat((byte)0xA5, 8).ToArray()));
        _ctx[CpuRegister.Rdi] = unchecked((ulong)handle);
        _ctx[CpuRegister.Rsi] = stateAddress;

        Assert.Equal(0, PadExports.PadGetTriggerEffectState(_ctx));
        var state = new byte[8];
        Assert.True(_memory.TryRead(stateAddress, state));
        Assert.Equal(new byte[8], state);
    }
}
