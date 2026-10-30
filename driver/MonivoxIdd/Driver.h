// Monivox – Indirect Display Driver (IddCx 1.4, UMDF 2)
//
// Stellt N virtuelle Monitore bereit. Anzahl und Auflösung kommen aus der Registry:
//   HKLM\SOFTWARE\Monivox  ->  REG_SZ "Monitors" = "1920x2160@60;1920x2160@60"
// Die Monivox-App schreibt diesen Wert und startet das Gerät danach neu.
//
// Aufbau orientiert sich am offiziellen Microsoft "IddSampleDriver"
// (Windows-driver-samples, MIT-Lizenz).

#pragma once

#define NOMINMAX
#include <windows.h>
#include <bugcodes.h>
#include <wudfwdm.h>
#include <wdf.h>
#include <iddcx.h>

#include <dxgi1_5.h>
#include <d3d11_2.h>
#include <avrt.h>
#include <wrl.h>

#include <memory>
#include <vector>
#include <string>

namespace Monivox
{
    constexpr UINT kMaxMonitors = 8;

    struct MonitorConfig
    {
        DWORD Width = 1920;
        DWORD Height = 1080;
        DWORD RefreshHz = 60;
    };

    // Liest die Monitor-Konfiguration aus der Registry. Fällt auf 2x 1920x1080@60 zurück.
    std::vector<MonitorConfig> LoadMonitorConfig();

    // Parst "1920x2160@60;1280x720@144" – öffentlich für Tests.
    std::vector<MonitorConfig> ParseMonitorConfig(const std::wstring& value);

    // Erzeugt eine gültige 128-Byte-EDID mit eindeutigem Produktcode je Monitor-Index,
    // damit Windows Position/Skalierung pro virtuellem Monitor dauerhaft speichert.
    std::vector<BYTE> BuildEdid(UINT monitorIndex);

    // Liefert den Monitor-Index aus einer von BuildEdid erzeugten EDID, sonst -1.
    int MonitorIndexFromEdid(const BYTE* data, UINT size);

    // D3D11-Gerät auf dem von Windows vorgegebenen Render-Adapter.
    struct Direct3DDevice
    {
        explicit Direct3DDevice(LUID adapterLuid);
        HRESULT Init();

        LUID AdapterLuid;
        Microsoft::WRL::ComPtr<IDXGIFactory5> DxgiFactory;
        Microsoft::WRL::ComPtr<IDXGIAdapter1> Adapter;
        Microsoft::WRL::ComPtr<ID3D11Device> Device;
        Microsoft::WRL::ComPtr<ID3D11DeviceContext> DeviceContext;
    };

    // Holt Frames aus der IddCx-Swapchain und gibt sie zurück.
    // Prototyp: Die Frames werden nur freigegeben, das Bild holt sich der Compositor
    // über Windows.Graphics.Capture. Später (Roadmap Phase 5) wird hier das Frame in eine
    // gemeinsam genutzte Textur für den Compositor kopiert.
    class SwapChainProcessor
    {
    public:
        SwapChainProcessor(IDDCX_SWAPCHAIN hSwapChain, std::shared_ptr<Direct3DDevice> device, HANDLE newFrameEvent);
        ~SwapChainProcessor();

    private:
        static DWORD CALLBACK RunThread(LPVOID argument);
        void Run();
        void RunCore();

        IDDCX_SWAPCHAIN m_hSwapChain;
        std::shared_ptr<Direct3DDevice> m_Device;
        HANDLE m_hAvailableBufferEvent;
        Microsoft::WRL::Wrappers::Thread m_hThread;
        Microsoft::WRL::Wrappers::Event m_hTerminateEvent;
    };

    class IndirectDeviceContext
    {
    public:
        explicit IndirectDeviceContext(WDFDEVICE wdfDevice);
        void InitAdapter();
        void FinishInit();

        const std::vector<MonitorConfig>& Monitors() const { return m_Monitors; }

    private:
        WDFDEVICE m_WdfDevice;
        IDDCX_ADAPTER m_Adapter = nullptr;
        std::vector<MonitorConfig> m_Monitors;
    };

    class IndirectMonitorContext
    {
    public:
        IndirectMonitorContext(IDDCX_MONITOR monitor, UINT index, MonitorConfig config);
        ~IndirectMonitorContext();

        void AssignSwapChain(IDDCX_SWAPCHAIN swapChain, LUID renderAdapter, HANDLE newFrameEvent);
        void UnassignSwapChain();

        UINT Index() const { return m_Index; }
        const MonitorConfig& Config() const { return m_Config; }

    private:
        IDDCX_MONITOR m_Monitor;
        UINT m_Index;
        MonitorConfig m_Config;
        std::unique_ptr<SwapChainProcessor> m_ProcessingThread;
    };
}

struct IndirectDeviceContextWrapper
{
    Monivox::IndirectDeviceContext* pContext;
    void Cleanup()
    {
        delete pContext;
        pContext = nullptr;
    }
};

struct IndirectMonitorContextWrapper
{
    Monivox::IndirectMonitorContext* pContext;
    void Cleanup()
    {
        delete pContext;
        pContext = nullptr;
    }
};

WDF_DECLARE_CONTEXT_TYPE(IndirectDeviceContextWrapper);
WDF_DECLARE_CONTEXT_TYPE(IndirectMonitorContextWrapper);
