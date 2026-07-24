#include <windows.h>
#include <shobjidl_core.h>
#include <cstdio>

using GetClassObject = HRESULT (STDAPICALLTYPE*)(REFCLSID, REFIID, void**);
using CanUnloadNow = HRESULT (STDAPICALLTYPE*)();
const CLSID CommandClsid = { 0x2a10d2ee, 0xe9c6, 0x4a2a, { 0x8b, 0x47, 0x20, 0x3b, 0xf9, 0xc1, 0xa2, 0x01 } };

int wmain(int argc, wchar_t** argv)
{
    if (argc != 2) return 2;
    const auto module = LoadLibraryW(argv[1]);
    if (!module) return 3;
    const auto getClassObject = reinterpret_cast<GetClassObject>(GetProcAddress(module, "DllGetClassObject"));
    const auto canUnloadNow = reinterpret_cast<CanUnloadNow>(GetProcAddress(module, "DllCanUnloadNow"));
    if (!getClassObject || !canUnloadNow) return 4;

    for (int iteration = 0; iteration < 1000; ++iteration)
    {
        IClassFactory* factory = nullptr;
        if (FAILED(getClassObject(CommandClsid, IID_PPV_ARGS(&factory)))) return 5;
        IExplorerCommand* root = nullptr;
        if (FAILED(factory->CreateInstance(nullptr, IID_PPV_ARGS(&root)))) return 6;
        factory->Release();
        PWSTR title = nullptr;
        if (FAILED(root->GetTitle(nullptr, &title)) || !title) return 7;
        CoTaskMemFree(title);
        EXPCMDFLAGS flags{};
        if (FAILED(root->GetFlags(&flags)) || !(flags & ECF_HASSUBCOMMANDS)) return 8;
        IEnumExplorerCommand* commands = nullptr;
        if (FAILED(root->EnumSubCommands(&commands)) || !commands) return 9;
        ULONG count = 0;
        for (;;)
        {
            IExplorerCommand* command = nullptr;
            ULONG fetched = 0;
            const auto hr = commands->Next(1, &command, &fetched);
            if (hr == S_FALSE) break;
            if (FAILED(hr) || fetched != 1 || !command) return 10;
            command->Release();
            ++count;
        }
        commands->Release();
        root->Release();
        if (count != 3 || canUnloadNow() != S_OK) return 11;
    }
    FreeLibrary(module);
    std::puts("Explorer command lifecycle test passed (1000 iterations).");
    return 0;
}
