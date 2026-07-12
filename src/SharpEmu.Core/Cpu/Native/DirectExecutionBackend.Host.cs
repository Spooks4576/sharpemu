// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Diagnostics;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Threading;

namespace SharpEmu.Core.Cpu.Native;

public sealed unsafe partial class DirectExecutionBackend
{
	private const int PROT_NONE = 0x0;
	private const int PROT_READ = 0x1;
	private const int PROT_WRITE = 0x2;
	private const int PROT_EXEC = 0x4;
	private const int MAP_PRIVATE = 0x02;
	private const int MAP_ANONYMOUS = 0x20;
	private const int MAP_FIXED = 0x10;
	private const int MAP_FIXED_NOREPLACE = 0x100000;
	private const int O_RDONLY = 0;

	private static long _nextUnixTlsIndex;
	private static ConcurrentDictionary<ulong, nuint>? _unixAllocationSizes;
	private static ConcurrentDictionary<ulong, nuint> UnixAllocationSizes =>
		_unixAllocationSizes ??= new ConcurrentDictionary<ulong, nuint>();
	private static readonly ThreadLocal<Dictionary<uint, nint>> UnixTlsValues = new(() => new Dictionary<uint, nint>());
	private static readonly HostTlsGetValueDelegate UnixTlsGetValueDelegate = UnixTlsGetValue;
	private static readonly HostQueryPerformanceCounterDelegate UnixQueryPerformanceCounterDelegate = UnixQueryPerformanceCounter;
	private static readonly HostSwitchToThreadDelegate UnixSwitchToThreadDelegate = UnixSwitchToThread;
	private static readonly HostSleepDelegate UnixSleepDelegate = UnixSleep;
	private static readonly nint UnixTlsGetValueTarget = Marshal.GetFunctionPointerForDelegate(UnixTlsGetValueDelegate);
	private static readonly nint UnixQueryPerformanceCounterTarget = Marshal.GetFunctionPointerForDelegate(UnixQueryPerformanceCounterDelegate);
	private static readonly nint UnixSwitchToThreadTarget = Marshal.GetFunctionPointerForDelegate(UnixSwitchToThreadDelegate);
	private static readonly nint UnixSleepTarget = Marshal.GetFunctionPointerForDelegate(UnixSleepDelegate);
	private static readonly nint UnixTlsGetValueThunk = CreateWin64UIntThunk(UnixTlsGetValueTarget);
	private static readonly nint UnixQueryPerformanceCounterThunk = CreateWin64PointerThunk(UnixQueryPerformanceCounterTarget);
	private static readonly nint UnixSwitchToThreadThunk = CreateWin64NoArgThunk(UnixSwitchToThreadTarget);
	private static readonly nint UnixSleepThunk = CreateWin64UIntThunk(UnixSleepTarget);

	[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
	private delegate nint HostTlsGetValueDelegate(uint index);

	[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
	private delegate int HostQueryPerformanceCounterDelegate(nint counter);

	[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
	private delegate int HostSwitchToThreadDelegate();

	[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
	private delegate void HostSleepDelegate(uint milliseconds);

	private static nint ResolveHostProcedure(string name)
	{
		if (OperatingSystem.IsWindows())
		{
			var kernel32 = WindowsGetModuleHandle("kernel32.dll");
			return kernel32 != 0 ? WindowsGetProcAddress(kernel32, name) : 0;
		}

		return name switch
		{
			"TlsGetValue" => UnixTlsGetValueThunk,
			"QueryPerformanceCounter" => UnixQueryPerformanceCounterThunk,
			"SwitchToThread" => UnixSwitchToThreadThunk,
			"Sleep" => UnixSleepThunk,
			_ => 0
		};
	}

	private static uint TlsAlloc()
	{
		if (OperatingSystem.IsWindows())
		{
			return WindowsTlsAlloc();
		}

		var index = Interlocked.Increment(ref _nextUnixTlsIndex);
		return index > uint.MaxValue ? uint.MaxValue : (uint)index;
	}

	private static bool TlsFree(uint index)
	{
		if (OperatingSystem.IsWindows())
		{
			return WindowsTlsFree(index);
		}

		UnixTlsValues.Value?.Remove(index);
		return true;
	}

	private static bool TlsSetValue(uint index, nint value)
	{
		if (OperatingSystem.IsWindows())
		{
			return WindowsTlsSetValue(index, value);
		}

		UnixTlsValues.Value![index] = value;
		return true;
	}

	private static nint TlsGetValue(uint index)
	{
		if (OperatingSystem.IsWindows())
		{
			return WindowsTlsGetValue(index);
		}

		return UnixTlsGetValue(index);
	}

	private static nint UnixTlsGetValue(uint index) =>
		UnixTlsValues.Value is { } values && values.TryGetValue(index, out var value) ? value : 0;

	private static int UnixQueryPerformanceCounter(nint counter)
	{
		if (counter == 0)
		{
			return 0;
		}

		*(long*)counter = Stopwatch.GetTimestamp();
		return 1;
	}

	private static int UnixSwitchToThread()
	{
		Thread.Yield();
		return 1;
	}

	private static void UnixSleep(uint milliseconds)
	{
		Thread.Sleep(milliseconds > int.MaxValue ? int.MaxValue : (int)milliseconds);
	}

	private static void* VirtualAlloc(void* address, nuint size, uint allocationType, uint protection)
	{
		if (OperatingSystem.IsWindows())
		{
			return WindowsVirtualAlloc(address, size, allocationType, protection);
		}

		if ((allocationType & MEM_COMMIT) != 0 && (allocationType & MEM_RESERVE) == 0)
		{
			return mprotect(address, size, ToUnixProtection(protection)) == 0 ? address : null;
		}

		var flags = MAP_PRIVATE | MAP_ANONYMOUS;
		if (address != null)
		{
			flags |= MAP_FIXED_NOREPLACE;
		}

		var alignedSize = (nuint)AlignUp((ulong)size, 0x1000);
		var prot = (allocationType & MEM_COMMIT) != 0 ? ToUnixProtection(protection) : PROT_NONE;
		var result = mmap(address, alignedSize, prot, flags, -1, 0);
		if (result == (void*)-1)
		{
			return null;
		}

		UnixAllocationSizes[(ulong)result] = alignedSize;
		return result;
	}

	private static bool VirtualFree(void* address, nuint size, uint freeType)
	{
		if (OperatingSystem.IsWindows())
		{
			return WindowsVirtualFree(address, size, freeType);
		}

		if (freeType != MEM_RELEASE || address == null)
		{
			return false;
		}

		var addressKey = (ulong)address;
		var length = size != 0
			? (nuint)AlignUp((ulong)size, 0x1000)
			: (UnixAllocationSizes.TryGetValue(addressKey, out var trackedSize)
				? trackedSize
				: QueryUnixRegionSize(addressKey));

		if (length == 0 || munmap(address, length) != 0)
		{
			return false;
		}

		UnixAllocationSizes.TryRemove(addressKey, out _);
		return true;
	}

	private static bool VirtualProtect(void* address, nuint size, uint protection, uint* oldProtection)
	{
		if (OperatingSystem.IsWindows())
		{
			return WindowsVirtualProtect(address, size, protection, oldProtection);
		}

		if (oldProtection != null)
		{
			*oldProtection = QueryUnixProtection((ulong)address);
		}

		var start = AlignDown((ulong)address, 0x1000);
		var end = AlignUp(checked((ulong)address + size), 0x1000);
		return mprotect((void*)start, (nuint)(end - start), ToUnixProtection(protection)) == 0;
	}

	private static void* GetCurrentProcess() =>
		OperatingSystem.IsWindows() ? WindowsGetCurrentProcess() : null;

	private static bool FlushInstructionCache(void* process, void* address, nuint size) =>
		OperatingSystem.IsWindows() ? WindowsFlushInstructionCache(process, address, size) : true;

	private static nuint VirtualQuery(void* address, out MEMORY_BASIC_INFORMATION64 buffer, nuint length)
	{
		if (OperatingSystem.IsWindows())
		{
			return WindowsVirtualQuery(address, out buffer, length);
		}

		return TryQueryUnixMemory((ulong)address, out buffer) ? (nuint)sizeof(MEMORY_BASIC_INFORMATION64) : 0;
	}

	private static void* AddVectoredExceptionHandler(uint first, nint handler) =>
		OperatingSystem.IsWindows() ? WindowsAddVectoredExceptionHandler(first, handler) : null;

	private static uint RemoveVectoredExceptionHandler(void* handle) =>
		OperatingSystem.IsWindows() ? WindowsRemoveVectoredExceptionHandler(handle) : 0;

	private static nint SetUnhandledExceptionFilter(nint filter) =>
		OperatingSystem.IsWindows() ? WindowsSetUnhandledExceptionFilter(filter) : 0;

	private static uint GetCurrentThreadId() =>
		OperatingSystem.IsWindows() ? WindowsGetCurrentThreadId() : unchecked((uint)Environment.CurrentManagedThreadId);

	private static nint GetCurrentThread() =>
		OperatingSystem.IsWindows() ? WindowsGetCurrentThread() : 1;

	private static nuint SetThreadAffinityMask(nint thread, nuint mask) =>
		OperatingSystem.IsWindows() ? WindowsSetThreadAffinityMask(thread, mask) : 1;

	private static nint OpenThread(uint desiredAccess, bool inheritHandle, uint threadId) =>
		OperatingSystem.IsWindows() ? WindowsOpenThread(desiredAccess, inheritHandle, threadId) : 0;

	private static uint SuspendThread(nint thread) =>
		OperatingSystem.IsWindows() ? WindowsSuspendThread(thread) : uint.MaxValue;

	private static uint ResumeThread(nint thread) =>
		OperatingSystem.IsWindows() ? WindowsResumeThread(thread) : uint.MaxValue;

	private static bool GetThreadContext(nint thread, void* context) =>
		OperatingSystem.IsWindows() && WindowsGetThreadContext(thread, context);

	private static bool CloseHandle(nint handle) =>
		OperatingSystem.IsWindows() && WindowsCloseHandle(handle);

	private static nint CreateWin64UIntThunk(nint target)
	{
		var code = (byte*)VirtualAlloc(null, 32, MEM_COMMIT | MEM_RESERVE, PAGE_EXECUTE_READWRITE);
		if (code == null)
		{
			return 0;
		}

		var offset = 0;
		code[offset++] = 0x89; code[offset++] = 0xCF; // mov edi, ecx
		code[offset++] = 0x48; code[offset++] = 0xB8; // mov rax, target
		*(nint*)(code + offset) = target;
		offset += sizeof(nint);
		code[offset++] = 0xFF; code[offset++] = 0xE0; // jmp rax
		_ = VirtualProtect(code, 32, PAGE_EXECUTE_READ, null);
		_ = FlushInstructionCache(GetCurrentProcess(), code, 32);
		return (nint)code;
	}

	private static nint CreateWin64NoArgThunk(nint target)
	{
		var code = (byte*)VirtualAlloc(null, 32, MEM_COMMIT | MEM_RESERVE, PAGE_EXECUTE_READWRITE);
		if (code == null)
		{
			return 0;
		}

		var offset = 0;
		code[offset++] = 0x48; code[offset++] = 0xB8; // mov rax, target
		*(nint*)(code + offset) = target;
		offset += sizeof(nint);
		code[offset++] = 0xFF; code[offset++] = 0xE0; // jmp rax
		_ = VirtualProtect(code, 32, PAGE_EXECUTE_READ, null);
		_ = FlushInstructionCache(GetCurrentProcess(), code, 32);
		return (nint)code;
	}

	private static nint CreateWin64PointerThunk(nint target)
	{
		var code = (byte*)VirtualAlloc(null, 32, MEM_COMMIT | MEM_RESERVE, PAGE_EXECUTE_READWRITE);
		if (code == null)
		{
			return 0;
		}

		var offset = 0;
		code[offset++] = 0x48; code[offset++] = 0x89; code[offset++] = 0xCF; // mov rdi, rcx
		code[offset++] = 0x48; code[offset++] = 0xB8; // mov rax, target
		*(nint*)(code + offset) = target;
		offset += sizeof(nint);
		code[offset++] = 0xFF; code[offset++] = 0xE0; // jmp rax
		_ = VirtualProtect(code, 32, PAGE_EXECUTE_READ, null);
		_ = FlushInstructionCache(GetCurrentProcess(), code, 32);
		return (nint)code;
	}

	private static nint CreateWin64ImportGatewayThunk(nint target)
	{
		if (OperatingSystem.IsWindows())
		{
			return target;
		}

		var code = (byte*)VirtualAlloc(null, 64, MEM_COMMIT | MEM_RESERVE, PAGE_EXECUTE_READWRITE);
		if (code == null)
		{
			return 0;
		}

		var offset = 0;
		code[offset++] = 0x48; code[offset++] = 0x89; code[offset++] = 0xCF; // mov rdi, rcx
		code[offset++] = 0x89; code[offset++] = 0xD6;                         // mov esi, edx
		code[offset++] = 0x4C; code[offset++] = 0x89; code[offset++] = 0xC2;   // mov rdx, r8
		code[offset++] = 0x48; code[offset++] = 0xB8;                         // mov rax, target
		*(nint*)(code + offset) = target;
		offset += sizeof(nint);
		code[offset++] = 0xFF; code[offset++] = 0xE0;                         // jmp rax
		_ = VirtualProtect(code, 64, PAGE_EXECUTE_READ, null);
		_ = FlushInstructionCache(GetCurrentProcess(), code, 64);
		return (nint)code;
	}

	private static int ToUnixProtection(uint protection) =>
		protection switch
		{
			1 => PROT_NONE,
			2 => PROT_READ,
			PAGE_READWRITE => PROT_READ | PROT_WRITE,
			16 => PROT_EXEC,
			PAGE_EXECUTE_READ => PROT_READ | PROT_EXEC,
			PAGE_EXECUTE_READWRITE or 128 => PROT_READ | PROT_WRITE | PROT_EXEC,
			_ => PROT_READ | PROT_WRITE
		};

	private static uint QueryUnixProtection(ulong address) =>
		TryQueryUnixMemory(address, out var info) ? info.Protect : PAGE_READWRITE;

	private static nuint QueryUnixRegionSize(ulong address) =>
		TryQueryUnixMemory(address, out var info) ? (nuint)info.RegionSize : 0;

	// Allocated eagerly from native memory: this buffer is used from the SIGSEGV
	// handler, where the GC must not be involved at all (a GC allocation here crashed
	// coreclr internals once the maps file outgrew a lazily grown buffer). 8MB fits
	// ~100k mappings. Guarded by an Interlocked spin gate for the same reason — no
	// managed blocking in signal context.
	private const int UnixMapsBufferSize = 8 * 1024 * 1024;
	private static readonly byte* UnixMapsBuffer = (byte*)NativeMemory.Alloc(UnixMapsBufferSize);
	private static int _unixMapsGate;

	private static bool TryQueryUnixMemory(ulong address, out MEMORY_BASIC_INFORMATION64 info)
	{
		while (Interlocked.CompareExchange(ref _unixMapsGate, 1, 0) != 0)
		{
			Thread.SpinWait(64);
		}

		try
		{
			return TryQueryUnixMemoryLocked(address, out info);
		}
		finally
		{
			Volatile.Write(ref _unixMapsGate, 0);
		}
	}

	private static bool TryQueryUnixMemoryLocked(ulong address, out MEMORY_BASIC_INFORMATION64 info)
	{
		info = default;
		var maps = UnixMapsBuffer;
		var mapsLength = ReadUnixProcSelfMaps(maps, UnixMapsBufferSize);
		if (mapsLength == UnixMapsBufferSize)
		{
			// Truncated listing: mappings past the cut-off would be misreported as
			// MEM_FREE and later zero-wiped. Fail the query instead; the fault then
			// surfaces with full diagnostics rather than corrupting guest memory.
			return false;
		}
		var offset = 0;
		var pageAddress = AlignDown(address, 0x1000);
		var nextMappedAddress = ulong.MaxValue;
		while (offset < mapsLength)
		{
			var lineStart = offset;
			while (offset < mapsLength && maps[offset] != (byte)'\n')
			{
				offset++;
			}

			var lineEnd = offset;
			if (offset < mapsLength && maps[offset] == (byte)'\n')
			{
				offset++;
			}

			var dash = lineStart;
			while (dash < lineEnd && maps[dash] != (byte)'-')
			{
				dash++;
			}

			var space = dash + 1;
			while (space < lineEnd && maps[space] != (byte)' ')
			{
				space++;
			}

			if (dash == lineStart ||
				dash >= lineEnd ||
				space >= lineEnd ||
				!TryParseHexBytes(maps, lineStart, dash, out var start) ||
				!TryParseHexBytes(maps, dash + 1, space, out var end))
			{
				continue;
			}

			if (address >= start && address < end)
			{
				var permsStart = space + 1;
				info.BaseAddress = start;
				info.AllocationBase = start;
				info.RegionSize = end - start;
				info.State = MEM_COMMIT;
				info.Protect = UnixPermsToProtection(maps, permsStart, lineEnd);
				info.AllocationProtect = info.Protect;
				return true;
			}

			if (start > pageAddress && start < nextMappedAddress)
			{
				nextMappedAddress = start;
			}
		}

		var freeSize = nextMappedAddress == ulong.MaxValue
			? 0x1000UL
			: Math.Max(0x1000UL, nextMappedAddress - pageAddress);
		info.BaseAddress = pageAddress;
		info.AllocationBase = pageAddress;
		info.RegionSize = freeSize;
		info.State = MEM_FREE;
		info.Protect = PAGE_NOACCESS;
		info.AllocationProtect = PAGE_NOACCESS;
		return true;
	}

	private static int ReadUnixProcSelfMaps(byte* destination, int capacity)
	{
		Span<byte> path = stackalloc byte[16];
		path[0] = (byte)'/';
		path[1] = (byte)'p';
		path[2] = (byte)'r';
		path[3] = (byte)'o';
		path[4] = (byte)'c';
		path[5] = (byte)'/';
		path[6] = (byte)'s';
		path[7] = (byte)'e';
		path[8] = (byte)'l';
		path[9] = (byte)'f';
		path[10] = (byte)'/';
		path[11] = (byte)'m';
		path[12] = (byte)'a';
		path[13] = (byte)'p';
		path[14] = (byte)'s';
		path[15] = 0;

		fixed (byte* pathPointer = path)
		{
			var fd = unix_open(pathPointer, O_RDONLY);
			if (fd < 0)
			{
				return 0;
			}

			var total = 0;
			try
			{
				while (total < capacity)
				{
					var read = unix_read(fd, destination + total, (nuint)(capacity - total));
					if (read <= 0)
					{
						break;
					}

					total += checked((int)read);
				}
			}
			finally
			{
				_ = unix_close(fd);
			}

			return total;
		}
	}

	private static bool TryParseHexBytes(byte* bytes, int start, int end, out ulong value)
	{
		value = 0;
		if (start >= end)
		{
			return false;
		}

		for (var i = start; i < end; i++)
		{
			var b = bytes[i];
			uint digit;
			if (b >= (byte)'0' && b <= (byte)'9')
			{
				digit = (uint)(b - (byte)'0');
			}
			else if (b >= (byte)'a' && b <= (byte)'f')
			{
				digit = (uint)(b - (byte)'a' + 10);
			}
			else if (b >= (byte)'A' && b <= (byte)'F')
			{
				digit = (uint)(b - (byte)'A' + 10);
			}
			else
			{
				return false;
			}

			value = (value << 4) | digit;
		}

		return true;
	}

	private static uint UnixPermsToProtection(byte* bytes, int start, int lineEnd)
	{
		var read = start < lineEnd && bytes[start] == (byte)'r';
		var write = start + 1 < lineEnd && bytes[start + 1] == (byte)'w';
		var exec = start + 2 < lineEnd && bytes[start + 2] == (byte)'x';
		if (exec)
		{
			return write ? PAGE_EXECUTE_READWRITE : PAGE_EXECUTE_READ;
		}

		return write ? PAGE_READWRITE : (read ? 2u : 1u);
	}

	[DllImport("libc", SetLastError = true)]
	private static extern void* mmap(void* addr, nuint length, int prot, int flags, int fd, nint offset);

	[DllImport("libc", SetLastError = true)]
	private static extern int munmap(void* addr, nuint length);

	[DllImport("libc", SetLastError = true)]
	private static extern int mprotect(void* addr, nuint len, int prot);

	[DllImport("libc", EntryPoint = "open", SetLastError = true)]
	private static extern int unix_open(byte* pathname, int flags);

	[DllImport("libc", EntryPoint = "read", SetLastError = true)]
	private static extern nint unix_read(int fd, void* buffer, nuint count);

	[DllImport("libc", EntryPoint = "close", SetLastError = true)]
	private static extern int unix_close(int fd);
}
