// MonivoxDevice – legt das Software-Gerät für den mitgelieferten Virtual Display Driver an oder entfernt es.
//
//   MonivoxDevice.exe create   -> erzeugt SWD\Monivox\MttVDD mit Hardware-ID Root\MttVDD (bleibt bestehen)
//   MonivoxDevice.exe remove   -> entfernt das Gerät wieder
//
// Der Treiber selbst muss vorher mit "pnputil /add-driver MttVDD.inf /install" im Driver Store liegen.
// Benötigt Administratorrechte. (Ersetzt das sonst übliche Drittwerkzeug "nefcon".)

#include <windows.h>
#include <swdevice.h>
#include <cstdio>
#include <cwchar>

namespace
{
    struct CreateResult
    {
        HANDLE Done;
        HRESULT Hr;
        wchar_t InstanceId[256];
    };

    VOID WINAPI CreationCallback(HSWDEVICE, HRESULT hr, PVOID context, PCWSTR instanceId)
    {
        auto* result = static_cast<CreateResult*>(context);
        result->Hr = hr;
        if (instanceId) wcsncpy_s(result->InstanceId, instanceId, _TRUNCATE);
        SetEvent(result->Done);
    }

    HRESULT OpenDevice(HSWDEVICE& device, CreateResult& result)
    {
        SW_DEVICE_CREATE_INFO info{};
        info.cbSize = sizeof(info);
        info.pszInstanceId = L"MttVDD";
        info.pszzHardwareIds = L"Root\\MttVDD\0\0";
        info.pszzCompatibleIds = L"Root\\MttVDD\0\0";
        info.pszDeviceDescription = L"Virtual Display Driver (MONIVOX)";
        info.CapabilityFlags = SWDeviceCapabilitiesRemovable | SWDeviceCapabilitiesSilentInstall | SWDeviceCapabilitiesDriverRequired;

        result.Done = CreateEventW(nullptr, TRUE, FALSE, nullptr);
        result.Hr = E_PENDING;
        result.InstanceId[0] = L'\0';

        HRESULT hr = SwDeviceCreate(L"Monivox", L"HTREE\\ROOT\\0", &info, 0, nullptr,
                                    CreationCallback, &result, &device);
        if (FAILED(hr)) return hr;

        if (WaitForSingleObject(result.Done, 15000) != WAIT_OBJECT_0)
        {
            return HRESULT_FROM_WIN32(WAIT_TIMEOUT);
        }
        return result.Hr;
    }
}

int wmain(int argc, wchar_t** argv)
{
    if (argc < 2 || (_wcsicmp(argv[1], L"create") != 0 && _wcsicmp(argv[1], L"remove") != 0))
    {
        fwprintf(stderr, L"Aufruf: MonivoxDevice.exe create|remove\n");
        return 2;
    }
    const bool create = _wcsicmp(argv[1], L"create") == 0;

    HSWDEVICE device = nullptr;
    CreateResult result{};
    HRESULT hr = OpenDevice(device, result);
    if (FAILED(hr))
    {
        fwprintf(stderr, L"SwDeviceCreate fehlgeschlagen: 0x%08X\n", static_cast<unsigned>(hr));
        if (device) SwDeviceClose(device);
        if (result.Done) CloseHandle(result.Done);
        return 1;
    }

    // create: Gerät soll das Schließen des Handles überleben (bleibt bis "remove").
    // remove: Lebensdauer an das Handle binden -> Schließen entfernt das Gerät.
    hr = SwDeviceSetLifetime(device, create ? SWDeviceLifetimeParentPresent : SWDeviceLifetimeHandle);
    if (FAILED(hr))
    {
        fwprintf(stderr, L"SwDeviceSetLifetime fehlgeschlagen: 0x%08X\n", static_cast<unsigned>(hr));
    }
    SwDeviceClose(device);
    CloseHandle(result.Done);

    if (SUCCEEDED(hr))
    {
        wprintf(create ? L"Gerät angelegt: %s\n" : L"Gerät entfernt: %s\n", result.InstanceId);
        return 0;
    }
    return 1;
}
