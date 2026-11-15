// Read and round-trip compiled maps with MatrixGame's own CStorage code.
#include <windows.h>
#include <stdio.h>
#include <wchar.h>
#include <string.h>

#include "CStorage.hpp"

using namespace Base;

static bool Exists(const wchar_t *path)
{
    return GetFileAttributesW(path) != INVALID_FILE_ATTRIBUTES;
}

static bool IsCMap(const wchar_t *path)
{
    HANDLE file = CreateFileW(path, GENERIC_READ, FILE_SHARE_READ, NULL,
                              OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, NULL);
    if (file == INVALID_HANDLE_VALUE) return false;
    BYTE header[8];
    DWORD count = 0;
    bool ok = ReadFile(file, header, sizeof(header), &count, NULL) &&
              count == sizeof(header) && memcmp(header, "STRG", 4) == 0;
    CloseHandle(file);
    DWORD version = 1;
    if (ok) memcpy(&version, header + 4, sizeof(version));
    return ok && version == 0;
}

static int Inspect(const wchar_t *source)
{
    if (!IsCMap(source)) return 2;

    CStorage storage;
    if (!storage.Load(source)) return 3;

    CDataBuf *names = storage.GetBuf(L"properties", L"Name", ST_WCHAR);
    CDataBuf *values = storage.GetBuf(L"properties", L"Value", ST_WCHAR);
    CDataBuf *points = storage.GetBuf(L"points", L"Data", ST_BYTE);
    CDataBuf *move = storage.GetBuf(L"move", L"Data", ST_BYTE);
    CDataBuf *roads = storage.GetBuf(L"roads", L"Data", ST_BYTE);
    if (!names || !values || !points || !move || !roads ||
        names->GetArraysCount() != values->GetArraysCount() ||
        points->GetArraysCount() == 0 || move->GetArraysCount() == 0 ||
        roads->GetArraysCount() == 0 || roads->GetArrayLength(0) < 4) {
        fwprintf(stderr, L"Missing core map tables.\n");
        return 4;
    }

    DWORD road_version;
    memcpy(&road_version, roads->GetFirst<BYTE>(0), sizeof(road_version));
    wprintf(L"properties=%lu points_bytes=%lu move_bytes=%lu roads_bytes=%lu roads_version=%lu\n",
            names->GetArraysCount(), points->GetArrayLength(0),
            move->GetArrayLength(0), roads->GetArrayLength(0), road_version);
    for (DWORD i = 0; i < names->GetArraysCount(); ++i) {
        CWStr key = names->GetAsWStr(i);
        CWStr value = values->GetAsWStr(i);
        wprintf(L"%ls=%ls\n", key.Get(), value.Get());
    }
    return 0;
}

static int Roundtrip(const wchar_t *source, const wchar_t *output)
{
    if (!IsCMap(source) || Exists(output)) {
        fwprintf(stderr, L"Expected STRG v0 map and a new output path.\n");
        return 2;
    }
    CStorage storage;
    if (!storage.Load(source)) return 3;
    storage.Save(output, false);
    return Exists(output) ? 0 : 4;
}

int wmain(int argc, wchar_t **argv)
{
    if (argc == 3 && _wcsicmp(argv[1], L"inspect") == 0)
        return Inspect(argv[2]);
    if (argc == 4 && _wcsicmp(argv[1], L"roundtrip") == 0)
        return Roundtrip(argv[2], argv[3]);

    fwprintf(stderr, L"Usage:\n"
            L"  cmap_tool inspect map.cmap\n"
            L"  cmap_tool roundtrip map.cmap new_map.cmap\n");
    return 1;
}
