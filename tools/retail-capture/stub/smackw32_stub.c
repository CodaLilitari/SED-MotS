/*
 * Inert stand-in for RAD's smackw32.dll so the retail JK.EXE loads under Wine.
 * Part of SED-MotS (Red's claude branch). Finishes Wren's level-preview work: Wren (a Grok bot,
 * one of Andrei's collaborators) wrote the original jk.sh/run.sh; recovered 2026-10-31.
 * SPDX-License-Identifier: GPL-3.0-or-later
 *
 * Original code: no Smacker code, headers or data are used. JK 1.01 imports nine functions
 * from smackw32.DLL, by ordinal only. All nine call sites sit in the cutscene player
 * (0x426b6d..0x426e91 in the 1.01 executable, sha256 4509cd69...51d9), which runs only when a
 * video\*.smk file exists; the capture layout stages none. Every function returns 0, so even
 * a reached open call reports "no video" and JK skips the cutscene.
 *
 * Argument counts are the pushes before each `call dword ptr [IAT]` in JK.EXE 1.01, read with
 * llvm-objdump on 31 October 2026. No `add esp` follows any call, so every import is stdcall
 * and the callee pops its arguments; the counts below must match or the stack would tear.
 *   #14  3 args  call at 0x426b7f            (open: name, flags, extra buffer)
 *   #17  2 args  call at 0x426e91            (sound on/off: handle, flag)
 *   #18  1 arg   call at 0x426c6a            (close: handle)
 *   #19  1 arg   calls at 0x426d68, 0x426e1a (decode frame: handle)
 *   #21  1 arg   call at 0x426e53            (next frame: handle)
 *   #23  7 args  calls at 0x426d5b, 0x426e0d (blit to buffer: handle, left, top, pitch, height, buffer, flags)
 *   #26  3 args  call at 0x426e46            (track data: handle, buffer, size)
 *   #32  1 arg   call at 0x426ce0            (wait: handle)
 *   #38  1 arg   call at 0x426b6d            (use DirectSound: DirectSound object)
 * The roles in brackets are what the call sites do with the results; nothing depends on them.
 */

typedef unsigned int u32;

#define INERT return 0

u32 __stdcall smack_ordinal_14(u32 a, u32 b, u32 c) { (void)a; (void)b; (void)c; INERT; }
u32 __stdcall smack_ordinal_17(u32 a, u32 b) { (void)a; (void)b; INERT; }
u32 __stdcall smack_ordinal_18(u32 a) { (void)a; INERT; }
u32 __stdcall smack_ordinal_19(u32 a) { (void)a; INERT; }
u32 __stdcall smack_ordinal_21(u32 a) { (void)a; INERT; }
u32 __stdcall smack_ordinal_23(u32 a, u32 b, u32 c, u32 d, u32 e, u32 f, u32 g)
{
    (void)a; (void)b; (void)c; (void)d; (void)e; (void)f; (void)g;
    INERT;
}
u32 __stdcall smack_ordinal_26(u32 a, u32 b, u32 c) { (void)a; (void)b; (void)c; INERT; }
u32 __stdcall smack_ordinal_32(u32 a) { (void)a; INERT; }
u32 __stdcall smack_ordinal_38(u32 a) { (void)a; INERT; }
