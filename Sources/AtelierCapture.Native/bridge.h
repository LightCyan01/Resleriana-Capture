#pragma once

#include <cstdint>

enum class CapturePhase : std::uint32_t { Entering, Returned, Faulted, Leaving };

struct CaptureFrame {
    std::uint32_t size;
    std::uint32_t version;
    CapturePhase phase;
    std::uint32_t argument_count;
    std::uint64_t binding;
    void* receiver;
    void* method_info;
    const void* const* arguments;
    const void* result;
    std::uintptr_t token;
};

using CaptureCallback = void(__cdecl*)(CaptureFrame*) noexcept;
static_assert(sizeof(CaptureFrame) == 64);

extern "C" {
__declspec(dllexport) int __cdecl capture_find(const char* identity, const char* shape) noexcept;
__declspec(dllexport) void* __cdecl capture_entry(int index) noexcept;
__declspec(dllexport) int __cdecl capture_bind(int index, void* source, void* original, CaptureCallback callback) noexcept;
__declspec(dllexport) int __cdecl capture_close(int index) noexcept;
__declspec(dllexport) int __cdecl capture_restored(int index) noexcept;
__declspec(dllexport) int __cdecl capture_forward_only(int index) noexcept;
__declspec(dllexport) std::uint64_t __cdecl capture_active(int index) noexcept;
__declspec(dllexport) const char* __cdecl capture_catalog() noexcept;
#ifdef CAPTURE_NATIVE_CHECKS
using CapturePause = void(__cdecl*)(unsigned) noexcept;
__declspec(dllexport) void __cdecl capture_test_pause(CapturePause callback) noexcept;
#endif
}
