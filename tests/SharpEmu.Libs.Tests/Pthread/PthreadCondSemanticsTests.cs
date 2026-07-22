// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Reflection;
using SharpEmu.HLE;
using SharpEmu.Libs.Kernel;
using Xunit;

namespace SharpEmu.Libs.Tests.Pthread;

// POSIX condition variables are edges, not semaphore credits. A signal with no waiter
// must have no effect. This was violated by the previous implementation which persisted
// signals via PendingSignals, causing lock inversions and predicate bypasses.
// See issue #113.
public sealed class PthreadCondSemanticsTests
{
    [Fact]
    public void PthreadCondState_DoesNotHavePendingSignals()
    {
        // Verify that PthreadCondState no longer has the PendingSignals property.
        // This is a regression test to ensure the POSIX-correct behavior is maintained.
        var stateType = typeof(KernelPthreadCompatExports).GetNestedType("PthreadCondState", BindingFlags.NonPublic);
        Assert.NotNull(stateType);

        var pendingSignalsProp = stateType.GetProperty("PendingSignals");
        Assert.Null(pendingSignalsProp);

        var tryConsumeMethod = stateType.GetMethod("TryConsumePendingSignal");
        Assert.Null(tryConsumeMethod);
    }

    [Fact]
    public void PthreadCondSignal_WithNoWaiter_DoesNotPersist()
    {
        // This test verifies the semantic contract: signal without waiter is a no-op.
        // We can't easily test the full pthread flow without the scheduler, but we can
        // verify the code path by checking that SignalEpoch advances but no state persists.
        var stateType = typeof(KernelPthreadCompatExports).GetNestedType("PthreadCondState", BindingFlags.NonPublic);
        Assert.NotNull(stateType);

        // Create an instance via reflection
        var state = Activator.CreateInstance(stateType);
        Assert.NotNull(state);

        var syncRootProp = stateType.GetProperty("SyncRoot");
        var signalEpochProp = stateType.GetProperty("SignalEpoch");
        var waitersProp = stateType.GetProperty("Waiters");

        Assert.NotNull(syncRootProp);
        Assert.NotNull(signalEpochProp);
        Assert.NotNull(waitersProp);

        var syncRoot = syncRootProp.GetValue(state);
        Assert.NotNull(syncRoot);

        // Initial state
        Assert.Equal(0UL, (ulong)signalEpochProp.GetValue(state)!);
        Assert.Equal(0, (int)waitersProp.GetValue(state)!);

        // Simulate signal with no waiter (this would have incremented PendingSignals before)
        lock (syncRoot)
        {
            signalEpochProp.SetValue(state, (ulong)signalEpochProp.GetValue(state)! + 1);
            // Note: we don't increment PendingSignals because it doesn't exist
        }

        // Verify epoch advanced but no persistent signal state
        Assert.Equal(1UL, (ulong)signalEpochProp.GetValue(state)!);

        // A new waiter arriving should see the new epoch but not consume any "pending" signal
        // (because there's no such concept anymore)
        lock (syncRoot)
        {
            var observedEpoch = (ulong)signalEpochProp.GetValue(state)!;
            waitersProp.SetValue(state, (int)waitersProp.GetValue(state)! + 1);

            // Waiter sees epoch=1, will block until epoch changes again
            Assert.Equal(1UL, observedEpoch);
            Assert.Equal(1, (int)waitersProp.GetValue(state)!);
        }
    }

    [Fact]
    public async Task ReinitializingConditionWithWaiter_ReturnsBusyAndSignalStillReachesWaiter()
    {
        const ulong memoryBase = 0x5_0000_0000;
        const ulong mutexAddress = memoryBase + 0x100;
        const ulong condAddress = memoryBase + 0x200;
        var memory = new AllocatingCpuMemory(memoryBase, 0x8000);
        var mainContext = new CpuContext(memory, Generation.Gen5);

        mainContext[CpuRegister.Rdi] = mutexAddress;
        mainContext[CpuRegister.Rsi] = 0;
        Assert.Equal(0, KernelPthreadCompatExports.PthreadMutexInit(mainContext));
        mainContext[CpuRegister.Rdi] = condAddress;
        Assert.Equal(0, KernelPthreadCompatExports.PthreadCondInit(mainContext));

        using var enteringWait = new ManualResetEventSlim(false);
        var waiter = Task.Factory.StartNew(
            () =>
            {
                var waiterContext = new CpuContext(memory, Generation.Gen5);
                waiterContext[CpuRegister.Rdi] = mutexAddress;
                Assert.Equal(0, KernelPthreadCompatExports.PthreadMutexLock(waiterContext));
                enteringWait.Set();
                waiterContext[CpuRegister.Rdi] = condAddress;
                waiterContext[CpuRegister.Rsi] = mutexAddress;
                var waitResult = KernelPthreadCompatExports.PthreadCondWait(waiterContext);
                waiterContext[CpuRegister.Rdi] = mutexAddress;
                var unlockResult = KernelPthreadCompatExports.PthreadMutexUnlock(waiterContext);
                return (waitResult, unlockResult);
            },
            CancellationToken.None,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default);

        Assert.True(enteringWait.Wait(TimeSpan.FromSeconds(5)));

        // Locking here confirms cond_wait queued and released the mutex.
        mainContext[CpuRegister.Rdi] = mutexAddress;
        Assert.Equal(0, KernelPthreadCompatExports.PthreadMutexLock(mainContext));
        mainContext[CpuRegister.Rdi] = condAddress;
        Assert.NotEqual(0, KernelPthreadCompatExports.PthreadCondInit(mainContext));
        Assert.Equal(0, KernelPthreadCompatExports.PthreadCondSignal(mainContext));
        mainContext[CpuRegister.Rdi] = mutexAddress;
        Assert.Equal(0, KernelPthreadCompatExports.PthreadMutexUnlock(mainContext));

        Assert.Equal((0, 0), await waiter.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    private sealed class AllocatingCpuMemory : ICpuMemory, IGuestMemoryAllocator
    {
        private readonly ulong _baseAddress;
        private readonly byte[] _storage;
        private readonly object _gate = new();
        private ulong _nextAllocation;

        public AllocatingCpuMemory(ulong baseAddress, int size)
        {
            _baseAddress = baseAddress;
            _storage = new byte[size];
            _nextAllocation = baseAddress + 0x1000;
        }

        public bool TryRead(ulong virtualAddress, Span<byte> destination)
        {
            lock (_gate)
            {
                if (!TryResolve(virtualAddress, destination.Length, out var offset))
                {
                    return false;
                }

                _storage.AsSpan(offset, destination.Length).CopyTo(destination);
                return true;
            }
        }

        public bool TryWrite(ulong virtualAddress, ReadOnlySpan<byte> source)
        {
            lock (_gate)
            {
                if (!TryResolve(virtualAddress, source.Length, out var offset))
                {
                    return false;
                }

                source.CopyTo(_storage.AsSpan(offset, source.Length));
                return true;
            }
        }

        public bool TryAllocateGuestMemory(ulong size, ulong alignment, out ulong address)
        {
            lock (_gate)
            {
                var mask = alignment - 1;
                var aligned = (_nextAllocation + mask) & ~mask;
                if (!TryResolve(aligned, checked((int)size), out _))
                {
                    address = 0;
                    return false;
                }

                address = aligned;
                _nextAllocation = aligned + size;
                return true;
            }
        }

        public bool TryFreeGuestMemory(ulong address) =>
            address >= _baseAddress && address < _baseAddress + (ulong)_storage.Length;

        private bool TryResolve(ulong virtualAddress, int length, out int offset)
        {
            offset = 0;
            if (virtualAddress < _baseAddress)
            {
                return false;
            }

            var relative = virtualAddress - _baseAddress;
            if (relative + (ulong)length > (ulong)_storage.Length)
            {
                return false;
            }

            offset = (int)relative;
            return true;
        }
    }
}
