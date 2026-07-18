// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.HLE;
using SharpEmu.Libs.Agc;
using Xunit;

namespace SharpEmu.Libs.Tests.Agc;

public sealed class AgcFusedShaderTests
{
    private const ulong Base = 0x1_0000_0000;
    private const ulong FrontAddress = Base + 0x100;
    private const ulong BackAddress = Base + 0x200;
    private const ulong BackRegistersAddress = Base + 0x300;
    private const ulong ResultAddress = Base + 0x400;
    private const ulong ScratchAddress = Base + 0x500;
    private const ulong FrontRegistersAddress = Base + 0x600;
    private const ulong FrontSpecialsAddress = Base + 0x700;
    private const ulong BackSpecialsAddress = Base + 0x740;
    private const ulong FrontCodeAddress = 0x12_3456_7890_00;

    [Fact]
    public void GetFusedShaderSizeReportsBackRegisterStorage()
    {
        var (memory, context) = CreateContext();
        context[CpuRegister.Rdi] = ResultAddress;
        context[CpuRegister.Rsi] = FrontAddress;
        context[CpuRegister.Rdx] = BackAddress;

        Assert.Equal(0, AgcExports.GetFusedShaderSize(context));
        Assert.True(context.TryReadUInt64(ResultAddress, out var size));
        Assert.True(context.TryReadUInt64(ResultAddress + 8, out var alignment));
        Assert.Equal(32UL, size);
        Assert.Equal(4UL, alignment);
    }

    [Fact]
    public void FuseGsShaderHalvesPatchesFrontProgramAndChecksums()
    {
        var (memory, context) = CreateContext();

        context[CpuRegister.Rdi] = ResultAddress;
        context[CpuRegister.Rsi] = FrontAddress;
        context[CpuRegister.Rdx] = BackAddress;
        context[CpuRegister.Rcx] = ScratchAddress;

        Assert.Equal(0, AgcExports.FuseShaderHalves(context));

        var result = new byte[0x60];
        Assert.True(memory.TryRead(ResultAddress, result));
        Assert.Equal(2, result[0x5A]);
        Assert.Equal(0UL, BinaryPrimitives.ReadUInt64LittleEndian(result.AsSpan(0x08)));
        Assert.Equal(ScratchAddress, BinaryPrimitives.ReadUInt64LittleEndian(result.AsSpan(0x20)));
        var scratch = new byte[32];
        Assert.True(memory.TryRead(ScratchAddress, scratch));
        AssertRegister(scratch, 0, 0xC8, (uint)((FrontCodeAddress >> 8) & uint.MaxValue));
        AssertRegister(scratch, 1, 0xC9, (uint)(FrontCodeAddress >> 40));
        AssertRegister(scratch, 2, 0x80, 0x1111_1111);
        AssertRegister(scratch, 3, 0x80, 0x2222_2222);
    }

    [Fact]
    public void FuseHsShaderHalvesPatchesLocalShaderProgram()
    {
        var (memory, context) = CreateContext(frontType: 5, backType: 7);
        WriteRegister(memory, BackRegistersAddress, 0, 0x148, 0xAAAA_AAAA);
        WriteRegister(memory, BackRegistersAddress, 1, 0x149, 0xBBBB_BBBB);

        context[CpuRegister.Rdi] = ResultAddress;
        context[CpuRegister.Rsi] = FrontAddress;
        context[CpuRegister.Rdx] = BackAddress;
        context[CpuRegister.Rcx] = ScratchAddress;

        Assert.Equal(0, AgcExports.FuseShaderHalves(context));

        var result = new byte[0x60];
        Assert.True(memory.TryRead(ResultAddress, result));
        Assert.Equal(3, result[0x5A]);
        var scratch = new byte[32];
        Assert.True(memory.TryRead(ScratchAddress, scratch));
        AssertRegister(scratch, 0, 0x148, (uint)((FrontCodeAddress >> 8) & uint.MaxValue));
        AssertRegister(scratch, 1, 0x149, (uint)(FrontCodeAddress >> 40));
    }

    [Fact]
    public void FuseShaderHalvesRejectsMismatchedStageEnableBits()
    {
        var (memory, context) = CreateContext();
        Assert.True(memory.TryWrite(
            FrontSpecialsAddress + 0x0C,
            BitConverter.GetBytes(1u << 22)));

        context[CpuRegister.Rdi] = ResultAddress;
        context[CpuRegister.Rsi] = FrontAddress;
        context[CpuRegister.Rdx] = BackAddress;
        context[CpuRegister.Rcx] = ScratchAddress;

        Assert.Equal(unchecked((int)0x8A6C0008), AgcExports.FuseShaderHalves(context));
    }

    private static (FakeCpuMemory Memory, CpuContext Context) CreateContext(byte frontType = 4, byte backType = 6)
    {
        var memory = new FakeCpuMemory(Base, 0x1000);
        var context = new CpuContext(memory, Generation.Gen5);
        Span<byte> front = stackalloc byte[0x60];
        Span<byte> back = stackalloc byte[0x60];
        front.Clear();
        back.Clear();
        front[0x5A] = frontType;
        front[0x5C] = 2;
        back[0x5A] = backType;
        back[0x5C] = 4;
        BinaryPrimitives.WriteUInt64LittleEndian(front[0x08..], Base + 0x900);
        BinaryPrimitives.WriteUInt64LittleEndian(front[0x10..], FrontCodeAddress);
        BinaryPrimitives.WriteUInt64LittleEndian(front[0x20..], FrontRegistersAddress);
        BinaryPrimitives.WriteUInt64LittleEndian(front[0x28..], FrontSpecialsAddress);
        BinaryPrimitives.WriteUInt64LittleEndian(back[0x08..], Base + 0x980);
        BinaryPrimitives.WriteUInt64LittleEndian(back[0x20..], BackRegistersAddress);
        BinaryPrimitives.WriteUInt64LittleEndian(back[0x28..], BackSpecialsAddress);
        Assert.True(memory.TryWrite(FrontAddress, front));
        Assert.True(memory.TryWrite(BackAddress, back));

        WriteRegister(memory, FrontRegistersAddress, 0, 0x80, 0x1111_1111);
        WriteRegister(memory, FrontRegistersAddress, 1, 0x80, 0x2222_2222);
        WriteRegister(memory, BackRegistersAddress, 0, 0xC8, 0xAAAA_AAAA);
        WriteRegister(memory, BackRegistersAddress, 1, 0xC9, 0xBBBB_BBBB);
        WriteRegister(memory, BackRegistersAddress, 2, 0x80, 0xCCCC_CCCC);
        WriteRegister(memory, BackRegistersAddress, 3, 0x80, 0xDDDD_DDDD);
        WriteRegister(memory, FrontSpecialsAddress, 1, 0, 0);
        WriteRegister(memory, BackSpecialsAddress, 1, 0, 0);
        return (memory, context);
    }

    private static void WriteRegister(FakeCpuMemory memory, ulong address, int index, uint offset, uint value)
    {
        Span<byte> register = stackalloc byte[8];
        BinaryPrimitives.WriteUInt32LittleEndian(register, offset);
        BinaryPrimitives.WriteUInt32LittleEndian(register[4..], value);
        Assert.True(memory.TryWrite(address + ((ulong)index * 8), register));
    }

    private static void AssertRegister(byte[] registers, int index, uint offset, uint value)
    {
        var register = registers.AsSpan(index * 8, 8);
        Assert.Equal(offset, BinaryPrimitives.ReadUInt32LittleEndian(register));
        Assert.Equal(value, BinaryPrimitives.ReadUInt32LittleEndian(register[4..]));
    }
}
