/*
 * calclink.h - flat C ABI over tilibs (libticables2, libticalcs2, libtifiles2, libticonv)
 *
 * This is the ONLY native interface the C# side (CalcLink.Core) binds to.
 * Design rules:
 *   - Plain C, __cdecl, x64 and ARM64. No GLib types cross this boundary.
 *   - All strings are UTF-8, NUL-terminated. All paths are UTF-8.
 *   - Return value: 0 (CL_OK) on success; > 0 is a tilibs error code
 *     (turn it into text with cl_error_message); < 0 is a CL_E_* shim error.
 *   - Memory returned by the library is freed with the matching cl_free* call.
 *   - A cl_session is NOT thread-safe: call it from one thread at a time.
 *     The only exception is cl_cancel(), which may be called from any thread.
 *
 * Copyright (C) 2026 CalcLink contributors. Licensed GPL-2.0-or-later.
 */
#ifndef CALCLINK_H
#define CALCLINK_H

#include <stdint.h>

#ifdef __cplusplus
extern "C" {
#endif

#if defined(CALCLINK_BUILD)
#  define CL_API __declspec(dllexport)
#else
#  define CL_API __declspec(dllimport)
#endif
#define CL_CALL __cdecl

/* Bump on any breaking change to structs or signatures, or when C# starts needing a new export.
 * C# checks this at load. v2: cl_get_calc_id, cl_read_file_vars; cl_set_clock formats 0 = keep.
 * v3: cl_reset_cancel; calls no longer clear the cancel flag themselves. */
#define CL_ABI_VERSION 3

/* ---- Shim error codes (negative). Positive codes come from tilibs. ---- */
#define CL_OK               0
#define CL_E_INVALID_ARG   (-1)
#define CL_E_NO_MEMORY     (-2)
#define CL_E_NOT_SUPPORTED (-3)   /* operation not in cl_features() for this model */
#define CL_E_NOT_OPEN      (-4)
#define CL_E_CANCELLED     (-5)   /* tilibs ERROR_ABORT (256) is mapped to this */
#define CL_E_BAD_FILE      (-6)   /* file is not a TI file / wrong model */
#define CL_E_INTERNAL      (-7)

/* ---- Variable attributes (mirror tilibs FileAttr) ---- */
#define CL_ATTR_NONE      0
#define CL_ATTR_LOCKED    1
#define CL_ATTR_PROTECTED 2
#define CL_ATTR_ARCHIVED  3

/* ---- File classes returned by cl_classify_file ---- */
#define CL_FILE_UNKNOWN   0
#define CL_FILE_VARIABLE  1   /* single var or group (.8xp, .8xg, .89p, .tns ...) */
#define CL_FILE_APP       2   /* flash app (.8xk, .8ek, .89k ...) */
#define CL_FILE_OS        3   /* OS upgrade (.8xu, .8eu, .89u, .tno, .tcc ...) */
#define CL_FILE_BACKUP    4   /* native backup (.8xb ...) */
#define CL_FILE_TIGROUP   5   /* .tig */
#define CL_FILE_CERT      6   /* certificate (.8xq, .89q ...) */

typedef struct cl_session cl_session;

/* A USB-attached calculator/cable found by cl_probe_usb. */
typedef struct {
    int32_t  cable_model;   /* tilibs CableModel (CABLE_USB = 5, CABLE_SLV = 4) */
    int32_t  cable_port;    /* tilibs CablePort (1..4) */
    int32_t  calc_model;    /* tilibs CalcModel, USB variant (e.g. CALC_TI84PCE_USB = 20) */
    uint16_t vid;
    uint16_t pid;
    char     name[64];      /* UTF-8, e.g. "TI-84 Plus CE" */
} cl_device;

/* Calculator identity/version, from ticalcs_calc_get_version. */
typedef struct {
    uint32_t mask;              /* tilibs InfosMask: which fields below are valid */
    int32_t  calc_model;
    char     product_name[64];
    char     product_id[32];
    char     os_version[32];
    char     boot_version[32];
    char     boot2_version[32];
    uint64_t ram_user;
    uint64_t ram_free;
    uint64_t flash_user;
    uint64_t flash_free;
    uint16_t lcd_width;
    uint16_t lcd_height;
    uint16_t bits_per_pixel;
    uint16_t hw_version;
    uint8_t  battery_ok;        /* 1 = enough for an OS transfer */
    uint8_t  run_level;         /* 1 = boot code, 2 = OS */
    uint8_t  exact_math;
    uint8_t  clock_support;
    uint8_t  color_screen;
    uint8_t  python_on_board;
    uint8_t  reserved[2];
} cl_calc_info;

/* One variable or app on the calculator (flattened dirlist). */
typedef struct {
    const char* folder;     /* UTF-8 display folder, "" if the model has no folders */
    const char* name;       /* UTF-8 display name (detokenized) */
    const char* type_name;  /* UTF-8 short type, e.g. "PRGM", "APPV", "FLASH" */
    const char* file_ext;   /* file extension without dot, e.g. "8xp" */
    uint32_t    size;       /* bytes */
    uint8_t     type;       /* raw tilibs type id */
    uint8_t     attr;       /* CL_ATTR_* */
    uint8_t     is_app;     /* 1 if from the apps tree */
    uint8_t     reserved;
    void*       opaque;     /* shim-owned raw VarEntry; valid until cl_free_dirlist */
} cl_var;

typedef struct {
    uint16_t year;
    uint8_t  month, day, hours, minutes, seconds;
    uint8_t  time_format;   /* 12 or 24. cl_set_clock: 0 = keep the calculator's setting */
    uint8_t  date_format;   /* 1-based, see ticalcs_clock_format2date. cl_set_clock: 0 = keep */
    uint8_t  reserved[3];
} cl_clock;

/* Progress callback. Called on the thread that is running the operation.
 * current/total: overall progress units (bytes or items); total may be 0 if unknown.
 * rate_kbps: link rate in kilobytes/s (KB/s, as tilibs reports it) if known, else 0. text: UTF-8 status line (may be ""). */
typedef void (CL_CALL *cl_progress_fn)(void* user, int32_t current, int32_t total,
                                       float rate_kbps, const char* text);

/* ---- Library ---- */
CL_API int32_t     CL_CALL cl_abi_version(void);
CL_API int         CL_CALL cl_init(void);            /* idempotent; inits all four libs */
CL_API void        CL_CALL cl_shutdown(void);
CL_API const char* CL_CALL cl_lib_versions(void);    /* static UTF-8, e.g. "ticables2 1.3.6; ticalcs2 ..." */
CL_API char*       CL_CALL cl_error_message(int code); /* free with cl_free */
CL_API void        CL_CALL cl_free(void* p);

/* ---- Discovery ---- */
/* Fills up to `capacity` devices; *count receives the number found (may exceed capacity). */
CL_API int CL_CALL cl_probe_usb(cl_device* out, int32_t capacity, int32_t* count);
CL_API int CL_CALL cl_classify_file(const char* path, int32_t* file_class, int32_t* calc_model);
CL_API const char* CL_CALL cl_model_name(int32_t calc_model);   /* static UTF-8 */

/* ---- Session ---- */
/* timeout_ds: link timeout in tenths of a second (tilibs convention); 0 = default. */
CL_API int      CL_CALL cl_open(int32_t cable_model, int32_t cable_port, int32_t calc_model,
                                int32_t timeout_ds, cl_session** out);
CL_API void     CL_CALL cl_close(cl_session* s);
CL_API uint32_t CL_CALL cl_features(cl_session* s);  /* tilibs CalcFeatures bits (OPS_*, FTS_*) */
CL_API int32_t  CL_CALL cl_calc_model(cl_session* s);
CL_API void     CL_CALL cl_set_progress(cl_session* s, cl_progress_fn fn, void* user);
/* Cancellation. cl_cancel sets the session's cancel flag; the running call (and any later call)
 * then stops with CL_E_CANCELLED at its next check. The flag stays set until cl_reset_cancel, so a
 * cancel that arrives between two calls of one operation is not lost. The caller owns the flag:
 * call cl_reset_cancel once at the start of each operation (a sequence of calls), before it can be
 * cancelled, never between its calls. */
CL_API void     CL_CALL cl_cancel(cl_session* s);    /* thread-safe */
CL_API void     CL_CALL cl_reset_cancel(cl_session* s);

CL_API int CL_CALL cl_is_ready(cl_session* s);
CL_API int CL_CALL cl_get_info(cl_session* s, cl_calc_info* out);
/* Calculator ID ("ID-list", OPS_IDLIST) as tilibs returns it: for Z80/68k models the hex digits
 * the About screen shows, without dashes (e.g. 14 digits for a TI-83 Plus). UTF-8, NUL-terminated.
 * capacity >= 64 is always enough. */
CL_API int CL_CALL cl_get_calc_id(cl_session* s, char* out, int32_t capacity);
/* Free RAM / flash in bytes; 0 = not reported by this model (e.g. flash on a TI-83 Plus). */
CL_API int CL_CALL cl_get_memfree(cl_session* s, uint64_t* ram, uint64_t* flash);

/* ---- Variables ---- */
CL_API int  CL_CALL cl_get_dirlist(cl_session* s, cl_var** out, int32_t* count);
CL_API void CL_CALL cl_free_dirlist(cl_var* list, int32_t count);
/* The variables inside a variable or group file (CL_FILE_VARIABLE; anything else is CL_E_BAD_FILE),
 * with folder/name/type exactly as cl_get_dirlist reports them, so a caller can spot variables that
 * already exist before sending. Needs no session. Free with cl_free_dirlist. */
CL_API int  CL_CALL cl_read_file_vars(const char* path, cl_var** out, int32_t* count);
/* Sends any file; dispatches on cl_classify_file (var/group, app, OS, backup, tigroup, cert). */
CL_API int CL_CALL cl_send_file(cl_session* s, const char* path);
CL_API int CL_CALL cl_recv_var(cl_session* s, const cl_var* v, const char* path);
CL_API int CL_CALL cl_delete_var(cl_session* s, const cl_var* v);
CL_API int CL_CALL cl_rename_var(cl_session* s, const cl_var* v, const char* new_folder, const char* new_name);
CL_API int CL_CALL cl_set_attr(cl_session* s, const cl_var* v, int32_t attr);
CL_API int CL_CALL cl_new_folder(cl_session* s, const char* name);
CL_API int CL_CALL cl_execute(cl_session* s, const cl_var* v);

/* ---- Tools ---- */
CL_API int CL_CALL cl_send_key(cl_session* s, uint32_t key);
/* Screenshot as 32-bit BGRA (B,G,R,A byte order, A=255), row-major, stride = width*4.
 * *pixels is freed with cl_free. */
CL_API int CL_CALL cl_recv_screen(cl_session* s, uint8_t** pixels, int32_t* width, int32_t* height);
CL_API int CL_CALL cl_get_clock(cl_session* s, cl_clock* out);
CL_API int CL_CALL cl_set_clock(cl_session* s, const cl_clock* in);
/* Full backup to `path`: TIGroup (.tig) if the extension is .tig, else native backup. */
CL_API int CL_CALL cl_backup(cl_session* s, const char* path);
CL_API int CL_CALL cl_dump_rom(cl_session* s, const char* path);

#ifdef __cplusplus
}
#endif

#endif /* CALCLINK_H */
