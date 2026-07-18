// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using SharpEmu.Libs.Kernel;
using Xunit;

namespace SharpEmu.Libs.Tests.Kernel;

public sealed class KernelAioExportsTests
{
    private const ulong Base = 0x1_0000_0000;
    private const ulong StateAddress = Base + 0x100;

    [Theory]
    [InlineData("2pOuoWoCxdk")]
    [InlineData("KOF-oJbQVvc")]
    public void SingleRequestCompletionWritesCompletedState(string nid)
    {
        var memory = new FakeCpuMemory(Base, 0x1000);
        var context = new CpuContext(memory, Generation.Gen5);
        context[CpuRegister.Rdi] = 2;
        context[CpuRegister.Rsi] = StateAddress;

        var manager = new ModuleManager();
        manager.RegisterExports(SharpEmu.Generated.SysAbiExportRegistry.CreateExports(Generation.Gen5));

        Assert.True(manager.TryDispatch(nid, context, out _));
        Assert.Equal(0UL, context[CpuRegister.Rax]);
        Assert.True(context.TryReadUInt32(StateAddress, out var state));
        Assert.Equal(3U, state);
    }
}
