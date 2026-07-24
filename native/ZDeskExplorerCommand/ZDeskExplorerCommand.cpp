#include <windows.h>
#include <shobjidl_core.h>
#include <shellapi.h>
#include <strsafe.h>

#pragma comment(lib, "ole32.lib")
#pragma comment(linker, "/EXPORT:DllGetClassObject,PRIVATE")
#pragma comment(linker, "/EXPORT:DllCanUnloadNow,PRIVATE")

// {2A10D2EE-E9C6-4A2A-8B47-203BF9C1A201}
const CLSID CLSID_ZDeskExplorerCommand = { 0x2a10d2ee, 0xe9c6, 0x4a2a, { 0x8b, 0x47, 0x20, 0x3b, 0xf9, 0xc1, 0xa2, 0x01 } };

enum class CommandKind { Root, Normal, Folder, Exit };
static volatile LONG g_objectCount = 0;

static HRESULT CopyText(PCWSTR text, PWSTR* output)
{
    if (!output) return E_POINTER;
    *output = nullptr;
    const size_t length = wcslen(text) + 1;
    auto memory = static_cast<PWSTR>(CoTaskMemAlloc(length * sizeof(wchar_t)));
    if (!memory) return E_OUTOFMEMORY;
    StringCchCopyW(memory, length, text);
    *output = memory;
    return S_OK;
}

static PCWSTR TitleFor(CommandKind kind)
{
    switch (kind)
    {
    case CommandKind::Normal: return L"普通布局";
    case CommandKind::Folder: return L"映射布局";
    case CommandKind::Exit: return L"退出 Z-Desk";
    default: return L"创建 Z-Desk 布局";
    }
}

static PCWSTR ArgumentFor(CommandKind kind)
{
    switch (kind)
    {
    case CommandKind::Folder: return L"--create-layout=folder";
    case CommandKind::Exit: return L"--exit";
    default: return L"--create-layout=empty";
    }
}

static bool ReadExecutablePath(wchar_t* buffer, DWORD length)
{
    HKEY key{};
    if (RegOpenKeyExW(HKEY_CURRENT_USER, L"Software\\ZDesk", 0, KEY_READ, &key) != ERROR_SUCCESS) return false;
    DWORD type = REG_SZ, size = length * sizeof(wchar_t);
    const auto result = RegQueryValueExW(key, L"ExecutablePath", nullptr, &type, reinterpret_cast<BYTE*>(buffer), &size);
    RegCloseKey(key);
    return result == ERROR_SUCCESS && (type == REG_SZ || type == REG_EXPAND_SZ) && GetFileAttributesW(buffer) != INVALID_FILE_ATTRIBUTES;
}

static bool IsZDeskRunning()
{
    const auto activationEvent = OpenEventW(SYNCHRONIZE, FALSE, L"Local\\ZDesk.Activate.v1");
    if (!activationEvent) return false;
    CloseHandle(activationEvent);
    return true;
}

class ExplorerCommand;

class CommandEnumerator final : public IEnumExplorerCommand
{
public:
    CommandEnumerator() : refs_(1), index_(0) { InterlockedIncrement(&g_objectCount); }
    ~CommandEnumerator() { InterlockedDecrement(&g_objectCount); }
    HRESULT STDMETHODCALLTYPE QueryInterface(REFIID iid, void** result) override
    {
        if (!result) return E_POINTER;
        *result = nullptr;
        if (iid == IID_IUnknown || iid == IID_IEnumExplorerCommand) { *result = this; AddRef(); return S_OK; }
        return E_NOINTERFACE;
    }
    ULONG STDMETHODCALLTYPE AddRef() override { return InterlockedIncrement(&refs_); }
    ULONG STDMETHODCALLTYPE Release() override { const auto refs = InterlockedDecrement(&refs_); if (!refs) delete this; return refs; }
    HRESULT STDMETHODCALLTYPE Next(ULONG count, IExplorerCommand** commands, ULONG* fetched) override;
    HRESULT STDMETHODCALLTYPE Skip(ULONG count) override { index_ = min(3u, index_ + count); return index_ < 3 ? S_OK : S_FALSE; }
    HRESULT STDMETHODCALLTYPE Reset() override { index_ = 0; return S_OK; }
    HRESULT STDMETHODCALLTYPE Clone(IEnumExplorerCommand** result) override
    {
        if (!result) return E_POINTER;
        auto clone = new CommandEnumerator();
        clone->index_ = index_;
        *result = clone;
        return S_OK;
    }
private:
    LONG refs_;
    ULONG index_;
};

class ExplorerCommand final : public IExplorerCommand
{
public:
    explicit ExplorerCommand(CommandKind kind) : refs_(1), kind_(kind) { InterlockedIncrement(&g_objectCount); }
    ~ExplorerCommand() { InterlockedDecrement(&g_objectCount); }
    HRESULT STDMETHODCALLTYPE QueryInterface(REFIID iid, void** result) override
    {
        if (!result) return E_POINTER;
        *result = nullptr;
        if (iid == IID_IUnknown || iid == IID_IExplorerCommand) { *result = this; AddRef(); return S_OK; }
        return E_NOINTERFACE;
    }
    ULONG STDMETHODCALLTYPE AddRef() override { return InterlockedIncrement(&refs_); }
    ULONG STDMETHODCALLTYPE Release() override { const auto refs = InterlockedDecrement(&refs_); if (!refs) delete this; return refs; }
    HRESULT STDMETHODCALLTYPE GetTitle(IShellItemArray*, PWSTR* name) override { return CopyText(TitleFor(kind_), name); }
    HRESULT STDMETHODCALLTYPE GetIcon(IShellItemArray*, PWSTR* icon) override
    {
        if (!icon) return E_POINTER;
        wchar_t executable[MAX_PATH]{};
        if (!ReadExecutablePath(executable, ARRAYSIZE(executable))) { *icon = nullptr; return E_NOTIMPL; }
        return CopyText(executable, icon);
    }
    HRESULT STDMETHODCALLTYPE GetToolTip(IShellItemArray*, PWSTR* tip) override { if (!tip) return E_POINTER; *tip = nullptr; return E_NOTIMPL; }
    HRESULT STDMETHODCALLTYPE GetCanonicalName(GUID* guid) override
    {
        if (!guid) return E_POINTER;
        *guid = CLSID_ZDeskExplorerCommand;
        guid->Data1 += static_cast<unsigned long>(kind_);
        return S_OK;
    }
    HRESULT STDMETHODCALLTYPE GetState(IShellItemArray*, BOOL, EXPCMDSTATE* state) override
    {
        if (!state) return E_POINTER;
        *state = IsZDeskRunning() ? ECS_ENABLED : ECS_HIDDEN;
        return S_OK;
    }
    HRESULT STDMETHODCALLTYPE Invoke(IShellItemArray*, IBindCtx*) override
    {
        if (kind_ == CommandKind::Root) return E_NOTIMPL;
        wchar_t executable[MAX_PATH]{};
        if (!ReadExecutablePath(executable, ARRAYSIZE(executable))) return E_FAIL;
        return reinterpret_cast<INT_PTR>(ShellExecuteW(nullptr, L"open", executable, ArgumentFor(kind_), nullptr, SW_SHOWNORMAL)) > 32 ? S_OK : E_FAIL;
    }
    HRESULT STDMETHODCALLTYPE GetFlags(EXPCMDFLAGS* flags) override
    {
        if (!flags) return E_POINTER;
        *flags = kind_ == CommandKind::Root ? ECF_HASSUBCOMMANDS : ECF_DEFAULT;
        return S_OK;
    }
    HRESULT STDMETHODCALLTYPE EnumSubCommands(IEnumExplorerCommand** commands) override
    {
        if (!commands) return E_POINTER;
        *commands = nullptr;
        if (kind_ != CommandKind::Root) return E_NOTIMPL;
        *commands = new CommandEnumerator();
        return S_OK;
    }
private:
    LONG refs_;
    CommandKind kind_;
};

HRESULT CommandEnumerator::Next(ULONG count, IExplorerCommand** commands, ULONG* fetched)
{
    if (!commands || (count != 1 && !fetched)) return E_POINTER;
    ULONG written = 0;
    while (written < count && index_ < 3)
    {
        commands[written++] = new ExplorerCommand(static_cast<CommandKind>(static_cast<int>(CommandKind::Normal) + index_++));
    }
    if (fetched) *fetched = written;
    return written == count ? S_OK : S_FALSE;
}

class CommandFactory final : public IClassFactory
{
public:
    CommandFactory() : refs_(1) { InterlockedIncrement(&g_objectCount); }
    ~CommandFactory() { InterlockedDecrement(&g_objectCount); }
    HRESULT STDMETHODCALLTYPE QueryInterface(REFIID iid, void** result) override
    {
        if (!result) return E_POINTER;
        *result = nullptr;
        if (iid == IID_IUnknown || iid == IID_IClassFactory) { *result = this; AddRef(); return S_OK; }
        return E_NOINTERFACE;
    }
    ULONG STDMETHODCALLTYPE AddRef() override { return InterlockedIncrement(&refs_); }
    ULONG STDMETHODCALLTYPE Release() override { const auto refs = InterlockedDecrement(&refs_); if (!refs) delete this; return refs; }
    HRESULT STDMETHODCALLTYPE CreateInstance(IUnknown* outer, REFIID iid, void** result) override
    {
        if (outer) return CLASS_E_NOAGGREGATION;
        auto command = new ExplorerCommand(CommandKind::Root);
        const auto hr = command->QueryInterface(iid, result);
        command->Release();
        return hr;
    }
    HRESULT STDMETHODCALLTYPE LockServer(BOOL lock) override
    {
        if (lock) InterlockedIncrement(&g_objectCount);
        else InterlockedDecrement(&g_objectCount);
        return S_OK;
    }
private:
    LONG refs_;
};

extern "C" BOOL WINAPI DllMain(HINSTANCE, DWORD, LPVOID) { return TRUE; }
STDAPI DllGetClassObject(REFCLSID clsid, REFIID iid, void** result)
{
    if (clsid != CLSID_ZDeskExplorerCommand) return CLASS_E_CLASSNOTAVAILABLE;
    auto factory = new CommandFactory();
    const auto hr = factory->QueryInterface(iid, result);
    factory->Release();
    return hr;
}
STDAPI DllCanUnloadNow() { return InterlockedCompareExchange(&g_objectCount, 0, 0) == 0 ? S_OK : S_FALSE; }
