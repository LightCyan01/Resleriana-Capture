#define CAPTURE_THROWER
#include "checks.h"

static thread_local void* thrown_address;
extern "C" __declspec(dllexport) void __cdecl fixture_throw(void* identity) {
    try { throw FixtureException{identity}; }
    catch (FixtureException& error) { thrown_address = &error; throw; }
}
extern "C" __declspec(dllexport) void* __cdecl fixture_exception_address() { return thrown_address; }
