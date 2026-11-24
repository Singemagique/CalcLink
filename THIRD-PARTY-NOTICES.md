# Third-party notices

CalcLink is licensed GPL-2.0-or-later (see [LICENSE](LICENSE)). Its releases include the third-party components below, each under its own license.

* **Where the license texts are.** In a CalcLink package (portable zip or installed folder), they are in `licenses\`. That folder is collected from tilibs and vcpkg by the native build (`native/CMakeLists.txt`).
* **Versions.** The versions are those pinned by `native/vcpkg.json` (its `builtin-baseline`), by the tilibs commit in `native/CMakeLists.txt`, and by the package versions in the `.csproj` files.
* **Source code.** The source of every release is its Git tag, which pins all of the above. On request, the maintainers provide the corresponding source of the LGPL libraries as built for that release.

## Native backend (`native\<rid>\`)

| Component | Use | License | License text | Source |
|---|---|---|---|---|
| tilibs: libticables2, libticalcs2, libtifiles2, libticonv | Calculator protocols, file formats and character sets. Linked statically into `calclink_native.dll`, with the two patches in `native/patches/` | GPL-2.0-or-later | `licenses\tilibs.txt` | <https://github.com/debrouxl/tilibs> |
| GLib | Used by tilibs (`glib-2.0-0.dll`) | LGPL-2.1-or-later | `licenses\glib.txt` | <https://gitlab.gnome.org/GNOME/glib> |
| gettext libintl | Used by GLib (`intl-8.dll`) | LGPL-2.1-or-later | `licenses\gettext-libintl.txt` | <https://www.gnu.org/software/gettext/> |
| GNU libiconv | Character set conversion (`iconv-2.dll`) | LGPL-2.1-or-later | `licenses\libiconv.txt` | <https://www.gnu.org/software/libiconv/> |
| libusb | USB access through WinUSB (`libusb-1.0.dll`) | LGPL-2.1-or-later | `licenses\libusb.txt` | <https://github.com/libusb/libusb> |
| PCRE2 | Used by GLib (`pcre2-8.dll`) | BSD-3-Clause WITH PCRE2-exception | `licenses\pcre2.txt` | <https://github.com/PCRE2Project/pcre2> |
| zlib | Compression (`libz.dll`) | Zlib | `licenses\zlib.txt` | <https://zlib.net/> |
| libarchive | TI group archives used by tilibs (`archive.dll`) | BSD-2-Clause (some files under other permissive licenses; see the text) | `licenses\libarchive.txt` | <https://github.com/libarchive/libarchive> |
| Microsoft Visual C++ runtime | `vcruntime140*.dll`, `msvcp140*.dll`, `concrt140.dll` | Visual Studio redistributable terms | — | Microsoft |

**The LGPL libraries are separate DLLs.** CalcLink loads them dynamically, so you can replace any of them with your own build of the same library and version.

## App and command-line tool

| Component | License | Source |
|---|---|---|
| .NET runtime and libraries (self-contained) | MIT | <https://github.com/dotnet/runtime> |
| Microsoft.Extensions.DependencyInjection | MIT | <https://github.com/dotnet/runtime> |
| CommunityToolkit.Mvvm | MIT | <https://github.com/CommunityToolkit/dotnet> |
| Windows App SDK runtime (self-contained), including WinUI 3 | Microsoft Software License Terms for the Windows App SDK. Its files are redistributable with applications under the "Distributable Code" section of those terms. | <https://github.com/microsoft/WindowsAppSDK> |

## Trademarks

"TI", "TI-83 Plus", "TI-84 Plus", "TI-Nspire" and related names are trademarks of Texas Instruments. CalcLink is not affiliated with or endorsed by Texas Instruments.
