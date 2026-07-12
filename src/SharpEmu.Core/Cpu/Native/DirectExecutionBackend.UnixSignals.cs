// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;

namespace SharpEmu.Core.Cpu.Native;

public sealed unsafe partial class DirectExecutionBackend
{
	private const int SIGILL = 4;
	private const int SIGABRT = 6;
	private const int SIGBUS = 7;
	private const int SIGSEGV = 11;
	private const int SIGURG = 23;
	private const int SA_SIGINFO = 0x4;
	private const uint EXCEPTION_ACCESS_VIOLATION = 0xC0000005u;
	private const uint EXCEPTION_ILLEGAL_INSTRUCTION = 0xC000001Du;
	private const int UnixGregsOffset = 40;
	private const int UnixRegR8 = 0;
	private const int UnixRegR9 = 1;
	private const int UnixRegR10 = 2;
	private const int UnixRegR11 = 3;
	private const int UnixRegR12 = 4;
	private const int UnixRegR13 = 5;
	private const int UnixRegR14 = 6;
	private const int UnixRegR15 = 7;
	private const int UnixRegRdi = 8;
	private const int UnixRegRsi = 9;
	private const int UnixRegRbp = 10;
	private const int UnixRegRbx = 11;
	private const int UnixRegRdx = 12;
	private const int UnixRegRax = 13;
	private const int UnixRegRcx = 14;
	private const int UnixRegRsp = 15;
	private const int UnixRegRip = 16;
	private const int UnixRegErr = 19;
	private const int UnixRegCr2 = 22;

	// Main guest executable is mapped at this base; IDA (which loads the ELF at its 0-based
	// p_vaddr) address = guest RIP - this. Surfaced in crash dumps as module_offset.
	private const ulong GuestModuleBaseForDiag = 0x800000000UL;

	private static int _unixSignalHandlersInstalled;
	private static readonly UnixSignalHandlerDelegate UnixSignalHandlerDelegateInstance = UnixSignalHandler;
	private static readonly nint UnixSignalHandlerPtr = Marshal.GetFunctionPointerForDelegate(UnixSignalHandlerDelegateInstance);

	[ThreadStatic]
	private static int _unixSignalDepth;

	[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
	private delegate void UnixSignalHandlerDelegate(int signal, nint sigInfo, nint ucontext);

	private unsafe struct UnixSigAction
	{
		public nint Handler;
		public fixed ulong Mask[16];
		public int Flags;
		public nint Restorer;
	}

	private static void InstallUnixSignalHandlers()
	{
		if (Interlocked.Exchange(ref _unixSignalHandlersInstalled, 1) != 0)
		{
			Console.Error.WriteLine("[LOADER][INFO] Native Unix signal handlers already installed.");
			return;
		}

		InstallUnixSignalHandler(SIGSEGV);
		InstallUnixSignalHandler(SIGBUS);
		InstallUnixSignalHandler(SIGILL);
		InstallUnixSignalHandler(SIGABRT);
		InstallUnixSignalHandler(SIGURG);
		Console.Error.WriteLine("[LOADER][INFO] Native Unix signal handlers installed.");
	}

	// coreclr's own handlers, saved at install time. The CLR converts hardware faults in
	// managed code (null-reference reads, GC probes) into managed exceptions via its SIGSEGV
	// handler; replacing it without chaining corrupts the runtime — any managed NRE then gets
	// misrouted into the guest return stub and the process later dies with the misleading
	// "UnmanagedCallersOnly" fail-fast or a SIGSEGV inside libcoreclr.
	private static readonly UnixSigAction[] PreviousUnixSignalActions = new UnixSigAction[64];

	private static void InstallUnixSignalHandler(int signal)
	{
		UnixSigAction action = default;
		action.Handler = UnixSignalHandlerPtr;
		action.Flags = SA_SIGINFO;
		UnixSigAction previous = default;
		if (sigaction(signal, &action, &previous) != 0)
		{
			Console.Error.WriteLine($"[LOADER][WARNING] sigaction({signal}) failed; native Unix fault recovery may be unavailable.");
			return;
		}

		if ((uint)signal < (uint)PreviousUnixSignalActions.Length)
		{
			PreviousUnixSignalActions[signal] = previous;
		}
	}

	private static bool TryChainPreviousUnixHandler(int signal, nint sigInfo, nint ucontext)
	{
		if ((uint)signal >= (uint)PreviousUnixSignalActions.Length)
		{
			return false;
		}

		var previous = PreviousUnixSignalActions[signal];
		var handler = previous.Handler;
		if (handler == 0 || handler == 1)
		{
			// SIG_DFL / SIG_IGN — nothing meaningful to chain to.
			return false;
		}

		if ((previous.Flags & SA_SIGINFO) != 0)
		{
			((delegate* unmanaged[Cdecl]<int, nint, nint, void>)handler)(signal, sigInfo, ucontext);
		}
		else
		{
			((delegate* unmanaged[Cdecl]<int, void>)handler)(signal);
		}

		return true;
	}

	// Guest code lives at the module base (0x8_0000_0000) and in the vmem arena
	// (0x10_0000_0000+); host JIT/CLR/libc code sits at 0x55…/0x7F… — far above.
	private static bool IsGuestCodeRip(ulong rip) =>
		rip >= 0x0000_0004_0000_0000UL && rip < 0x0000_5500_0000_0000UL;

	// Context-sampling slots for TryCaptureUnixThreadContext: the watchdog sends SIGURG to a
	// specific running guest thread via tgkill, and the handler copies that thread's ucontext
	// registers here. Allocation-free by design — the sampled thread is normally executing
	// raw guest code.
	private static int _unixContextSampleGate;
	private static int _unixContextSampleReady;
	private static ulong _unixSampleRip;
	private static ulong _unixSampleRsp;
	private static ulong _unixSampleRbp;
	private static ulong _unixSampleRax;
	private static ulong _unixSampleRbx;
	private static ulong _unixSampleRcx;
	private static ulong _unixSampleRdx;

	private static void UnixSignalHandler(int signal, nint sigInfo, nint ucontext)
	{
		if (signal == SIGURG)
		{
			if (ucontext != 0)
			{
				_unixSampleRip = ReadUnixCtxU64((void*)ucontext, UnixRegRip);
				_unixSampleRsp = ReadUnixCtxU64((void*)ucontext, UnixRegRsp);
				_unixSampleRbp = ReadUnixCtxU64((void*)ucontext, UnixRegRbp);
				_unixSampleRax = ReadUnixCtxU64((void*)ucontext, UnixRegRax);
				_unixSampleRbx = ReadUnixCtxU64((void*)ucontext, UnixRegRbx);
				_unixSampleRcx = ReadUnixCtxU64((void*)ucontext, UnixRegRcx);
				_unixSampleRdx = ReadUnixCtxU64((void*)ucontext, UnixRegRdx);
				Volatile.Write(ref _unixContextSampleReady, 1);
			}
			return;
		}

		if (signal == SIGABRT)
		{
			if (_unixSignalDepth == 0)
			{
				_unixSignalDepth++;
				try
				{
					var backend = _activeExecutionBackend;
					if (backend is not null)
					{
						Console.Error.WriteLine("[LOADER][ERROR] SIGABRT caught (likely CLR heap-corruption fail-fast); dumping recent imports:");
						backend.DumpRecentImportTrace();
						Console.Error.Flush();
					}
				}
				catch
				{
				}
				finally
				{
					_unixSignalDepth--;
				}
			}

			RestoreDefaultSignalActionAndReraise(signal);
			return;
		}

		if (ucontext == 0 || _unixSignalDepth > 0)
		{
			// Unrecoverable: either no context, or a fault occurred while already handling one
			// (nested/stack fault). We cannot safely recover, but capture the faulting RIP and
			// fault address so the crash is not a black box (previously exited 139 with no RIP).
			if (ucontext != 0)
			{
				var nestedRip = ReadUnixCtxU64((void*)ucontext, UnixRegRip);
				var nestedTarget = signal == SIGILL ? nestedRip : ReadUnixCtxU64((void*)ucontext, UnixRegCr2);
				if (nestedTarget == 0 && sigInfo != 0)
				{
					nestedTarget = (ulong)Marshal.ReadIntPtr(sigInfo + 16);
				}
				var moduleOffset = nestedRip >= GuestModuleBaseForDiag ? nestedRip - GuestModuleBaseForDiag : nestedRip;
				try
				{
					Console.Error.WriteLine(
						$"[LOADER][ERROR] Unrecoverable signal {signal} (depth={_unixSignalDepth}) at RIP=0x{nestedRip:X16} " +
						$"(module_offset=0x{moduleOffset:X}) target=0x{nestedTarget:X16} — not caught, terminating.");
					Console.Error.Flush();
				}
				catch
				{
				}
			}
			RestoreDefaultSignalActionAndReraise(signal);
			return;
		}

		_unixSignalDepth++;
		try
		{
			var backend = _activeExecutionBackend;
			if (backend is null)
			{
				if (!TryChainPreviousUnixHandler(signal, sigInfo, ucontext))
				{
					RestoreDefaultSignalActionAndReraise(signal);
				}
				return;
			}

			var rip = ReadUnixCtxU64((void*)ucontext, UnixRegRip);
			var rsp = ReadUnixCtxU64((void*)ucontext, UnixRegRsp);
			var faultAddress = signal == SIGILL
				? rip
				: ReadUnixCtxU64((void*)ucontext, UnixRegCr2);
			if (faultAddress == 0 && sigInfo != 0)
			{
				faultAddress = (ulong)Marshal.ReadIntPtr(sigInfo + 16);
			}

			var pageFaultError = signal == SIGILL ? 0x10UL : ReadUnixCtxU64((void*)ucontext, UnixRegErr);
			var accessType = signal == SIGILL
				? 8UL
				: ((pageFaultError & 0x10) != 0 ? 8UL : ((pageFaultError & 0x2) != 0 ? 1UL : 0UL));

			EXCEPTION_RECORD record = default;
			record.ExceptionCode = signal == SIGILL ? EXCEPTION_ILLEGAL_INSTRUCTION : EXCEPTION_ACCESS_VIOLATION;
			record.ExceptionAddress = (void*)rip;
			record.NumberParameters = signal == SIGILL ? 0u : 2u;
			if (signal != SIGILL)
			{
				record.ExceptionInformation[0] = accessType;
				record.ExceptionInformation[1] = faultAddress;
			}

			byte* windowsContext = stackalloc byte[Win64ContextSize];
			FillWindowsContextFromUnix((void*)ucontext, windowsContext);
			EXCEPTION_POINTERS pointers = new()
			{
				ExceptionRecord = &record,
				ContextRecord = windowsContext
			};

			var handled = record.ExceptionCode == EXCEPTION_ACCESS_VIOLATION &&
				backend.TryHandleLazyCommittedPage(&record, rip, rsp);
			if (!handled)
			{
				handled = backend.VectoredHandler(&pointers) == -1;
			}

			if (handled)
			{
				ApplyWindowsContextToUnix(windowsContext, (void*)ucontext);
				return;
			}

			if (!IsGuestCodeRip(rip))
			{
				// Fault in host/CLR code that isn't a guest lazy-commit touch: this is the
				// runtime's fault to process (managed NRE, GC probe). Hand it back to the
				// handler coreclr installed instead of hijacking a CLR thread into the
				// guest return stub, which corrupts the runtime.
				if (!TryChainPreviousUnixHandler(signal, sigInfo, ucontext))
				{
					RestoreDefaultSignalActionAndReraise(signal);
				}
				return;
			}

			backend.LastError = $"Unix native signal {signal} at RIP=0x{rip:X16}, target=0x{faultAddress:X16}";
			backend.ActiveForcedGuestExit = true;
			if (backend._guestReturnStub != 0)
			{
				WriteUnixCtxU64((void*)ucontext, UnixRegRip, (ulong)backend._guestReturnStub);
				WriteUnixCtxU64((void*)ucontext, UnixRegRax, 0xFFFF_FFFFu);
				return;
			}

			RestoreDefaultSignalActionAndReraise(signal);
		}
		finally
		{
			_unixSignalDepth--;
		}
	}

	private static void FillWindowsContextFromUnix(void* unixContext, byte* windowsContext)
	{
		*(ulong*)(windowsContext + CTX_RAX) = ReadUnixCtxU64(unixContext, UnixRegRax);
		*(ulong*)(windowsContext + CTX_RCX) = ReadUnixCtxU64(unixContext, UnixRegRcx);
		*(ulong*)(windowsContext + CTX_RDX) = ReadUnixCtxU64(unixContext, UnixRegRdx);
		*(ulong*)(windowsContext + CTX_RBX) = ReadUnixCtxU64(unixContext, UnixRegRbx);
		*(ulong*)(windowsContext + CTX_RSP) = ReadUnixCtxU64(unixContext, UnixRegRsp);
		*(ulong*)(windowsContext + CTX_RBP) = ReadUnixCtxU64(unixContext, UnixRegRbp);
		*(ulong*)(windowsContext + CTX_RSI) = ReadUnixCtxU64(unixContext, UnixRegRsi);
		*(ulong*)(windowsContext + CTX_RDI) = ReadUnixCtxU64(unixContext, UnixRegRdi);
		*(ulong*)(windowsContext + CTX_R8) = ReadUnixCtxU64(unixContext, UnixRegR8);
		*(ulong*)(windowsContext + CTX_R9) = ReadUnixCtxU64(unixContext, UnixRegR9);
		*(ulong*)(windowsContext + CTX_R10) = ReadUnixCtxU64(unixContext, UnixRegR10);
		*(ulong*)(windowsContext + CTX_R11) = ReadUnixCtxU64(unixContext, UnixRegR11);
		*(ulong*)(windowsContext + CTX_R12) = ReadUnixCtxU64(unixContext, UnixRegR12);
		*(ulong*)(windowsContext + CTX_R13) = ReadUnixCtxU64(unixContext, UnixRegR13);
		*(ulong*)(windowsContext + CTX_R14) = ReadUnixCtxU64(unixContext, UnixRegR14);
		*(ulong*)(windowsContext + CTX_R15) = ReadUnixCtxU64(unixContext, UnixRegR15);
		*(ulong*)(windowsContext + CTX_RIP) = ReadUnixCtxU64(unixContext, UnixRegRip);
	}

	private static void ApplyWindowsContextToUnix(byte* windowsContext, void* unixContext)
	{
		WriteUnixCtxU64(unixContext, UnixRegRax, *(ulong*)(windowsContext + CTX_RAX));
		WriteUnixCtxU64(unixContext, UnixRegRcx, *(ulong*)(windowsContext + CTX_RCX));
		WriteUnixCtxU64(unixContext, UnixRegRdx, *(ulong*)(windowsContext + CTX_RDX));
		WriteUnixCtxU64(unixContext, UnixRegRbx, *(ulong*)(windowsContext + CTX_RBX));
		WriteUnixCtxU64(unixContext, UnixRegRsp, *(ulong*)(windowsContext + CTX_RSP));
		WriteUnixCtxU64(unixContext, UnixRegRbp, *(ulong*)(windowsContext + CTX_RBP));
		WriteUnixCtxU64(unixContext, UnixRegRsi, *(ulong*)(windowsContext + CTX_RSI));
		WriteUnixCtxU64(unixContext, UnixRegRdi, *(ulong*)(windowsContext + CTX_RDI));
		WriteUnixCtxU64(unixContext, UnixRegR8, *(ulong*)(windowsContext + CTX_R8));
		WriteUnixCtxU64(unixContext, UnixRegR9, *(ulong*)(windowsContext + CTX_R9));
		WriteUnixCtxU64(unixContext, UnixRegR10, *(ulong*)(windowsContext + CTX_R10));
		WriteUnixCtxU64(unixContext, UnixRegR11, *(ulong*)(windowsContext + CTX_R11));
		WriteUnixCtxU64(unixContext, UnixRegR12, *(ulong*)(windowsContext + CTX_R12));
		WriteUnixCtxU64(unixContext, UnixRegR13, *(ulong*)(windowsContext + CTX_R13));
		WriteUnixCtxU64(unixContext, UnixRegR14, *(ulong*)(windowsContext + CTX_R14));
		WriteUnixCtxU64(unixContext, UnixRegR15, *(ulong*)(windowsContext + CTX_R15));
		WriteUnixCtxU64(unixContext, UnixRegRip, *(ulong*)(windowsContext + CTX_RIP));
	}

	private static ulong ReadUnixCtxU64(void* context, int gregIndex) =>
		*(ulong*)((byte*)context + UnixGregsOffset + gregIndex * sizeof(ulong));

	private static void WriteUnixCtxU64(void* context, int gregIndex, ulong value) =>
		*(ulong*)((byte*)context + UnixGregsOffset + gregIndex * sizeof(ulong)) = value;

	private static void RestoreDefaultSignalActionAndReraise(int signal)
	{
		UnixSigAction action = default;
		action.Handler = 0;
		_ = sigaction(signal, &action, null);
		_ = raise(signal);
	}

	// Linux counterpart of the Windows SuspendThread/GetThreadContext capture: signal the
	// target thread with SIGURG (via tgkill so only that thread receives it) and wait briefly
	// for its handler to publish the interrupted register state. Only meaningful for threads
	// currently executing guest code; the watchdog uses it to find where a spinning guest
	// thread is stuck.
	private static bool TryCaptureUnixThreadContext(int hostThreadId, out HostThreadContextSnapshot snapshot)
	{
		snapshot = default;
		if (hostThreadId == 0 || hostThreadId == unchecked((int)GetCurrentThreadId()))
		{
			return false;
		}

		if (Interlocked.CompareExchange(ref _unixContextSampleGate, 1, 0) != 0)
		{
			return false;
		}

		try
		{
			Volatile.Write(ref _unixContextSampleReady, 0);
			if (syscall(TgkillSyscallNumber, getpid(), hostThreadId, SIGURG) != 0)
			{
				return false;
			}

			var spinWatch = Stopwatch.GetTimestamp();
			var timeoutTicks = Stopwatch.Frequency / 100; // 10ms
			while (Volatile.Read(ref _unixContextSampleReady) == 0)
			{
				if (Stopwatch.GetTimestamp() - spinWatch > timeoutTicks)
				{
					return false;
				}
				Thread.SpinWait(64);
			}

			snapshot = new HostThreadContextSnapshot(
				true,
				_unixSampleRip,
				_unixSampleRsp,
				_unixSampleRbp,
				_unixSampleRax,
				_unixSampleRbx,
				_unixSampleRcx,
				_unixSampleRdx);
			return true;
		}
		finally
		{
			Volatile.Write(ref _unixContextSampleGate, 0);
		}
	}

	private const long TgkillSyscallNumber = 234;

	[DllImport("libc", SetLastError = true)]
	private static extern long syscall(long number, long arg1, long arg2, long arg3);

	[DllImport("libc")]
	private static extern int getpid();

	[DllImport("libc", SetLastError = true)]
	private static extern int sigaction(int signum, UnixSigAction* act, UnixSigAction* oldact);

	[DllImport("libc", SetLastError = true)]
	private static extern int raise(int sig);
}
