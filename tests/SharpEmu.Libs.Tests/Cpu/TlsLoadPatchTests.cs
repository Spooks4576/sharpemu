// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Core.Cpu.Native;
using Xunit;

namespace SharpEmu.Libs.Tests.Cpu;

public sealed class TlsLoadPatchTests
{
    [Fact]
    public unsafe void NonRaxDestinationPreservesGuestRax()
    {
        Span<byte> storage = stackalloc byte[128];
        fixed (byte* code = storage)
        {
            var length = DirectExecutionBackend.EmitTlsLoadHandler(
                code,
                destinationRegister: 3,
                tlsIndex: 7,
                tlsGetValueAddress: 0x1122_3344_5566_7788UL);
            var emitted = storage[..length];

            Assert.Equal(0x9C, emitted[0]); // pushfq
            Assert.Equal(0x50, emitted[1]); // push rax
            Assert.True(Contains(emitted, [0x48, 0x89, 0xC3])); // mov rbx, rax
            Assert.Equal<byte>([0x58, 0x9D, 0xC3], emitted[^3..]); // pop rax; popfq; ret
        }
    }

    [Fact]
    public unsafe void RaxDestinationLeavesTlsResultInRax()
    {
        Span<byte> storage = stackalloc byte[128];
        fixed (byte* code = storage)
        {
            var length = DirectExecutionBackend.EmitTlsLoadHandler(
                code,
                destinationRegister: 0,
                tlsIndex: 7,
                tlsGetValueAddress: 0x1122_3344_5566_7788UL);
            var emitted = storage[..length];

            Assert.Equal(0x9C, emitted[0]); // pushfq
            Assert.Equal(0x51, emitted[1]); // push rcx; rax is the destination
            Assert.Equal<byte>([0x59, 0x9D, 0xC3], emitted[^3..]); // pop rcx; popfq; ret
        }
    }

    private static bool Contains(ReadOnlySpan<byte> source, ReadOnlySpan<byte> value)
    {
        for (var index = 0; index <= source.Length - value.Length; index++)
        {
            if (source.Slice(index, value.Length).SequenceEqual(value))
            {
                return true;
            }
        }

        return false;
    }
}
