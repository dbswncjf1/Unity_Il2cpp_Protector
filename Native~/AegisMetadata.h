#pragma once
#include <stdint.h>
#include <stdlib.h>
#include <string.h>
#include <atomic>
#include "utils/Logging.h"
#if IL2CPP_TARGET_WINDOWS
#include "os/Win32/WindowsHeaders.h"
#elif IL2CPP_TARGET_ANDROID
#include <errno.h>
#include <sys/mman.h>
#else
#error AegisGuard native metadata supports only Windows and Android.
#endif
#include "AegisBuildKey.h"
#include "monocypher.c"

// At-rest protection and memory hygiene, NOT runtime dump prevention. IL2CPP
// retains pointers into this plaintext region until GlobalMetadata::Clear().
// Read-only/locked pages remain readable through authorized process-memory APIs.
namespace AegisMetadata
{
    // One initialization per OS process. After the build-key shares are consumed,
    // runtime teardown/reinitialization deliberately fails closed.
    static std::atomic<int> s_State(0);
    static uint8_t* s_Allocation = NULL;
    static size_t s_Length = 0;
    static bool s_Locked = false;

    static uint64_t U64(const uint8_t* p)
    {
        uint64_t n = 0;
        for (int i = 0; i < 8; i++) n |= ((uint64_t)p[i]) << (8 * i);
        return n;
    }
    static void Release(bool readOnly)
    {
        if (!s_Allocation) return;
        bool writable = !readOnly;
#if IL2CPP_TARGET_WINDOWS
        DWORD oldProtection;
        if (readOnly) writable = VirtualProtect(s_Allocation, s_Length, PAGE_READWRITE, &oldProtection) != 0;
        if (writable) crypto_wipe(s_Allocation, s_Length);
        else il2cpp::utils::Logging::Write("AEGIS_NATIVE_MEMORY_WIPE_FAILED os_error=%lu", (unsigned long)GetLastError());
        if (s_Locked) VirtualUnlock(s_Allocation, s_Length);
        if (!VirtualFree(s_Allocation, 0, MEM_RELEASE))
        {
            il2cpp::utils::Logging::Write("AEGIS_NATIVE_MEMORY_RELEASE_FAILED");
            exit(86);
        }
#else
        if (readOnly) writable = mprotect(s_Allocation, s_Length, PROT_READ | PROT_WRITE) == 0;
        if (writable) crypto_wipe(s_Allocation, s_Length);
        else il2cpp::utils::Logging::Write("AEGIS_NATIVE_MEMORY_WIPE_FAILED os_error=%d", errno);
        if (s_Locked) munlock(s_Allocation, s_Length);
        if (munmap(s_Allocation, s_Length) != 0)
        {
            il2cpp::utils::Logging::Write("AEGIS_NATIVE_MEMORY_RELEASE_FAILED");
            exit(86);
        }
#endif
        s_Allocation = NULL; s_Length = 0; s_Locked = false;
    }
    static void* Open(const void* mapped, int64_t bytes)
    {
        int unused = 0;
        if (!s_State.compare_exchange_strong(unused, 1))
        {
            il2cpp::utils::Logging::Write("AEGIS_NATIVE_METADATA_REINITIALIZATION_REJECTED");
            return NULL;
        }
        const uint8_t* input = (const uint8_t*)mapped;
        if (!input || bytes < 56 || bytes > 1024LL * 1024 * 1024 || memcmp(input, "AGMETA02", 8) != 0) return NULL;
        uint64_t length = U64(input + 8);
        if (length != (uint64_t)(bytes - 56) || length < 8) return NULL;
        s_Length = (size_t)length;
        int lockError = 0, dumpExcluded = -1, dumpError = 0;
#if IL2CPP_TARGET_WINDOWS
        s_Allocation = (uint8_t*)VirtualAlloc(NULL, s_Length, MEM_RESERVE | MEM_COMMIT, PAGE_READWRITE);
        if (!s_Allocation) return NULL;
        s_Locked = VirtualLock(s_Allocation, s_Length) != 0;
        if (!s_Locked) lockError = (int)GetLastError();
#else
        void* allocation = mmap(NULL, s_Length, PROT_READ | PROT_WRITE, MAP_PRIVATE | MAP_ANONYMOUS, -1, 0);
        if (allocation == MAP_FAILED) return NULL;
        s_Allocation = (uint8_t*)allocation;
        s_Locked = mlock(s_Allocation, s_Length) == 0;
        if (!s_Locked) lockError = errno;
#if defined(MADV_DONTDUMP)
        dumpExcluded = madvise(s_Allocation, s_Length, MADV_DONTDUMP) == 0 ? 1 : 0;
        if (!dumpExcluded) dumpError = errno;
#endif
#endif
        uint8_t key[32];
        const volatile uint8_t* left = AegisKeyLeft;
        const volatile uint8_t* right = AegisKeyRight;
        for (int i = 0; i < 32; i++) key[i] = left[i] ^ right[i];
        crypto_wipe(AegisKeyLeft, sizeof(AegisKeyLeft));
        crypto_wipe(AegisKeyRight, sizeof(AegisKeyRight));
        int error = crypto_aead_unlock(s_Allocation, input + 40, key, input + 16, input, 40, input + 56, s_Length);
        crypto_wipe(key, sizeof(key));
        if (error != 0 || s_Allocation[0] != 0xAF || s_Allocation[1] != 0x1B || s_Allocation[2] != 0xB1 || s_Allocation[3] != 0xFA ||
            s_Allocation[4] != 39 || s_Allocation[5] != 0 || s_Allocation[6] != 0 || s_Allocation[7] != 0)
        {
            Release(false); return NULL;
        }
#if IL2CPP_TARGET_WINDOWS
        DWORD oldProtection;
        bool readOnly = VirtualProtect(s_Allocation, s_Length, PAGE_READONLY, &oldProtection) != 0;
#else
        bool readOnly = mprotect(s_Allocation, s_Length, PROT_READ) == 0;
#endif
        if (!readOnly)
        {
            il2cpp::utils::Logging::Write("AEGIS_NATIVE_MEMORY_READONLY_FAILED");
            Release(false); return NULL;
        }
        s_State.store(2);
        il2cpp::utils::Logging::Write("AEGIS_NATIVE_MEMORY readonly=1 locked=%d lock_error=%d dump_excluded=%d dump_error=%d key_shares_wiped=1", s_Locked ? 1 : 0, lockError, dumpExcluded, dumpError);
        return s_Allocation;
    }
    static void Close(void* plain)
    {
        if (!plain) return;
        int ready = 2;
        if (!s_State.compare_exchange_strong(ready, 3) || plain != s_Allocation)
        {
            il2cpp::utils::Logging::Write("AEGIS_NATIVE_METADATA_LIFECYCLE_REJECTED");
            exit(86);
        }
        Release(true);
    }
}
