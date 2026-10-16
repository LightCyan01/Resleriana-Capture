#define WIN32_LEAN_AND_MEAN
#include <Windows.h>
#include <atomic>
#include <cstdint>
#include <cstring>
#include <cstdio>
#include <cstddef>
#include <immintrin.h>
#include <stdexcept>
#include <thread>
#include <vector>
#include "bridge.h"
#include "checks.h"

static std::atomic<std::uint64_t> assertions;
static std::atomic<int> calls;
static std::atomic<int> destructors;
static thread_local bool should_throw;
static thread_local bool should_reenter;
static void* expected_method = reinterpret_cast<void*>(0x1234567812345678ULL);
static std::uint64_t receiver_memory[32];
static std::uint64_t argument_memory[16][16];
static std::uint64_t value_memory[16];
static std::uint64_t output_memory[16];
static std::uintptr_t reentry;
static std::uintptr_t cross_reentry;
static int reentry_slot;
static std::atomic<bool> block_original;
static std::atomic<bool> inside_original;
static std::atomic<unsigned> pause_point;
static std::atomic<bool> paused;
static std::atomic<bool> release_pause;
static std::atomic<bool> paused_second;
static std::atomic<bool> release_second;
static thread_local bool pause_this_thread;
static void* fault_identity = reinterpret_cast<void*>(0x1234543212345432ULL);

struct Nullable32 { bool hasValue; std::int32_t value; };
struct Nullable64 { bool hasValue; std::int64_t value; };
struct CancellationToken { void* source; };
struct NativeTask { void* source; std::int16_t token; };
static_assert(sizeof(Nullable32) == 8 && alignof(Nullable32) == 4 && offsetof(Nullable32, value) == 4);
static_assert(sizeof(Nullable64) == 16 && alignof(Nullable64) == 8 && offsetof(Nullable64, value) == 8);
static_assert(sizeof(CancellationToken) == 8 && alignof(CancellationToken) == 8);
static_assert(sizeof(NativeTask) == 16 && alignof(NativeTask) == 8 && offsetof(NativeTask, token) == 8);

struct Guard { ~Guard() noexcept { ++destructors; } };
static void require(bool value) { ++assertions; if (!value) throw std::runtime_error("native fixture invariant"); }

static void original_begin(void* self, void* method) {
    const auto incoming_error = GetLastError();
    const auto incoming_mxcsr = _mm_getcsr();
    Guard guard;
    ++calls;
    require(method == expected_method);
    require(incoming_error == 0x2222 || incoming_error == 0x4321);
    require(incoming_mxcsr == 0x1F80 || incoming_mxcsr == 0x5F80);
    if (self) require(self == receiver_memory);
    SetLastError(0x4321);
    _mm_setcsr(0x5F80);
    inside_original.store(true);
    while (block_original.load()) SwitchToThread();
    if (should_reenter) {
        should_reenter = false;
        reinterpret_cast<void(__cdecl*)(void*,void*)>(reentry)(receiver_memory, expected_method);
        reinterpret_cast<void(__cdecl*)(void*,void*)>(cross_reentry)(receiver_memory, expected_method);
        should_reenter = true;
    }
    if (self) ++receiver_memory[0];
    if (should_throw) fixture_throw(fault_identity);
}

using DobbyPrepare = int(__cdecl*)(void*,void*,void**);
using DobbyAction = int(__cdecl*)(void*);

#include "fixture-entries.inc"

#ifdef CAPTURE_NATIVE_CHECKS
static void __cdecl pause_entry(unsigned point) noexcept {
    if (!pause_this_thread) return;
    if (pause_point.load() == 3 && point == 2) {
        paused_second.store(true);
        while (!release_second.load()) SwitchToThread();
        return;
    }
    if (pause_point.load() != point && !(pause_point.load() == 3 && point == 1)) return;
    paused.store(true);
    while (!release_pause.load()) SwitchToThread();
}
#endif

extern "C" __declspec(dllexport) void __cdecl fixture_scramble_thread_state() noexcept {
    _mm_setcsr((_mm_getcsr() ^ 0x2000) | 0x1F80);
    SetLastError(0x9999);
}

extern "C" __declspec(dllexport) std::uint64_t __cdecl fixture_run(
    CaptureCallback callback, DobbyPrepare prepare, DobbyAction commit, DobbyAction destroy, unsigned race) noexcept {
    try {
        _mm_setcsr(0x1F80);
        std::fprintf(stderr, "native finite fixture starting\n");
        run_entries(callback, prepare, commit);
        require(capture_find("not-in-catalog", "v:reference:") == -1);
        require(capture_find(identities_for_checks[0], "unsupported") == -1);
        reentry_slot = capture_find("BattleServerControl.SyncActionStart()", "v:reference:");
        require(reentry_slot >= 0);
        reentry = reinterpret_cast<std::uintptr_t>(capture_entry(reentry_slot));
        cross_reentry = reinterpret_cast<std::uintptr_t>(capture_entry(capture_find("BattleServerControl.SyncActionEnd()", "v:reference:")));
        for (std::size_t index = 0; index < source_count; ++index) {
            DWORD64 base{};
            const auto unwind = RtlLookupFunctionEntry(reinterpret_cast<DWORD64>(capture_entry(static_cast<int>(index))), &base, nullptr);
            require(unwind && unwind->UnwindData && base);
        }
        should_reenter = true;
        SetLastError(0x2222);
        reinterpret_cast<void(__cdecl*)(void*,void*)>(reentry)(receiver_memory, expected_method);
        should_reenter = false;

        block_original.store(race == 0);
        inside_original.store(false);
#ifdef CAPTURE_NATIVE_CHECKS
        if (race) {
            pause_point.store(race);
            capture_test_pause(&pause_entry);
        }
#endif
        std::thread caller([race] {
            _mm_setcsr(0x1F80);
            should_reenter = true;
            SetLastError(0x2222);
#ifdef CAPTURE_NATIVE_CHECKS
            pause_this_thread = race != 0;
#endif
            reinterpret_cast<void(__cdecl*)(void*,void*)>(reentry)(receiver_memory, expected_method);
        });
#ifdef CAPTURE_NATIVE_CHECKS
        if (race) {
            while (!paused.load()) SwitchToThread();
        } else
#endif
        while (!inside_original.load()) SwitchToThread();
        std::atomic<bool> closed{false};
        std::thread closer([&] { require(capture_close(reentry_slot) == 0); closed.store(true); });
        for (unsigned i = 0; i < 1000; ++i) SwitchToThread();
        if (race == 0 || race == 2) require(!closed.load());
        block_original.store(false);
        if (race == 1 || race == 3) {
            while (!closed.load()) SwitchToThread();
        }
        if (race == 2) release_pause.store(true);
        if (race == 0) caller.join();
        closer.join();
        for (std::size_t index = 0; index < source_count; ++index) {
            require(capture_active(static_cast<int>(index)) == 0);
            if (static_cast<int>(index) == reentry_slot) require(destroy(sources[index]) == 0);
        }
        if (race == 3) {
            release_pause.store(true);
            while (!paused_second.load()) SwitchToThread();
            require(capture_active(reentry_slot) == 1);
        }
        require(capture_restored(reentry_slot) == 0);
        if (race != 0) { release_pause.store(true); release_second.store(true); caller.join(); }
        auto before = calls.load();
        SetLastError(0x2222);
        reinterpret_cast<void(__cdecl*)(void*,void*)>(reentry)(receiver_memory, expected_method);
        require(calls.load() == before + 1);
        require(capture_active(reentry_slot) == 0);
        return assertions.load();
    } catch (...) { return 0; }
}
