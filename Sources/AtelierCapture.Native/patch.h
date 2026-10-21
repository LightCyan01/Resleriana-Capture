#pragma once

#include <cstdint>

enum class CapturePatchStatus : int {
    Ok = 0,
    InvalidArgument = -1,
    Busy = -2,
    DobbyContract = -3,
    Enumeration = -4,
    Capacity = -5,
    ThreadContext = -6,
    ActivePatchOrDobby = -7,
    UnqualifiedStack = -8,
    SourceChanged = -9,
    PreparedEntryChanged = -10,
    Protection = -11,
    Verification = -12,
    ThreadChurn = -13
};

extern "C" {
// Only the two pinned, position-independent constructor leaves can be copied.
// Successful gateways remain allocated until process exit.
__declspec(dllexport) void* __cdecl capture_leaf_gateway(void* source, std::uint32_t rva) noexcept;
// Caller owns the prepared Dobby record and verifies the pinned DLL hash.
// Stack checks cover all regions above RSP in its allocation, up to 1 MiB;
// unreadable, guarded or uncommitted regions conservatively decline publication.
// The span must contain the complete verified patch and no interior entry/call-return target.
// SourceChanged makes no write; it does not establish the observed source/entry state.
// Verification rolls bytes back to expected; failed commit also verifies entry active=0.
// Unrecoverable byte/entry rollback or failed live-thread resume terminates with E043CA01/02.
__declspec(dllexport) int __cdecl capture_commit_guarded(
    void* source, std::uint32_t span, const void* expected_original,
    void* dobby_module, std::uint32_t dobby_size) noexcept;

// Isolated reversible qualification only; production shutdown drains to ForwardOnly.
// Restore requires the bridge drained and no new gateway users.
__declspec(dllexport) int __cdecl capture_restore_guarded(
    void* source, std::uint32_t span, const void* expected_patched,
    const void* original, void* dobby_module, std::uint32_t dobby_size) noexcept;
}
