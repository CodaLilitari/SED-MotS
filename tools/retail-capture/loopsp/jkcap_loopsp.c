/*
 * Inert, local-only DirectPlay service provider so the retail JK.EXE can host a game under Wine 9.0.
 * Part of SED-MotS (Red's claude branch). Finishes Wren's level-preview work: Wren (a Grok bot,
 * one of Andrei's collaborators) wrote the original jk.sh/run.sh; recovered 2026-10-31.
 * SPDX-License-Identifier: GPL-3.0-or-later
 *
 * Why: Wine 9.0's TCP/IP provider (dpwsockx.dll) implements its Open callback as a stub that returns
 * DPERR_UNSUPPORTED, so JK's Host button ends in "Host Error: Cannot connect to that network service"
 * (wine log: "fixme:dplay:DPWSCB_Open ... stub", "err:dplay:DP_SecureOpen Unable to open session:
 * DPERR_UNSUPPORTED"). Wine's dplayx loads any provider registered under
 * HKLM\Software\Microsoft\DirectPlay\Service Providers, calls its SPInit, and keeps the session,
 * player and message bookkeeping itself. This provider accepts every request and sends nothing:
 * a game hosted through it has exactly one machine, the host. No sockets, no imports, no entry point.
 *
 * Original code: no DirectX or Wine headers are used. The structure layouts were read on
 * 31 October 2026 from the DWARF debug information of Wine 9.0's own dpwsockx.dll
 * (llvm-dwarfdump): DPSP_SPCALLBACKS 0x60 bytes with the slots below, SPINITDATA 0x28,
 * DPSP_GETCAPSDATA 0x10, DPCAPS 0x28. Every callback DirectPlay passes data to takes one pointer
 * and is __stdcall. Close (0x18), SendToGroup (0x3c) and Shutdown (0x40) take no argument or are
 * obsolete, so they stay empty; dplayx checks optional slots before calling them. GetCaps is
 * called without a check, so it is always provided.
 */

typedef unsigned int u32;

#define DP_OK 0u
#define DPSP_MAJORVERSION 0x00060000u

typedef u32 (__stdcall *sp_callback)(void *data);

struct sp_callbacks {                         /* DPSP_SPCALLBACKS */
    u32 size;                                 /* 0x00 */
    u32 version;                              /* 0x04 */
    sp_callback enum_sessions;                /* 0x08 required */
    sp_callback reply;                        /* 0x0c required */
    sp_callback send;                         /* 0x10 required */
    sp_callback add_player_to_group;          /* 0x14 */
    void *close;                              /* 0x18 no argument: empty */
    sp_callback create_group;                 /* 0x1c */
    sp_callback create_player;                /* 0x20 */
    sp_callback delete_group;                 /* 0x24 */
    sp_callback delete_player;                /* 0x28 */
    sp_callback get_address;                  /* 0x2c empty: no address to report */
    sp_callback get_caps;                     /* 0x30 always called */
    sp_callback open;                         /* 0x34 the slot Wine 9.0's TCP/IP provider fails */
    sp_callback remove_player_from_group;     /* 0x38 */
    void *send_to_group;                      /* 0x3c obsolete: empty */
    void *shutdown;                           /* 0x40 no argument: empty */
    sp_callback close_ex;                     /* 0x44 */
    sp_callback shutdown_ex;                  /* 0x48 */
    sp_callback get_address_choices;          /* 0x4c empty */
    sp_callback send_ex;                      /* 0x50 empty: dplayx falls back to send */
    sp_callback send_to_group_ex;             /* 0x54 empty */
    sp_callback cancel;                       /* 0x58 empty */
    sp_callback get_message_queue;            /* 0x5c empty */
};

struct sp_init_data {                         /* SPINITDATA */
    struct sp_callbacks *callbacks;           /* 0x00 filled by the provider */
    void *service_provider;                   /* 0x04 */
    void *name;                               /* 0x08 */
    void *guid;                               /* 0x0c */
    u32 reserved1;                            /* 0x10 */
    u32 reserved2;                            /* 0x14 */
    u32 header_size;                          /* 0x18 bytes the provider prefixes to messages */
    void *address;                            /* 0x1c */
    u32 address_size;                         /* 0x20 */
    u32 provider_version;                     /* 0x24 */
};

struct dp_caps {                              /* DPCAPS */
    u32 size, flags, max_buffer_size, max_queue_size, max_players;
    u32 hundred_baud, latency, max_local_players, header_length, timeout;
};

struct get_caps_data {                        /* DPSP_GETCAPSDATA */
    u32 player;
    struct dp_caps *caps;
    u32 flags;
    void *service_provider;
};

_Static_assert(sizeof(struct sp_callbacks) == 0x60, "DPSP_SPCALLBACKS is 0x60 bytes");
_Static_assert(sizeof(struct sp_init_data) == 0x28, "SPINITDATA is 0x28 bytes");
_Static_assert(sizeof(struct dp_caps) == 0x28, "DPCAPS is 0x28 bytes");
_Static_assert(sizeof(struct get_caps_data) == 0x10, "DPSP_GETCAPSDATA is 0x10 bytes");

/* Accept the request; there is no network, so there is nothing to do. */
static u32 __stdcall accept(void *data) { (void)data; return DP_OK; }

static u32 __stdcall get_caps(void *data)
{
    struct get_caps_data *request = (struct get_caps_data *)data;
    struct dp_caps *caps = request ? request->caps : 0;
    if (caps && caps->size >= sizeof(struct dp_caps)) {
        caps->flags = 0;
        caps->max_buffer_size = 65480;        /* never used: nothing is transmitted */
        caps->max_queue_size = 0;
        caps->max_players = 65536;
        caps->hundred_baud = 100000;
        caps->latency = 1;
        caps->max_local_players = 65536;
        caps->header_length = 0;
        caps->timeout = 5000;
    }
    return DP_OK;
}

u32 __stdcall SPInit(struct sp_init_data *init)
{
    struct sp_callbacks *cb = init->callbacks;
    cb->size = sizeof(struct sp_callbacks);
    cb->version = DPSP_MAJORVERSION;
    cb->enum_sessions = accept;
    cb->reply = accept;
    cb->send = accept;
    cb->add_player_to_group = accept;
    cb->create_group = accept;
    cb->create_player = accept;
    cb->delete_group = accept;
    cb->delete_player = accept;
    cb->get_caps = get_caps;
    cb->open = accept;
    cb->remove_player_from_group = accept;
    cb->close_ex = accept;
    cb->shutdown_ex = accept;
    init->header_size = 0;
    init->provider_version = DPSP_MAJORVERSION;
    return DP_OK;
}
