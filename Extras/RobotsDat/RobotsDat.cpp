// Convert the planetary battle configuration using MatrixGame's own storage code.
#include <windows.h>
#include <stdio.h>
#include <wchar.h>

#include "CStorage.hpp"
#include "CBlockPar.hpp"

using namespace Base;

static bool Exists(const wchar_t *path)
{
    return GetFileAttributesW(path) != INVALID_FILE_ATTRIBUTES;
}

static int Decode(const wchar_t *source, const wchar_t *iface, const wchar_t *data)
{
    if (!Exists(source) || Exists(iface) || Exists(data)) {
        fwprintf(stderr, L"Source is missing or an output already exists.\n");
        return 2;
    }

    CStorage storage;
    if (!storage.Load(source) || !storage.IsTablePresent(L"if") ||
        !storage.IsTablePresent(L"da")) {
        fwprintf(stderr, L"Expected STRG storage with 'if' and 'da' roots.\n");
        return 3;
    }

    CBlockPar iface_tree(true);
    CBlockPar data_tree(true);
    storage.RestoreBlockPar(L"if", iface_tree);
    storage.RestoreBlockPar(L"da", data_tree);
    iface_tree.SaveInTextFile(iface);
    data_tree.SaveInTextFile(data);
    return Exists(iface) && Exists(data) ? 0 : 4;
}

static int Encode(const wchar_t *iface, const wchar_t *data, const wchar_t *output)
{
    if (!Exists(iface) || !Exists(data) || Exists(output)) {
        fwprintf(stderr, L"An input is missing or the output already exists.\n");
        return 2;
    }

    CBlockPar iface_tree(true);
    CBlockPar data_tree(true);
    iface_tree.LoadFromTextFile(iface);
    data_tree.LoadFromTextFile(data);

    CStorage storage;
    storage.StoreBlockPar(L"if", iface_tree);
    storage.StoreBlockPar(L"da", data_tree);
    storage.Save(output, true);
    return Exists(output) ? 0 : 4;
}

int wmain(int argc, wchar_t **argv)
{
    if (argc == 5 && _wcsicmp(argv[1], L"decode") == 0)
        return Decode(argv[2], argv[3], argv[4]);
    if (argc == 5 && _wcsicmp(argv[1], L"encode") == 0)
        return Encode(argv[2], argv[3], argv[4]);

    fwprintf(stderr, L"Usage:\n"
            L"  robots_dat_tool decode robots.dat if.txt da.txt\n"
            L"  robots_dat_tool encode if.txt da.txt new_robots.dat\n");
    return 1;
}
