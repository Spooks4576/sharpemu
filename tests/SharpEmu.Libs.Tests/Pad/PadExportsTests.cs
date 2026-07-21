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
    [InlineData(0, 0)]
    [InlineData(1, 0)]
    [InlineData(2, InvalidHandle)]
    [InlineData(-1, InvalidHandle)]
    public void GetTriggerEffectState_ValidatesHandle(int handle, int expected)
    {
        _ctx[CpuRegister.Rdi] = unchecked((ulong)handle);
        _ctx[CpuRegister.Rsi] = Base;
        Assert.Equal(expected, PadExports.PadGetTriggerEffectState(_ctx));
    }

    [Fact]
    public void GetTriggerEffectState_RejectsNullState()
    {
        _ctx[CpuRegister.Rdi] = 1;
        _ctx[CpuRegister.Rsi] = 0;
        Assert.Equal(
            (int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT,
            PadExports.PadGetTriggerEffectState(_ctx));
    }

    [Fact]
    public void GetTriggerEffectState_ReportsLastAppliedModes()
    {
        // ScePadTriggerEffectParam: mask, then a 56-byte command per trigger with
        // the mode dword first: L2 = weapon (2), R2 = feedback (1).
        var parameter = new byte[120];
        parameter[0] = 0x03;
        parameter[8] = 2;
        parameter[64] = 1;
        const ulong parameterAddress = Base;
        Assert.True(_memory.TryWrite(parameterAddress, parameter));

        _ctx[CpuRegister.Rdi] = 1;
        _ctx[CpuRegister.Rsi] = parameterAddress;
        Assert.Equal(0, PadExports.PadSetTriggerEffect(_ctx));

        const ulong stateAddress = Base + 0x200;
        _ctx[CpuRegister.Rdi] = 1;
        _ctx[CpuRegister.Rsi] = stateAddress;
        Assert.Equal(0, PadExports.PadGetTriggerEffectState(_ctx));

        var state = new byte[2];
        Assert.True(_memory.TryRead(stateAddress, state));
        Assert.Equal(2, state[0]);
        Assert.Equal(1, state[1]);
    }
}
