/*
 * calclink-driver: list TI calculator USB devices, and assign Windows' built-in WinUSB
 * driver to one of them (what Zadig does, limited to TI calculators and cables).
 *
 *   calclink-driver list
 *       One line per connected TI calculator/cable (UTF-8, tab-separated):
 *       <hardware id> <TAB> <description> <TAB> <driver service or empty> <TAB> <problem code>
 *
 *   calclink-driver install <hardware id> [--log <file>]
 *       Needs administrator rights. <hardware id> must be one of the TI IDs below,
 *       e.g. USB\VID_0451&PID_E001 (SilverLink).
 *
 * How install works (Windows 10/11, where WinUSB ships with Windows):
 *   1. Write a small INF that tells Windows to use winusb.inf for this hardware ID, in a
 *      fresh folder under %windir%\Temp that only Administrators and SYSTEM can write to.
 *   2. Write a catalog (.cat) holding the INF's hash.
 *   3. Create a one-off self-signed code-signing certificate, sign the catalog with it, then
 *      delete its private key, so the certificate can never sign anything else.
 *   4. Add the certificate (public part only) to the machine's Root and TrustedPublisher
 *      stores, so Windows accepts the package without a warning.
 *   5. Install the package for the connected device (UpdateDriverForPlugAndPlayDevices,
 *      forced, so it replaces e.g. TI Connect's driver), then delete the temporary folder.
 *   On failure, the certificate added in step 4 is removed again. On success, older
 *   CalcLink certificates for the same hardware ID are removed.
 *
 * The approach follows libwdi (Pete Batard, https://github.com/pbatard/libwdi), which Zadig
 * uses; this is an independent implementation of the WinUSB-only part.
 *
 * Exit codes: 0 done, 1 usage, 2 not elevated, 3 hardware ID not a TI calculator,
 *             4 device not connected, 5 could not build or sign the package,
 *             6 Windows could not install it, 10 done but Windows wants a restart.
 *
 * Copyright (C) 2026 CalcLink contributors. Licensed GPL-2.0-or-later.
 */
#define WIN32_LEAN_AND_MEAN
#define UNICODE
#define _UNICODE
#include <windows.h>
#include <objbase.h>
#include <setupapi.h>
#include <cfgmgr32.h>
#include <newdev.h>
#include <wincrypt.h>
#include <wintrust.h>
#include <mscat.h>
#include <mssip.h>
#include <sddl.h>
#include <stdarg.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <wchar.h>

#pragma comment(lib, "setupapi.lib")
#pragma comment(lib, "cfgmgr32.lib")
#pragma comment(lib, "newdev.lib")
#pragma comment(lib, "crypt32.lib")
#pragma comment(lib, "wintrust.lib")
#pragma comment(lib, "advapi32.lib")
#pragma comment(lib, "ole32.lib")

enum {
    EXIT_OK = 0, EXIT_USAGE = 1, EXIT_NOT_ADMIN = 2, EXIT_NOT_TI = 3, EXIT_NOT_PRESENT = 4,
    EXIT_PACKAGE = 5, EXIT_INSTALL = 6, EXIT_REBOOT = 10,
};

/* USB IDs tilibs drives (libticables ticables.h). Texas Instruments' vendor ID 0451 is also
 * used by unrelated chips (USB hubs and more), so nothing outside this list is ever touched. */
static const struct { const wchar_t* hwid; const wchar_t* name; } k_devices[] = {
    { L"USB\\VID_0451&PID_E001", L"TI-Graph Link USB (SilverLink)" },
    { L"USB\\VID_0451&PID_E003", L"TI-84 Plus" },
    { L"USB\\VID_0451&PID_E004", L"TI-89 Titanium" },
    { L"USB\\VID_0451&PID_E008", L"TI-84 Plus Silver Edition / CE" },
    { L"USB\\VID_0451&PID_E012", L"TI-Nspire" },
};

static FILE* g_log;

/* Writes UTF-8 to stdout and, if open, to the log file. */
static void out(const wchar_t* fmt, ...)
{
    wchar_t wbuf[2048];
    va_list ap;
    va_start(ap, fmt);
    _vsnwprintf_s(wbuf, ARRAYSIZE(wbuf), _TRUNCATE, fmt, ap);
    va_end(ap);
    char buf[4096];
    int n = WideCharToMultiByte(CP_UTF8, 0, wbuf, -1, buf, sizeof(buf), NULL, NULL);
    if (n <= 1) {
        return;
    }
    fwrite(buf, 1, (size_t)n - 1, stdout);
    fflush(stdout);
    if (g_log) {
        fwrite(buf, 1, (size_t)n - 1, g_log);
        fflush(g_log);
    }
}

static void out_error(const wchar_t* what, DWORD err)
{
    wchar_t* msg = NULL;
    FormatMessageW(FORMAT_MESSAGE_ALLOCATE_BUFFER | FORMAT_MESSAGE_FROM_SYSTEM | FORMAT_MESSAGE_IGNORE_INSERTS,
        NULL, err, 0, (LPWSTR)&msg, 0, NULL);
    if (msg) {
        size_t len = wcslen(msg);
        while (len > 0 && (msg[len - 1] == L'\r' || msg[len - 1] == L'\n' || msg[len - 1] == L' ')) {
            msg[--len] = 0;
        }
    }
    out(L"error: %ls: %ls (0x%08lX)\n", what, msg ? msg : L"unknown error", (unsigned long)err);
    LocalFree(msg);
}

static const wchar_t* known_name(const wchar_t* hwid)
{
    for (size_t i = 0; i < ARRAYSIZE(k_devices); i++) {
        if (_wcsicmp(hwid, k_devices[i].hwid) == 0) {
            return k_devices[i].name;
        }
    }
    return NULL;
}

/* Matches "USB\VID_0451&PID_E001" against one entry of a device's hardware-ID list, which may
 * carry a "&REV_xxxx" suffix. */
static BOOL hwid_matches(const wchar_t* entry, const wchar_t* hwid)
{
    size_t n = wcslen(hwid);
    return _wcsnicmp(entry, hwid, n) == 0 && (entry[n] == 0 || entry[n] == L'&');
}

/* ---------------------------------------------------------------------------------------- */
/* Device enumeration                                                                        */
/* ---------------------------------------------------------------------------------------- */

typedef BOOL (*device_cb)(HDEVINFO set, SP_DEVINFO_DATA* dev, const wchar_t* hwid, void* ctx);

/* Calls cb for every present USB device whose hardware IDs include a known TI ID.
 * Stops early when cb returns FALSE. */
static void for_each_ti_device(device_cb cb, void* ctx)
{
    HDEVINFO set = SetupDiGetClassDevsW(NULL, L"USB", NULL, DIGCF_ALLCLASSES | DIGCF_PRESENT);
    if (set == INVALID_HANDLE_VALUE) {
        return;
    }
    SP_DEVINFO_DATA dev = { sizeof(dev) };
    for (DWORD i = 0; SetupDiEnumDeviceInfo(set, i, &dev); i++) {
        wchar_t ids[2048] = { 0 };
        if (!SetupDiGetDeviceRegistryPropertyW(set, &dev, SPDRP_HARDWAREID, NULL, (BYTE*)ids, sizeof(ids) - 4, NULL)) {
            continue;
        }
        const wchar_t* match = NULL;
        for (const wchar_t* p = ids; *p && !match; p += wcslen(p) + 1) {
            for (size_t k = 0; k < ARRAYSIZE(k_devices); k++) {
                if (hwid_matches(p, k_devices[k].hwid)) {
                    match = k_devices[k].hwid;
                    break;
                }
            }
        }
        if (match && !cb(set, &dev, match, ctx)) {
            break;
        }
    }
    SetupDiDestroyDeviceInfoList(set);
}

static void reg_string(HDEVINFO set, SP_DEVINFO_DATA* dev, DWORD prop, wchar_t* buf, DWORD cch)
{
    buf[0] = 0;
    if (!SetupDiGetDeviceRegistryPropertyW(set, dev, prop, NULL, (BYTE*)buf, (cch - 1) * sizeof(wchar_t), NULL)) {
        buf[0] = 0;
    }
    buf[cch - 1] = 0;
}

static BOOL list_cb(HDEVINFO set, SP_DEVINFO_DATA* dev, const wchar_t* hwid, void* ctx)
{
    (void)ctx;
    wchar_t desc[256], service[128];
    reg_string(set, dev, SPDRP_FRIENDLYNAME, desc, ARRAYSIZE(desc));
    if (!desc[0]) {
        reg_string(set, dev, SPDRP_DEVICEDESC, desc, ARRAYSIZE(desc));
    }
    reg_string(set, dev, SPDRP_SERVICE, service, ARRAYSIZE(service));
    for (wchar_t* p = desc; *p; p++) {
        if (*p == L'\t' || *p == L'\r' || *p == L'\n') {
            *p = L' ';
        }
    }
    ULONG status = 0, problem = 0;
    if (CM_Get_DevNode_Status(&status, &problem, dev->DevInst, 0) != CR_SUCCESS) {
        problem = 0;
    }
    out(L"%ls\t%ls\t%ls\t%lu\n", hwid, desc[0] ? desc : known_name(hwid), service, problem);
    return TRUE;
}

static BOOL present_cb(HDEVINFO set, SP_DEVINFO_DATA* dev, const wchar_t* hwid, void* ctx)
{
    (void)set;
    (void)dev;
    struct { const wchar_t* want; BOOL found; }* p = ctx;
    if (_wcsicmp(hwid, p->want) == 0) {
        p->found = TRUE;
        return FALSE;
    }
    return TRUE;
}

static BOOL is_present(const wchar_t* hwid)
{
    struct { const wchar_t* want; BOOL found; } ctx = { hwid, FALSE };
    for_each_ti_device(present_cb, &ctx);
    return ctx.found;
}

/* ---------------------------------------------------------------------------------------- */
/* Driver package: INF + catalog                                                             */
/* ---------------------------------------------------------------------------------------- */

/* "USB\VID_0451&PID_E001" -> "calclink_vid_0451_pid_e001" */
static void package_name(const wchar_t* hwid, wchar_t* buf, size_t cch)
{
    const wchar_t* p = wcschr(hwid, L'\\');
    p = p ? p + 1 : hwid;
    swprintf_s(buf, cch, L"calclink_%ls", p);
    for (wchar_t* q = buf; *q; q++) {
        *q = (*q == L'&') ? L'_' : towlower(*q);
    }
}

static BOOL write_inf(const wchar_t* path, const wchar_t* hwid, const wchar_t* cat_name, const wchar_t* device_name)
{
    GUID guid;
    wchar_t guid_str[64];
    if (FAILED(CoCreateGuid(&guid)) || !StringFromGUID2(&guid, guid_str, ARRAYSIZE(guid_str))) {
        return FALSE;
    }
    SYSTEMTIME now;
    GetLocalTime(&now);

    wchar_t inf[4096];
    swprintf_s(inf, ARRAYSIZE(inf),
        L"; WinUSB for %ls, generated by CalcLink (calclink-driver.exe).\r\n"
        L"; Uses the WinUSB driver that ships with Windows 8 and later; installs no files.\r\n"
        L"\r\n"
        L"[Version]\r\n"
        L"Signature   = \"$Windows NT$\"\r\n"
        L"Class       = USBDevice\r\n"
        L"ClassGuid   = {88bae032-5a81-49f0-bc3d-a4ff138216d6}\r\n"
        L"Provider    = %%ProviderName%%\r\n"
        L"CatalogFile = %ls\r\n"
        L"DriverVer   = %02u/%02u/%04u, 1.0.0.0\r\n"
        L"PnpLockdown = 1\r\n"
        L"\r\n"
        L"[Manufacturer]\r\n"
        L"%%VendorName%% = CalcLink, NTamd64, NTarm64, NTx86\r\n"
        L"\r\n"
        L"[CalcLink.NTamd64]\r\n"
        L"%%DeviceName%% = USB_Install, %ls\r\n"
        L"\r\n"
        L"[CalcLink.NTarm64]\r\n"
        L"%%DeviceName%% = USB_Install, %ls\r\n"
        L"\r\n"
        L"[CalcLink.NTx86]\r\n"
        L"%%DeviceName%% = USB_Install, %ls\r\n"
        L"\r\n"
        L"[USB_Install]\r\n"
        L"Include = winusb.inf\r\n"
        L"Needs   = WINUSB.NT\r\n"
        L"\r\n"
        L"[USB_Install.Services]\r\n"
        L"Include = winusb.inf\r\n"
        L"Needs   = WINUSB.NT.Services\r\n"
        L"\r\n"
        L"[USB_Install.HW]\r\n"
        L"AddReg = Dev_AddReg\r\n"
        L"\r\n"
        L"[Dev_AddReg]\r\n"
        L"; libusb-1.0 finds the device through this interface GUID.\r\n"
        L"HKR,,DeviceInterfaceGUIDs,0x10000,\"%ls\"\r\n"
        L"\r\n"
        L"[Strings]\r\n"
        L"ProviderName = \"CalcLink\"\r\n"
        L"VendorName   = \"Texas Instruments\"\r\n"
        L"DeviceName   = \"%ls (WinUSB)\"\r\n",
        device_name, cat_name, now.wMonth, now.wDay, now.wYear, hwid, hwid, hwid, guid_str, device_name);

    /* INF files may be UTF-16LE with a BOM; that keeps non-ASCII device names intact. */
    HANDLE h = CreateFileW(path, GENERIC_WRITE, 0, NULL, CREATE_NEW, FILE_ATTRIBUTE_NORMAL, NULL);
    if (h == INVALID_HANDLE_VALUE) {
        return FALSE;
    }
    static const BYTE bom[] = { 0xFF, 0xFE };
    DWORD written;
    BOOL ok = WriteFile(h, bom, sizeof(bom), &written, NULL)
        && WriteFile(h, inf, (DWORD)(wcslen(inf) * sizeof(wchar_t)), &written, NULL);
    CloseHandle(h);
    return ok;
}

static BOOL put_attr_str(HANDLE cat, CRYPTCATMEMBER* member, const wchar_t* name, const wchar_t* value)
{
    const DWORD flags = CRYPTCAT_ATTR_AUTHENTICATED | CRYPTCAT_ATTR_NAMEASCII | CRYPTCAT_ATTR_DATAASCII;
    DWORD cb = (DWORD)((wcslen(value) + 1) * sizeof(wchar_t));
    return member
        ? CryptCATPutAttrInfo(cat, member, (LPWSTR)name, flags, cb, (BYTE*)value) != NULL
        : CryptCATPutCatAttrInfo(cat, (LPWSTR)name, flags, cb, (BYTE*)value) != NULL;
}

/* Creates a catalog holding the SHA-1 hash of the INF (the hash PnP checks for an INF). */
static BOOL write_catalog(const wchar_t* cat_path, const wchar_t* inf_path, const wchar_t* inf_name, const wchar_t* hwid)
{
    /* INF subject type (CRYPT_SUBJTYPE_CATALOG_IMAGE family used for flat files) */
    static GUID inf_subject = { 0xDE351A42, 0x8E59, 0x11D0, { 0x8C, 0x47, 0x00, 0xC0, 0x4F, 0xC2, 0x95, 0xEE } };
    BOOL ok = FALSE;
    HCRYPTPROV prov = 0;
    HANDLE cat = INVALID_HANDLE_VALUE, file = INVALID_HANDLE_VALUE;
    BYTE hash[20];
    DWORD cb_hash = sizeof(hash);

    file = CreateFileW(inf_path, GENERIC_READ, FILE_SHARE_READ, NULL, OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, NULL);
    if (file == INVALID_HANDLE_VALUE || !CryptCATAdminCalcHashFromFileHandle(file, &cb_hash, hash, 0) || cb_hash != 20) {
        out_error(L"hashing the INF", GetLastError());
        goto done;
    }
    if (!CryptAcquireContextW(&prov, NULL, NULL, PROV_RSA_FULL, CRYPT_VERIFYCONTEXT)) {
        out_error(L"crypto context", GetLastError());
        goto done;
    }
    cat = CryptCATOpen((LPWSTR)cat_path, CRYPTCAT_OPEN_CREATENEW, prov, 0, 0);
    if (cat == INVALID_HANDLE_VALUE) {
        out_error(L"creating the catalog", GetLastError());
        goto done;
    }

    wchar_t hwid_lower[64];
    wcscpy_s(hwid_lower, ARRAYSIZE(hwid_lower), hwid);
    _wcslwr_s(hwid_lower, ARRAYSIZE(hwid_lower));
    if (!put_attr_str(cat, NULL, L"HWID1", hwid_lower) || !put_attr_str(cat, NULL, L"OS", L"10_X86,10_X64,10_ARM64")) {
        out_error(L"catalog attributes", GetLastError());
        goto done;
    }

    /* Member: tag = hex hash, indirect data = SPC_LINK "<<<Obsolete>>>" + SHA-1 digest. */
    wchar_t tag[41];
    for (int i = 0; i < 20; i++) {
        swprintf_s(&tag[2 * i], 3, L"%02X", hash[i]);
    }
    SPC_LINK link = { 0 };
    link.dwLinkChoice = SPC_FILE_LINK_CHOICE;
    link.pwszFile = L"<<<Obsolete>>>";
    BYTE encoded[64];
    DWORD cb_encoded = sizeof(encoded);
    if (!CryptEncodeObject(X509_ASN_ENCODING, SPC_CAB_DATA_OBJID, &link, encoded, &cb_encoded)) {
        out_error(L"encoding the catalog entry", GetLastError());
        goto done;
    }
    SIP_INDIRECT_DATA sip = { 0 };
    sip.Data.pszObjId = SPC_CAB_DATA_OBJID;
    sip.Data.Value.cbData = cb_encoded;
    sip.Data.Value.pbData = encoded;
    sip.DigestAlgorithm.pszObjId = szOID_OIWSEC_sha1;
    sip.Digest.cbData = 20;
    sip.Digest.pbData = hash;
    CRYPTCATMEMBER* member = CryptCATPutMemberInfo(cat, NULL, tag, &inf_subject, 0x200, sizeof(sip), (BYTE*)&sip);
    if (!member) {
        out_error(L"adding the INF to the catalog", GetLastError());
        goto done;
    }
    wchar_t name_lower[MAX_PATH];
    wcscpy_s(name_lower, ARRAYSIZE(name_lower), inf_name);
    _wcslwr_s(name_lower, ARRAYSIZE(name_lower));
    if (!put_attr_str(cat, member, L"File", name_lower)
        || !put_attr_str(cat, member, L"OSAttr", L"2:6.0,2:6.1,2:6.2,2:6.3,2:10.0")) {
        out_error(L"catalog entry attributes", GetLastError());
        goto done;
    }
    if (!CryptCATPersistStore(cat)) {
        out_error(L"saving the catalog", GetLastError());
        goto done;
    }
    ok = TRUE;

done:
    if (cat != INVALID_HANDLE_VALUE) {
        CryptCATClose(cat);
    }
    if (prov) {
        CryptReleaseContext(prov, 0);
    }
    if (file != INVALID_HANDLE_VALUE) {
        CloseHandle(file);
    }
    return ok;
}

/* ---------------------------------------------------------------------------------------- */
/* Signing (mssign32.dll has no import library or header in the SDK)                         */
/* ---------------------------------------------------------------------------------------- */

typedef struct { DWORD cbSize; LPCWSTR pwszFileName; HANDLE hFile; } CL_SIGNER_FILE_INFO;
typedef struct { DWORD cbSize; DWORD* pdwIndex; DWORD dwSubjectChoice; CL_SIGNER_FILE_INFO* pSignerFileInfo; } CL_SIGNER_SUBJECT_INFO;
typedef struct { DWORD cbSize; PCCERT_CONTEXT pSigningCert; DWORD dwCertPolicy; HCERTSTORE hCertStore; } CL_SIGNER_CERT_STORE_INFO;
typedef struct { DWORD cbSize; DWORD dwCertChoice; CL_SIGNER_CERT_STORE_INFO* pCertStoreInfo; HWND hwnd; } CL_SIGNER_CERT;
typedef struct {
    DWORD cbSize; ALG_ID algidHash; DWORD dwAttrChoice; void* pAttrAuthcode;
    PCRYPT_ATTRIBUTES psAuthenticated; PCRYPT_ATTRIBUTES psUnauthenticated;
} CL_SIGNER_SIGNATURE_INFO;
typedef struct { DWORD cbSize; DWORD cbBlob; BYTE* pbBlob; } CL_SIGNER_CONTEXT;
typedef HRESULT (WINAPI *SignerSignEx_t)(DWORD, CL_SIGNER_SUBJECT_INFO*, CL_SIGNER_CERT*, CL_SIGNER_SIGNATURE_INFO*,
    void*, LPCWSTR, PCRYPT_ATTRIBUTES, void*, CL_SIGNER_CONTEXT**);
typedef HRESULT (WINAPI *SignerFreeSignerContext_t)(CL_SIGNER_CONTEXT*);

#define CL_SIGNER_SUBJECT_FILE   0x01
#define CL_SIGNER_CERT_STORE     0x02
#define CL_SIGNER_CERT_POLICY_CHAIN 0x02
#define CL_SIGNER_NO_ATTR        0x00

static BOOL sign_file(const wchar_t* path, PCCERT_CONTEXT cert)
{
    HMODULE mssign = LoadLibraryExW(L"mssign32.dll", NULL, LOAD_LIBRARY_SEARCH_SYSTEM32);
    if (!mssign) {
        out_error(L"loading mssign32.dll", GetLastError());
        return FALSE;
    }
    SignerSignEx_t sign = (SignerSignEx_t)(void*)GetProcAddress(mssign, "SignerSignEx");
    SignerFreeSignerContext_t free_ctx = (SignerFreeSignerContext_t)(void*)GetProcAddress(mssign, "SignerFreeSignerContext");
    BOOL ok = FALSE;
    if (sign && free_ctx) {
        CL_SIGNER_FILE_INFO file = { sizeof(file), path, NULL };
        DWORD index = 0;
        CL_SIGNER_SUBJECT_INFO subject = { sizeof(subject), &index, CL_SIGNER_SUBJECT_FILE, &file };
        CL_SIGNER_CERT_STORE_INFO store = { sizeof(store), cert, CL_SIGNER_CERT_POLICY_CHAIN, NULL };
        CL_SIGNER_CERT signer = { sizeof(signer), CL_SIGNER_CERT_STORE, &store, NULL };

        /* Authenticode attributes: empty SpcSpOpusInfo, statement type "individual code signing". */
        static BYTE opus[] = { 0x30, 0x00 };
        static BYTE statement[] = { 0x30, 0x0C, 0x06, 0x0A, 0x2B, 0x06, 0x01, 0x04, 0x01, 0x82, 0x37, 0x02, 0x01, 0x15 };
        CRYPT_INTEGER_BLOB opus_blob = { sizeof(opus), opus }, statement_blob = { sizeof(statement), statement };
        CRYPT_ATTRIBUTE attrs[2] = {
            { SPC_SP_OPUS_INFO_OBJID, 1, &opus_blob },
            { SPC_STATEMENT_TYPE_OBJID, 1, &statement_blob },
        };
        CRYPT_ATTRIBUTES auth = { 2, attrs };
        CL_SIGNER_SIGNATURE_INFO sig = { sizeof(sig), CALG_SHA_256, CL_SIGNER_NO_ATTR, NULL, &auth, NULL };

        CL_SIGNER_CONTEXT* ctx = NULL;
        HRESULT hr = sign(0, &subject, &signer, &sig, NULL, NULL, NULL, NULL, &ctx);
        if (hr == S_OK) {
            ok = TRUE;
        } else {
            out_error(L"signing the catalog", (DWORD)hr);
        }
        if (ctx) {
            free_ctx(ctx);
        }
    } else {
        out_error(L"mssign32.dll entry points", GetLastError());
    }
    FreeLibrary(mssign);
    return ok;
}

/* ---------------------------------------------------------------------------------------- */
/* Certificate                                                                               */
/* ---------------------------------------------------------------------------------------- */

typedef struct {
    wchar_t container[64];
    PCCERT_CONTEXT cert;
} signing_key;

static void cert_subject(const wchar_t* hwid, wchar_t* buf, size_t cch)
{
    const wchar_t* p = wcschr(hwid, L'\\');
    swprintf_s(buf, cch, L"CalcLink USB driver for %ls", p ? p + 1 : hwid);
}

/* Creates an RSA-3072 key in a new machine key container and a self-signed certificate that is
 * only valid for code signing. The key is destroyed by destroy_key() once the catalog is signed. */
static BOOL create_key(const wchar_t* subject, signing_key* key)
{
    BOOL ok = FALSE;
    HCRYPTPROV prov = 0;
    HCRYPTKEY hkey = 0;
    BYTE* name = NULL;
    BYTE* eku_enc = NULL;
    GUID guid;

    if (FAILED(CoCreateGuid(&guid)) || !StringFromGUID2(&guid, key->container, ARRAYSIZE(key->container))) {
        return FALSE;
    }
    if (!CryptAcquireContextW(&prov, key->container, MS_ENH_RSA_AES_PROV_W, PROV_RSA_AES,
            CRYPT_NEWKEYSET | CRYPT_MACHINE_KEYSET | CRYPT_SILENT)) {
        out_error(L"creating a key container", GetLastError());
        key->container[0] = 0;
        return FALSE;
    }
    if (!CryptGenKey(prov, AT_SIGNATURE, (3072u << 16), &hkey)) {
        out_error(L"generating a key", GetLastError());
        goto done;
    }

    wchar_t x500[256];
    swprintf_s(x500, ARRAYSIZE(x500), L"CN=\"%ls\", O=\"CalcLink (created on this PC)\"", subject);
    DWORD cb_name = 0;
    if (!CertStrToNameW(X509_ASN_ENCODING, x500, CERT_X500_NAME_STR, NULL, NULL, &cb_name, NULL)
        || !(name = malloc(cb_name))
        || !CertStrToNameW(X509_ASN_ENCODING, x500, CERT_X500_NAME_STR, NULL, name, &cb_name, NULL)) {
        out_error(L"encoding the certificate name", GetLastError());
        goto done;
    }
    CERT_NAME_BLOB name_blob = { cb_name, name };

    LPSTR usage = szOID_PKIX_KP_CODE_SIGNING;
    CERT_ENHKEY_USAGE eku = { 1, &usage };
    DWORD cb_eku = 0;
    if (!CryptEncodeObject(X509_ASN_ENCODING, X509_ENHANCED_KEY_USAGE, &eku, NULL, &cb_eku)
        || !(eku_enc = malloc(cb_eku))
        || !CryptEncodeObject(X509_ASN_ENCODING, X509_ENHANCED_KEY_USAGE, &eku, eku_enc, &cb_eku)) {
        out_error(L"encoding the key usage", GetLastError());
        goto done;
    }
    CERT_EXTENSION ext = { szOID_ENHANCED_KEY_USAGE, TRUE, { cb_eku, eku_enc } };
    CERT_EXTENSIONS exts = { 1, &ext };

    CRYPT_KEY_PROV_INFO prov_info = { 0 };
    prov_info.pwszContainerName = key->container;
    prov_info.pwszProvName = MS_ENH_RSA_AES_PROV_W;
    prov_info.dwProvType = PROV_RSA_AES;
    prov_info.dwFlags = CRYPT_MACHINE_KEYSET;
    prov_info.dwKeySpec = AT_SIGNATURE;
    CRYPT_ALGORITHM_IDENTIFIER alg = { szOID_RSA_SHA256RSA, { 0, NULL } };

    /* Valid from yesterday (clock skew) for 30 years. The signature is only checked when the
     * driver is installed, so expiry never disables a driver that is already installed. */
    SYSTEMTIME start, end;
    FILETIME ft;
    ULARGE_INTEGER t;
    GetSystemTimeAsFileTime(&ft);
    t.LowPart = ft.dwLowDateTime;
    t.HighPart = ft.dwHighDateTime;
    t.QuadPart -= 24ull * 3600 * 10000000;
    ft.dwLowDateTime = t.LowPart;
    ft.dwHighDateTime = t.HighPart;
    FileTimeToSystemTime(&ft, &start);
    end = start;
    end.wYear += 30;
    if (end.wMonth == 2 && end.wDay == 29) {
        end.wDay = 28;
    }

    key->cert = CertCreateSelfSignCertificate(0, &name_blob, 0, &prov_info, &alg, &start, &end, &exts);
    if (!key->cert) {
        out_error(L"creating the certificate", GetLastError());
        goto done;
    }
    ok = TRUE;

done:
    free(name);
    free(eku_enc);
    if (hkey) {
        CryptDestroyKey(hkey);
    }
    CryptReleaseContext(prov, 0);
    return ok;
}

/* Deletes the private key: after this, the certificate can verify the catalog but sign nothing. */
static void destroy_key(signing_key* key)
{
    if (key->container[0]) {
        HCRYPTPROV prov = 0;
        if (!CryptAcquireContextW(&prov, key->container, MS_ENH_RSA_AES_PROV_W, PROV_RSA_AES,
                CRYPT_DELETEKEYSET | CRYPT_MACHINE_KEYSET | CRYPT_SILENT)) {
            out_error(L"deleting the private key", GetLastError());
        } else {
            out(L"Deleted the certificate's private key.\n");
        }
        key->container[0] = 0;
    }
}

static HCERTSTORE open_machine_store(const wchar_t* name)
{
    return CertOpenStore(CERT_STORE_PROV_SYSTEM_W, 0, 0,
        CERT_SYSTEM_STORE_LOCAL_MACHINE | CERT_STORE_OPEN_EXISTING_FLAG, name);
}

/* Adds the public certificate (encoded bytes only, so no link to the deleted key) to the
 * machine Root and TrustedPublisher stores. */
static BOOL trust_cert(PCCERT_CONTEXT cert)
{
    static const wchar_t* stores[] = { L"Root", L"TrustedPublisher" };
    for (size_t i = 0; i < ARRAYSIZE(stores); i++) {
        HCERTSTORE store = open_machine_store(stores[i]);
        PCCERT_CONTEXT added = NULL;
        if (!store || !CertAddEncodedCertificateToStore(store, X509_ASN_ENCODING, cert->pbCertEncoded,
                cert->cbCertEncoded, CERT_STORE_ADD_REPLACE_EXISTING, &added)) {
            out_error(L"adding the certificate to the trusted stores", GetLastError());
            if (store) {
                CertCloseStore(store, 0);
            }
            return FALSE;
        }
        CRYPT_DATA_BLOB friendly = { (DWORD)((wcslen(L"CalcLink USB driver") + 1) * sizeof(wchar_t)),
                                     (BYTE*)L"CalcLink USB driver" };
        CertSetCertificateContextProperty(added, CERT_FRIENDLY_NAME_PROP_ID, 0, &friendly);
        CertFreeCertificateContext(added);
        CertCloseStore(store, 0);
    }
    return TRUE;
}

/* Removes CalcLink certificates with this subject from Root and TrustedPublisher. With keep set,
 * the certificate with the same thumbprint as keep stays. Returns how many were removed. */
static int remove_certs(const wchar_t* subject, PCCERT_CONTEXT keep)
{
    static const wchar_t* stores[] = { L"Root", L"TrustedPublisher" };
    int removed = 0;
    for (size_t i = 0; i < ARRAYSIZE(stores); i++) {
        HCERTSTORE store = open_machine_store(stores[i]);
        if (!store) {
            continue;
        }
        PCCERT_CONTEXT c = NULL;
        while ((c = CertFindCertificateInStore(store, X509_ASN_ENCODING, 0, CERT_FIND_SUBJECT_STR_W, subject, c)) != NULL) {
            wchar_t cn[256] = { 0 };
            CertGetNameStringW(c, CERT_NAME_ATTR_TYPE, 0, szOID_COMMON_NAME, cn, ARRAYSIZE(cn));
            if (wcscmp(cn, subject) != 0 || (keep && CertCompareCertificate(X509_ASN_ENCODING, c->pCertInfo, keep->pCertInfo))) {
                continue;
            }
            PCCERT_CONTEXT dup = CertDuplicateCertificateContext(c);
            if (CertDeleteCertificateFromStore(dup)) {
                removed++;
            }
        }
        CertCloseStore(store, 0);
    }
    return removed;
}

/* Removes exactly this certificate from Root and TrustedPublisher. */
static void untrust_cert(PCCERT_CONTEXT cert)
{
    static const wchar_t* stores[] = { L"Root", L"TrustedPublisher" };
    for (size_t i = 0; i < ARRAYSIZE(stores); i++) {
        HCERTSTORE store = open_machine_store(stores[i]);
        if (!store) {
            continue;
        }
        PCCERT_CONTEXT c = CertFindCertificateInStore(store, X509_ASN_ENCODING, 0, CERT_FIND_EXISTING, cert, NULL);
        if (c) {
            CertDeleteCertificateFromStore(c);
        }
        CertCloseStore(store, 0);
    }
}

/* ---------------------------------------------------------------------------------------- */
/* Working folder:%windir%\Temp\CalcLink-driver-<guid>, writable by Administrators and SYSTEM  */
/* only, so nothing running without elevation can swap the INF between signing and install.  */
/* ---------------------------------------------------------------------------------------- */

static BOOL make_work_dir(wchar_t* dir, size_t cch)
{
    wchar_t windir[MAX_PATH], guid_str[64];
    GUID guid;
    if (!GetSystemWindowsDirectoryW(windir, ARRAYSIZE(windir))
        || FAILED(CoCreateGuid(&guid)) || !StringFromGUID2(&guid, guid_str, ARRAYSIZE(guid_str))) {
        return FALSE;
    }
    swprintf_s(dir, cch, L"%ls\\Temp\\CalcLink-driver-%ls", windir, guid_str);

    SECURITY_ATTRIBUTES sa = { sizeof(sa), NULL, FALSE };
    if (!ConvertStringSecurityDescriptorToSecurityDescriptorW(L"D:P(A;OICI;FA;;;BA)(A;OICI;FA;;;SY)",
            SDDL_REVISION_1, &sa.lpSecurityDescriptor, NULL)) {
        return FALSE;
    }
    BOOL ok = CreateDirectoryW(dir, &sa);
    LocalFree(sa.lpSecurityDescriptor);
    return ok;
}

static void remove_work_dir(const wchar_t* dir, const wchar_t* inf, const wchar_t* cat)
{
    DeleteFileW(inf);
    DeleteFileW(cat);
    RemoveDirectoryW(dir);
}

/* ---------------------------------------------------------------------------------------- */

static BOOL is_elevated(void)
{
    SID_IDENTIFIER_AUTHORITY nt = SECURITY_NT_AUTHORITY;
    PSID admins = NULL;
    BOOL member = FALSE;
    if (AllocateAndInitializeSid(&nt, 2, SECURITY_BUILTIN_DOMAIN_RID, DOMAIN_ALIAS_RID_ADMINS, 0, 0, 0, 0, 0, 0, &admins)) {
        if (!CheckTokenMembership(NULL, admins, &member)) {
            member = FALSE;
        }
        FreeSid(admins);
    }
    return member;
}

static int install(const wchar_t* hwid)
{
    const wchar_t* name = known_name(hwid);
    if (!name) {
        out(L"error: %ls is not a TI calculator or cable that CalcLink supports.\n", hwid);
        return EXIT_NOT_TI;
    }
    if (!is_elevated()) {
        out(L"error: administrator rights are needed to install a driver.\n");
        return EXIT_NOT_ADMIN;
    }
    if (!is_present(hwid)) {
        out(L"error: no device with hardware ID %ls is connected.\n", hwid);
        return EXIT_NOT_PRESENT;
    }

    wchar_t base[64], dir[MAX_PATH], inf_name[MAX_PATH], cat_name[MAX_PATH], inf[MAX_PATH], cat[MAX_PATH], subject[128];
    package_name(hwid, base, ARRAYSIZE(base));
    swprintf_s(inf_name, ARRAYSIZE(inf_name), L"%ls.inf", base);
    swprintf_s(cat_name, ARRAYSIZE(cat_name), L"%ls.cat", base);
    cert_subject(hwid, subject, ARRAYSIZE(subject));

    if (!make_work_dir(dir, ARRAYSIZE(dir))) {
        out_error(L"creating the working folder", GetLastError());
        return EXIT_PACKAGE;
    }
    swprintf_s(inf, ARRAYSIZE(inf), L"%ls\\%ls", dir, inf_name);
    swprintf_s(cat, ARRAYSIZE(cat), L"%ls\\%ls", dir, cat_name);
    out(L"Device: %ls (%ls)\n", name, hwid);

    int rc = EXIT_PACKAGE;
    signing_key key = { { 0 }, NULL };
    BOOL trusted = FALSE;

    if (!write_inf(inf, hwid, cat_name, name)) {
        out_error(L"writing the INF", GetLastError());
        goto done;
    }
    if (!write_catalog(cat, inf, inf_name, hwid)) {
        goto done;
    }
    out(L"Created the driver package (%ls).\n", inf_name);
    if (!create_key(subject, &key)) {
        goto done;
    }
    BOOL signed_ok = sign_file(cat, key.cert);
    if (signed_ok) {
        out(L"Signed the package with a certificate created on this PC.\n");
    }
    destroy_key(&key); /* straight after signing, whatever the outcome */
    if (!signed_ok) {
        goto done;
    }
    if (!trust_cert(key.cert)) {
        goto done;
    }
    trusted = TRUE;
    out(L"Added the certificate to the Trusted Root and Trusted Publishers stores.\n");

    BOOL reboot = FALSE;
    out(L"Installing WinUSB for the device...\n");
    if (!UpdateDriverForPlugAndPlayDevicesW(NULL, hwid, inf, INSTALLFLAG_FORCE, &reboot)) {
        out_error(L"installing the driver", GetLastError());
        rc = EXIT_INSTALL;
        goto done;
    }
    int old = remove_certs(subject, key.cert);
    if (old > 0) {
        out(L"Removed %d older CalcLink certificate entr%ls for this device.\n", old, old == 1 ? L"y" : L"ies");
    }
    out(reboot ? L"Done. Windows needs a restart to finish.\n" : L"Done. The device now uses WinUSB.\n");
    rc = reboot ? EXIT_REBOOT : EXIT_OK;

done:
    if (rc != EXIT_OK && rc != EXIT_REBOOT && trusted) {
        /* Nothing was installed with it: take this certificate out of the trusted stores again
         * (older CalcLink certificates for the device stay, their drivers are still installed). */
        untrust_cert(key.cert);
        out(L"Removed the certificate again because the installation failed.\n");
    }
    destroy_key(&key);
    if (key.cert) {
        CertFreeCertificateContext(key.cert);
    }
    remove_work_dir(dir, inf, cat);
    return rc;
}

int wmain(int argc, wchar_t** argv)
{
    /* Runs elevated from the app folder, which may be writable by the user: load system
     * libraries only from System32 (static imports are restricted by /DEPENDENTLOADFLAG). */
    SetDefaultDllDirectories(LOAD_LIBRARY_SEARCH_SYSTEM32);
    SetSearchPathMode(BASE_SEARCH_PATH_ENABLE_SAFE_SEARCHMODE | BASE_SEARCH_PATH_PERMANENT);

    if (argc >= 2 && _wcsicmp(argv[1], L"list") == 0 && argc == 2) {
        for_each_ti_device(list_cb, NULL);
        return EXIT_OK;
    }
    if (argc >= 3 && _wcsicmp(argv[1], L"install") == 0) {
        const wchar_t* log_path = NULL;
        if (argc == 5 && _wcsicmp(argv[3], L"--log") == 0) {
            log_path = argv[4];
        } else if (argc != 3) {
            goto usage;
        }
        if (log_path && _wfopen_s(&g_log, log_path, L"wb") != 0) {
            g_log = NULL;
        }
        if (FAILED(CoInitializeEx(NULL, COINIT_APARTMENTTHREADED))) {
            return EXIT_PACKAGE;
        }
        int rc = install(argv[2]);
        out(L"exit code %d\n", rc);
        if (g_log) {
            fclose(g_log);
        }
        CoUninitialize();
        return rc;
    }
usage:
    fwprintf(stderr,
        L"usage:\n"
        L"  calclink-driver list\n"
        L"  calclink-driver install <hardware id> [--log <file>]   (needs administrator rights)\n");
    return EXIT_USAGE;
}
