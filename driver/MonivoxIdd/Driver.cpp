// Monivox – Indirect Display Driver
// Siehe Driver.h für den Überblick.

#include "Driver.h"

using namespace Microsoft::WRL;
using namespace Monivox;

extern "C" DRIVER_INITIALIZE DriverEntry;

EVT_WDF_DRIVER_DEVICE_ADD SdDeviceAdd;
EVT_WDF_DEVICE_D0_ENTRY SdDeviceD0Entry;

EVT_IDD_CX_ADAPTER_INIT_FINISHED SdAdapterInitFinished;
EVT_IDD_CX_ADAPTER_COMMIT_MODES SdAdapterCommitModes;
EVT_IDD_CX_PARSE_MONITOR_DESCRIPTION SdParseMonitorDescription;
EVT_IDD_CX_MONITOR_GET_DEFAULT_DESCRIPTION_MODES SdMonitorGetDefaultModes;
EVT_IDD_CX_MONITOR_QUERY_TARGET_MODES SdMonitorQueryModes;
EVT_IDD_CX_MONITOR_ASSIGN_SWAPCHAIN SdMonitorAssignSwapChain;
EVT_IDD_CX_MONITOR_UNASSIGN_SWAPCHAIN SdMonitorUnassignSwapChain;

// ---------------------------------------------------------------------------
// Konfiguration
// ---------------------------------------------------------------------------

namespace Monivox
{
    std::vector<MonitorConfig> ParseMonitorConfig(const std::wstring& value)
    {
        std::vector<MonitorConfig> result;
        size_t start = 0;
        while (start < value.size() && result.size() < kMaxMonitors)
        {
            size_t end = value.find(L';', start);
            if (end == std::wstring::npos) end = value.size();
            std::wstring item = value.substr(start, end - start);
            start = end + 1;

            unsigned w = 0, h = 0, hz = 60;
            int matched = swscanf_s(item.c_str(), L"%ux%u@%u", &w, &h, &hz);
            if (matched < 2) continue;
            // Plausibilitätsgrenzen: Windows verlangt mind. 640x480 nicht zwingend,
            // aber sehr kleine Modi werden von vielen Apps schlecht behandelt.
            if (w < 320 || h < 240 || w > 7680 || h > 4320) continue;
            if (hz < 24 || hz > 500) hz = 60;
            result.push_back({ w, h, hz });
        }
        return result;
    }

    std::vector<MonitorConfig> LoadMonitorConfig()
    {
        wchar_t buffer[512] = {};
        DWORD size = sizeof(buffer);
        LSTATUS status = RegGetValueW(HKEY_LOCAL_MACHINE, L"SOFTWARE\\Monivox", L"Monitors",
                                      RRF_RT_REG_SZ, nullptr, buffer, &size);
        std::vector<MonitorConfig> monitors;
        if (status == ERROR_SUCCESS)
        {
            monitors = ParseMonitorConfig(buffer);
        }
        if (monitors.empty())
        {
            monitors = { { 1920, 1080, 60 }, { 1920, 1080, 60 } };
        }
        return monitors;
    }

    // Hersteller-ID "MVX" (MONIVOX), komprimiertes ASCII nach EDID 1.3
    static constexpr BYTE kEdidManufacturer[2] = {
        static_cast<BYTE>((('M' - 64) << 2) | (('V' - 64) >> 3)),
        static_cast<BYTE>(((('V' - 64) & 0x7) << 5) | ('X' - 64)) };
    static constexpr WORD kEdidProductBase = 0x5D00;

    std::vector<BYTE> BuildEdid(UINT monitorIndex)
    {
        std::vector<BYTE> e(128, 0);
        const BYTE header[8] = { 0x00, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0x00 };
        memcpy(e.data(), header, 8);
        e[8] = kEdidManufacturer[0];
        e[9] = kEdidManufacturer[1];
        WORD product = static_cast<WORD>(kEdidProductBase + monitorIndex);
        e[10] = static_cast<BYTE>(product & 0xFF);
        e[11] = static_cast<BYTE>(product >> 8);
        DWORD serial = 0x53440000u + monitorIndex + 1;
        memcpy(&e[12], &serial, 4);
        e[16] = 1;      // Woche
        e[17] = 36;     // Jahr 1990+36 = 2026
        e[18] = 1;      // EDID 1.3
        e[19] = 3;
        e[20] = 0x80;   // digitaler Eingang
        e[21] = 0;      // Größe unbekannt
        e[22] = 0;
        e[23] = 0x78;   // Gamma 2.2
        e[24] = 0x0A;   // RGB, bevorzugtes Timing im ersten Deskriptor
        // Chromatizität sRGB
        const BYTE chroma[10] = { 0xEE, 0x91, 0xA3, 0x54, 0x4C, 0x99, 0x26, 0x0F, 0x50, 0x54 };
        memcpy(&e[25], chroma, 10);
        // keine Established Timings (35-37 = 0), Standard Timings unbenutzt
        for (int i = 38; i < 54; i += 2) { e[i] = 0x01; e[i + 1] = 0x01; }
        // Deskriptor 1: Detailed Timing 1920x1080@60 (CEA-861 Standard).
        // Die tatsächlichen Modi liefert der Treiber in EvtIddCxParseMonitorDescription.
        const BYTE dtd[18] = { 0x02, 0x3A, 0x80, 0x18, 0x71, 0x38, 0x2D, 0x40, 0x58, 0x2C,
                               0x45, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x1E };
        memcpy(&e[54], dtd, 18);
        // Deskriptor 2: Monitorname
        BYTE* name = &e[72];
        name[3] = 0xFC;
        char text[14] = {};
        sprintf_s(text, "Monivox %u", monitorIndex + 1); // max. 13 Zeichen laut EDID
        size_t len = strlen(text);
        for (size_t i = 0; i < 13; ++i)
        {
            name[5 + i] = i < len ? static_cast<BYTE>(text[i]) : (i == len ? 0x0A : 0x20);
        }
        // Deskriptor 3: Seriennummer (Text) – hilft Windows bei der Unterscheidung
        BYTE* sn = &e[90];
        sn[3] = 0xFF;
        char serialText[14] = {};
        sprintf_s(serialText, "MVX-%04u", monitorIndex + 1);
        len = strlen(serialText);
        for (size_t i = 0; i < 13; ++i)
        {
            sn[5 + i] = i < len ? static_cast<BYTE>(serialText[i]) : (i == len ? 0x0A : 0x20);
        }
        // Deskriptor 4: Dummy
        e[108 + 3] = 0x10;
        e[126] = 0; // keine Erweiterungsblöcke
        BYTE sum = 0;
        for (int i = 0; i < 127; ++i) sum = static_cast<BYTE>(sum + e[i]);
        e[127] = static_cast<BYTE>(0x100 - sum);
        return e;
    }

    int MonitorIndexFromEdid(const BYTE* data, UINT size)
    {
        if (data == nullptr || size < 128) return -1;
        if (data[8] != kEdidManufacturer[0] || data[9] != kEdidManufacturer[1]) return -1;
        WORD product = static_cast<WORD>(data[10] | (data[11] << 8));
        if (product < kEdidProductBase || product >= kEdidProductBase + kMaxMonitors) return -1;
        return product - kEdidProductBase;
    }
}

// ---------------------------------------------------------------------------
// Modus-Hilfen
// ---------------------------------------------------------------------------

static void FillSignalInfo(DISPLAYCONFIG_VIDEO_SIGNAL_INFO& mode, DWORD width, DWORD height, DWORD vsync, bool monitorMode)
{
    mode.totalSize.cx = mode.activeSize.cx = width;
    mode.totalSize.cy = mode.activeSize.cy = height;
    mode.AdditionalSignalInfo.vSyncFreqDivider = monitorMode ? 0 : 1;
    mode.AdditionalSignalInfo.videoStandard = 255;
    mode.vSyncFreq.Numerator = vsync;
    mode.vSyncFreq.Denominator = 1;
    mode.hSyncFreq.Numerator = vsync * height;
    mode.hSyncFreq.Denominator = 1;
    mode.scanLineOrdering = DISPLAYCONFIG_SCANLINE_ORDERING_PROGRESSIVE;
    mode.pixelRate = static_cast<UINT64>(vsync) * width * height;
}

static IDDCX_MONITOR_MODE CreateMonitorMode(DWORD width, DWORD height, DWORD vsync)
{
    IDDCX_MONITOR_MODE mode = {};
    mode.Size = sizeof(mode);
    mode.Origin = IDDCX_MONITOR_MODE_ORIGIN_DRIVER;
    FillSignalInfo(mode.MonitorVideoSignalInfo, width, height, vsync, true);
    return mode;
}

static IDDCX_TARGET_MODE CreateTargetMode(DWORD width, DWORD height, DWORD vsync)
{
    IDDCX_TARGET_MODE mode = {};
    mode.Size = sizeof(mode);
    FillSignalInfo(mode.TargetVideoSignalInfo.targetVideoSignalInfo, width, height, vsync, false);
    return mode;
}

// Modusliste für einen Monitor: konfigurierter Modus zuerst (bevorzugt),
// zusätzlich 60 Hz und kleinere Standardmodi im gleichen Seitenverhältnis-Umfeld.
struct ModeEntry { DWORD W, H, Hz; };

static std::vector<ModeEntry> ModesFor(const MonitorConfig& cfg)
{
    std::vector<ModeEntry> modes;
    modes.push_back({ cfg.Width, cfg.Height, cfg.RefreshHz });
    if (cfg.RefreshHz != 60) modes.push_back({ cfg.Width, cfg.Height, 60 });

    static const ModeEntry common[] = {
        { 3840, 2160, 60 }, { 2560, 1440, 60 }, { 1920, 1080, 60 }, { 1600, 900, 60 },
        { 1280, 720, 60 }, { 1920, 2160, 60 }, { 1280, 1440, 60 }, { 1024, 768, 60 } };
    for (const auto& m : common)
    {
        bool smaller = m.W <= cfg.Width && m.H <= cfg.Height;
        bool duplicate = false;
        for (const auto& existing : modes)
        {
            if (existing.W == m.W && existing.H == m.H && existing.Hz == m.Hz) duplicate = true;
        }
        if (smaller && !duplicate) modes.push_back(m);
    }
    return modes;
}

static const MonitorConfig* ConfigForIndex(UINT index)
{
    // Konfiguration wird beim Treiberstart einmal geladen (Neustart des Geräts bei Änderung).
    static std::vector<MonitorConfig> s_config = LoadMonitorConfig();
    return index < s_config.size() ? &s_config[index] : nullptr;
}

// ---------------------------------------------------------------------------
// Treiber-Einstieg
// ---------------------------------------------------------------------------

extern "C" NTSTATUS DriverEntry(PDRIVER_OBJECT pDriverObject, PUNICODE_STRING pRegistryPath)
{
    WDF_DRIVER_CONFIG config;
    WDF_OBJECT_ATTRIBUTES attributes;
    WDF_OBJECT_ATTRIBUTES_INIT(&attributes);
    WDF_DRIVER_CONFIG_INIT(&config, SdDeviceAdd);
    return WdfDriverCreate(pDriverObject, pRegistryPath, &attributes, &config, WDF_NO_HANDLE);
}

_Use_decl_annotations_
NTSTATUS SdDeviceAdd(WDFDRIVER driver, PWDFDEVICE_INIT pDeviceInit)
{
    UNREFERENCED_PARAMETER(driver);

    WDF_PNPPOWER_EVENT_CALLBACKS pnpPowerCallbacks;
    WDF_PNPPOWER_EVENT_CALLBACKS_INIT(&pnpPowerCallbacks);
    pnpPowerCallbacks.EvtDeviceD0Entry = SdDeviceD0Entry;
    WdfDeviceInitSetPnpPowerEventCallbacks(pDeviceInit, &pnpPowerCallbacks);

    IDD_CX_CLIENT_CONFIG iddConfig;
    IDD_CX_CLIENT_CONFIG_INIT(&iddConfig);
    iddConfig.EvtIddCxAdapterInitFinished = SdAdapterInitFinished;
    iddConfig.EvtIddCxParseMonitorDescription = SdParseMonitorDescription;
    iddConfig.EvtIddCxMonitorGetDefaultDescriptionModes = SdMonitorGetDefaultModes;
    iddConfig.EvtIddCxMonitorQueryTargetModes = SdMonitorQueryModes;
    iddConfig.EvtIddCxAdapterCommitModes = SdAdapterCommitModes;
    iddConfig.EvtIddCxMonitorAssignSwapChain = SdMonitorAssignSwapChain;
    iddConfig.EvtIddCxMonitorUnassignSwapChain = SdMonitorUnassignSwapChain;

    NTSTATUS status = IddCxDeviceInitConfig(pDeviceInit, &iddConfig);
    if (!NT_SUCCESS(status)) return status;

    WDF_OBJECT_ATTRIBUTES attr;
    WDF_OBJECT_ATTRIBUTES_INIT_CONTEXT_TYPE(&attr, IndirectDeviceContextWrapper);
    attr.EvtCleanupCallback = [](WDFOBJECT object)
    {
        auto* wrapper = WdfObjectGet_IndirectDeviceContextWrapper(object);
        if (wrapper) wrapper->Cleanup();
    };

    WDFDEVICE device = nullptr;
    status = WdfDeviceCreate(&pDeviceInit, &attr, &device);
    if (!NT_SUCCESS(status)) return status;

    status = IddCxDeviceInitialize(device);
    if (!NT_SUCCESS(status)) return status;

    auto* wrapper = WdfObjectGet_IndirectDeviceContextWrapper(device);
    wrapper->pContext = new IndirectDeviceContext(device);
    return status;
}

_Use_decl_annotations_
NTSTATUS SdDeviceD0Entry(WDFDEVICE device, WDF_POWER_DEVICE_STATE previousState)
{
    UNREFERENCED_PARAMETER(previousState);
    auto* wrapper = WdfObjectGet_IndirectDeviceContextWrapper(device);
    wrapper->pContext->InitAdapter();
    return STATUS_SUCCESS;
}

// ---------------------------------------------------------------------------
// Direct3DDevice
// ---------------------------------------------------------------------------

Direct3DDevice::Direct3DDevice(LUID adapterLuid) : AdapterLuid(adapterLuid) {}

HRESULT Direct3DDevice::Init()
{
    HRESULT hr = CreateDXGIFactory2(0, IID_PPV_ARGS(&DxgiFactory));
    if (FAILED(hr)) return hr;
    hr = DxgiFactory->EnumAdapterByLuid(AdapterLuid, IID_PPV_ARGS(&Adapter));
    if (FAILED(hr)) return hr;
    return D3D11CreateDevice(Adapter.Get(), D3D_DRIVER_TYPE_UNKNOWN, nullptr,
                             D3D11_CREATE_DEVICE_BGRA_SUPPORT, nullptr, 0, D3D11_SDK_VERSION,
                             &Device, nullptr, &DeviceContext);
}

// ---------------------------------------------------------------------------
// SwapChainProcessor
// ---------------------------------------------------------------------------

SwapChainProcessor::SwapChainProcessor(IDDCX_SWAPCHAIN hSwapChain, std::shared_ptr<Direct3DDevice> device, HANDLE newFrameEvent)
    : m_hSwapChain(hSwapChain), m_Device(std::move(device)), m_hAvailableBufferEvent(newFrameEvent)
{
    m_hTerminateEvent.Attach(CreateEvent(nullptr, FALSE, FALSE, nullptr));
    m_hThread = CreateThread(nullptr, 0, RunThread, this, 0, nullptr);
}

SwapChainProcessor::~SwapChainProcessor()
{
    SetEvent(m_hTerminateEvent.Get());
    if (m_hThread)
    {
        WaitForSingleObject(m_hThread, INFINITE);
        CloseHandle(m_hThread);
        m_hThread = nullptr;
    }
}

DWORD CALLBACK SwapChainProcessor::RunThread(LPVOID argument)
{
    reinterpret_cast<SwapChainProcessor*>(argument)->Run();
    return 0;
}

void SwapChainProcessor::Run()
{
    // Multimedia-Priorität, damit Frames auch unter Last rechtzeitig abgeholt werden.
    DWORD avTask = 0;
    HANDLE avTaskHandle = AvSetMmThreadCharacteristicsW(L"Distribution", &avTask);

    RunCore();

    // Swapchain gehört jetzt uns – bei Ende/Fehler freigeben, damit IddCx eine neue zuweisen kann.
    WdfObjectDelete(reinterpret_cast<WDFOBJECT>(m_hSwapChain));
    m_hSwapChain = nullptr;

    if (avTaskHandle) AvRevertMmThreadCharacteristics(avTaskHandle);
}

void SwapChainProcessor::RunCore()
{
    ComPtr<IDXGIDevice> dxgiDevice;
    HRESULT hr = m_Device->Device.As(&dxgiDevice);
    if (FAILED(hr)) return;

    IDARG_IN_SWAPCHAINSETDEVICE setDevice = {};
    setDevice.pDevice = dxgiDevice.Get();
    hr = IddCxSwapChainSetDevice(m_hSwapChain, &setDevice);
    if (FAILED(hr)) return;

    for (;;)
    {
        IDARG_OUT_RELEASEANDACQUIREBUFFER buffer = {};
        hr = IddCxSwapChainReleaseAndAcquireBuffer(m_hSwapChain, &buffer);

        if (hr == E_PENDING)
        {
            HANDLE waitHandles[] = { m_hAvailableBufferEvent, m_hTerminateEvent.Get() };
            DWORD wait = WaitForMultipleObjects(ARRAYSIZE(waitHandles), waitHandles, FALSE, 16);
            if (wait == WAIT_OBJECT_0 || wait == WAIT_TIMEOUT) continue;
            break; // Terminate oder Fehler
        }
        if (FAILED(hr)) break;

        // Frame liegt in buffer.MetaData.pSurface (IDXGIResource, gehört uns bis zum nächsten Acquire).
        // Prototyp: keine Weiterverarbeitung.
        {
            ComPtr<IDXGIResource> acquired;
            acquired.Attach(buffer.MetaData.pSurface);
        }

        hr = IddCxSwapChainFinishedProcessingFrame(m_hSwapChain);
        if (FAILED(hr)) break;
    }
}

// ---------------------------------------------------------------------------
// IndirectDeviceContext
// ---------------------------------------------------------------------------

IndirectDeviceContext::IndirectDeviceContext(WDFDEVICE wdfDevice)
    : m_WdfDevice(wdfDevice)
{
    for (UINT i = 0; i < kMaxMonitors; ++i)
    {
        const MonitorConfig* cfg = ConfigForIndex(i);
        if (!cfg) break;
        m_Monitors.push_back(*cfg);
    }
}

void IndirectDeviceContext::InitAdapter()
{
    IDDCX_ADAPTER_CAPS caps = {};
    caps.Size = sizeof(caps);
    caps.MaxMonitorsSupported = static_cast<UINT>(m_Monitors.size());
    caps.EndPointDiagnostics.Size = sizeof(caps.EndPointDiagnostics);
    caps.EndPointDiagnostics.GammaSupport = IDDCX_FEATURE_IMPLEMENTATION_NONE;
    caps.EndPointDiagnostics.TransmissionType = IDDCX_TRANSMISSION_TYPE_WIRED_OTHER;
    caps.EndPointDiagnostics.pEndPointFriendlyName = L"Monivox Virtual Adapter";
    caps.EndPointDiagnostics.pEndPointManufacturerName = L"Monivox";
    caps.EndPointDiagnostics.pEndPointModelName = L"Monivox IDD";

    IDDCX_ENDPOINT_VERSION version = {};
    version.Size = sizeof(version);
    version.MajorVer = 0;
    version.MinorVer = 1;
    caps.EndPointDiagnostics.pFirmwareVersion = &version;
    caps.EndPointDiagnostics.pHardwareVersion = &version;

    // Adapterobjekt zeigt auf denselben Kontext, aber ohne Cleanup-Callback
    // (Besitzer ist das WDF-Device).
    WDF_OBJECT_ATTRIBUTES attr;
    WDF_OBJECT_ATTRIBUTES_INIT_CONTEXT_TYPE(&attr, IndirectDeviceContextWrapper);

    IDARG_IN_ADAPTER_INIT adapterInit = {};
    adapterInit.WdfDevice = m_WdfDevice;
    adapterInit.pCaps = &caps;
    adapterInit.ObjectAttributes = &attr;

    IDARG_OUT_ADAPTER_INIT adapterInitOut = {};
    NTSTATUS status = IddCxAdapterInitAsync(&adapterInit, &adapterInitOut);
    if (NT_SUCCESS(status))
    {
        m_Adapter = adapterInitOut.AdapterObject;
        auto* wrapper = WdfObjectGet_IndirectDeviceContextWrapper(adapterInitOut.AdapterObject);
        wrapper->pContext = this;
    }
}

void IndirectDeviceContext::FinishInit()
{
    for (UINT i = 0; i < m_Monitors.size(); ++i)
    {
        WDF_OBJECT_ATTRIBUTES attr;
        WDF_OBJECT_ATTRIBUTES_INIT_CONTEXT_TYPE(&attr, IndirectMonitorContextWrapper);
        attr.EvtCleanupCallback = [](WDFOBJECT object)
        {
            auto* wrapper = WdfObjectGet_IndirectMonitorContextWrapper(object);
            if (wrapper) wrapper->Cleanup();
        };

        std::vector<BYTE> edid = BuildEdid(i);

        IDDCX_MONITOR_INFO info = {};
        info.Size = sizeof(info);
        info.MonitorType = DISPLAYCONFIG_OUTPUT_TECHNOLOGY_HDMI;
        info.ConnectorIndex = i;
        info.MonitorDescription.Size = sizeof(info.MonitorDescription);
        info.MonitorDescription.Type = IDDCX_MONITOR_DESCRIPTION_TYPE_EDID;
        info.MonitorDescription.DataSize = static_cast<UINT>(edid.size());
        info.MonitorDescription.pData = edid.data();

        // Stabile Container-ID je Index, damit Windows die Monitore nach Neustart wiedererkennt.
        // {5D1A0000-5D15-4D15-9000-0000000000xx}
        info.MonitorContainerId = { 0x5D1A0000, 0x5D15, 0x4D15, { 0x90, 0x00, 0, 0, 0, 0, 0, static_cast<BYTE>(i) } };

        IDARG_IN_MONITORCREATE create = {};
        create.ObjectAttributes = &attr;
        create.pMonitorInfo = &info;

        IDARG_OUT_MONITORCREATE createOut = {};
        NTSTATUS status = IddCxMonitorCreate(m_Adapter, &create, &createOut);
        if (!NT_SUCCESS(status)) continue;

        auto* wrapper = WdfObjectGet_IndirectMonitorContextWrapper(createOut.MonitorObject);
        wrapper->pContext = new IndirectMonitorContext(createOut.MonitorObject, i, m_Monitors[i]);

        IDARG_OUT_MONITORARRIVAL arrivalOut = {};
        IddCxMonitorArrival(createOut.MonitorObject, &arrivalOut);
    }
}

// ---------------------------------------------------------------------------
// IndirectMonitorContext
// ---------------------------------------------------------------------------

IndirectMonitorContext::IndirectMonitorContext(IDDCX_MONITOR monitor, UINT index, MonitorConfig config)
    : m_Monitor(monitor), m_Index(index), m_Config(config) {}

IndirectMonitorContext::~IndirectMonitorContext()
{
    m_ProcessingThread.reset();
}

void IndirectMonitorContext::AssignSwapChain(IDDCX_SWAPCHAIN swapChain, LUID renderAdapter, HANDLE newFrameEvent)
{
    m_ProcessingThread.reset();

    auto device = std::make_shared<Direct3DDevice>(renderAdapter);
    if (FAILED(device->Init()))
    {
        // Ohne Gerät kann die Swapchain nicht bedient werden. Löschen veranlasst
        // IddCx, die Zuweisung erneut zu versuchen.
        WdfObjectDelete(reinterpret_cast<WDFOBJECT>(swapChain));
        return;
    }
    m_ProcessingThread = std::make_unique<SwapChainProcessor>(swapChain, device, newFrameEvent);
}

void IndirectMonitorContext::UnassignSwapChain()
{
    m_ProcessingThread.reset();
}

// ---------------------------------------------------------------------------
// IddCx-Callbacks
// ---------------------------------------------------------------------------

_Use_decl_annotations_
NTSTATUS SdAdapterInitFinished(IDDCX_ADAPTER adapterObject, const IDARG_IN_ADAPTER_INIT_FINISHED* pInArgs)
{
    auto* wrapper = WdfObjectGet_IndirectDeviceContextWrapper(adapterObject);
    if (NT_SUCCESS(pInArgs->AdapterInitStatus))
    {
        wrapper->pContext->FinishInit();
    }
    return STATUS_SUCCESS;
}

_Use_decl_annotations_
NTSTATUS SdAdapterCommitModes(IDDCX_ADAPTER adapterObject, const IDARG_IN_COMMITMODES* pInArgs)
{
    UNREFERENCED_PARAMETER(adapterObject);
    UNREFERENCED_PARAMETER(pInArgs);
    // Kein echter Ausgang – Moduswechsel erfordern keine Hardware-Aktion.
    return STATUS_SUCCESS;
}

_Use_decl_annotations_
NTSTATUS SdParseMonitorDescription(const IDARG_IN_PARSEMONITORDESCRIPTION* pInArgs, IDARG_OUT_PARSEMONITORDESCRIPTION* pOutArgs)
{
    int index = MonitorIndexFromEdid(static_cast<const BYTE*>(pInArgs->MonitorDescription.pData),
                                     pInArgs->MonitorDescription.DataSize);
    const MonitorConfig* cfg = index >= 0 ? ConfigForIndex(static_cast<UINT>(index)) : nullptr;
    if (!cfg) return STATUS_INVALID_PARAMETER;

    auto modes = ModesFor(*cfg);
    pOutArgs->MonitorModeBufferOutputCount = static_cast<UINT>(modes.size());

    if (pInArgs->MonitorModeBufferInputCount < modes.size())
    {
        // Erster Aufruf fragt nur die Anzahl ab.
        return pInArgs->MonitorModeBufferInputCount > 0 ? STATUS_BUFFER_TOO_SMALL : STATUS_SUCCESS;
    }

    for (size_t i = 0; i < modes.size(); ++i)
    {
        pInArgs->pMonitorModes[i] = CreateMonitorMode(modes[i].W, modes[i].H, modes[i].Hz);
    }
    pOutArgs->PreferredMonitorModeIdx = 0;
    return STATUS_SUCCESS;
}

_Use_decl_annotations_
NTSTATUS SdMonitorGetDefaultModes(IDDCX_MONITOR monitorObject, const IDARG_IN_GETDEFAULTDESCRIPTIONMODES* pInArgs, IDARG_OUT_GETDEFAULTDESCRIPTIONMODES* pOutArgs)
{
    // Wird nur ohne EDID aufgerufen; wir liefern immer eine EDID. Trotzdem korrekt implementiert.
    auto* wrapper = WdfObjectGet_IndirectMonitorContextWrapper(monitorObject);
    if (!wrapper || !wrapper->pContext) return STATUS_INVALID_PARAMETER;

    auto modes = ModesFor(wrapper->pContext->Config());
    pOutArgs->DefaultMonitorModeBufferOutputCount = static_cast<UINT>(modes.size());
    if (pInArgs->DefaultMonitorModeBufferInputCount < modes.size())
    {
        return pInArgs->DefaultMonitorModeBufferInputCount > 0 ? STATUS_BUFFER_TOO_SMALL : STATUS_SUCCESS;
    }
    for (size_t i = 0; i < modes.size(); ++i)
    {
        pInArgs->pDefaultMonitorModes[i] = CreateMonitorMode(modes[i].W, modes[i].H, modes[i].Hz);
    }
    pOutArgs->PreferredMonitorModeIdx = 0;
    return STATUS_SUCCESS;
}

_Use_decl_annotations_
NTSTATUS SdMonitorQueryModes(IDDCX_MONITOR monitorObject, const IDARG_IN_QUERYTARGETMODES* pInArgs, IDARG_OUT_QUERYTARGETMODES* pOutArgs)
{
    auto* wrapper = WdfObjectGet_IndirectMonitorContextWrapper(monitorObject);
    if (!wrapper || !wrapper->pContext) return STATUS_INVALID_PARAMETER;

    auto modes = ModesFor(wrapper->pContext->Config());
    pOutArgs->TargetModeBufferOutputCount = static_cast<UINT>(modes.size());
    if (pInArgs->TargetModeBufferInputCount >= modes.size())
    {
        for (size_t i = 0; i < modes.size(); ++i)
        {
            pInArgs->pTargetModes[i] = CreateTargetMode(modes[i].W, modes[i].H, modes[i].Hz);
        }
    }
    return STATUS_SUCCESS;
}

_Use_decl_annotations_
NTSTATUS SdMonitorAssignSwapChain(IDDCX_MONITOR monitorObject, const IDARG_IN_SETSWAPCHAIN* pInArgs)
{
    auto* wrapper = WdfObjectGet_IndirectMonitorContextWrapper(monitorObject);
    wrapper->pContext->AssignSwapChain(pInArgs->hSwapChain, pInArgs->RenderAdapterLuid, pInArgs->hNextSurfaceAvailable);
    return STATUS_SUCCESS;
}

_Use_decl_annotations_
NTSTATUS SdMonitorUnassignSwapChain(IDDCX_MONITOR monitorObject)
{
    auto* wrapper = WdfObjectGet_IndirectMonitorContextWrapper(monitorObject);
    wrapper->pContext->UnassignSwapChain();
    return STATUS_SUCCESS;
}
