# CalcLink native layer: calclink_native.dll (flat C ABI over tilibs) + clprobe.exe
# Copyright (C) 2026 CalcLink contributors. Licensed GPL-2.0-or-later.
cmake_minimum_required(VERSION 3.25)

project(calclink_native VERSION 1.0.0 LANGUAGES C CXX)

if(NOT WIN32 OR NOT MSVC)
    message(FATAL_ERROR "CalcLink's native layer targets Windows (x64/ARM64) with MSVC only.")
endif()

include(CheckIPOSupported)
include(GNUInstallDirs)

# ---------------------------------------------------------------------------
# Options
# ---------------------------------------------------------------------------
set(TILIBS_SOURCE_DIR "" CACHE PATH
    "Path to a local tilibs checkout (commit 6dba390e). Empty = fetch it from GitHub.")
set(TILIBS_GIT_REPOSITORY "https://github.com/debrouxl/tilibs.git" CACHE STRING "tilibs repository")
set(TILIBS_GIT_TAG "6dba390e7390c4b98ae96287b39a3971c331fbef" CACHE STRING "tilibs commit")
option(CALCLINK_TILIBS_NLS
    "Build tilibs with gettext translations (needs msgfmt; the .mo files are not shipped)" OFF)
option(CALCLINK_TILIBS_CABLE_LOGGING
    "Build libticables with ENABLE_LOGGING (logs every byte to %USERPROFILE%\\.ticables)" OFF)
option(CALCLINK_BUILD_CLPROBE "Build the clprobe hardware smoke-test tool" ON)

# ---------------------------------------------------------------------------
# tilibs, consumed with add_subdirectory and linked statically into the DLL
# ---------------------------------------------------------------------------
# tilibs' CMakeLists require only CMake 3.12. Make the policies introduced later
# behave like ours inside its directories, otherwise:
#  - CMP0091 OLD there + NEW here => tilibs would get NO /MD flag (=> /MT) and
#    clash with calclink_native (/MD) at link time (LNK2038);
#  - CMP0077 OLD would make option() discard the BUILD_SHARED_LIBS/BUILD_TESTS we set;
#  - CMP0141 OLD would drop debug-info flags from tilibs objects.
set(CMAKE_POLICY_DEFAULT_CMP0077 NEW)
set(CMAKE_POLICY_DEFAULT_CMP0091 NEW)
set(CMAKE_POLICY_DEFAULT_CMP0141 NEW)

set(BUILD_SHARED_LIBS OFF CACHE BOOL "tilibs: static libraries, linked into calclink_native.dll" FORCE)
set(BUILD_TESTS OFF CACHE BOOL "tilibs: do not build torture tests" FORCE)
set(ENABLE_LOGGING ${CALCLINK_TILIBS_CABLE_LOGGING} CACHE BOOL "libticables cable logging" FORCE)
# USB via libusb-1.0 (works with the WinUSB, libusbK and libusb0.sys drivers) instead of
# tilibs' default libusb-win32 on Windows. Needs patches/tilibs-0001-windows-libusb-1.0-backend.patch.
set(TICABLES_WIN32_LIBUSB1 ON CACHE BOOL "tilibs: libusb-1.0 backend on Windows" FORCE)
set(_tilibs_patch "${CMAKE_CURRENT_SOURCE_DIR}/patches/tilibs-0001-windows-libusb-1.0-backend.patch")
# UTF-8 (g_fopen) for the files libticalcs writes itself: ROM dumps and certificate receipts.
set(_tilibs_patch2 "${CMAKE_CURRENT_SOURCE_DIR}/patches/tilibs-0002-utf8-file-paths.patch")

if(NOT CALCLINK_TILIBS_NLS)
    # tilibs enables NLS when FindGettext finds msgmerge+msgfmt. Pre-seeding the cache
    # with a false-but-not-NOTFOUND value makes find_program() skip the search and
    # find_package_handle_standard_args() report Gettext as not found.
    set(GETTEXT_MSGMERGE_EXECUTABLE "IGNORE" CACHE FILEPATH "disabled by CALCLINK_TILIBS_NLS=OFF" FORCE)
    set(GETTEXT_MSGFMT_EXECUTABLE   "IGNORE" CACHE FILEPATH "disabled by CALCLINK_TILIBS_NLS=OFF" FORCE)
endif()

# Debug: tilibs adds /fsanitize=address to CMAKE_<LANG>_FLAGS_DEBUG for MSVC, which
# cannot be combined with the default /RTC1. Strip /RTC* (inherited by tilibs).
foreach(_lang C CXX)
    string(REGEX REPLACE "/RTC[1csu]+" "" CMAKE_${_lang}_FLAGS_DEBUG "${CMAKE_${_lang}_FLAGS_DEBUG}")
endforeach()

# vcpkg: make sure pkg-config is found (host tool, also when cross-compiling to ARM64).
if(NOT PKG_CONFIG_EXECUTABLE AND DEFINED VCPKG_INSTALLED_DIR AND DEFINED VCPKG_HOST_TRIPLET)
    find_program(PKG_CONFIG_EXECUTABLE NAMES pkgconf pkg-config
        HINTS "${VCPKG_INSTALLED_DIR}/${VCPKG_HOST_TRIPLET}/tools/pkgconf"
        NO_DEFAULT_PATH)
endif()

if(TILIBS_SOURCE_DIR)
    get_filename_component(TILIBS_DIR "${TILIBS_SOURCE_DIR}" ABSOLUTE)
    if(NOT EXISTS "${TILIBS_DIR}/libticalcs/trunk/src/ticalcs.h")
        message(FATAL_ERROR "TILIBS_SOURCE_DIR=${TILIBS_SOURCE_DIR} does not look like a tilibs checkout")
    endif()
    file(READ "${TILIBS_DIR}/libticables/trunk/CMakeLists.txt" _ticables_cmake)
    if(NOT _ticables_cmake MATCHES "TICABLES_WIN32_LIBUSB1")
        message(FATAL_ERROR "The tilibs checkout at ${TILIBS_DIR} lacks the libusb-1.0 Windows patch. "
            "Apply it with:\n  git -C \"${TILIBS_DIR}\" apply \"${_tilibs_patch}\"")
    endif()
    file(READ "${TILIBS_DIR}/libticalcs/trunk/src/romdump.cc" _romdump_cc)
    if(NOT _romdump_cc MATCHES "g_fopen\\(filename")
        message(FATAL_ERROR "The tilibs checkout at ${TILIBS_DIR} lacks the UTF-8 file path patch. "
            "Apply it with:\n  git -C \"${TILIBS_DIR}\" apply \"${_tilibs_patch2}\"")
    endif()
else()
    include(FetchContent)
    # Full-signature FetchContent_Populate (not deprecated by CMP0169): we need
    # add_subdirectory(... EXCLUDE_FROM_ALL) so tilibs' own install rules
    # (static libs, headers, .pc files, locale) do not end up in our install tree.
    FetchContent_Populate(tilibs
        QUIET
        GIT_REPOSITORY "${TILIBS_GIT_REPOSITORY}"
        GIT_TAG        "${TILIBS_GIT_TAG}"
        GIT_SUBMODULES ""
        PATCH_COMMAND  git apply --ignore-whitespace "${_tilibs_patch}" "${_tilibs_patch2}"
        SOURCE_DIR     "${CMAKE_BINARY_DIR}/_deps/tilibs-src"
        BINARY_DIR     "${CMAKE_BINARY_DIR}/_deps/tilibs-build"
        SUBBUILD_DIR   "${CMAKE_BINARY_DIR}/_deps/tilibs-subbuild")
    set(TILIBS_DIR "${tilibs_SOURCE_DIR}")
endif()
message(STATUS "tilibs: ${TILIBS_DIR}")

# Note: tilibs' create_lib_target_with_deps() links its internal static libs by path
# (<binary dir>/../../lib<x>/trunk/<x>.lib), so do not set CMAKE_ARCHIVE_OUTPUT_DIRECTORY.
add_subdirectory("${TILIBS_DIR}" "${CMAKE_BINARY_DIR}/tilibs" EXCLUDE_FROM_ALL)

set(_tilibs_targets ticonv tifiles2 ticables2 ticalcs2)
foreach(_t IN LISTS _tilibs_targets)
    if(NOT TARGET ${_t})
        message(FATAL_ERROR "tilibs did not define target ${_t}")
    endif()
    # tilibs does include_directories(${CMAKE_SOURCE_DIR}/win32_config), which points
    # at OUR source dir when it is a subdirectory; libticables/link_usb.cc includes
    # <config.h> unconditionally.
    target_include_directories(${_t} PRIVATE "${TILIBS_DIR}/win32_config")
endforeach()

set(TILIBS_INCLUDE_DIRS
    "${TILIBS_DIR}/libticonv/trunk/src"
    "${TILIBS_DIR}/libtifiles/trunk/src"
    "${TILIBS_DIR}/libticables/trunk/src"
    "${TILIBS_DIR}/libticalcs/trunk/src")

# Public tilibs headers need glib.h and iconv.h (tilibs links these PRIVATE only).
find_package(PkgConfig REQUIRED)
pkg_check_modules(CALCLINK_GLIB REQUIRED IMPORTED_TARGET glib-2.0)
find_package(Iconv REQUIRED)

# ---------------------------------------------------------------------------
# calclink_native.dll
# ---------------------------------------------------------------------------
add_library(calclink SHARED
    src/calclink.cc
    include/calclink.h)

# The managed app assembly is CalcLink.dll; Windows file names are case-insensitive,
# so the native DLL must not be called calclink.dll.
set_target_properties(calclink PROPERTIES
    OUTPUT_NAME calclink_native
    CXX_STANDARD 20
    CXX_STANDARD_REQUIRED ON
    CXX_EXTENSIONS OFF
    RUNTIME_OUTPUT_DIRECTORY "${CMAKE_BINARY_DIR}/bin"
    LIBRARY_OUTPUT_DIRECTORY "${CMAKE_BINARY_DIR}/bin"
    ARCHIVE_OUTPUT_DIRECTORY "${CMAKE_BINARY_DIR}/lib"
    VERSION ${PROJECT_VERSION})

target_include_directories(calclink PUBLIC "${CMAKE_CURRENT_SOURCE_DIR}/include")
target_include_directories(calclink SYSTEM PRIVATE ${TILIBS_INCLUDE_DIRS})
target_compile_definitions(calclink PRIVATE
    CALCLINK_BUILD
    __WIN32__
    _CRT_SECURE_NO_WARNINGS
    WIN32_LEAN_AND_MEAN
    NOMINMAX)
target_compile_options(calclink PRIVATE /W4 /utf-8 /permissive- /Zc:__cplusplus /external:W0)
target_link_libraries(calclink PRIVATE
    ticalcs2 tifiles2 ticables2 ticonv
    PkgConfig::CALCLINK_GLIB
    Iconv::Iconv)

check_ipo_supported(RESULT _calclink_ipo OUTPUT _calclink_ipo_msg LANGUAGES CXX)
if(_calclink_ipo)
    # tilibs objects are /GL in Release; link with /LTCG as well.
    set_target_properties(calclink PROPERTIES
        INTERPROCEDURAL_OPTIMIZATION_RELEASE TRUE
        INTERPROCEDURAL_OPTIMIZATION_RELWITHDEBINFO TRUE)
endif()

# ---------------------------------------------------------------------------
# clprobe.exe (hardware smoke test)
# ---------------------------------------------------------------------------
if(CALCLINK_BUILD_CLPROBE)
    add_executable(clprobe tools/clprobe.c)
    set_target_properties(clprobe PROPERTIES
        C_STANDARD 17
        C_STANDARD_REQUIRED ON
        RUNTIME_OUTPUT_DIRECTORY "${CMAKE_BINARY_DIR}/bin")
    target_compile_options(clprobe PRIVATE /W4 /utf-8)
    target_compile_definitions(clprobe PRIVATE _CRT_SECURE_NO_WARNINGS)
    target_link_libraries(clprobe PRIVATE calclink)   # links calclink_native.lib
endif()

# ---------------------------------------------------------------------------
# calclink-driver.exe (assigns WinUSB to a TI calculator; the app runs it elevated)
# ---------------------------------------------------------------------------
# Self-contained on purpose: no tilibs, static CRT, and system DLLs resolved from System32
# only (/DEPENDENTLOADFLAG), because it runs as administrator from the app folder, which a
# per-user install leaves writable by the user.
add_executable(calclink_driver tools/calclink-driver.c)
set_target_properties(calclink_driver PROPERTIES
    OUTPUT_NAME calclink-driver
    C_STANDARD 17
    C_STANDARD_REQUIRED ON
    MSVC_RUNTIME_LIBRARY "MultiThreaded$<$<CONFIG:Debug>:Debug>"
    RUNTIME_OUTPUT_DIRECTORY "${CMAKE_BINARY_DIR}/bin")
target_compile_options(calclink_driver PRIVATE /W4 /utf-8 /sdl)
target_compile_definitions(calclink_driver PRIVATE _CRT_SECURE_NO_WARNINGS _WIN32_WINNT=0x0A00)
target_link_options(calclink_driver PRIVATE /DEPENDENTLOADFLAG:0x800)

# ---------------------------------------------------------------------------
# Install: everything that has to ship goes into <prefix>/bin
# ---------------------------------------------------------------------------
set(_install_targets calclink calclink_driver)
if(CALCLINK_BUILD_CLPROBE)
    list(APPEND _install_targets clprobe)
endif()

# Where file(GET_RUNTIME_DEPENDENCIES) looks for GLib, libintl, libiconv, PCRE2,
# zlib, libarchive, libusb0 ... DLLs.
# (clprobe.exe depends on calclink_native.dll from our own bin dir; it is resolved there
# and then post-excluded, because the target itself is installed by install(TARGETS).)
set(_dep_dirs "${CMAKE_BINARY_DIR}/bin")
if(DEFINED VCPKG_INSTALLED_DIR AND DEFINED VCPKG_TARGET_TRIPLET)
    if(CMAKE_BUILD_TYPE STREQUAL "Debug")
        list(APPEND _dep_dirs "${VCPKG_INSTALLED_DIR}/${VCPKG_TARGET_TRIPLET}/debug/bin")
    endif()
    list(APPEND _dep_dirs "${VCPKG_INSTALLED_DIR}/${VCPKG_TARGET_TRIPLET}/bin")
endif()
if(CMAKE_BUILD_TYPE STREQUAL "Debug")
    # tilibs Debug objects use /fsanitize=address => clang_rt.asan_dynamic-<arch>.dll
    get_filename_component(_msvc_bin "${CMAKE_CXX_COMPILER}" DIRECTORY)
    list(APPEND _dep_dirs "${_msvc_bin}")
endif()

install(TARGETS ${_install_targets}
    RUNTIME_DEPENDENCIES
        PRE_EXCLUDE_REGEXES  "^api-ms-" "^ext-ms-"
        POST_EXCLUDE_REGEXES "[Ss][Yy][Ss][Tt][Ee][Mm]32" "[Ss][Yy][Ss][Ww][Oo][Ww]64"
                             "[Cc][Aa][Ll][Cc][Ll][Ii][Nn][Kk]_[Nn][Aa][Tt][Ii][Vv][Ee]\\.[Dd][Ll][Ll]$"
        DIRECTORIES ${_dep_dirs}
    RUNTIME DESTINATION "${CMAKE_INSTALL_BINDIR}"
    LIBRARY DESTINATION "${CMAKE_INSTALL_BINDIR}"
    ARCHIVE DESTINATION "${CMAKE_INSTALL_LIBDIR}")

install(FILES include/calclink.h DESTINATION "${CMAKE_INSTALL_INCLUDEDIR}")

# License texts of everything linked into or shipped next to calclink_native.dll, for
# THIRD-PARTY-NOTICES.md. Packaging copies bin/licenses/ into the app folder.
install(FILES "${TILIBS_DIR}/libticalcs/trunk/COPYING"
    DESTINATION "${CMAKE_INSTALL_BINDIR}/licenses" RENAME "tilibs.txt")
if(DEFINED VCPKG_INSTALLED_DIR AND DEFINED VCPKG_TARGET_TRIPLET)
    foreach(_port glib gettext-libintl libiconv libusb pcre2 zlib libarchive)
        set(_copyright "${VCPKG_INSTALLED_DIR}/${VCPKG_TARGET_TRIPLET}/share/${_port}/copyright")
        if(NOT EXISTS "${_copyright}")
            message(FATAL_ERROR "No license file for the vcpkg port ${_port} at ${_copyright}")
        endif()
        install(FILES "${_copyright}" DESTINATION "${CMAKE_INSTALL_BINDIR}/licenses" RENAME "${_port}.txt")
    endforeach()
endif()

# VC++ runtime (vcruntime140*.dll, msvcp140*.dll, ...) next to the DLL, for an
# app-local deployment (system32 copies are excluded from RUNTIME_DEPENDENCIES above).
set(CMAKE_INSTALL_SYSTEM_RUNTIME_DESTINATION "${CMAKE_INSTALL_BINDIR}")
set(CMAKE_INSTALL_UCRT_LIBRARIES OFF)   # UCRT is part of Windows 10+
if(CMAKE_BUILD_TYPE STREQUAL "Debug")
    set(CMAKE_INSTALL_DEBUG_LIBRARIES ON)
    set(CMAKE_INSTALL_DEBUG_LIBRARIES_ONLY ON)
endif()
include(InstallRequiredSystemLibraries)
