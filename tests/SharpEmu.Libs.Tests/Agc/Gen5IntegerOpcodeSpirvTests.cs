// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.HLE;
using SharpEmu.ShaderCompiler;
using SharpEmu.ShaderCompiler.Vulkan;
using Xunit;

namespace SharpEmu.Libs.Tests.Agc;

public sealed class Gen5IntegerOpcodeSpirvTests
{
    private const ulong ShaderAddress = 0x1_0000_0000;

    [Fact]
    public void VBfeI32_DecodesAndEmitsSignedBitFieldExtract()
    {
        // v_bfe_i32 v3, v0, v1, v2
        var spirv = Compile([0xD1490003u, 0x040A0300u]);

        Assert.True(ContainsOpcode(spirv, 202), "expected OpBitFieldSExtract");
    }

    [Fact]
    public void VCmpLtU64_DecodesRegisterPairsAndEmitsUnsignedCompare()
    {
        // v_cmp_lt_u64 vcc, v[0:1], v[2:3]
        var spirv = Compile([0x7D920500u]);

        Assert.True(ContainsOpcode(spirv, 176), "expected OpULessThan");
    }

    private static bool ContainsOpcode(byte[] spirv, ushort expectedOpcode)
    {
        for (var offset = 5 * sizeof(uint); offset + sizeof(uint) <= spirv.Length;)
        {
            var word = BinaryPrimitives.ReadUInt32LittleEndian(
                spirv.AsSpan(offset, sizeof(uint)));
            var wordCount = (int)(word >> 16);
            if (wordCount <= 0)
            {
                return false;
            }

            if ((ushort)word == expectedOpcode)
            {
                return true;
            }

            offset += wordCount * sizeof(uint);
        }

        return false;
    }

    private static byte[] Compile(uint[] programWords)
    {
        var memory = new FakeCpuMemory(ShaderAddress, 0x2000);
        var ctx = new CpuContext(memory, Generation.Gen5);
        Gen5ShaderAtomicDecodeTests.WriteProgram(memory, ShaderAddress, programWords);
        var shaderRegisters = new Dictionary<uint, uint>
        {
            [Gen5ShaderAtomicDecodeTests.ComputePgmRsrc2Register] = 16u << 1,
        };

        Assert.True(
            Gen5ShaderTranslator.TryCreateState(
                ctx,
                ShaderAddress,
                0,
                shaderRegisters,
                Gen5ShaderAtomicDecodeTests.ComputeUserDataRegister,
                out var state,
                out var error),
            error);
        Assert.True(
            Gen5ShaderScalarEvaluator.TryEvaluate(ctx, state, out var evaluation, out error),
            error);
        Assert.True(
            Gen5SpirvTranslator.TryCompileComputeShader(
                state, evaluation, 1, 1, 1, out var shader, out error),
            error);
        return shader.Spirv;
    }
}
