using System;
using System.Diagnostics;
using System.Linq;
using Il2CppInterop.Common.XrefScans;
using TerraFX.Interop.Windows;

namespace Il2CppInterop.Runtime;

internal class MemoryUtils
{
    public static unsafe nint FindSignatureInModule(
        ProcessModule module,
        SignatureDefinition sigDef
    )
    {
        if (module == null)
            throw new ArgumentNullException(nameof(module));
        var ptr = FindSignatureInMemoryRange(
            module.BaseAddress,
            module.ModuleMemorySize,
            sigDef.pattern.ToCharArray(),
            sigDef.mask.ToCharArray(),
            sigDef.offset
        );
        if (ptr != 0 && sigDef.xref)
            ptr = XrefScannerLowLevel.JumpTargets(ptr).FirstOrDefault();
        return ptr;
    }

    internal static unsafe nint FindSignatureInMemoryRange(
        nint block,
        long blockSize,
        char[] pattern,
        char[] mask,
        long sigOffset = 0
    )
    {
        if (blockSize < 0 || (block == 0 && blockSize != 0))
            throw new ArgumentOutOfRangeException(nameof(blockSize));
        if (OperatingSystem.IsWindowsVersionAtLeast(6, 1))
        {
            const uint readable =
                PAGE.PAGE_READONLY
                | PAGE.PAGE_READWRITE
                | PAGE.PAGE_WRITECOPY
                | PAGE.PAGE_EXECUTE_READ
                | PAGE.PAGE_EXECUTE_READWRITE
                | PAGE.PAGE_EXECUTE_WRITECOPY;
            var end = checked((long)block + blockSize);
            var current = (long)block;
            while (current < end)
            {
                MEMORY_BASIC_INFORMATION region = default;
                if (
                    Windows.VirtualQuery(
                        (void*)current,
                        &region,
                        (nuint)sizeof(MEMORY_BASIC_INFORMATION)
                    ) == 0
                )
                    break;
                var regionEnd = checked((long)region.BaseAddress + (long)region.RegionSize);
                if (regionEnd <= current)
                    throw new InvalidOperationException("Memory region did not advance");
                var scanEnd = Math.Min(end, regionEnd);
                if (
                    region.State == MEM.MEM_COMMIT
                    && (region.Protect & PAGE.PAGE_GUARD) == 0
                    && (region.Protect & readable) != 0
                )
                {
                    var ptr = FindSignatureInBlock(
                        (nint)current,
                        scanEnd - current,
                        pattern,
                        mask,
                        sigOffset
                    );
                    if (ptr != 0)
                        return ptr;
                }
                current = scanEnd;
            }
            return 0;
        }
        return FindSignatureInBlock(block, blockSize, pattern, mask, sigOffset);
    }

    public static nint FindSignatureInBlock(
        nint block,
        long blockSize,
        string pattern,
        string mask,
        long sigOffset = 0
    )
    {
        if (pattern == null)
            throw new ArgumentNullException(nameof(pattern));
        if (mask == null)
            throw new ArgumentNullException(nameof(mask));
        return FindSignatureInBlock(
            block,
            blockSize,
            pattern.ToCharArray(),
            mask.ToCharArray(),
            sigOffset
        );
    }

    public static unsafe nint FindSignatureInBlock(
        nint block,
        long blockSize,
        char[] pattern,
        char[] mask,
        long sigOffset = 0
    )
    {
        if (pattern == null)
            throw new ArgumentNullException(nameof(pattern));
        if (mask == null)
            throw new ArgumentNullException(nameof(mask));
        if (pattern.Length == 0 || pattern.Length != mask.Length)
            throw new ArgumentException(
                "Signature pattern and mask must have equal nonzero lengths"
            );
        if (blockSize < 0 || (block == 0 && blockSize != 0))
            throw new ArgumentOutOfRangeException(nameof(blockSize));
        for (var i = 0; i < mask.Length; i++)
            if (mask[i] != 'x' && mask[i] != '?')
                throw new ArgumentException("Signature mask accepts only x and ?");
        // Backport 81a6f78: the final candidate must fit entirely in the readable block.
        for (long address = 0; address <= blockSize - mask.Length; address++)
        {
            var found = true;
            for (uint offset = 0; offset < mask.Length; offset++)
                if (
                    *(byte*)(address + block + offset) != (byte)pattern[offset]
                    && mask[offset] != '?'
                )
                {
                    found = false;
                    break;
                }

            if (found)
            {
                var position = checked(address + sigOffset);
                if (position < 0 || position >= blockSize)
                    throw new ArgumentOutOfRangeException(nameof(sigOffset));
                return (nint)checked((long)block + position);
            }
        }

        return 0;
    }

    public struct SignatureDefinition
    {
        public string pattern;
        public string mask;
        public int offset;
        public bool xref;
    }
}
