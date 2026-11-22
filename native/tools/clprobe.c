/*
 * clprobe - hardware smoke test for calclink_native.dll.
 *
 *   clprobe [options]
 *
 *   (no options)        probe USB, open the first device, print info + dirlist
 *   --list              probe USB only
 *   --model N           open the first device as tilibs CalcModel N regardless of what the probe
 *                       said (SilverLink). E.g. 4 = TI-83 Plus, 5 = TI-84 Plus, 9 = TI-89 Titanium.
 *   --screen FILE.bmp   capture the calculator screen to a 32-bit BMP
 *   --recv NAME DIR     receive the variable/app called NAME (display name) into DIR
 *   --send FILE         send FILE (variable, group, app, OS, backup, TIGroup) to the calculator
 *   --delete NAME       delete the variable called NAME from the calculator
 *
 * Actions run in the order: info + dirlist, --screen, --recv, --send, --delete. Options may be
 * repeated where it makes sense (--recv, --send, --delete). Exit code: 0 if every requested action
 * succeeded, 1 otherwise.
 *
 * Output is UTF-8 (the console code page is switched to 65001).
 *
 * Copyright (C) 2026 CalcLink contributors. Licensed GPL-2.0-or-later.
 */
#define WIN32_LEAN_AND_MEAN
#include <windows.h>

#include <stdio.h>
#include <stdlib.h>
#include <string.h>

#include "calclink.h"

#define MAX_ACTIONS 16

static void print_error(const char* what, int code)
{
    char* msg = cl_error_message(code);
    fprintf(stderr, "%s failed: %d: %s\n", what, code, msg ? msg : "(no message)");
    cl_free(msg);
}

static void CL_CALL on_progress(void* user, int32_t current, int32_t total, float rate_kbps, const char* text)
{
    (void)user;
    fprintf(stderr, "\r  [%ld/%ld] %.1f KB/s %-40.40s", (long)current, (long)total, rate_kbps, text ? text : "");
}

/* Writes BGRA pixels (top-down rows) as a 32-bit bottom-up BMP. */
static int write_bmp(const char* path, const uint8_t* bgra, int32_t w, int32_t h)
{
    BITMAPFILEHEADER fh;
    BITMAPINFOHEADER ih;
    FILE* f = NULL;
    const uint32_t stride = (uint32_t)w * 4u;

    memset(&fh, 0, sizeof(fh));
    memset(&ih, 0, sizeof(ih));
    fh.bfType = 0x4D42; /* "BM" */
    fh.bfOffBits = sizeof(fh) + sizeof(ih);
    fh.bfSize = fh.bfOffBits + stride * (uint32_t)h;
    ih.biSize = sizeof(ih);
    ih.biWidth = w;
    ih.biHeight = h; /* bottom-up */
    ih.biPlanes = 1;
    ih.biBitCount = 32;
    ih.biCompression = BI_RGB;
    ih.biSizeImage = stride * (uint32_t)h;

    if (fopen_s(&f, path, "wb") != 0 || f == NULL)
    {
        return -1;
    }
    fwrite(&fh, sizeof(fh), 1, f);
    fwrite(&ih, sizeof(ih), 1, f);
    for (int32_t y = h - 1; y >= 0; y--)
    {
        fwrite(bgra + (size_t)y * stride, 1, stride, f);
    }
    fclose(f);
    return 0;
}

static const cl_var* find_var(const cl_var* vars, int32_t n, const char* name)
{
    for (int32_t i = 0; i < n; i++)
    {
        if (strcmp(vars[i].name, name) == 0)
        {
            return &vars[i];
        }
    }
    return NULL;
}

int main(int argc, char** argv)
{
    int list_only = 0;
    int32_t forced_model = 0;
    const char* screen_path = NULL;
    const char* recv_names[MAX_ACTIONS];
    const char* recv_dirs[MAX_ACTIONS];
    const char* send_paths[MAX_ACTIONS];
    const char* delete_names[MAX_ACTIONS];
    int n_recv = 0, n_send = 0, n_delete = 0;
    int failures = 0;
    cl_device devs[8];
    int32_t n = 0;
    int ret;

    SetConsoleOutputCP(CP_UTF8);

    for (int i = 1; i < argc; i++)
    {
        if (strcmp(argv[i], "--list") == 0)
        {
            list_only = 1;
        }
        else if (strcmp(argv[i], "--model") == 0 && i + 1 < argc)
        {
            forced_model = (int32_t)atoi(argv[++i]);
        }
        else if (strcmp(argv[i], "--screen") == 0 && i + 1 < argc)
        {
            screen_path = argv[++i];
        }
        else if (strcmp(argv[i], "--recv") == 0 && i + 2 < argc && n_recv < MAX_ACTIONS)
        {
            recv_names[n_recv] = argv[++i];
            recv_dirs[n_recv++] = argv[++i];
        }
        else if (strcmp(argv[i], "--send") == 0 && i + 1 < argc && n_send < MAX_ACTIONS)
        {
            send_paths[n_send++] = argv[++i];
        }
        else if (strcmp(argv[i], "--delete") == 0 && i + 1 < argc && n_delete < MAX_ACTIONS)
        {
            delete_names[n_delete++] = argv[++i];
        }
        else
        {
            fprintf(stderr, "unknown or incomplete option: %s\n"
                            "usage: clprobe [--list] [--model N] [--screen FILE.bmp] [--recv NAME DIR]... "
                            "[--send FILE]... [--delete NAME]...\n", argv[i]);
            return 2;
        }
    }

    printf("calclink ABI %ld, %s\n", (long)cl_abi_version(), cl_lib_versions());
    if (cl_abi_version() != CL_ABI_VERSION)
    {
        fprintf(stderr, "ABI mismatch: header %d, DLL %ld\n", CL_ABI_VERSION, (long)cl_abi_version());
        return 2;
    }

    ret = cl_init();
    if (ret != CL_OK)
    {
        print_error("cl_init", ret);
        return 1;
    }

    ret = cl_probe_usb(devs, (int32_t)(sizeof(devs) / sizeof(devs[0])), &n);
    if (ret != CL_OK)
    {
        print_error("cl_probe_usb", ret);
        cl_shutdown();
        return 1;
    }
    printf("%ld USB device(s)\n", (long)n);
    for (int32_t i = 0; i < n && i < (int32_t)(sizeof(devs) / sizeof(devs[0])); i++)
    {
        printf("  #%ld cable=%ld port=%ld calc=%ld (%s) vid=%04x pid=%04x \"%s\"\n",
               (long)i, (long)devs[i].cable_model, (long)devs[i].cable_port,
               (long)devs[i].calc_model, cl_model_name(devs[i].calc_model),
               (unsigned)devs[i].vid, (unsigned)devs[i].pid, devs[i].name);
    }

    if (n > 0 && forced_model > 0)
    {
        printf("using forced model %ld (%s) instead of probed %ld\n", (long)forced_model,
               cl_model_name(forced_model), (long)devs[0].calc_model);
        devs[0].calc_model = forced_model;
    }

    if (!list_only && n > 0 && devs[0].calc_model != 0)
    {
        cl_session* s = NULL;
        cl_calc_info info;
        cl_var* vars = NULL;
        int32_t nvars = 0;

        ret = cl_open(devs[0].cable_model, devs[0].cable_port, devs[0].calc_model, 0, &s);
        if (ret != CL_OK)
        {
            print_error("cl_open", ret);
            cl_shutdown();
            return 1;
        }
        cl_set_progress(s, on_progress, NULL);
        printf("opened: model=%ld features=0x%08lx\n", (long)cl_calc_model(s), (unsigned long)cl_features(s));

        ret = cl_is_ready(s);
        if (ret != CL_OK)
        {
            print_error("cl_is_ready", ret);
        }

        ret = cl_get_info(s, &info);
        if (ret == CL_OK)
        {
            printf("info: product=\"%s\" id=%s os=%s boot=%s hw=%u lcd=%ux%u@%ubpp\n"
                   "      ram free=%llu flash free=%llu battery_ok=%u run_level=%u\n",
                   info.product_name, info.product_id, info.os_version, info.boot_version,
                   (unsigned)info.hw_version, (unsigned)info.lcd_width, (unsigned)info.lcd_height,
                   (unsigned)info.bits_per_pixel,
                   (unsigned long long)info.ram_free, (unsigned long long)info.flash_free,
                   (unsigned)info.battery_ok, (unsigned)info.run_level);
        }
        else
        {
            print_error("cl_get_info", ret);
        }

        ret = cl_get_dirlist(s, &vars, &nvars);
        fprintf(stderr, "\n");
        if (ret == CL_OK)
        {
            printf("%ld entries:\n", (long)nvars);
            for (int32_t i = 0; i < nvars; i++)
            {
                printf("  %-3s %-12s %-20s %-8s .%-4s %8lu attr=%u type=0x%02x\n",
                       vars[i].is_app ? "APP" : "", vars[i].folder, vars[i].name,
                       vars[i].type_name, vars[i].file_ext, (unsigned long)vars[i].size,
                       (unsigned)vars[i].attr, (unsigned)vars[i].type);
            }
        }
        else
        {
            print_error("cl_get_dirlist", ret);
            vars = NULL;
            nvars = 0;
            failures++;
        }

        if (screen_path != NULL)
        {
            uint8_t* px = NULL;
            int32_t w = 0, h = 0;
            ret = cl_recv_screen(s, &px, &w, &h);
            if (ret == CL_OK)
            {
                if (write_bmp(screen_path, px, w, h) == 0)
                {
                    printf("screen: %ldx%ld -> %s\n", (long)w, (long)h, screen_path);
                }
                else
                {
                    fprintf(stderr, "screen: could not write %s\n", screen_path);
                    failures++;
                }
                cl_free(px);
            }
            else
            {
                print_error("cl_recv_screen", ret);
                failures++;
            }
        }

        for (int i = 0; i < n_recv; i++)
        {
            const cl_var* v = find_var(vars, nvars, recv_names[i]);
            char path[MAX_PATH];
            if (v == NULL)
            {
                fprintf(stderr, "recv: no variable named %s\n", recv_names[i]);
                failures++;
                continue;
            }
            snprintf(path, sizeof(path), "%s\\%s.%s", recv_dirs[i], v->name, v->file_ext);
            ret = cl_recv_var(s, v, path);
            fprintf(stderr, "\n");
            if (ret == CL_OK)
            {
                printf("recv: %s (%lu bytes on calc) -> %s\n", v->name, (unsigned long)v->size, path);
            }
            else
            {
                print_error("cl_recv_var", ret);
                failures++;
            }
        }

        for (int i = 0; i < n_send; i++)
        {
            ret = cl_send_file(s, send_paths[i]);
            fprintf(stderr, "\n");
            if (ret == CL_OK)
            {
                printf("send: %s ok\n", send_paths[i]);
            }
            else
            {
                print_error("cl_send_file", ret);
                failures++;
            }
        }

        if (n_delete > 0)
        {
            /* Re-read the list so variables sent above can be found. */
            if (vars != NULL)
            {
                cl_free_dirlist(vars, nvars);
                vars = NULL;
                nvars = 0;
            }
            ret = cl_get_dirlist(s, &vars, &nvars);
            fprintf(stderr, "\n");
            if (ret != CL_OK)
            {
                print_error("cl_get_dirlist", ret);
                failures++;
            }
            for (int i = 0; i < n_delete && ret == CL_OK; i++)
            {
                const cl_var* v = find_var(vars, nvars, delete_names[i]);
                int dret;
                if (v == NULL)
                {
                    fprintf(stderr, "delete: no variable named %s\n", delete_names[i]);
                    failures++;
                    continue;
                }
                dret = cl_delete_var(s, v);
                if (dret == CL_OK)
                {
                    printf("delete: %s ok\n", delete_names[i]);
                }
                else
                {
                    print_error("cl_delete_var", dret);
                    failures++;
                }
            }
        }

        if (n_send > 0 || n_delete > 0)
        {
            /* Final listing so the effect of send/delete is visible. */
            if (vars != NULL)
            {
                cl_free_dirlist(vars, nvars);
                vars = NULL;
                nvars = 0;
            }
            if (cl_get_dirlist(s, &vars, &nvars) == CL_OK)
            {
                printf("after: %ld entries\n", (long)nvars);
            }
            fprintf(stderr, "\n");
        }

        if (vars != NULL)
        {
            cl_free_dirlist(vars, nvars);
        }
        cl_close(s);
    }
    else if (!list_only && n > 0)
    {
        printf("first device has no known calculator model; not opening it (try --model N)\n");
    }

    cl_shutdown();
    return failures ? 1 : 0;
}
