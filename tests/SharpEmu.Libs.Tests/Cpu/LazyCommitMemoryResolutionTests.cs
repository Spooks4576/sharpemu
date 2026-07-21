// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Core.Cpu.Native;
using SharpEmu.Core.Loader;
using SharpEmu.Core.Memory;
using SharpEmu.HLE;
using Xunit;

namespace SharpEmu.Libs.Tests.Cpu;

public sealed class LazyCommitMemoryResolutionTests
{
    [Fact]
    public void NestedMemoryWrappersResolveToVirtualMemoryOwner()
    {
        var virtualMemory = new FakeVirtualMemory();
        ICpuMemory wrapped = new MemoryWrapper(new MemoryWrapper(virtualMemory));

        Assert.True(DirectExecutionBackend.TryGetVirtualMemory(wrapped, out var resolved));
        Assert.Same(virtualMemory, resolved);
    }

    [Fact]
    public void SelfReferencingWrapperIsRejected()
    {
        var wrapper = new SelfReferencingMemoryWrapper();

        Assert.False(DirectExecutionBackend.TryGetVirtualMemory(wrapper, out _));
    }

    private sealed class MemoryWrapper(ICpuMemory inner) : ICpuMemory, ICpuMemoryWrapper
    {
        public ICpuMemory Inner { get; } = inner;

        public bool TryRead(ulong virtualAddress, Span<byte> destination) =>
            Inner.TryRead(virtualAddress, destination);

        public bool TryWrite(ulong virtualAddress, ReadOnlySpan<byte> source) =>
            Inner.TryWrite(virtualAddress, source);
    }

    private sealed class SelfReferencingMemoryWrapper : ICpuMemory, ICpuMemoryWrapper
    {
        public ICpuMemory Inner => this;

        public bool TryRead(ulong virtualAddress, Span<byte> destination) => false;

        public bool TryWrite(ulong virtualAddress, ReadOnlySpan<byte> source) => false;
    }

    private sealed class FakeVirtualMemory : IVirtualMemory
    {
        public void Clear()
        {
        }

        public void Map(
            ulong virtualAddress,
            ulong memorySize,
            ulong fileOffset,
            ReadOnlySpan<byte> fileData,
            ProgramHeaderFlags protection)
        {
        }

        public IReadOnlyList<VirtualMemoryRegion> SnapshotRegions() => [];

        public bool TryRead(ulong virtualAddress, Span<byte> destination) => false;

        public bool TryWrite(ulong virtualAddress, ReadOnlySpan<byte> source) => false;
    }
}
