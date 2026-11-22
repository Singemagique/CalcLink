/*
 * calclink.cc - implementation of the flat C ABI declared in calclink.h,
 * on top of tilibs (libticables2, libticalcs2, libtifiles2, libticonv).
 *
 * Copyright (C) 2026 CalcLink contributors. Licensed GPL-2.0-or-later.
 *
 * Behavioural reference: TiLP 2 (tilp_device.c, tilp_calcs.c, tilp_vars.c,
 * tilp_screen.c, clock.c, gtk_update.c). Where TiLP has a quirk that matters
 * for talking to real hardware (timeouts, ready-check dance, retries), it is
 * reproduced here and marked "TiLP:".
 *
 * Memory rules: everything handed to the caller is allocated with malloc()
 * and released with free() (cl_free / cl_free_dirlist). Everything tilibs or
 * GLib hands to us is released with the matching tilibs/GLib function before
 * we return. The two are never mixed.
 */

#ifndef CALCLINK_BUILD
#  define CALCLINK_BUILD 1
#endif

// tilibs is compiled with __WIN32__ defined (tilibs/CMakeLists.txt); match it so
// that every conditional in its public headers resolves the same way here.
#ifndef __WIN32__
#  define __WIN32__ 1
#endif

#ifndef WIN32_LEAN_AND_MEAN
#  define WIN32_LEAN_AND_MEAN
#endif
#ifndef NOMINMAX
#  define NOMINMAX
#endif
#include <windows.h>   // must precede ticables.h (timeout.h pulls in <sysinfoapi.h>)

#include <atomic>
#include <climits>
#include <cstdint>
#include <cstdio>
#include <cstdlib>
#include <cstring>
#include <mutex>
#include <new>
#include <string>
#include <vector>

#include <ticalcs.h>   // also brings in ticables.h, tifiles.h, ticonv.h and glib.h

#include "calclink.h"

static_assert(sizeof(int) == 4, "CalcUpdate::cancel is accessed with std::atomic_ref<int>");
static_assert(CL_ATTR_NONE == ATTRB_NONE && CL_ATTR_LOCKED == ATTRB_LOCKED &&
              CL_ATTR_PROTECTED == ATTRB_PROTECTED && CL_ATTR_ARCHIVED == ATTRB_ARCHIVED,
              "CL_ATTR_* must mirror tilibs FileAttr");
static_assert(CABLE_SLV == 4 && CABLE_USB == 5, "calclink.h documents CABLE_SLV = 4, CABLE_USB = 5");
static_assert(CALC_TI84PCE_USB == 20 && CALC_MAX == 23, "CalcModel numbering changed in tilibs");

// ---------------------------------------------------------------------------
// Session
// ---------------------------------------------------------------------------

struct cl_session
{
    CableHandle*   cable       = nullptr;
    CalcHandle*    calc        = nullptr;
    CableModel     cable_model = CABLE_NUL;
    CablePort      cable_port  = PORT_0;
    CalcModel      model       = CALC_NONE;
    unsigned int   timeout_ds  = 0;        // 0 = ticables default (DFLT_TIMEOUT)
    bool           attached    = false;    // false after a failed re-open in cl_is_ready
    CalcUpdate     updat{};                // one CalcUpdate per session (callbacks have no user pointer)
    cl_progress_fn progress    = nullptr;
    void*          progress_user = nullptr;
};

namespace {

// tilibs' CalcUpdate callbacks are void(*)(void). The session whose operation is
// running on this thread is published here for the duration of each API call.
thread_local cl_session* t_current = nullptr;

constexpr int TI_ERR_NOT_READY = 257;   // libticalcs ERR_NOT_READY (error.h is private)
constexpr uint16_t TI_USB_VID  = 0x0451; // Texas Instruments

std::mutex g_init_mutex;
bool       g_inited = false;

// ----- small helpers -------------------------------------------------------

template <class F>
int guarded(F&& f) noexcept
{
    try
    {
        return f();
    }
    catch (const std::bad_alloc&)
    {
        return CL_E_NO_MEMORY;
    }
    catch (...)
    {
        return CL_E_INTERNAL;
    }
}

int ensure_init() noexcept
{
    try
    {
        std::lock_guard<std::mutex> lock(g_init_mutex);
        if (!g_inited)
        {
            // libticonv has no *_library_init; the other three are refcounted internally.
            ticables_library_init();
            tifiles_library_init();
            ticalcs_library_init();
            g_inited = true;
        }
        return CL_OK;
    }
    catch (...)
    {
        return CL_E_INTERNAL;
    }
}

std::atomic_ref<int> cancel_flag(cl_session* s)
{
    return std::atomic_ref<int>(s->updat.cancel);
}

int map_ret(cl_session* s, int ret)
{
    if (ret == 0)
    {
        return CL_OK;
    }
    if (ret == ERROR_ABORT)
    {
        return CL_E_CANCELLED;
    }
    if (s != nullptr && cancel_flag(s).load() != 0)
    {
        // The user asked to cancel and the transfer then failed (usually a timeout
        // because the calculator side was abandoned): report it as a cancellation.
        return CL_E_CANCELLED;
    }
    if (ret < 0)
    {
        // A few tilibs paths return -1; never let that collide with CL_E_* codes.
        return CL_E_INTERNAL;
    }
    return ret;
}

int check_session(cl_session* s)
{
    if (s == nullptr)
    {
        return CL_E_INVALID_ARG;
    }
    if (s->calc == nullptr || s->cable == nullptr || !s->attached)
    {
        return CL_E_NOT_OPEN;
    }
    return CL_OK;
}

uint32_t features_of(cl_session* s)
{
    return static_cast<uint32_t>(ticalcs_calc_features(s->calc));
}

bool has_features(cl_session* s, uint32_t bits)
{
    return (features_of(s) & bits) == bits;
}

char* dup_cstr(const char* src)
{
    if (src == nullptr)
    {
        src = "";
    }
    const size_t n = std::strlen(src);
    char* p = static_cast<char*>(std::malloc(n + 1));
    if (p == nullptr)
    {
        throw std::bad_alloc();
    }
    std::memcpy(p, src, n + 1);
    return p;
}

// Returns valid UTF-8. tilibs text is UTF-8 in practice (gettext codeset is forced to
// UTF-8, names go through ticonv_*_to_utf8), but OS error strings may be in the ANSI
// code page and fixed-size buffers may cut a multi-byte sequence.
std::string to_utf8(const char* s)
{
    if (s == nullptr)
    {
        return std::string();
    }
    if (g_utf8_validate(s, -1, nullptr))
    {
        return std::string(s);
    }
    gchar* conv = g_locale_to_utf8(s, -1, nullptr, nullptr, nullptr);
    if (conv != nullptr)
    {
        std::string r(conv);
        g_free(conv);
        return r;
    }
    gchar* fixed = g_utf8_make_valid(s, -1);
    std::string r(fixed != nullptr ? fixed : "");
    g_free(fixed);
    return r;
}

// Takes ownership of a GLib-allocated string.
std::string take_gstr(gchar* g)
{
    std::string r = (g != nullptr) ? to_utf8(g) : std::string();
    g_free(g);
    return r;
}

// Copies UTF-8 into a fixed buffer, truncating on a code point boundary.
void copy_utf8(char* dst, size_t cap, const char* src)
{
    if (cap == 0)
    {
        return;
    }
    const std::string v = to_utf8(src);
    size_t n = v.size();
    if (n >= cap)
    {
        n = cap - 1;
        while (n > 0 && (static_cast<unsigned char>(v[n]) & 0xC0) == 0x80)
        {
            --n;
        }
    }
    std::memcpy(dst, v.data(), n);
    dst[n] = '\0';
}

// Extension of the *file name* (tifiles_fext_get would also match a dot in a directory).
std::string file_ext_of(const char* path)
{
    const char* base = path;
    for (const char* p = path; *p; ++p)
    {
        if (*p == '\\' || *p == '/' || *p == ':')
        {
            base = p + 1;
        }
    }
    const char* dot = std::strrchr(base, '.');
    return dot != nullptr ? std::string(dot + 1) : std::string();
}

bool is_regular_file(const char* path)
{
    return g_file_test(path, G_FILE_TEST_IS_REGULAR) != FALSE;
}

// ----- CalcUpdate plumbing -------------------------------------------------

void reset_counters(CalcUpdate& u)
{
    u.cnt1 = u.max1 = 0;
    u.cnt2 = u.max2 = 0;
    u.cnt3 = u.max3 = 0;
    u.rate = 0.0f;
}

void emit_progress(cl_session* s) noexcept
{
    if (s == nullptr || s->progress == nullptr)
    {
        return;
    }
    try
    {
        const CalcUpdate& u = s->updat;

        // tilibs granularities: cnt1/max1 = bytes of the current packet stream / variable,
        // cnt2/max2 = entries of a group or pages of an app/OS, cnt3/max3 = items of a
        // whole operation (tigroup, backup, or a multi-file job). Report the coarsest level
        // that is actually in use so the bar moves monotonically over the whole operation.
        int32_t cur;
        int32_t tot;
        if (u.max3 > 1)
        {
            cur = u.cnt3;
            tot = u.max3;
        }
        else if (u.max2 > 1)
        {
            cur = u.cnt2;
            tot = u.max2;
        }
        else
        {
            cur = u.cnt1;
            tot = u.max1;
        }
        if (tot < 0)
        {
            tot = 0;
        }
        if (cur < 0)
        {
            cur = 0;
        }
        if (tot > 0 && cur > tot)
        {
            cur = tot;
        }

        // ticables reports bytes per millisecond, i.e. kilobytes per second (TiLP shows
        // it as "Kbytes/s").
        float rate = u.rate;
        if (!(rate >= 0.0f) || rate > 1.0e7f)
        {
            rate = 0.0f;
        }

        char text[sizeof(u.text)];
        std::memcpy(text, u.text, sizeof(text));
        text[sizeof(text) - 1] = '\0';
        const std::string utf8 = to_utf8(text);

        s->progress(s->progress_user, cur, tot, rate, utf8.c_str());
    }
    catch (...)
    {
        // never propagate into tilibs
    }
}

void cb_start(void)
{
    if (cl_session* s = t_current)
    {
        reset_counters(s->updat);
    }
}

void cb_stop(void)
{
    if (cl_session* s = t_current)
    {
        reset_counters(s->updat);
    }
}

void cb_refresh(void)
{
    // TiLP pumps the GTK main loop here; we have nothing to pump.
}

void cb_pbar(void)
{
    emit_progress(t_current);
}

void cb_label(void)
{
    emit_progress(t_current);
}

void init_update(CalcUpdate& u)
{
    std::memset(&u, 0, sizeof(u));
    u.mask    = (1 << 0) | (1 << 1);   // as TiLP's gtk_update (unused by tilibs)
    u.start   = cb_start;
    u.stop    = cb_stop;
    u.refresh = cb_refresh;
    u.pbar    = cb_pbar;
    u.label   = cb_label;
}

// Publishes the session to the callbacks and resets cancel/progress state.
class OpScope
{
public:
    explicit OpScope(cl_session* s) : prev_(t_current)
    {
        t_current = s;
        cancel_flag(s).store(0);
        reset_counters(s->updat);
        s->updat.text[0] = '\0';
    }
    ~OpScope()
    {
        t_current = prev_;
    }
    OpScope(const OpScope&) = delete;
    OpScope& operator=(const OpScope&) = delete;

private:
    cl_session* prev_;
};

// TiLP: tilp_options_increase_timeout() for apps, OS, TIGroup restore and deletions.
class TimeoutBump
{
public:
    explicit TimeoutBump(cl_session* s) : cable_(s->cable)
    {
        old_ = ticables_get_timeout(cable_);
        const unsigned int want = tifiles_calc_is_ti8x(s->model) ? 300u : 150u;
        if (old_ < want)
        {
            ticables_options_set_timeout(cable_, want);
        }
    }
    ~TimeoutBump()
    {
        ticables_options_set_timeout(cable_, old_);
    }
    TimeoutBump(const TimeoutBump&) = delete;
    TimeoutBump& operator=(const TimeoutBump&) = delete;

private:
    CableHandle* cable_;
    unsigned int old_;
};

// ----- open / close helpers ------------------------------------------------

void release_handles(cl_session* s)
{
    if (s->calc != nullptr)
    {
        if (s->attached)
        {
            ticalcs_cable_detach(s->calc);   // closes the cable
        }
        ticalcs_handle_del(s->calc);         // would detach too, but be explicit (TiLP order)
        s->calc = nullptr;
    }
    if (s->cable != nullptr)
    {
        ticables_handle_del(s->cable);
        s->cable = nullptr;
    }
    s->attached = false;
}

// Creates cable + calc handles and attaches them. ticalcs_cable_attach() itself calls
// ticables_cable_open(), so the cable must NOT be opened beforehand (ticables does not
// guard against a double open).
int create_handles(cl_session* s)
{
    s->cable = ticables_handle_new(s->cable_model, s->cable_port);
    if (s->cable == nullptr)
    {
        return CL_E_INVALID_ARG;
    }
    if (s->timeout_ds != 0)
    {
        ticables_options_set_timeout(s->cable, s->timeout_ds);
    }

    s->calc = ticalcs_handle_new(s->model);
    if (s->calc == nullptr)
    {
        ticables_handle_del(s->cable);
        s->cable = nullptr;
        return CL_E_NOT_SUPPORTED;
    }

    int ret = ticalcs_update_set(s->calc, &s->updat);
    if (ret == 0)
    {
        ret = ticalcs_cable_attach(s->calc, s->cable);
    }
    if (ret != 0)
    {
        // attach marks the handle attached even when opening failed; handle_del copes.
        ticalcs_handle_del(s->calc);
        s->calc = nullptr;
        ticables_handle_del(s->cable);
        s->cable = nullptr;
        return map_ret(nullptr, ret);
    }
    s->attached = true;

    if (s->cable_model != CABLE_USB)
    {
        // TiLP: "BlackLink & ParallelLink need a reset before use" (applied to every non-DirectLink cable).
        ticables_cable_reset(s->cable);
        Sleep(2000);
    }
    return CL_OK;
}

// TiLP: on Windows every ready check over DirectLink closes and re-opens the device,
// because the calculator may have been switched off / re-plugged since the last command.
int reopen(cl_session* s)
{
    release_handles(s);
    return create_handles(s);
}

// TiLP: tilp_calc_check_version("2.09") before deleting / creating folders on AMS.
int check_ams_209(cl_session* s)
{
    if (!(tifiles_is_flash(s->model) && ticonv_model_is_ti68k(s->model)))
    {
        return CL_OK;
    }
    if (!has_features(s, OPS_VERSION))
    {
        return CL_OK;
    }
    CalcInfos infos;
    std::memset(&infos, 0, sizeof(infos));
    const int ret = ticalcs_calc_get_version(s->calc, &infos);
    if (ret != 0)
    {
        return map_ret(s, ret);
    }
    if (std::strcmp(infos.os_version, "2.09") < 0)
    {
        return CL_E_NOT_SUPPORTED;
    }
    return CL_OK;
}

// ----- names ---------------------------------------------------------------

// Models whose variable names are stored tokenized (ticonv_varname_detokenize does real work).
bool model_has_tokenized_names(CalcModel m)
{
    switch (m)
    {
    case CALC_TI73:
    case CALC_TI82:
    case CALC_TI83:
    case CALC_TI83P:
    case CALC_TI84P:
    case CALC_TI84PC:
    case CALC_TI85:
    case CALC_TI86:
        return true;
    default:
        return false;
    }
}

// UTF-8 display name -> raw on-calc name (inverse of ticonv_varname_to_utf8), as GFM's ctree.c does.
int utf8_to_calc_name(CalcModel m, const char* utf8, unsigned char type, bool is_folder, char* dst, size_t cap)
{
    if (utf8 == nullptr || *utf8 == '\0' || !g_utf8_validate(utf8, -1, nullptr))
    {
        return CL_E_INVALID_ARG;
    }
    unsigned short* u16 = ticonv_utf8_to_utf16(utf8);
    if (u16 == nullptr)
    {
        return CL_E_INVALID_ARG;
    }
    char* ti = ticonv_charset_utf16_to_ti(m, u16);
    ticonv_utf16_free(u16);
    if (ti == nullptr)
    {
        return CL_E_INVALID_ARG;
    }

    std::string out;
    if (!is_folder && model_has_tokenized_names(m))
    {
        char* tok = ticonv_varname_tokenize(m, ti, type);
        out = (tok != nullptr) ? tok : "";
        ticonv_varname_free(tok);
        if (out.size() > 8)
        {
            out.resize(8);   // GFM: TI-Z80 names are at most 8 bytes
        }
    }
    else
    {
        out = ti;
    }
    ticonv_ti_free(ti);

    if (out.empty() || out.size() >= cap)
    {
        return CL_E_INVALID_ARG;
    }
    std::memset(dst, 0, cap);
    std::memcpy(dst, out.data(), out.size());
    return CL_OK;
}

// ----- dirlist flattening --------------------------------------------------

void free_var_fields(cl_var& e)
{
    std::free(const_cast<char*>(e.folder));
    std::free(const_cast<char*>(e.name));
    std::free(const_cast<char*>(e.type_name));
    std::free(const_cast<char*>(e.file_ext));
    if (e.opaque != nullptr)
    {
        tifiles_ve_delete(static_cast<VarEntry*>(e.opaque));
    }
    e = cl_var{};
}

// Moves a built list into a malloc'd array the caller frees with cl_free_dirlist. On success the
// vector no longer owns the entries' fields; on failure (throws) it still does.
void hand_over(std::vector<cl_var>& list, cl_var** out, int32_t* count)
{
    if (list.size() > static_cast<size_t>(INT32_MAX))
    {
        throw std::bad_alloc();
    }
    if (!list.empty())
    {
        cl_var* arr = static_cast<cl_var*>(std::malloc(list.size() * sizeof(cl_var)));
        if (arr == nullptr)
        {
            throw std::bad_alloc();
        }
        std::memcpy(arr, list.data(), list.size() * sizeof(cl_var));
        *out = arr;
        *count = static_cast<int32_t>(list.size());
        list.clear();
    }
}

void add_entry(std::vector<cl_var>& out, CalcModel m, const VarEntry* ve, bool is_app)
{
    out.push_back(cl_var{});
    cl_var& e = out.back();

    // Own copy of the raw entry WITHOUT data: tifiles_ve_dup() would g_malloc0(ve->size)
    // bytes per entry, which is wasteful for large apps / Nspire documents.
    VarEntry* copy = tifiles_ve_create();
    if (copy == nullptr)
    {
        throw std::bad_alloc();
    }
    std::memcpy(copy, ve, sizeof(VarEntry));
    copy->data = nullptr;
    e.opaque = copy;

    e.folder    = dup_cstr(ve->folder[0] ? take_gstr(ticonv_varname_to_utf8(m, ve->folder, 0xFF)).c_str() : "");
    e.name      = dup_cstr(take_gstr(ticonv_varname_to_utf8(m, ve->name, ve->type)).c_str());
    e.type_name = dup_cstr(tifiles_vartype2string(m, ve->type));
    e.file_ext  = dup_cstr(tifiles_vartype2fext(m, ve->type));
    e.size      = ve->size;
    e.type      = ve->type;
    e.attr      = (ve->attr <= ATTRB_ARCHIVED) ? ve->attr : static_cast<uint8_t>(CL_ATTR_NONE);
    e.is_app    = is_app ? 1 : 0;
}

// Vars:  root(TreeInfo) -> folder (NULL on TI-8x, VarEntry on TI-9x) -> var (VarEntry)
// Apps:  root(TreeInfo) -> (NULL)                                    -> app (VarEntry)
void walk_two_level(std::vector<cl_var>& out, CalcModel m, GNode* root, bool is_app)
{
    for (GNode* f = root->children; f != nullptr; f = f->next)
    {
        for (GNode* v = f->children; v != nullptr; v = v->next)
        {
            if (v->data != nullptr)
            {
                add_entry(out, m, static_cast<VarEntry*>(v->data), is_app);
            }
        }
    }
}

// Nspire: arbitrary depth; directories are VarEntry nodes of type NSP_DIR.
void walk_nspire(std::vector<cl_var>& out, CalcModel m, GNode* node, int depth)
{
    if (depth > 64)
    {
        return;
    }
    for (GNode* c = node->children; c != nullptr; c = c->next)
    {
        const VarEntry* ve = static_cast<VarEntry*>(c->data);
        if (ve != nullptr && ve->type != NSP_DIR)
        {
            add_entry(out, m, ve, false);
        }
        if (c->children != nullptr)
        {
            walk_nspire(out, m, c, depth + 1);
        }
    }
}

// ----- file classification -------------------------------------------------

constexpr uint8_t TIFL_TYPE_CERT = 0x25;   // TI83p_CERT == TI84p_CERT == TI89_CERTIF

int32_t classify(const char* path)
{
    if (!tifiles_file_is_ti(path))
    {
        return CL_FILE_UNKNOWN;
    }
    if (tifiles_file_is_tigroup(path))
    {
        return CL_FILE_TIGROUP;
    }
    if (tifiles_file_is_os(path))
    {
        return CL_FILE_OS;
    }
    if (tifiles_file_is_app(path))
    {
        return CL_FILE_APP;
    }
    if (tifiles_file_is_flash(path))
    {
        uint8_t dev_type = 0;
        uint8_t data_type = 0;
        if (tifiles_file_has_tifl_header(path, &dev_type, &data_type) && data_type == TIFL_TYPE_CERT)
        {
            return CL_FILE_CERT;
        }
        return CL_FILE_UNKNOWN;   // licence, other FLASH payloads
    }
    // TI-68k "backups" share the group extension (89g); TiLP sends those as variables.
    if (tifiles_file_is_backup(path) && !tifiles_file_is_group(path))
    {
        return CL_FILE_BACKUP;
    }
    if (tifiles_file_is_regular(path))
    {
        return CL_FILE_VARIABLE;
    }
    return CL_FILE_UNKNOWN;
}

// ----- names for UI --------------------------------------------------------

const char* const k_model_names[CALC_MAX] =
{
    "Unknown",                       // CALC_NONE
    "TI-73",                         // CALC_TI73
    "TI-82",                         // CALC_TI82
    "TI-83",                         // CALC_TI83
    "TI-83 Plus",                    // CALC_TI83P
    "TI-84 Plus",                    // CALC_TI84P
    "TI-85",                         // CALC_TI85
    "TI-86",                         // CALC_TI86
    "TI-89",                         // CALC_TI89
    "TI-89 Titanium",                // CALC_TI89T
    "TI-92",                         // CALC_TI92
    "TI-92 Plus",                    // CALC_TI92P
    "Voyage 200",                    // CALC_V200
    "TI-84 Plus",                    // CALC_TI84P_USB
    "TI-89 Titanium",                // CALC_TI89T_USB
    "TI-Nspire",                     // CALC_NSPIRE
    "TI-80",                         // CALC_TI80
    "TI-84 Plus C Silver Edition",   // CALC_TI84PC
    "TI-84 Plus C Silver Edition",   // CALC_TI84PC_USB
    "TI-83 Premium CE",              // CALC_TI83PCE_USB
    "TI-84 Plus CE",                 // CALC_TI84PCE_USB
    "TI-82 Advanced",                // CALC_TI82A_USB
    "TI-84 Plus T",                  // CALC_TI84PT_USB
};

const char* variant_name(CableVariant v)
{
    switch (v)
    {
    case CABLE_VARIANT_TIGLUSB:  return "TI-GRAPH LINK USB (SilverLink)";
    case CABLE_VARIANT_TI84P:    return "TI-84 Plus";
    case CABLE_VARIANT_TI84PSE:  return "TI-84 Plus Silver Edition";
    case CABLE_VARIANT_TI84PCSE: return "TI-84 Plus C Silver Edition";
    case CABLE_VARIANT_TI84PCE:  return "TI-84 Plus CE";
    case CABLE_VARIANT_TI83PCE:  return "TI-83 Premium CE";
    case CABLE_VARIANT_TI82A:    return "TI-82 Advanced";
    case CABLE_VARIANT_TI89TM:   return "TI-89 Titanium";
    case CABLE_VARIANT_NSPIRE:   return "TI-Nspire";
    case CABLE_VARIANT_TI84PT:   return "TI-84 Plus T";
    default:                     return nullptr;
    }
}

const char* cl_error_text(int code)
{
    switch (code)
    {
    case CL_OK:              return "No error.";
    case CL_E_INVALID_ARG:   return "Invalid argument.";
    case CL_E_NO_MEMORY:     return "Out of memory.";
    case CL_E_NOT_SUPPORTED: return "This operation is not supported by the connected calculator model.";
    case CL_E_NOT_OPEN:      return "The connection to the calculator is not open.";
    case CL_E_CANCELLED:     return "The operation was cancelled.";
    case CL_E_BAD_FILE:      return "The file is not a TI calculator file, or it is not intended for this calculator model.";
    case CL_E_INTERNAL:      return "Internal error in the CalcLink native library.";
    default:                 return nullptr;
    }
}

} // namespace

// ===========================================================================
// Library
// ===========================================================================

extern "C" {

CL_API int32_t CL_CALL cl_abi_version(void)
{
    return CL_ABI_VERSION;
}

CL_API int CL_CALL cl_init(void)
{
    return ensure_init();
}

CL_API void CL_CALL cl_shutdown(void)
{
    try
    {
        std::lock_guard<std::mutex> lock(g_init_mutex);
        if (g_inited)
        {
            ticalcs_library_exit();
            tifiles_library_exit();
            ticables_library_exit();
            g_inited = false;
        }
    }
    catch (...)
    {
    }
}

CL_API const char* CL_CALL cl_lib_versions(void)
{
    static std::once_flag once;
    static char buffer[256] = "";
    try
    {
        std::call_once(once, []
        {
            std::snprintf(buffer, sizeof(buffer), "ticables2 %s; tifiles2 %s; ticalcs2 %s; ticonv %s",
                          ticables_version_get(), tifiles_version_get(),
                          ticalcs_version_get(), ticonv_version_get());
        });
    }
    catch (...)
    {
    }
    return buffer;
}

CL_API char* CL_CALL cl_error_message(int code)
{
    try
    {
        std::string text;
        if (const char* own = cl_error_text(code))
        {
            text = own;
        }
        else if (code > 0)
        {
            // Each *_error_get returns 0 when it recognised the code, else the code itself.
            char* msg = nullptr;
            if (ticalcs_error_get(code, &msg) == 0 && msg != nullptr)
            {
                text = to_utf8(msg);
                ticalcs_error_free(msg);
            }
            else
            {
                msg = nullptr;
                if (ticables_error_get(code, &msg) == 0 && msg != nullptr)
                {
                    text = to_utf8(msg);
                    ticables_error_free(msg);
                }
                else
                {
                    msg = nullptr;
                    if (tifiles_error_get(code, &msg) == 0 && msg != nullptr)
                    {
                        text = to_utf8(msg);
                        tifiles_error_free(msg);
                    }
                }
            }
        }
        if (text.empty())
        {
            char buf[64];
            std::snprintf(buf, sizeof(buf), "Unknown error %d.", code);
            text = buf;
        }
        return dup_cstr(text.c_str());
    }
    catch (...)
    {
        return nullptr;
    }
}

CL_API void CL_CALL cl_free(void* p)
{
    std::free(p);
}

// ===========================================================================
// Discovery
// ===========================================================================

CL_API int CL_CALL cl_probe_usb(cl_device* out, int32_t capacity, int32_t* count)
{
    return guarded([&]() -> int
    {
        if (count == nullptr || capacity < 0 || (capacity > 0 && out == nullptr))
        {
            return CL_E_INVALID_ARG;
        }
        *count = 0;
        int r = ensure_init();
        if (r != CL_OK)
        {
            return r;
        }

        CableDeviceInfo* infos = nullptr;
        int n_infos = 0;
        int ret = ticables_get_usb_device_info(&infos, &n_infos);
        if (ret != 0 && n_infos == 0)
        {
            if (infos != nullptr)
            {
                ticables_free_usb_device_info(infos);
            }
            // tilibs reports "no TI device on the bus" as ERR_LIBUSB_OPEN (37 in libticables'
            // private error.h; verified at runtime). For a probe that just means zero devices.
            // Anything else (e.g. ERR_LIBUSBWIN32_NOT_PRESENT: driver missing) is a real error.
            constexpr int kTicablesErrLibusbOpen = 37;
            if (ret == kTicablesErrLibusbOpen)
            {
                return CL_OK;
            }
            return map_ret(nullptr, ret);
        }

        // Same enumeration order as above (both are "in the same order as PORT#x").
        int* pids = nullptr;
        int n_pids = 0;
        if (ticables_get_usb_devices(&pids, &n_pids) != 0)
        {
            n_pids = 0;
        }

        int32_t found = 0;
        for (int i = 0; i < n_infos && i < PORT_MAX - PORT_1; ++i)
        {
            const CableDeviceInfo& info = infos[i];
            if (info.family == CABLE_FAMILY_UNKNOWN)
            {
                continue;
            }

            cl_device dev;
            std::memset(&dev, 0, sizeof(dev));
            dev.cable_port = PORT_1 + i;
            dev.vid = TI_USB_VID;
            dev.pid = static_cast<uint16_t>((pids != nullptr && i < n_pids) ? pids[i] : 0);

            const char* name = nullptr;
            if (info.family == CABLE_FAMILY_DBUS)
            {
                // SilverLink: the cable says nothing about the calculator; ask it (TiLP:
                // tilp_device_probe_usb step 2). Takes a few seconds if nothing answers (fast probe, then slow probe).
                dev.cable_model = CABLE_SLV;
                CalcModel model = CALC_NONE;
                CableHandle* h = ticables_handle_new(CABLE_SLV, static_cast<CablePort>(dev.cable_port));
                if (h != nullptr)
                {
                    ticables_options_set_timeout(h, 10);
                    if (ticables_cable_open(h) == 0)
                    {
                        // ticalcs_probe_calc tries the fast FLASH probe (73/83+/84+/89/92+/V200)
                        // first, then the slower probe for non-FLASH models (82/83/85/86/92).
                        // ticalcs_probe_usb_calc would only do the first step.
                        if (ticalcs_probe_calc(h, &model) != 0)
                        {
                            model = CALC_NONE;
                        }
                        ticables_cable_close(h);
                    }
                    ticables_handle_del(h);
                }
                dev.calc_model = model;
                name = (model > CALC_NONE && model < CALC_MAX) ? k_model_names[model] : variant_name(info.variant);
            }
            else
            {
                dev.cable_model = CABLE_USB;
                dev.calc_model = ticalcs_device_info_to_model(&info);
                name = variant_name(info.variant);
                if (name == nullptr && dev.calc_model > CALC_NONE && dev.calc_model < CALC_MAX)
                {
                    name = k_model_names[dev.calc_model];
                }
            }
            copy_utf8(dev.name, sizeof(dev.name), name != nullptr ? name : "TI USB device");

            if (found < capacity)
            {
                out[found] = dev;
            }
            ++found;
        }

        if (pids != nullptr)
        {
            ticables_free_usb_devices(pids);
        }
        if (infos != nullptr)
        {
            ticables_free_usb_device_info(infos);
        }
        *count = found;
        return CL_OK;
    });
}

CL_API int CL_CALL cl_classify_file(const char* path, int32_t* file_class, int32_t* calc_model)
{
    return guarded([&]() -> int
    {
        if (path == nullptr || file_class == nullptr)
        {
            return CL_E_INVALID_ARG;
        }
        *file_class = CL_FILE_UNKNOWN;
        if (calc_model != nullptr)
        {
            *calc_model = CALC_NONE;
        }
        int r = ensure_init();
        if (r != CL_OK)
        {
            return r;
        }
        if (!g_utf8_validate(path, -1, nullptr))
        {
            return CL_E_INVALID_ARG;
        }
        if (!is_regular_file(path))
        {
            return CL_E_BAD_FILE;
        }
        *file_class = classify(path);
        if (calc_model != nullptr && *file_class != CL_FILE_UNKNOWN)
        {
            *calc_model = tifiles_file_get_model(path);
        }
        return CL_OK;
    });
}

CL_API const char* CL_CALL cl_model_name(int32_t calc_model)
{
    if (calc_model > CALC_NONE && calc_model < CALC_MAX)
    {
        return k_model_names[calc_model];
    }
    return k_model_names[CALC_NONE];
}

// ===========================================================================
// Session
// ===========================================================================

CL_API int CL_CALL cl_open(int32_t cable_model, int32_t cable_port, int32_t calc_model,
                           int32_t timeout_ds, cl_session** out)
{
    return guarded([&]() -> int
    {
        if (out == nullptr)
        {
            return CL_E_INVALID_ARG;
        }
        *out = nullptr;
        if (cable_model <= CABLE_NUL || cable_model >= CABLE_MAX ||
            cable_port < PORT_0 || cable_port >= PORT_MAX ||
            calc_model <= CALC_NONE || calc_model >= CALC_MAX ||
            timeout_ds < 0)
        {
            return CL_E_INVALID_ARG;
        }
        int r = ensure_init();
        if (r != CL_OK)
        {
            return r;
        }

        cl_session* s = new cl_session();
        s->cable_model = static_cast<CableModel>(cable_model);
        s->cable_port  = static_cast<CablePort>(cable_port);
        // CALC_TI84P over a DirectLink cable must be driven as CALC_TI84P_USB, etc.
        s->model       = ticalcs_remap_model_to_usb(s->cable_model, static_cast<CalcModel>(calc_model));
        s->timeout_ds  = static_cast<unsigned int>(timeout_ds);
        init_update(s->updat);

        {
            OpScope op(s);
            r = create_handles(s);
        }
        if (r != CL_OK)
        {
            delete s;
            return r;
        }
        *out = s;
        return CL_OK;
    });
}

CL_API void CL_CALL cl_close(cl_session* s)
{
    if (s == nullptr)
    {
        return;
    }
    try
    {
        {
            OpScope op(s);
            release_handles(s);
        }
        delete s;
    }
    catch (...)
    {
    }
}

CL_API uint32_t CL_CALL cl_features(cl_session* s)
{
    if (check_session(s) != CL_OK)
    {
        return 0;
    }
    return features_of(s);
}

CL_API int32_t CL_CALL cl_calc_model(cl_session* s)
{
    return (s != nullptr) ? static_cast<int32_t>(s->model) : CALC_NONE;
}

CL_API void CL_CALL cl_set_progress(cl_session* s, cl_progress_fn fn, void* user)
{
    if (s == nullptr)
    {
        return;
    }
    s->progress = fn;
    s->progress_user = user;
}

CL_API void CL_CALL cl_cancel(cl_session* s)
{
    if (s == nullptr)
    {
        return;
    }
    cancel_flag(s).store(1);
}

CL_API int CL_CALL cl_is_ready(cl_session* s)
{
    return guarded([&]() -> int
    {
        if (s == nullptr)
        {
            return CL_E_INVALID_ARG;
        }
        OpScope op(s);

        // TiLP (tilp_calc_isready, Windows branch): hot-plug - re-open DirectLink every time.
        // This also recovers a session whose previous re-open failed (attached == false).
        if (s->cable_model == CABLE_USB || !s->attached)
        {
            int r = reopen(s);
            if (r != CL_OK)
            {
                return r;
            }
        }

        if (!has_features(s, OPS_ISREADY))
        {
            return CL_OK;
        }

        // First check: fast.
        const unsigned int to = ticables_options_set_timeout(s->cable, 10);
        int err = ticalcs_calc_isready(s->calc);
        ticables_options_set_timeout(s->cable, to);

        if (err == TI_ERR_NOT_READY)
        {
            switch (s->model)
            {
            case CALC_TI89:
            case CALC_TI89T:
                err = ticalcs_calc_send_key(s->calc, 277);    // KEY89_HOME
                break;
            case CALC_TI92:
            case CALC_TI92P:
            case CALC_V200:
                err = ticalcs_calc_send_key(s->calc, 8273);   // KEY92P_HOME
                break;
            default:
                break;
            }
        }

        if (s->model == CALC_NSPIRE)
        {
            return map_ret(s, err);
        }

        if (err != 0)
        {
            // Second check: slower (normal timeout).
            err = ticalcs_calc_isready(s->calc);
        }
        return map_ret(s, err);
    });
}

CL_API int CL_CALL cl_get_info(cl_session* s, cl_calc_info* out)
{
    return guarded([&]() -> int
    {
        if (out == nullptr)
        {
            return CL_E_INVALID_ARG;
        }
        std::memset(out, 0, sizeof(*out));
        int r = check_session(s);
        if (r != CL_OK)
        {
            return r;
        }
        out->calc_model = s->model;
        out->mask = static_cast<uint32_t>(INFOS_CALC_MODEL);
        if (!has_features(s, OPS_VERSION))
        {
            return CL_E_NOT_SUPPORTED;
        }

        OpScope op(s);
        CalcInfos infos;
        std::memset(&infos, 0, sizeof(infos));
        const int ret = ticalcs_calc_get_version(s->calc, &infos);
        if (ret != 0)
        {
            return map_ret(s, ret);
        }

        out->mask = static_cast<uint32_t>(infos.mask) | static_cast<uint32_t>(INFOS_CALC_MODEL);
        out->calc_model = (infos.model > CALC_NONE && infos.model < CALC_MAX) ? infos.model : s->model;
        copy_utf8(out->product_name, sizeof(out->product_name), infos.product_name);
        copy_utf8(out->product_id, sizeof(out->product_id), infos.product_id);
        copy_utf8(out->os_version, sizeof(out->os_version), infos.os_version);
        copy_utf8(out->boot_version, sizeof(out->boot_version), infos.boot_version);
        copy_utf8(out->boot2_version, sizeof(out->boot2_version), infos.boot2_version);
        out->ram_user        = infos.ram_user;
        out->ram_free        = infos.ram_free;
        out->flash_user      = infos.flash_user;
        out->flash_free      = infos.flash_free;
        out->lcd_width       = infos.lcd_width;
        out->lcd_height      = infos.lcd_height;
        out->bits_per_pixel  = infos.bits_per_pixel;
        out->hw_version      = infos.hw_version;
        out->battery_ok      = infos.battery;
        out->run_level       = infos.run_level;
        out->exact_math      = infos.exact_math;
        out->clock_support   = infos.clock_support;
        out->color_screen    = infos.color_screen;
        out->python_on_board = infos.python_on_board;
        return CL_OK;
    });
}

CL_API int CL_CALL cl_get_calc_id(cl_session* s, char* out, int32_t capacity)
{
    return guarded([&]() -> int
    {
        if (out == nullptr || capacity <= 0)
        {
            return CL_E_INVALID_ARG;
        }
        out[0] = '\0';
        int r = check_session(s);
        if (r != CL_OK)
        {
            return r;
        }
        if (!has_features(s, OPS_IDLIST))
        {
            return CL_E_NOT_SUPPORTED;
        }

        OpScope op(s);
        // Drivers write up to ~24 chars (TI-73 family formats every received byte); oversize the buffer.
        uint8_t id[256] = {};
        const int ret = ticalcs_calc_recv_idlist(s->calc, id);
        if (ret != 0)
        {
            return map_ret(s, ret);
        }
        id[sizeof(id) - 1] = 0;
        copy_utf8(out, static_cast<size_t>(capacity), reinterpret_cast<const char*>(id));
        return CL_OK;
    });
}

CL_API int CL_CALL cl_get_memfree(cl_session* s, uint64_t* ram, uint64_t* flash)
{
    return guarded([&]() -> int
    {
        if (ram == nullptr && flash == nullptr)
        {
            return CL_E_INVALID_ARG;
        }
        if (ram != nullptr)
        {
            *ram = 0;
        }
        if (flash != nullptr)
        {
            *flash = 0;
        }
        int r = check_session(s);
        if (r != CL_OK)
        {
            return r;
        }
        if (!has_features(s, FTS_MEMFREE))
        {
            return CL_E_NOT_SUPPORTED;
        }
        OpScope op(s);
        if (ticalcs_model_supports_dusb(s->model))
        {
            Sleep(100);   // TiLP: "needed by TI84+/USB" when following a dirlist
        }
        uint32_t r32 = 0;
        uint32_t f32 = 0;
        const int ret = ticalcs_calc_get_memfree(s->calc, &r32, &f32);
        if (ret != 0)
        {
            return map_ret(s, ret);
        }
        // Drivers preset both to (uint32_t)-1 and fill in only what the calculator reports (the
        // TI-73/83+/84+ over DBUS report RAM only): map "not reported" to 0 = unknown.
        if (ram != nullptr)
        {
            *ram = (r32 == UINT32_MAX) ? 0 : r32;
        }
        if (flash != nullptr)
        {
            *flash = (f32 == UINT32_MAX) ? 0 : f32;
        }
        return CL_OK;
    });
}

// ===========================================================================
// Variables
// ===========================================================================

CL_API int CL_CALL cl_get_dirlist(cl_session* s, cl_var** out, int32_t* count)
{
    return guarded([&]() -> int
    {
        if (out == nullptr || count == nullptr)
        {
            return CL_E_INVALID_ARG;
        }
        *out = nullptr;
        *count = 0;
        int r = check_session(s);
        if (r != CL_OK)
        {
            return r;
        }
        if (!has_features(s, OPS_DIRLIST))
        {
            return CL_E_NOT_SUPPORTED;
        }

        OpScope op(s);
        GNode* vars = nullptr;
        GNode* apps = nullptr;
        const int ret = ticalcs_calc_get_dirlist(s->calc, &vars, &apps);

        struct TreeGuard
        {
            GNode** v;
            GNode** a;
            ~TreeGuard()
            {
                ticalcs_dirlist_destroy(v);
                ticalcs_dirlist_destroy(a);
            }
        } trees{ &vars, &apps };

        if (ret != 0)
        {
            return map_ret(s, ret);
        }

        std::vector<cl_var> list;
        try
        {
            if (vars != nullptr)
            {
                if (s->model == CALC_NSPIRE)
                {
                    walk_nspire(list, s->model, vars, 0);
                }
                else
                {
                    walk_two_level(list, s->model, vars, false);
                }
            }
            if (apps != nullptr)
            {
                walk_two_level(list, s->model, apps, true);
            }
            hand_over(list, out, count);
        }
        catch (...)
        {
            for (cl_var& e : list)
            {
                free_var_fields(e);
            }
            throw;
        }
        return CL_OK;
    });
}

CL_API int CL_CALL cl_read_file_vars(const char* path, cl_var** out, int32_t* count)
{
    return guarded([&]() -> int
    {
        if (out == nullptr || count == nullptr)
        {
            return CL_E_INVALID_ARG;
        }
        *out = nullptr;
        *count = 0;
        if (path == nullptr || !g_utf8_validate(path, -1, nullptr))
        {
            return CL_E_INVALID_ARG;
        }
        int r = ensure_init();
        if (r != CL_OK)
        {
            return r;
        }
        if (!is_regular_file(path) || classify(path) != CL_FILE_VARIABLE)
        {
            return CL_E_BAD_FILE;
        }

        const CalcModel m = tifiles_file_get_model(path);
        FileContent* content = tifiles_content_create_regular(m);
        if (content == nullptr)
        {
            return CL_E_NO_MEMORY;
        }
        if (tifiles_file_read_regular(path, content) != 0)
        {
            // tifiles_file_read_regular() frees the content itself on error.
            return CL_E_BAD_FILE;
        }
        std::vector<cl_var> list;
        try
        {
            for (unsigned int i = 0; i < content->num_entries; i++)
            {
                if (content->entries[i] != nullptr)
                {
                    add_entry(list, m, content->entries[i], false);
                }
            }
            hand_over(list, out, count);
        }
        catch (...)
        {
            for (cl_var& e : list)
            {
                free_var_fields(e);
            }
            tifiles_content_delete_regular(content);
            throw;
        }
        tifiles_content_delete_regular(content);
        return CL_OK;
    });
}

CL_API void CL_CALL cl_free_dirlist(cl_var* list, int32_t count)
{
    if (list == nullptr)
    {
        return;
    }
    try
    {
        for (int32_t i = 0; i < count; ++i)
        {
            free_var_fields(list[i]);
        }
    }
    catch (...)
    {
    }
    std::free(list);
}

CL_API int CL_CALL cl_send_file(cl_session* s, const char* path)
{
    return guarded([&]() -> int
    {
        if (path == nullptr || !g_utf8_validate(path, -1, nullptr))
        {
            return CL_E_INVALID_ARG;
        }
        int r = check_session(s);
        if (r != CL_OK)
        {
            return r;
        }
        if (!is_regular_file(path))
        {
            return CL_E_BAD_FILE;
        }

        const int32_t cls = classify(path);
        const CalcModel fmodel = tifiles_file_get_model(path);
        const bool compatible = (fmodel == CALC_NONE) || tifiles_calc_are_compat(s->model, fmodel);

        OpScope op(s);
        int ret = 0;
        switch (cls)
        {
        case CL_FILE_VARIABLE:
            if (!has_features(s, OPS_VARS))
            {
                return CL_E_NOT_SUPPORTED;
            }
            if (!compatible)
            {
                return CL_E_BAD_FILE;
            }
            {
                // Same as ticalcs_calc_send_var2(), but TI-8x variables are always sent to RAM.
                // A file's "archived" flag only records where the variable lived when it was
                // received. Verified on a TI-83 Plus (OS 1.16) over SilverLink: an RTS with the
                // archived flag is never answered (timeout, even with a 30 s timeout), while the
                // identical variable in RAM is accepted at once. Users archive on the calculator.
                FileContent* content = tifiles_content_create_regular(s->model);
                if (content == nullptr)
                {
                    return CL_E_NO_MEMORY;
                }
                ret = tifiles_file_read_regular(path, content);
                if (ret != 0)
                {
                    // tifiles_file_read_regular() frees the content itself on error.
                    return map_ret(s, ret);
                }
                if (tifiles_calc_is_ti8x(s->model))
                {
                    for (unsigned int i = 0; i < content->num_entries; i++)
                    {
                        if (content->entries[i] != nullptr && content->entries[i]->attr == ATTRB_ARCHIVED)
                        {
                            content->entries[i]->attr = ATTRB_NONE;
                        }
                    }
                }
                TimeoutBump bump(s);
                // TiLP: the last (here: only) file of a selection is sent with MODE_SEND_LAST_VAR.
                // A variable that already exists is overwritten without any reply from the calculator
                // (verified on a TI-83 Plus, OS 1.16, in RAM and archived), so callers must warn first:
                // see cl_read_file_vars.
                ret = ticalcs_calc_send_var(s->calc, MODE_SEND_LAST_VAR, content);
                tifiles_content_delete_regular(content);
            }
            break;

        case CL_FILE_APP:
            if (!has_features(s, OPS_FLASH) || !ticalcs_model_supports_installing_flashapps(s->model))
            {
                return CL_E_NOT_SUPPORTED;
            }
            if (!compatible)
            {
                return CL_E_BAD_FILE;
            }
            {
                TimeoutBump bump(s);
                ret = ticalcs_calc_send_app2(s->calc, path);
            }
            break;

        case CL_FILE_OS:
            if (!has_features(s, OPS_OS))
            {
                return CL_E_NOT_SUPPORTED;
            }
            if (!tifiles_file_test(path, TIFILE_OS, s->model))
            {
                return CL_E_BAD_FILE;
            }
            {
                // TiLP: no ready check before an OS transfer (boot code rejects RDY).
                TimeoutBump bump(s);
                ret = ticalcs_calc_send_os2(s->calc, path);
            }
            break;

        case CL_FILE_BACKUP:
            if (!has_features(s, OPS_BACKUP))
            {
                return CL_E_NOT_SUPPORTED;
            }
            if (!compatible)
            {
                return CL_E_BAD_FILE;
            }
            ret = ticalcs_calc_send_backup2(s->calc, path);
            break;

        case CL_FILE_TIGROUP:
            // ticalcs_calc_send_tigroup() needs a dirlist (to skip apps already present) and var transfer.
            if (!has_features(s, OPS_DIRLIST | OPS_VARS))
            {
                return CL_E_NOT_SUPPORTED;
            }
            {
                // TiLP "Send TIGroup": TIG_ALL, without TIG_BACKUP (which would erase a TI-68k first).
                TimeoutBump bump(s);
                ret = ticalcs_calc_send_tigroup2(s->calc, path, TIG_ALL);
            }
            break;

        case CL_FILE_CERT:
            if (!has_features(s, FTS_CERT))
            {
                return CL_E_NOT_SUPPORTED;
            }
            if (!compatible)
            {
                return CL_E_BAD_FILE;
            }
            ret = ticalcs_calc_send_cert2(s->calc, path);
            break;

        default:
            return CL_E_BAD_FILE;
        }
        return map_ret(s, ret);
    });
}

CL_API int CL_CALL cl_recv_var(cl_session* s, const cl_var* v, const char* path)
{
    return guarded([&]() -> int
    {
        if (v == nullptr || v->opaque == nullptr || path == nullptr || !g_utf8_validate(path, -1, nullptr))
        {
            return CL_E_INVALID_ARG;
        }
        int r = check_session(s);
        if (r != CL_OK)
        {
            return r;
        }
        VarEntry* ve = static_cast<VarEntry*>(v->opaque);

        OpScope op(s);
        int ret;
        if (v->is_app)
        {
            if (!has_features(s, FTS_FLASH))   // TiLP: tilp_calc_recv_app
            {
                return CL_E_NOT_SUPPORTED;
            }
            ret = ticalcs_calc_recv_app2(s->calc, path, ve);
        }
        else
        {
            if (!has_features(s, OPS_VARS))
            {
                return CL_E_NOT_SUPPORTED;
            }
            // Non-silent models (TI-82/85: FTS_SILENT unset) need the user to start the
            // transfer on the calculator (ticalcs_calc_recv_var_ns2); not supported yet.
            if (!has_features(s, FTS_SILENT))
            {
                return CL_E_NOT_SUPPORTED;
            }
            ret = ticalcs_calc_recv_var2(s->calc, MODE_NORMAL, path, ve);
        }
        return map_ret(s, ret);
    });
}

CL_API int CL_CALL cl_delete_var(cl_session* s, const cl_var* v)
{
    return guarded([&]() -> int
    {
        if (v == nullptr || v->opaque == nullptr)
        {
            return CL_E_INVALID_ARG;
        }
        int r = check_session(s);
        if (r != CL_OK)
        {
            return r;
        }
        if (!has_features(s, OPS_DELVAR))
        {
            return CL_E_NOT_SUPPORTED;
        }
        OpScope op(s);
        r = check_ams_209(s);
        if (r != CL_OK)
        {
            return r;
        }
        TimeoutBump bump(s);
        return map_ret(s, ticalcs_calc_del_var(s->calc, static_cast<VarEntry*>(v->opaque)));
    });
}

CL_API int CL_CALL cl_rename_var(cl_session* s, const cl_var* v, const char* new_folder, const char* new_name)
{
    return guarded([&]() -> int
    {
        if (v == nullptr || v->opaque == nullptr || new_name == nullptr || *new_name == '\0')
        {
            return CL_E_INVALID_ARG;
        }
        int r = check_session(s);
        if (r != CL_OK)
        {
            return r;
        }
        if (!has_features(s, OPS_RENAME))
        {
            return CL_E_NOT_SUPPORTED;
        }

        VarEntry* oldve = static_cast<VarEntry*>(v->opaque);
        VarEntry newve;
        std::memcpy(&newve, oldve, sizeof(VarEntry));
        newve.data = nullptr;   // not owned; never freed

        r = utf8_to_calc_name(s->model, new_name, oldve->type, false, newve.name, sizeof(newve.name));
        if (r != CL_OK)
        {
            return r;
        }
        if (new_folder != nullptr && *new_folder != '\0')
        {
            r = utf8_to_calc_name(s->model, new_folder, 0xFF, true, newve.folder, sizeof(newve.folder));
            if (r != CL_OK)
            {
                return r;
            }
        }

        OpScope op(s);
        return map_ret(s, ticalcs_calc_rename_var(s->calc, oldve, &newve));
    });
}

CL_API int CL_CALL cl_set_attr(cl_session* s, const cl_var* v, int32_t attr)
{
    return guarded([&]() -> int
    {
        if (v == nullptr || v->opaque == nullptr || attr < CL_ATTR_NONE || attr > CL_ATTR_ARCHIVED)
        {
            return CL_E_INVALID_ARG;
        }
        int r = check_session(s);
        if (r != CL_OK)
        {
            return r;
        }
        if (!has_features(s, OPS_CHATTR))
        {
            return CL_E_NOT_SUPPORTED;
        }
        OpScope op(s);
        return map_ret(s, ticalcs_calc_change_attr(s->calc, static_cast<VarEntry*>(v->opaque),
                                                   static_cast<FileAttr>(attr)));
    });
}

CL_API int CL_CALL cl_new_folder(cl_session* s, const char* name)
{
    return guarded([&]() -> int
    {
        if (name == nullptr || *name == '\0')
        {
            return CL_E_INVALID_ARG;
        }
        int r = check_session(s);
        if (r != CL_OK)
        {
            return r;
        }
        if (!has_features(s, OPS_NEWFLD))
        {
            return CL_E_NOT_SUPPORTED;
        }
        VarEntry vr;
        std::memset(&vr, 0, sizeof(vr));
        r = utf8_to_calc_name(s->model, name, 0xFF, true, vr.folder, sizeof(vr.folder));
        if (r != CL_OK)
        {
            return r;
        }
        OpScope op(s);
        r = check_ams_209(s);
        if (r != CL_OK)
        {
            return r;
        }
        return map_ret(s, ticalcs_calc_new_fld(s->calc, &vr));
    });
}

CL_API int CL_CALL cl_execute(cl_session* s, const cl_var* v)
{
    return guarded([&]() -> int
    {
        if (v == nullptr || v->opaque == nullptr)
        {
            return CL_E_INVALID_ARG;
        }
        int r = check_session(s);
        if (r != CL_OK)
        {
            return r;
        }
        // There is no OPS_* bit for remote execution. These models use noop_execute(),
        // which silently returns 0 - report them as unsupported instead.
        switch (s->model)
        {
        case CALC_TI80:
        case CALC_TI82:
        case CALC_TI85:
        case CALC_TI86:
        case CALC_NSPIRE:
            return CL_E_NOT_SUPPORTED;
        default:
            break;
        }
        if (s->calc->calc == nullptr || s->calc->calc->execute == nullptr)
        {
            return CL_E_NOT_SUPPORTED;
        }
        OpScope op(s);
        return map_ret(s, ticalcs_calc_execute(s->calc, static_cast<VarEntry*>(v->opaque), ""));
    });
}

// ===========================================================================
// Tools
// ===========================================================================

CL_API int CL_CALL cl_send_key(cl_session* s, uint32_t key)
{
    return guarded([&]() -> int
    {
        int r = check_session(s);
        if (r != CL_OK)
        {
            return r;
        }
        if (!has_features(s, OPS_KEYS))
        {
            return CL_E_NOT_SUPPORTED;
        }
        OpScope op(s);
        return map_ret(s, ticalcs_calc_send_key(s->calc, key));
    });
}

CL_API int CL_CALL cl_recv_screen(cl_session* s, uint8_t** pixels, int32_t* width, int32_t* height)
{
    return guarded([&]() -> int
    {
        if (pixels == nullptr || width == nullptr || height == nullptr)
        {
            return CL_E_INVALID_ARG;
        }
        *pixels = nullptr;
        *width = 0;
        *height = 0;
        int r = check_session(s);
        if (r != CL_OK)
        {
            return r;
        }
        if (!has_features(s, OPS_SCREEN))
        {
            return CL_E_NOT_SUPPORTED;
        }

        OpScope op(s);
        // TiLP default (options.screen_clipping = SCREEN_CLIPPED): only the visible LCD area.
        // This matters on the TI-89/89T, whose 240x128 frame buffer shows 160x100.
        // ticalcs_calc_recv_screen_rgb888() is NOT used: it always converts sc.width x
        // sc.height, which is wrong once the bitmap has been compacted to the clipped size.
        CalcScreenCoord sc;
        std::memset(&sc, 0, sizeof(sc));
        sc.format = SCREEN_CLIPPED;
        uint8_t* bitmap = nullptr;
        const int ret = ticalcs_calc_recv_screen(s->calc, &sc, &bitmap);
        if (ret != 0)
        {
            // Some backends free the bitmap on error without clearing the pointer, so it
            // is deliberately not freed here (possible small leak instead of a double free).
            return map_ret(s, ret);
        }
        if (bitmap == nullptr)
        {
            return CL_E_INTERNAL;
        }

        unsigned int w = sc.clipped_width ? sc.clipped_width : sc.width;
        unsigned int h = sc.clipped_height ? sc.clipped_height : sc.height;
        if (w == 0 || h == 0 || w > 320 || h > 240)
        {
            ticalcs_free_screen(bitmap);
            return CL_E_INTERNAL;
        }

        std::vector<uint8_t> rgb;
        try
        {
            rgb.resize(static_cast<size_t>(w) * h * 3);
        }
        catch (...)
        {
            ticalcs_free_screen(bitmap);
            throw;
        }
        const int conv = ticalcs_screen_convert_native_to_rgb888(sc.pixel_format, bitmap, w, h, rgb.data());
        ticalcs_free_screen(bitmap);
        if (conv != 0)
        {
            return map_ret(s, conv);
        }

        const size_t npix = static_cast<size_t>(w) * h;
        uint8_t* bgra = static_cast<uint8_t*>(std::malloc(npix * 4));
        if (bgra == nullptr)
        {
            return CL_E_NO_MEMORY;
        }
        for (size_t i = 0; i < npix; ++i)
        {
            bgra[i * 4 + 0] = rgb[i * 3 + 2];   // B
            bgra[i * 4 + 1] = rgb[i * 3 + 1];   // G
            bgra[i * 4 + 2] = rgb[i * 3 + 0];   // R
            bgra[i * 4 + 3] = 0xFF;             // A
        }
        *pixels = bgra;
        *width = static_cast<int32_t>(w);
        *height = static_cast<int32_t>(h);
        return CL_OK;
    });
}

CL_API int CL_CALL cl_get_clock(cl_session* s, cl_clock* out)
{
    return guarded([&]() -> int
    {
        if (out == nullptr)
        {
            return CL_E_INVALID_ARG;
        }
        std::memset(out, 0, sizeof(*out));
        int r = check_session(s);
        if (r != CL_OK)
        {
            return r;
        }
        if (!has_features(s, OPS_CLOCK))
        {
            return CL_E_NOT_SUPPORTED;
        }
        OpScope op(s);
        CalcClock c;
        std::memset(&c, 0, sizeof(c));
        const int ret = ticalcs_calc_get_clock(s->calc, &c);
        if (ret != 0)
        {
            return map_ret(s, ret);
        }
        out->year        = c.year;
        out->month       = c.month;
        out->day         = c.day;
        out->hours       = c.hours;
        out->minutes     = c.minutes;
        out->seconds     = c.seconds;
        out->time_format = c.time_format;
        out->date_format = c.date_format;
        return CL_OK;
    });
}

CL_API int CL_CALL cl_set_clock(cl_session* s, const cl_clock* in)
{
    return guarded([&]() -> int
    {
        if (in == nullptr || in->month < 1 || in->month > 12 || in->day < 1 || in->day > 31 ||
            in->hours > 23 || in->minutes > 59 || in->seconds > 59 ||
            (in->time_format != 0 && in->time_format != 12 && in->time_format != 24))
        {
            return CL_E_INVALID_ARG;
        }
        int r = check_session(s);
        if (r != CL_OK)
        {
            return r;
        }
        if (!has_features(s, OPS_CLOCK))
        {
            return CL_E_NOT_SUPPORTED;
        }
        OpScope op(s);
        // Start from the calculator's current clock so that what cl_clock leaves unspecified keeps
        // its value: the clock on/off state (no field) and the display formats (0 = keep).
        CalcClock c;
        std::memset(&c, 0, sizeof(c));
        if (ticalcs_calc_get_clock(s->calc, &c) != 0)
        {
            // Couldn't read it: fall back to TI's factory defaults (clock on, 12-hour, M/D/Y).
            std::memset(&c, 0, sizeof(c));
            c.state = 1;
            c.time_format = 12;
            c.date_format = 1;
        }
        c.year        = in->year;
        c.month       = in->month;
        c.day         = in->day;
        c.hours       = in->hours;
        c.minutes     = in->minutes;
        c.seconds     = in->seconds;
        if (in->time_format != 0)
        {
            c.time_format = in->time_format;
        }
        if (in->date_format != 0)
        {
            c.date_format = in->date_format;
        }
        return map_ret(s, ticalcs_calc_set_clock(s->calc, &c));
    });
}

CL_API int CL_CALL cl_backup(cl_session* s, const char* path)
{
    return guarded([&]() -> int
    {
        if (path == nullptr || *path == '\0' || !g_utf8_validate(path, -1, nullptr))
        {
            return CL_E_INVALID_ARG;
        }
        int r = check_session(s);
        if (r != CL_OK)
        {
            return r;
        }
        if (!has_features(s, OPS_BACKUP))
        {
            return CL_E_NOT_SUPPORTED;
        }

        OpScope op(s);
        const std::string ext = file_ext_of(path);
        if (g_ascii_strcasecmp(ext.c_str(), "tig") == 0)
        {
            // TiLP: backups as TIGroup only for FLASH models.
            if (!tifiles_is_flash(s->model))
            {
                return CL_E_NOT_SUPPORTED;
            }
            return map_ret(s, ticalcs_calc_recv_tigroup2(s->calc, path, TIG_ALL));
        }

        // TiLP (tilp_calc_recv_backup): non-silent models (and the TI-86) need the user to
        // start the backup on the calculator, so keep waiting while reads time out.
        const bool wait_for_user = !has_features(s, FTS_SILENT) || s->model == CALC_TI86;
        int ret;
        do
        {
            if (cancel_flag(s).load() != 0)
            {
                return CL_E_CANCELLED;
            }
            ret = ticalcs_calc_recv_backup2(s->calc, path);
        } while (ret == ERROR_READ_TIMEOUT && wait_for_user);
        return map_ret(s, ret);
    });
}

CL_API int CL_CALL cl_dump_rom(cl_session* s, const char* path)
{
    return guarded([&]() -> int
    {
        if (path == nullptr || *path == '\0' || !g_utf8_validate(path, -1, nullptr))
        {
            return CL_E_INVALID_ARG;
        }
        int r = check_session(s);
        if (r != CL_OK)
        {
            return r;
        }
        if (!has_features(s, OPS_ROMDUMP))
        {
            return CL_E_NOT_SUPPORTED;
        }
        OpScope op(s);
        // Step 1 sends/launches the dumper; step 2 receives the image.
        // NOTE: libticalcs writes ROM images with plain fopen() (ANSI code page path).
        int ret = ticalcs_calc_dump_rom_1(s->calc);
        if (ret == 0)
        {
            ret = ticalcs_calc_dump_rom_2(s->calc, ROMSIZE_AUTO, path);
        }
        return map_ret(s, ret);
    });
}

} // extern "C"
