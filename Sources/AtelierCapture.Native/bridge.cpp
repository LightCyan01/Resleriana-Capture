#define WIN32_LEAN_AND_MEAN
#include <Windows.h>
#include <atomic>
#include <cstring>
#include <immintrin.h>
#include <type_traits>
#include "bridge.h"

enum class Mode : std::uint32_t { Unbound, Live, Closing, Restored };

struct Binding {
    std::atomic<Mode> mode{Mode::Unbound};
    std::atomic<std::uint64_t> active{0};
    void* source{};
    void* original{};
    CaptureCallback callback{};
};

static thread_local unsigned native_depth;
static std::atomic<Mode> bridge_mode{Mode::Live};
static std::atomic<std::uint64_t> bridge_active{0};
static std::atomic<bool> drain_complete{false};
#ifdef CAPTURE_NATIVE_CHECKS
static std::atomic<CapturePause> pause_callback;
extern "C" void __cdecl capture_test_pause(CapturePause callback) noexcept { pause_callback.store(callback); }
static void pause_at(unsigned point) noexcept { if (auto callback = pause_callback.load()) callback(point); }
#else
static void pause_at(unsigned) noexcept { }
#endif
static SRWLOCK binding_lifecycle = SRWLOCK_INIT;

struct SetupLock {
    SetupLock() noexcept { AcquireSRWLockExclusive(&binding_lifecycle); }
    ~SetupLock() noexcept { ReleaseSRWLockExclusive(&binding_lifecycle); }
};

struct ThreadState {
    DWORD error{GetLastError()};
    unsigned mxcsr{_mm_getcsr()};
    void capture() noexcept { error = GetLastError(); mxcsr = _mm_getcsr(); }
    void restore() const noexcept { _mm_setcsr(mxcsr); SetLastError(error); }
    ~ThreadState() noexcept { restore(); }
};

static void notify(Binding& binding, CaptureFrame& frame, CapturePhase phase) noexcept {
    ThreadState preserve;
    frame.phase = phase;
    binding.callback(&frame);
}

struct Invocation {
    Binding& binding;
    CaptureFrame& frame;
    bool observing{};
    bool counted{};

    Invocation(Binding& value, CaptureFrame& call) noexcept : binding(value), frame(call) {
        for (;;) {
            const auto mode = bridge_mode.load(std::memory_order_seq_cst);
            if (mode == Mode::Restored) return;
            if (mode == Mode::Live || native_depth) {
                pause_at(1);
                bridge_active.fetch_add(1, std::memory_order_seq_cst);
                binding.active.fetch_add(1, std::memory_order_seq_cst);
                pause_at(2);
                if (native_depth || bridge_mode.load(std::memory_order_seq_cst) == Mode::Live) {
                    counted = true;
                    observing = true;
                    ++native_depth;
                    return;
                }
                binding.active.fetch_sub(1, std::memory_order_seq_cst);
                bridge_active.fetch_sub(1, std::memory_order_seq_cst);
                WakeByAddressAll(&bridge_active);
                continue;
            }
            auto expected = mode;
            WaitOnAddress(&bridge_mode, &expected, sizeof(expected), INFINITE);
        }
    }

    ~Invocation() noexcept {
        if (!counted) return;
        frame.result = nullptr;
        notify(binding, frame, CapturePhase::Leaving);
        --native_depth;
        binding.active.fetch_sub(1, std::memory_order_seq_cst);
        bridge_active.fetch_sub(1, std::memory_order_seq_cst);
        WakeByAddressAll(&bridge_active);
    }

    void* original() const noexcept { return observing ? binding.original : binding.source; }
};

template <typename Result, typename Call>
static Result invoke(Binding& binding, CaptureFrame& frame, Call call, const void* aggregate_result = nullptr) {
    ThreadState outcome;
    Invocation invocation(binding, frame);
    if (invocation.observing) notify(binding, frame, CapturePhase::Entering);
    if constexpr (std::is_void_v<Result>) {
        try {
            outcome.restore();
            call(invocation.original());
        } catch (...) {
            outcome.capture();
            if (invocation.observing) notify(binding, frame, CapturePhase::Faulted);
            throw;
        }
        outcome.capture();
        if (invocation.observing) notify(binding, frame, CapturePhase::Returned);
    } else {
        Result result;
        try {
            outcome.restore();
            result = call(invocation.original());
        } catch (...) {
            outcome.capture();
            if (invocation.observing) notify(binding, frame, CapturePhase::Faulted);
            throw;
        }
        outcome.capture();
        frame.result = aggregate_result ? aggregate_result : &result;
        if (invocation.observing) notify(binding, frame, CapturePhase::Returned);
        return result;
    }
}

#include "entries.inc"

extern "C" int __cdecl capture_find(const char* identity, const char* shape) noexcept {
    if (!identity || !shape) return -1;
    for (std::size_t i = 0; i < entry_count; ++i)
        if (!std::strcmp(identity, identities[i]) && !std::strcmp(shape, shapes[i])) return static_cast<int>(i);
    return -1;
}

extern "C" void* __cdecl capture_entry(int index) noexcept {
    return index >= 0 && static_cast<std::size_t>(index) < entry_count ? entries[index] : nullptr;
}

extern "C" int __cdecl capture_bind(int index, void* source, void* original, CaptureCallback callback) noexcept {
    SetupLock lock;
    if (!capture_entry(index) || !source || !original || !callback) return -1;
    if (bridge_mode.load(std::memory_order_seq_cst) != Mode::Live) return -3;
    auto& binding = bindings[index];
    auto expected = Mode::Unbound;
    if (!binding.mode.compare_exchange_strong(expected, Mode::Closing)) return -2;
    binding.source = source;
    binding.original = original;
    binding.callback = callback;
    binding.mode.store(Mode::Live, std::memory_order_seq_cst);
    WakeByAddressAll(&binding.mode);
    return 0;
}

extern "C" int __cdecl capture_close(int index) noexcept {
    if (!capture_entry(index) || native_depth) return -1;
    {
        SetupLock lock;
        if (bridge_mode.load(std::memory_order_seq_cst) == Mode::Restored) return -2;
        bridge_mode.store(Mode::Closing, std::memory_order_seq_cst);
    }
    for (;;) {
        auto count = bridge_active.load(std::memory_order_seq_cst);
        if (!count) { drain_complete.store(true, std::memory_order_seq_cst); return 0; }
        WaitOnAddress(&bridge_active, &count, sizeof(count), INFINITE);
    }
}

extern "C" int __cdecl capture_restored(int index) noexcept {
    SetupLock lock;
    if (!capture_entry(index)) return -1;
    // After capture_close drained the admitted trees, a delayed entrant may
    // increment before its Closing recheck. It cannot touch the old gateway.
    if (bridge_mode.load(std::memory_order_seq_cst) != Mode::Closing || !drain_complete.load(std::memory_order_seq_cst)) return -2;
    bridge_mode.store(Mode::Restored, std::memory_order_seq_cst);
    WakeByAddressAll(&bridge_mode);
    return 0;
}

extern "C" std::uint64_t __cdecl capture_active(int index) noexcept {
    return capture_entry(index) ? bindings[index].active.load(std::memory_order_seq_cst) : 0;
}

extern "C" const char* __cdecl capture_catalog() noexcept { return catalog_json; }
