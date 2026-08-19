#include <windows.h>
#include <cwchar>

namespace
{
HWND FindDesktopListView()
{
    HWND progman = FindWindowW(L"Progman", nullptr);
    HWND view = FindWindowExW(progman, nullptr, L"SHELLDLL_DefView", nullptr);
    if (view != nullptr)
        return FindWindowExW(view, nullptr, L"SysListView32", L"FolderView");

    struct SearchState { HWND Result = nullptr; } state;
    EnumWindows([](HWND window, LPARAM parameter) -> BOOL
    {
        auto* state = reinterpret_cast<SearchState*>(parameter);
        HWND view = FindWindowExW(window, nullptr, L"SHELLDLL_DefView", nullptr);
        if (view == nullptr) return TRUE;
        state->Result = FindWindowExW(view, nullptr, L"SysListView32", L"FolderView");
        return state->Result == nullptr;
    }, reinterpret_cast<LPARAM>(&state));
    return state.Result;
}
}

int wmain(int argc, wchar_t* argv[])
{
    if (argc != 3 || wcscmp(argv[1], L"--explorer-icon-watchdog") != 0)
        return 2;

    wchar_t* end = nullptr;
    const unsigned long processId = wcstoul(argv[2], &end, 10);
    if (processId == 0 || end == argv[2] || *end != L'\0')
        return 2;

    HANDLE process = OpenProcess(SYNCHRONIZE, FALSE, processId);
    if (process != nullptr)
    {
        WaitForSingleObject(process, INFINITE);
        CloseHandle(process);
    }

    HWND listView = FindDesktopListView();
    if (listView != nullptr)
        ShowWindow(listView, SW_SHOW);
    return 0;
}
