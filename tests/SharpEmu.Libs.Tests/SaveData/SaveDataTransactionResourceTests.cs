// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using SharpEmu.Libs.SaveData;
using Xunit;

namespace SharpEmu.Libs.Tests.SaveData;

[Collection("SaveDataMemoryState")]
public sealed class SaveDataTransactionResourceTests
{
    private const ulong Base = 0x1_0000_0000;

    [Fact]
    public void CreateTransactionResource_ReturnsIdWithoutWritingArgumentRegisters()
    {
        var memory = new FakeCpuMemory(Base, 0x1000);
        var ctx = new CpuContext(memory, Generation.Gen5);
        var sentinel = Enumerable.Repeat((byte)0xA5, 0x40).ToArray();
        Assert.True(memory.TryWrite(Base, sentinel));

        ctx[CpuRegister.Rdi] = 0xC0000;
        ctx[CpuRegister.Rsi] = Base;
        ctx[CpuRegister.Rdx] = Base + 0x08;
        ctx[CpuRegister.Rcx] = Base + 0x10;
        ctx[CpuRegister.R8] = Base + 0x18;
        ctx[CpuRegister.R9] = Base + 0x20;

        var result = SaveDataExports.SaveDataCreateTransactionResource(ctx);

        Assert.True(result > 0);
        Assert.Equal(unchecked((ulong)result), ctx[CpuRegister.Rax]);
        var readback = new byte[sentinel.Length];
        Assert.True(memory.TryRead(Base, readback));
        Assert.Equal(sentinel, readback);
    }
}
