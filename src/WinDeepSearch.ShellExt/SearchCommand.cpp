// The "WinDeepSearch" entry of the Windows 11 right-click menu. Windows only lists commands from packaged
// apps there, so the sparse package (packaging/AppxManifest.xml) declares this COM class; Invoke starts
// WinDeepSearch.exe, which sits in the same folder as this DLL, with the selected files and folders.
#include <windows.h>
#include <objbase.h>
#include <shellapi.h>
#include <shlguid.h>
#include <shlobj_core.h>
#include <shobjidl_core.h>
#include <shlwapi.h>
#include <wrl/client.h>
#include <wrl/implements.h>
#include <wrl/module.h>

#include <string>
#include <vector>

using Microsoft::WRL::ClassicCom;
using Microsoft::WRL::ComPtr;
using Microsoft::WRL::InhibitRoOriginateError;
using Microsoft::WRL::InProc;
using Microsoft::WRL::Module;
using Microsoft::WRL::RuntimeClass;
using Microsoft::WRL::RuntimeClassFlags;

static HMODULE g_module;

static std::wstring ExePath()
{
    std::wstring path(32768, L'\0');
    DWORD length = GetModuleFileNameW(g_module, path.data(), static_cast<DWORD>(path.size()));
    path.resize(length);
    path.resize(path.find_last_of(L'\\') + 1);
    return path + L"WinDeepSearch.exe";
}

// For a click on the empty space inside a folder Explorer may pass no items; ask its window for the folder.
static HRESULT FolderFromSite(IUnknown* site, PWSTR* path)
{
    if (!site) return E_FAIL;
    ComPtr<IServiceProvider> provider;
    HRESULT hr = site->QueryInterface(IID_PPV_ARGS(&provider));
    ComPtr<IShellBrowser> browser;
    if (SUCCEEDED(hr)) hr = provider->QueryService(SID_STopLevelBrowser, IID_PPV_ARGS(&browser));
    ComPtr<IShellView> view;
    if (SUCCEEDED(hr)) hr = browser->QueryActiveShellView(&view);
    ComPtr<IFolderView> folderView;
    if (SUCCEEDED(hr)) hr = view.As(&folderView);
    ComPtr<IShellItem> folder;
    if (SUCCEEDED(hr)) hr = folderView->GetFolder(IID_PPV_ARGS(&folder));
    if (SUCCEEDED(hr)) hr = folder->GetDisplayName(SIGDN_FILESYSPATH, path);
    return hr;
}

// Several selected items go to the app in a list file, one path per line: a command line holds at most
// 32,767 characters. The app deletes the file once it has read it.
static HRESULT WriteList(const std::vector<std::wstring>& paths, std::wstring* file)
{
    wchar_t folder[MAX_PATH + 1];
    if (!GetTempPathW(ARRAYSIZE(folder), folder)) return HRESULT_FROM_WIN32(GetLastError());
    GUID id;
    HRESULT hr = CoCreateGuid(&id);
    if (FAILED(hr)) return hr;
    wchar_t name[40];
    StringFromGUID2(id, name, ARRAYSIZE(name));
    *file = std::wstring(folder) + L"WinDeepSearch-selection-" + name + L".txt";

    std::wstring text = L"\xFEFF"; // UTF-16 byte order mark
    for (const auto& path : paths) text += path + L"\r\n";
    HANDLE handle = CreateFileW(file->c_str(), GENERIC_WRITE, 0, nullptr, CREATE_NEW, FILE_ATTRIBUTE_TEMPORARY, nullptr);
    if (handle == INVALID_HANDLE_VALUE) return HRESULT_FROM_WIN32(GetLastError());
    DWORD written = 0;
    BOOL ok = WriteFile(handle, text.data(), static_cast<DWORD>(text.size() * sizeof(wchar_t)), &written, nullptr);
    DWORD error = GetLastError();
    CloseHandle(handle);
    return ok ? S_OK : HRESULT_FROM_WIN32(error);
}

class __declspec(uuid("2CADCD13-EDE6-4B0F-94BB-2C46DF5954F1")) SearchCommand final
    : public RuntimeClass<RuntimeClassFlags<ClassicCom | InhibitRoOriginateError>, IExplorerCommand, IObjectWithSite>
{
public:
    IFACEMETHODIMP GetTitle(IShellItemArray*, PWSTR* name) override { return SHStrDupW(L"WinDeepSearch", name); }

    IFACEMETHODIMP GetIcon(IShellItemArray*, PWSTR* icon) override { return SHStrDupW((ExePath() + L",0").c_str(), icon); }

    IFACEMETHODIMP GetToolTip(IShellItemArray*, PWSTR* tip) override
    {
        *tip = nullptr;
        return E_NOTIMPL;
    }

    IFACEMETHODIMP GetCanonicalName(GUID* name) override
    {
        *name = __uuidof(SearchCommand);
        return S_OK;
    }

    IFACEMETHODIMP GetState(IShellItemArray*, BOOL, EXPCMDSTATE* state) override
    {
        *state = ECS_ENABLED;
        return S_OK;
    }

    IFACEMETHODIMP GetFlags(EXPCMDFLAGS* flags) override
    {
        *flags = ECF_DEFAULT;
        return S_OK;
    }

    IFACEMETHODIMP EnumSubCommands(IEnumExplorerCommand** commands) override
    {
        *commands = nullptr;
        return E_NOTIMPL;
    }

    IFACEMETHODIMP Invoke(IShellItemArray* items, IBindCtx*) override
    {
        std::vector<std::wstring> paths;
        HRESULT hr = S_OK;
        DWORD count = 0;
        if (items && SUCCEEDED(items->GetCount(&count)) && count > 0)
        {
            for (DWORD i = 0; i < count && SUCCEEDED(hr); i++)
            {
                ComPtr<IShellItem> item;
                PWSTR path = nullptr;
                hr = items->GetItemAt(i, &item);
                if (SUCCEEDED(hr)) hr = item->GetDisplayName(SIGDN_FILESYSPATH, &path);
                if (SUCCEEDED(hr))
                {
                    paths.emplace_back(path);
                    CoTaskMemFree(path);
                }
            }
        }
        else
        {
            PWSTR path = nullptr;
            hr = FolderFromSite(m_site.Get(), &path);
            if (SUCCEEDED(hr))
            {
                paths.emplace_back(path);
                CoTaskMemFree(path);
            }
        }
        if (FAILED(hr)) return hr;

        std::wstring arguments;
        if (paths.size() == 1)
        {
            std::wstring target = paths[0];
            // In "C:\" the trailing backslash would escape the closing quote; doubling it keeps the quote.
            if (!target.empty() && target.back() == L'\\') target += L'\\';
            arguments = L"--path \"" + target + L"\"";
        }
        else
        {
            std::wstring list;
            hr = WriteList(paths, &list);
            if (FAILED(hr)) return hr;
            arguments = L"--items \"" + list + L"\"";
        }
        std::wstring exe = ExePath();

        SHELLEXECUTEINFOW info{};
        info.cbSize = sizeof(info);
        info.fMask = SEE_MASK_NOASYNC;
        info.lpFile = exe.c_str();
        info.lpParameters = arguments.c_str();
        info.nShow = SW_SHOWNORMAL;
        return ShellExecuteExW(&info) ? S_OK : HRESULT_FROM_WIN32(GetLastError());
    }

    IFACEMETHODIMP SetSite(IUnknown* site) override
    {
        m_site = site;
        return S_OK;
    }

    IFACEMETHODIMP GetSite(REFIID riid, void** site) override { return m_site.CopyTo(riid, site); }

private:
    ComPtr<IUnknown> m_site;
};

CoCreatableClass(SearchCommand)

STDAPI DllCanUnloadNow()
{
    return Module<InProc>::GetModule().GetObjectCount() == 0 ? S_OK : S_FALSE;
}

STDAPI DllGetClassObject(REFCLSID clsid, REFIID riid, void** instance)
{
    return Module<InProc>::GetModule().GetClassObject(clsid, riid, instance);
}

BOOL WINAPI DllMain(HINSTANCE instance, DWORD reason, void*)
{
    if (reason == DLL_PROCESS_ATTACH)
    {
        g_module = instance;
        DisableThreadLibraryCalls(instance);
    }
    return TRUE;
}
