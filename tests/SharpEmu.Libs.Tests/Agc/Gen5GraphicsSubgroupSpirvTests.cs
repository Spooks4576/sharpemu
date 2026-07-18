// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.ShaderCompiler;
using SharpEmu.ShaderCompiler.Vulkan;
using Xunit;

namespace SharpEmu.Libs.Tests.Agc;

public sealed class Gen5GraphicsSubgroupSpirvTests
{
    [Fact]
    public void PixelDppInstructionDeclaresShuffleCapability()
    {
        var dppMove = new Gen5ShaderInstruction(
            0,
            Gen5ShaderEncoding.Vop1,
            "VMovB32",
            [],
            [Gen5Operand.Vector(1)],
            [Gen5Operand.Vector(0)],
            new Gen5DppControl(
                Control: 0x101,
                FetchInactive: true,
                BoundControl: false,
                AbsoluteMask: 0,
                NegateMask: 0,
                BankMask: 0xF,
                RowMask: 0xF));
        var end = new Gen5ShaderInstruction(
            4,
            Gen5ShaderEncoding.Sopp,
            "SEndpgm",
            [],
            [],
            [],
            null);
        var state = new Gen5ShaderState(
            new Gen5ShaderProgram(0, [dppMove, end]),
            [],
            null);
        var registers = new uint[256];
        var evaluation = new Gen5ShaderEvaluation(
            registers,
            registers,
            [],
            []);

        Assert.True(
            Gen5SpirvTranslator.TryCompilePixelShader(
                state,
                evaluation,
                Gen5PixelOutputKind.Float,
                out var shader,
                out var error),
            error);

        Assert.True(HasCapability(
            shader.Spirv,
            SpirvCapability.GroupNonUniformShuffle));
        Assert.True(HasFlatBuiltIn(
            shader.Spirv,
            SpirvBuiltIn.SubgroupLocalInvocationId));
    }

    private static bool HasCapability(byte[] spirv, SpirvCapability capability)
    {
        for (var offset = 5; offset < spirv.Length / sizeof(uint);)
        {
            var header = BitConverter.ToUInt32(spirv, offset * sizeof(uint));
            var wordCount = (int)(header >> 16);
            if ((SpirvOp)(ushort)header == SpirvOp.Capability &&
                wordCount >= 2 &&
                BitConverter.ToUInt32(
                    spirv,
                    (offset + 1) * sizeof(uint)) == (uint)capability)
            {
                return true;
            }

            offset += Math.Max(wordCount, 1);
        }

        return false;
    }

    private static bool HasFlatBuiltIn(byte[] spirv, SpirvBuiltIn builtIn)
    {
        var builtInVariable = 0u;
        var flatVariables = new HashSet<uint>();
        for (var offset = 5; offset < spirv.Length / sizeof(uint);)
        {
            var header = BitConverter.ToUInt32(spirv, offset * sizeof(uint));
            var wordCount = (int)(header >> 16);
            if ((SpirvOp)(ushort)header == SpirvOp.Decorate && wordCount >= 3)
            {
                var variable = BitConverter.ToUInt32(
                    spirv,
                    (offset + 1) * sizeof(uint));
                var decoration = (SpirvDecoration)BitConverter.ToUInt32(
                    spirv,
                    (offset + 2) * sizeof(uint));
                if (decoration == SpirvDecoration.BuiltIn &&
                    wordCount >= 4 &&
                    BitConverter.ToUInt32(
                        spirv,
                        (offset + 3) * sizeof(uint)) == (uint)builtIn)
                {
                    builtInVariable = variable;
                }
                else if (decoration == SpirvDecoration.Flat)
                {
                    flatVariables.Add(variable);
                }
            }

            offset += Math.Max(wordCount, 1);
        }

        return builtInVariable != 0 && flatVariables.Contains(builtInVariable);
    }
}
