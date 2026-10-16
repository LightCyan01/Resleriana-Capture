#pragma once
#include <cstdint>
struct FixtureException { void* identity; };
#ifdef CAPTURE_THROWER
#define CAPTURE_THROW_API __declspec(dllexport)
#else
#define CAPTURE_THROW_API __declspec(dllimport)
#endif
extern "C" CAPTURE_THROW_API void __cdecl fixture_throw(void* identity);
extern "C" CAPTURE_THROW_API void* __cdecl fixture_exception_address();
