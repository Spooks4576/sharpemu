// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Runtime.InteropServices;
using System.Threading;

namespace SharpEmu.Core.Cpu.Native;

public sealed unsafe partial class DirectExecutionBackend
{
	private const int SIGILL = 4;
	private const int SIGBUS = 7;
	private const int SIGSEGV = 11;
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
		Console.Error.WriteLine("[LOADER][INFO] Native Unix signal handlers installed.");
	}

	private static void InstallUnixSignalHandler(int signal)
	{
		UnixSigAction action = default;
		action.Handler = UnixSignalHandlerPtr;
		action.Flags = SA_SIGINFO;
		if (sigaction(signal, &action, null) != 0)
		{
			Console.Error.WriteLine($"[LOADER][WARNING] sigaction({signal}) failed; native Unix fault recovery may be unavailable.");
		}
	}

	private static void UnixSignalHandler(int signal, nint sigInfo, nint ucontext)
	{
		if (ucontext == 0 || _unixSignalDepth > 0)
		{
			RestoreDefaultSignalActionAndReraise(signal);
			return;
		}

		_unixSignalDepth++;
		try
		{
			var backend = _activeExecutionBackend;
			if (backend is null)
			{
				RestoreDefaultSignalActionAndReraise(signal);
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
				backend.VectoredHandler(&pointers);
			}

			if (handled)
			{
				ApplyWindowsContextToUnix(windowsContext, (void*)ucontext);
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

	[DllImport("libc", SetLastError = true)]
	private static extern int sigaction(int signum, UnixSigAction* act, UnixSigAction* oldact);

	[DllImport("libc", SetLastError = true)]
	private static extern int raise(int sig);
}
