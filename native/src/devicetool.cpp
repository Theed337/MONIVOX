// MonivoxDevice – legt Software-Geräte für den mitgelieferten Virtual Display Driver an oder entfernt sie.
//
//   MonivoxDevice.exe create [N]       -> sorgt dafür, dass die Geräte 1..N existieren (Standard 1)
//   MonivoxDevice.exe add i [j …]      -> sorgt dafür, dass die Geräte mit den angegebenen Nummern existieren
//   MonivoxDevice.exe remove i [j …]   -> entfernt die Geräte mit den angegebenen Nummern
//
// Warum mehrere Geräte? Der Virtual Display Driver hat einen bekannten Fehler: Laufen zwei oder mehr virtuelle
// Monitore an EINEM Treibergerät, bremst Windows auf allen Monitoren aus (Ruckeln beim Fensterziehen).
// Abhilfe: ein Gerät pro virtuellem Monitor, jedes mit genau einem Monitor.
//
// Instanz-IDs: SWD\Monivox\MttVDD (Gerät 1, kompatibel zu älteren Versionen), SWD\Monivox\MttVDD2 … MttVDD24.
// Hardware-ID: Root\MttVDD. Der Treiber muss vorher per "pnputil /add-driver MttVDD.inf /install" im Driver Store
// liegen. Benötigt Administratorrechte.

#include <windows.h>
#include <swdevice.h>
#include <cstdio>
#include <cwchar>
#include <string>
#include <vector>

namespace
{
    constexpr int kMaxDevices = 24;

    struct CreateResult
    {
        HANDLE Done = nullptr;
        HRESULT Hr = E_PENDING;
        wchar_t InstanceId[256]{};
    };

    VOID WINAPI CreationCallback(HSWDEVICE, HRESULT hr, PVOID context, PCWSTR instanceId)
    {
        auto* result = static_cast<CreateResult*>(context);
        result->Hr = hr;
        if (instanceId) wcsncpy_s(result->InstanceId, instanceId, _TRUNCATE);
        SetEvent(result->Done);
    }

    std::wstring InstanceName(int index)
    {
        return index == 1 ? std::wstring(L"MttVDD") : L"MttVDD" + std::to_wstring(index);
    }

    // Öffnet (bzw. legt an) das Gerät mit der angegebenen Nummer und setzt seine Lebensdauer.
    // keep = true: Gerät bleibt dauerhaft bestehen. keep = false: Gerät wird beim Schließen entfernt.
    HRESULT TouchDevice(int index, bool keep, std::wstring& instanceId)
    {
        std::wstring name = InstanceName(index);
        std::wstring description = L"Virtual Display Driver (MONIVOX " + std::to_wstring(index) + L")";

        SW_DEVICE_CREATE_INFO info{};
        info.cbSize = sizeof(info);
        info.pszInstanceId = name.c_str();
        info.pszzHardwareIds = L"Root\\MttVDD\0\0";
        info.pszzCompatibleIds = L"Root\\MttVDD\0\0";
        info.pszDeviceDescription = description.c_str();
        info.CapabilityFlags = SWDeviceCapabilitiesRemovable | SWDeviceCapabilitiesSilentInstall | SWDeviceCapabilitiesDriverRequired;

        CreateResult result;
        result.Done = CreateEventW(nullptr, TRUE, FALSE, nullptr);

        HSWDEVICE device = nullptr;
        HRESULT hr = SwDeviceCreate(L"Monivox", L"HTREE\\ROOT\\0", &info, 0, nullptr, CreationCallback, &result, &device);
        if (SUCCEEDED(hr))
        {
            hr = WaitForSingleObject(result.Done, 15000) == WAIT_OBJECT_0 ? result.Hr : HRESULT_FROM_WIN32(WAIT_TIMEOUT);
        }
        if (SUCCEEDED(hr))
        {
            hr = SwDeviceSetLifetime(device, keep ? SWDeviceLifetimeParentPresent : SWDeviceLifetimeHandle);
        }
        if (device) SwDeviceClose(device);
        CloseHandle(result.Done);
        instanceId = result.InstanceId;
        return hr;
    }
}

int wmain(int argc, wchar_t** argv)
{
    if (argc < 2 || (_wcsicmp(argv[1], L"create") != 0 && _wcsicmp(argv[1], L"remove") != 0 && _wcsicmp(argv[1], L"add") != 0))
    {
        fwprintf(stderr, L"Aufruf: MonivoxDevice.exe create [Anzahl] | remove Nummer [Nummer …]\n");
        return 2;
    }
    const bool create = _wcsicmp(argv[1], L"remove") != 0;

    std::vector<int> indices;
    if (_wcsicmp(argv[1], L"create") == 0)
    {
        int count = argc >= 3 ? _wtoi(argv[2]) : 1;
        if (count < 1) count = 1;
        if (count > kMaxDevices) count = kMaxDevices;
        for (int i = 1; i <= count; ++i) indices.push_back(i);
    }
    else
    {
        for (int a = 2; a < argc; ++a)
        {
            int i = _wtoi(argv[a]);
            if (i >= 1 && i <= kMaxDevices) indices.push_back(i);
        }
    }

    int failures = 0;
    for (int i : indices)
    {
        std::wstring id;
        HRESULT hr = TouchDevice(i, create, id);
        if (FAILED(hr))
        {
            fwprintf(stderr, L"Gerät %d: Fehler 0x%08X\n", i, static_cast<unsigned>(hr));
            ++failures;
            continue;
        }
        wprintf(create ? L"Gerät bereit: %s\n" : L"Gerät entfernt: %s\n", id.c_str());
    }
    return failures == 0 ? 0 : 1;
}
