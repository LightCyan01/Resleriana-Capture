#define WIN32_LEAN_AND_MEAN
#include <Windows.h>
#include <immintrin.h>
#include <intrin.h>
#include <cstdint>
#include "patch.h"

namespace {
constexpr std::uint32_t max_span = 64;
constexpr unsigned max_threads = 4096;
constexpr unsigned max_passes = 16;
constexpr std::uintptr_t max_stack = 1024 * 1024;
constexpr std::uintptr_t commit_rva = 0x2D30;
constexpr std::uintptr_t routing_commit_rva = 0x2810;
constexpr std::uintptr_t registry_rva = 0x31A08;
constexpr LONG no_more_threads = static_cast<LONG>(0x8000001Au);
using NextThread = LONG(NTAPI*)(HANDLE, HANDLE, ACCESS_MASK, ULONG, ULONG, PHANDLE);
using BufferSize = std::uint32_t(__cdecl*)(void*);
using BufferData = const void*(__cdecl*)(void*);
static SRWLOCK transaction = SRWLOCK_INIT;

struct ThreadRecord {
    HANDLE handle;
    DWORD id;
    bool suspended;
};

struct Work {
    ThreadRecord threads[max_threads];
    unsigned count;
    CONTEXT context;
    unsigned char stack[4096];
};

struct Prepared {
    std::uintptr_t entry;
    std::uintptr_t routing;
    std::uintptr_t buffer;
    std::uintptr_t size_function;
    std::uintptr_t data_function;
    std::uint32_t patch_size;
    unsigned char patch[max_span];
};

struct ThreadState {
    DWORD error{GetLastError()};
    unsigned mxcsr{_mm_getcsr()};
    ~ThreadState() noexcept { _mm_setcsr(mxcsr); SetLastError(error); }
};

bool read(std::uintptr_t address, void* output, SIZE_T size) noexcept {
    SIZE_T copied = 0;
    return address && ReadProcessMemory(GetCurrentProcess(), reinterpret_cast<void*>(address),
        output, size, &copied) && copied == size;
}

template <typename T>
bool read(std::uintptr_t address, T& value) noexcept { return read(address, &value, sizeof(value)); }

bool equal(const unsigned char* first, const unsigned char* second, std::uint32_t size) noexcept {
    for (std::uint32_t i = 0; i < size; ++i) if (first[i] != second[i]) return false;
    return true;
}

bool inside(std::uintptr_t value, std::uintptr_t start, std::uintptr_t size) noexcept {
    return value >= start && value - start < size;
}

bool image(void* module, std::uint32_t size) noexcept {
    IMAGE_DOS_HEADER dos{};
    IMAGE_NT_HEADERS64 nt{};
    const auto base = reinterpret_cast<std::uintptr_t>(module);
    if (!base || size < sizeof(nt) || !read(base, dos) || dos.e_magic != IMAGE_DOS_SIGNATURE
        || dos.e_lfanew < 0 || static_cast<std::uint32_t>(dos.e_lfanew) > size - sizeof(nt)
        || !read(base + static_cast<std::uint32_t>(dos.e_lfanew), nt)) return false;
    return nt.Signature == IMAGE_NT_SIGNATURE && nt.FileHeader.Machine == IMAGE_FILE_MACHINE_AMD64
        && nt.OptionalHeader.Magic == IMAGE_NT_OPTIONAL_HDR64_MAGIC
        && nt.OptionalHeader.SizeOfImage == size
        && reinterpret_cast<std::uintptr_t>(GetProcAddress(static_cast<HMODULE>(module), "DobbyCommit"))
            == base + commit_rva;
}

// These offsets belong only to shipped Dobby SHA256 8015de7d...055ded.
// The managed runtime certificate must verify that exact module before this call.
bool prepared_entry(void* source, std::uintptr_t module, std::uint32_t module_size,
    bool active, Prepared& result, bool capture_buffer) noexcept {
    std::uintptr_t head = 0, node = 0;
    if (!read(module + registry_rva, head) || !read(head, node)) return false;
    for (unsigned i = 0; i < max_threads && node != head; ++i) {
        std::uintptr_t entry = 0, owner = 0, following = 0;
        if (!read(node + 0x10, entry) || !read(entry + 8, owner) || !read(node, following)) return false;
        if (owner == reinterpret_cast<std::uintptr_t>(source)) {
            unsigned char committed = 0;
            std::uintptr_t routing = 0, vtable = 0, method = 0, buffer = 0, buffer_vtable = 0;
            std::uintptr_t size_function = 0, data_function = 0;
            if (!read(entry + 0x20, committed) || committed != static_cast<unsigned char>(active)
                || !read(entry + 0x10, routing) || !read(routing, vtable)
                || !read(vtable + 0x10, method) || method != module + routing_commit_rva
                || !read(routing + 0x28, buffer) || !read(buffer, buffer_vtable)
                || !read(buffer_vtable + 0x20, size_function)
                || !read(buffer_vtable + 0x38, data_function)
                || !inside(size_function, module, module_size)
                || !inside(data_function, module, module_size)) return false;
            if (!capture_buffer) return result.entry == entry && result.routing == routing
                && result.buffer == buffer && result.size_function == size_function
                && result.data_function == data_function;
            result.entry = entry; result.routing = routing; result.buffer = buffer;
            result.size_function = size_function; result.data_function = data_function;
            result.patch_size = reinterpret_cast<BufferSize>(size_function)(reinterpret_cast<void*>(buffer));
            if (!result.patch_size || result.patch_size > max_span) return false;
            const auto data = reinterpret_cast<BufferData>(data_function)(reinterpret_cast<void*>(buffer));
            return read(reinterpret_cast<std::uintptr_t>(data), result.patch, result.patch_size);
        }
        if (!following || following == node) return false;
        node = following;
    }
    return false;
}

bool same_source(void* source, const unsigned char* bytes, std::uint32_t size) noexcept {
    unsigned char actual[max_span];
    return read(reinterpret_cast<std::uintptr_t>(source), actual, size) && equal(actual, bytes, size);
}

bool set_active(const Prepared& entry, bool active) noexcept {
    const auto value = static_cast<unsigned char>(active);
    SIZE_T written = 0;
    unsigned char observed = 0;
    return WriteProcessMemory(GetCurrentProcess(), reinterpret_cast<void*>(entry.entry + 0x20),
        &value, sizeof(value), &written) && written == sizeof(value)
        && read(entry.entry + 0x20, observed) && observed == value;
}

bool write_bytes(void* source, const unsigned char* bytes, std::uint32_t size) noexcept {
    __try {
        auto target = static_cast<volatile unsigned char*>(source);
        for (std::uint32_t i = 0; i < size; ++i) target[i] = bytes[i];
        return true;
    } __except (EXCEPTION_EXECUTE_HANDLER) { return false; }
}

[[noreturn]] void fatal(unsigned code) noexcept {
    TerminateProcess(GetCurrentProcess(), 0xE043CA00u | code);
    __fastfail(7);
}

bool terminated(HANDLE thread) noexcept {
    DWORD exit_code = 0;
    // A signaled thread is terminal even when its actual exit code is STILL_ACTIVE.
    return WaitForSingleObject(thread, 0) == WAIT_OBJECT_0 && GetExitCodeThread(thread, &exit_code);
}

void resume(Work& work) noexcept {
    bool failed = false;
    for (unsigned i = work.count; i; --i) {
        auto& thread = work.threads[i - 1];
        if (thread.suspended) {
            const auto previous = ResumeThread(thread.handle);
            if ((!previous || previous == static_cast<DWORD>(-1)) && !terminated(thread.handle)) failed = true;
        }
        thread.suspended = false;
    }
    if (failed) fatal(2);
}

CapturePatchStatus suspend(Work& work, NextThread next) noexcept {
    const auto current = GetCurrentThreadId();
    constexpr ACCESS_MASK access = THREAD_SUSPEND_RESUME | THREAD_GET_CONTEXT | THREAD_QUERY_INFORMATION | SYNCHRONIZE;
    for (unsigned pass = 0; pass < max_passes; ++pass) {
        HANDLE cursor = nullptr;
        bool retained = false, added = false;
        for (;;) {
            HANDLE handle = nullptr;
            const auto status = next(GetCurrentProcess(), cursor, access, 0, 0, &handle);
            if (cursor && !retained) CloseHandle(cursor);
            cursor = nullptr; retained = false;
            if (status == no_more_threads) break;
            if (status < 0 || !handle) return CapturePatchStatus::Enumeration;
            cursor = handle;
            const auto id = GetThreadId(handle);
            if (!id) { CloseHandle(handle); return CapturePatchStatus::ThreadContext; }
            bool known = id == current;
            for (unsigned i = 0; i < work.count && !known; ++i)
                known = work.threads[i].id == id && (!terminated(work.threads[i].handle) || terminated(handle));
            if (known) continue;
            if (work.count == max_threads) { CloseHandle(handle); return CapturePatchStatus::Capacity; }
            auto& thread = work.threads[work.count++];
            thread.handle = handle; thread.id = id; retained = true; added = true;
            if (terminated(handle)) continue;
            if (SuspendThread(handle) == static_cast<DWORD>(-1)) {
                if (terminated(handle)) continue;
                return CapturePatchStatus::ThreadContext;
            }
            thread.suspended = true;
            work.context.ContextFlags = CONTEXT_CONTROL;
            if (!GetThreadContext(handle, &work.context) && !terminated(handle)) return CapturePatchStatus::ThreadContext;
        }
        if (!added) return CapturePatchStatus::Ok;
    }
    return CapturePatchStatus::ThreadChurn;
}

CapturePatchStatus contexts(Work& work, std::uintptr_t source, std::uint32_t span,
    std::uintptr_t module, std::uint32_t module_size) noexcept {
    for (unsigned i = 0; i < work.count; ++i) {
        if (terminated(work.threads[i].handle)) continue;
        work.context.ContextFlags = CONTEXT_CONTROL;
        if (!GetThreadContext(work.threads[i].handle, &work.context)) {
            if (terminated(work.threads[i].handle)) continue;
            return CapturePatchStatus::ThreadContext;
        }
        if (inside(work.context.Rip, source, span) || inside(work.context.Rip, module, module_size))
            return CapturePatchStatus::ActivePatchOrDobby;
        const auto start = static_cast<std::uintptr_t>(work.context.Rsp);
        if (!start || (start & 7)) return CapturePatchStatus::UnqualifiedStack;
        auto at = start;
        void* allocation = nullptr;
        for (;;) {
            MEMORY_BASIC_INFORMATION stack{};
            if (!VirtualQuery(reinterpret_cast<void*>(at), &stack, sizeof(stack)))
                return CapturePatchStatus::UnqualifiedStack;
            if (allocation && stack.AllocationBase != allocation) break;
            if (!allocation) allocation = stack.AllocationBase;
            const auto access = stack.Protect & 0xFFu;
            const bool readable = access == PAGE_READONLY || access == PAGE_READWRITE
                || access == PAGE_WRITECOPY || access == PAGE_EXECUTE_READ
                || access == PAGE_EXECUTE_READWRITE || access == PAGE_EXECUTE_WRITECOPY;
            const auto region = reinterpret_cast<std::uintptr_t>(stack.BaseAddress);
            const auto end = region + stack.RegionSize;
            if (!allocation || stack.State != MEM_COMMIT || stack.Type != MEM_PRIVATE
                || !readable || (stack.Protect & PAGE_GUARD) || region > at || end <= at
                || end < region || end - start > max_stack)
                return CapturePatchStatus::UnqualifiedStack;
            while (at < end) {
                const auto size = static_cast<SIZE_T>(end - at < sizeof(work.stack) ? end - at : sizeof(work.stack));
                if (!read(at, work.stack, size)) return CapturePatchStatus::UnqualifiedStack;
                for (SIZE_T offset = 0; offset + sizeof(std::uintptr_t) <= size; offset += sizeof(std::uintptr_t)) {
                    const auto value = *reinterpret_cast<const std::uintptr_t*>(work.stack + offset);
                    if (inside(value, module, module_size)) return CapturePatchStatus::ActivePatchOrDobby;
                }
                at += size;
            }
        }
    }
    return CapturePatchStatus::Ok;
}

CapturePatchStatus transact(Work& work, NextThread next, void* source, std::uint32_t span,
    const unsigned char* expected, const unsigned char* replacement, DWORD protection,
    std::uintptr_t module, std::uint32_t module_size, Prepared& entry, bool commit) noexcept {
    auto status = suspend(work, next);
    if (status != CapturePatchStatus::Ok) return status;
    status = contexts(work, reinterpret_cast<std::uintptr_t>(source), span, module, module_size);
    if (status != CapturePatchStatus::Ok) return status;
    if (!same_source(source, expected, span)) return CapturePatchStatus::SourceChanged;
    if (!prepared_entry(source, module, module_size, !commit, entry, false))
        return CapturePatchStatus::PreparedEntryChanged;
    MEMORY_BASIC_INFORMATION region{};
    if (!VirtualQuery(source, &region, sizeof(region)) || region.State != MEM_COMMIT
        || region.Protect != protection) return CapturePatchStatus::Protection;
    DWORD old = 0;
    if (!VirtualProtect(source, span, PAGE_EXECUTE_READWRITE, &old)) return CapturePatchStatus::Protection;
    bool changed = write_bytes(source, replacement, span);
    DWORD ignored = 0;
    const bool protected_again = VirtualProtect(source, span, protection, &ignored) != FALSE;
    const bool flushed = FlushInstructionCache(GetCurrentProcess(), source, span) != FALSE;
    changed = changed && protected_again && flushed && same_source(source, replacement, span);
    if (changed && commit) changed = set_active(entry, true);
    if (changed) return CapturePatchStatus::Ok;
    if (!VirtualProtect(source, span, PAGE_EXECUTE_READWRITE, &ignored)
        || !write_bytes(source, expected, span)
        || !VirtualProtect(source, span, protection, &ignored)
        || !FlushInstructionCache(GetCurrentProcess(), source, span)
        || !same_source(source, expected, span)
        || (commit && !set_active(entry, false))) fatal(1);
    return CapturePatchStatus::Verification;
}

int run(void* source, std::uint32_t span, const void* expected_pointer, const void* original,
    void* dobby_module, std::uint32_t dobby_size, bool commit) noexcept {
    ThreadState preserve;
    if (!source || !expected_pointer || !span || span > max_span || (!commit && !original))
        return static_cast<int>(CapturePatchStatus::InvalidArgument);
    if (!TryAcquireSRWLockExclusive(&transaction)) return static_cast<int>(CapturePatchStatus::Busy);
    CapturePatchStatus status = CapturePatchStatus::DobbyContract;
    Work* work = nullptr;
    Prepared entry{};
    unsigned char expected[max_span], replacement[max_span];
    MEMORY_BASIC_INFORMATION region{};
    SYSTEM_INFO system{};
    GetSystemInfo(&system);
    const auto start = reinterpret_cast<std::uintptr_t>(source);
    auto next = reinterpret_cast<NextThread>(GetProcAddress(GetModuleHandleW(L"ntdll.dll"), "NtGetNextThread"));
    if (!next || !image(dobby_module, dobby_size)) goto finish;
    status = CapturePatchStatus::InvalidArgument;
    if (!read(reinterpret_cast<std::uintptr_t>(expected_pointer), expected, span)
        || !system.dwPageSize || span > system.dwPageSize
        || start % system.dwPageSize > system.dwPageSize - span
        || !VirtualQuery(source, &region, sizeof(region)) || region.State != MEM_COMMIT
        || (region.Protect & PAGE_GUARD) || region.Protect == PAGE_NOACCESS) goto finish;
    status = CapturePatchStatus::DobbyContract;
    if (!prepared_entry(source, reinterpret_cast<std::uintptr_t>(dobby_module), dobby_size,
        !commit, entry, true) || entry.patch_size > span) goto finish;
    for (std::uint32_t i = 0; i < span; ++i) replacement[i] = expected[i];
    if (commit) {
        for (std::uint32_t i = 0; i < entry.patch_size; ++i) replacement[i] = entry.patch[i];
    } else if (!read(reinterpret_cast<std::uintptr_t>(original), replacement, span)) goto finish;
    work = static_cast<Work*>(VirtualAlloc(nullptr, sizeof(Work), MEM_COMMIT | MEM_RESERVE, PAGE_READWRITE));
    status = CapturePatchStatus::Capacity;
    if (!work) goto finish;
    // Touch all workspace pages before any thread is suspended.
    for (SIZE_T i = 0; i < sizeof(Work); ++i) reinterpret_cast<volatile unsigned char*>(work)[i] = 0;
    status = transact(*work, next, source, span, expected, replacement, region.Protect,
        reinterpret_cast<std::uintptr_t>(dobby_module), dobby_size, entry, commit);
finish:
    if (work) {
        resume(*work);
        for (unsigned i = 0; i < work->count; ++i) CloseHandle(work->threads[i].handle);
        VirtualFree(work, 0, MEM_RELEASE);
    }
    ReleaseSRWLockExclusive(&transaction);
    return static_cast<int>(status);
}
}

extern "C" void* __cdecl capture_leaf_gateway(void* source, std::uint32_t rva) noexcept {
    ThreadState preserve;
    constexpr unsigned char ship[] = {
        0x48,0x85,0xD2,0x74,0x1E,0x44,0x8B,0x42,0x1C,0x44,0x8B,0x4A,0x20,0x44,0x8B,0x52,
        0x24,0x8B,0x42,0x18,0x89,0x41,0x10,0x44,0x89,0x41,0x14,0x44,0x89,0x49,0x18,0x44,0x89,0x51,0x1C,0xC3
    };
    constexpr unsigned char rank[] = {
        0x48,0x85,0xD2,0x74,0x16,0x48,0x8B,0x42,0x20,0x44,0x8B,0x42,0x1C,0x8B,0x52,0x18,
        0x89,0x51,0x10,0x44,0x89,0x41,0x14,0x48,0x89,0x41,0x18,0xC3
    };
    const auto bytes = rva == 0x2CF1FF0 ? ship : rva == 0x2CEFBA0 ? rank : nullptr;
    const auto size = rva == 0x2CF1FF0 ? sizeof(ship) : sizeof(rank);
    unsigned char actual[sizeof(ship)];
    if (!bytes || !read(reinterpret_cast<std::uintptr_t>(source), actual, size)
        || !equal(actual, bytes, static_cast<std::uint32_t>(size))) return nullptr;
    auto gateway = static_cast<unsigned char*>(VirtualAlloc(nullptr, size, MEM_COMMIT | MEM_RESERVE, PAGE_READWRITE));
    if (!gateway) return nullptr;
    for (SIZE_T i = 0; i < size; ++i) gateway[i] = bytes[i];
    DWORD old = 0;
    if (!VirtualProtect(gateway, size, PAGE_EXECUTE_READ, &old)
        || !FlushInstructionCache(GetCurrentProcess(), gateway, size)
        || !read(reinterpret_cast<std::uintptr_t>(gateway), actual, size)
        || !equal(actual, bytes, static_cast<std::uint32_t>(size))) {
        VirtualFree(gateway, 0, MEM_RELEASE);
        return nullptr;
    }
    return gateway;
}

extern "C" int __cdecl capture_commit_guarded(void* source, std::uint32_t span,
    const void* expected_original, void* dobby_module, std::uint32_t dobby_size) noexcept {
    return run(source, span, expected_original, nullptr, dobby_module, dobby_size, true);
}

extern "C" int __cdecl capture_restore_guarded(void* source, std::uint32_t span,
    const void* expected_patched, const void* original, void* dobby_module,
    std::uint32_t dobby_size) noexcept {
    return run(source, span, expected_patched, original, dobby_module, dobby_size, false);
}
