// Monivox Compositor
//
// Zeigt die Bilder mehrerer (virtueller) Monitore in einem randlosen Vollbildfenster
// auf einem physischen Monitor an – jeweils in einem festgelegten Rechteck.
//
// Aufruf:  MonivoxCompositor.exe <config-datei>
//
// Konfigurationsformat (UTF-8 oder ASCII, eine Angabe pro Zeile):
//   physical=\\.\DISPLAY1
//   view=\\.\DISPLAY3,0,0,1920,2160        (Quellmonitor, Ziel-X, Ziel-Y, Breite, Höhe)
//   view=\\.\DISPLAY4,1920,0,1920,2160     (Zielkoordinaten relativ zum physischen Monitor)
//   clipcursor=1                            (optional, Standard 1)
//
// Exit-Codes: 0 = normal beendet, 2 = Konfigurationsfehler, 3 = Displayänderung erkannt
// (Aufrufer soll Layout neu berechnen und neu starten), 4 = Grafikfehler.
//
// Not-Aus: Strg+Alt+Shift+Q beendet den Compositor und gibt die Maus frei.

#include <windows.h>
#include <shellapi.h>
#include <d3d11_4.h>
#include <dxgi1_3.h>
#include <d3dcompiler.h>
#include <wrl/client.h>

#include <winrt/Windows.Foundation.h>
#include <winrt/Windows.Foundation.Metadata.h>
#include <winrt/Windows.Graphics.h>
#include <winrt/Windows.Graphics.Capture.h>
#include <winrt/Windows.Graphics.DirectX.h>
#include <winrt/Windows.Graphics.DirectX.Direct3D11.h>
#include <windows.graphics.capture.interop.h>
#include <windows.graphics.directx.direct3d11.interop.h>

#include <algorithm>
#include <atomic>
#include <cstdio>
#include <fstream>
#include <memory>
#include <mutex>
#include <string>
#include <vector>

using Microsoft::WRL::ComPtr;
namespace wgc = winrt::Windows::Graphics::Capture;
namespace wgd = winrt::Windows::Graphics::DirectX;
namespace wgd3d = winrt::Windows::Graphics::DirectX::Direct3D11;

namespace
{
    constexpr int kHotkeyQuit = 1;
    constexpr UINT_PTR kTimerClip = 1;

    FILE* g_log = nullptr;

    void Log(const wchar_t* fmt, ...)
    {
        if (!g_log) return;
        SYSTEMTIME t;
        GetLocalTime(&t);
        fwprintf(g_log, L"[%02u:%02u:%02u.%03u] ", t.wHour, t.wMinute, t.wSecond, t.wMilliseconds);
        va_list args;
        va_start(args, fmt);
        vfwprintf(g_log, fmt, args);
        va_end(args);
        fwprintf(g_log, L"\n");
        fflush(g_log);
    }

    // -----------------------------------------------------------------------
    // Konfiguration
    // -----------------------------------------------------------------------

    struct ViewConfig
    {
        std::wstring Device;
        RECT Dest{};
    };

    struct Config
    {
        std::wstring Physical;
        std::vector<ViewConfig> Views;
        bool ClipCursor = true;
    };

    std::wstring Trim(std::wstring s)
    {
        const wchar_t* ws = L" \t\r\n\xFEFF";
        size_t b = s.find_first_not_of(ws);
        size_t e = s.find_last_not_of(ws);
        if (b == std::wstring::npos) return L"";
        return s.substr(b, e - b + 1);
    }

    bool LoadConfig(const std::wstring& path, Config& cfg, std::wstring& error)
    {
        std::ifstream file(path, std::ios::binary);
        if (!file)
        {
            error = L"Konfigurationsdatei nicht lesbar: " + path;
            return false;
        }
        std::string raw((std::istreambuf_iterator<char>(file)), std::istreambuf_iterator<char>());
        int len = MultiByteToWideChar(CP_UTF8, 0, raw.data(), static_cast<int>(raw.size()), nullptr, 0);
        std::wstring text(len, L'\0');
        MultiByteToWideChar(CP_UTF8, 0, raw.data(), static_cast<int>(raw.size()), text.data(), len);

        size_t pos = 0;
        while (pos <= text.size())
        {
            size_t nl = text.find(L'\n', pos);
            if (nl == std::wstring::npos) nl = text.size();
            std::wstring line = Trim(text.substr(pos, nl - pos));
            pos = nl + 1;
            if (line.empty() || line[0] == L'#') continue;

            size_t eq = line.find(L'=');
            if (eq == std::wstring::npos) continue;
            std::wstring key = Trim(line.substr(0, eq));
            std::wstring value = Trim(line.substr(eq + 1));

            if (key == L"physical")
            {
                cfg.Physical = value;
            }
            else if (key == L"clipcursor")
            {
                cfg.ClipCursor = value != L"0";
            }
            else if (key == L"view")
            {
                size_t comma = value.find(L',');
                if (comma == std::wstring::npos) continue;
                ViewConfig v;
                v.Device = Trim(value.substr(0, comma));
                int x = 0, y = 0, w = 0, h = 0;
                if (swscanf_s(value.c_str() + comma + 1, L"%d,%d,%d,%d", &x, &y, &w, &h) != 4 || w <= 0 || h <= 0)
                {
                    error = L"Ungültige view-Zeile: " + line;
                    return false;
                }
                v.Dest = { x, y, x + w, y + h };
                cfg.Views.push_back(v);
            }
        }

        if (cfg.Physical.empty()) { error = L"'physical=' fehlt"; return false; }
        if (cfg.Views.empty()) { error = L"Keine 'view=' Einträge"; return false; }
        return true;
    }

    // -----------------------------------------------------------------------
    // Monitore
    // -----------------------------------------------------------------------

    struct MonitorLookup
    {
        std::wstring Device;
        HMONITOR Handle = nullptr;
        RECT Rect{};
    };

    BOOL CALLBACK EnumMonitorProc(HMONITOR monitor, HDC, LPRECT, LPARAM param)
    {
        auto* lookup = reinterpret_cast<MonitorLookup*>(param);
        MONITORINFOEXW info{};
        info.cbSize = sizeof(info);
        if (GetMonitorInfoW(monitor, &info) && _wcsicmp(info.szDevice, lookup->Device.c_str()) == 0)
        {
            lookup->Handle = monitor;
            lookup->Rect = info.rcMonitor;
            return FALSE;
        }
        return TRUE;
    }

    bool FindMonitor(const std::wstring& device, HMONITOR& handle, RECT& rect)
    {
        MonitorLookup lookup{ device };
        EnumDisplayMonitors(nullptr, nullptr, EnumMonitorProc, reinterpret_cast<LPARAM>(&lookup));
        handle = lookup.Handle;
        rect = lookup.Rect;
        return handle != nullptr;
    }

    // -----------------------------------------------------------------------
    // Shader: Vollbild-Dreieck, skaliert per Viewport in das Zielrechteck
    // -----------------------------------------------------------------------

    const char kShaderSource[] = R"(
Texture2D tex : register(t0);
SamplerState samp : register(s0);
struct VSOut { float4 pos : SV_POSITION; float2 uv : TEXCOORD0; };
VSOut VSMain(uint id : SV_VertexID)
{
    VSOut o;
    o.uv = float2((id << 1) & 2, id & 2);
    o.pos = float4(o.uv * float2(2, -2) + float2(-1, 1), 0, 1);
    return o;
}
float4 PSMain(VSOut i) : SV_TARGET { return float4(tex.Sample(samp, i.uv).rgb, 1); }
)";

    // -----------------------------------------------------------------------
    // Ansicht = ein Quellmonitor, der in ein Zielrechteck gezeichnet wird
    // -----------------------------------------------------------------------

    struct View
    {
        ViewConfig Cfg;
        HMONITOR Monitor = nullptr;
        RECT MonitorRect{};

        wgc::GraphicsCaptureItem Item{ nullptr };
        wgc::Direct3D11CaptureFramePool Pool{ nullptr };
        wgc::GraphicsCaptureSession Session{ nullptr };
        winrt::event_token FrameToken{};
        winrt::Windows::Graphics::SizeInt32 PoolSize{};

        std::mutex Lock;
        ComPtr<ID3D11Texture2D> Texture;
        ComPtr<ID3D11ShaderResourceView> Srv;
        UINT TexWidth = 0, TexHeight = 0;
    };

    class Compositor
    {
    public:
        explicit Compositor(Config cfg) : m_Cfg(std::move(cfg)) {}
        ~Compositor() { Shutdown(); }

        int Run(HINSTANCE instance);

    private:
        bool InitD3D();
        bool InitWindow(HINSTANCE instance);
        bool InitViews();
        void ComputeClipRect();
        void OnFrame(View& view, wgc::Direct3D11CaptureFramePool const& pool);
        void Render();
        void ApplyClip();
        void Shutdown();

        static LRESULT CALLBACK WndProc(HWND, UINT, WPARAM, LPARAM);

        Config m_Cfg;
        HWND m_Hwnd = nullptr;
        RECT m_PhysicalRect{};
        RECT m_ClipRect{};
        int m_ExitCode = 0;

        ComPtr<ID3D11Device> m_Device;
        ComPtr<ID3D11DeviceContext> m_Context;
        ComPtr<IDXGISwapChain1> m_SwapChain;
        ComPtr<ID3D11RenderTargetView> m_Rtv;
        ComPtr<ID3D11VertexShader> m_Vs;
        ComPtr<ID3D11PixelShader> m_Ps;
        ComPtr<ID3D11SamplerState> m_Sampler;
        wgd3d::IDirect3DDevice m_WinrtDevice{ nullptr };

        std::vector<std::unique_ptr<View>> m_Views;
        HANDLE m_FrameEvent = nullptr;
        std::atomic<bool> m_Dirty{ true };
        std::mutex m_ContextLock; // Immediate Context ist nicht threadsicher
    };

    Compositor* g_instance = nullptr;

    bool Compositor::InitD3D()
    {
        UINT flags = D3D11_CREATE_DEVICE_BGRA_SUPPORT;
        D3D_FEATURE_LEVEL levels[] = { D3D_FEATURE_LEVEL_11_1, D3D_FEATURE_LEVEL_11_0 };
        HRESULT hr = D3D11CreateDevice(nullptr, D3D_DRIVER_TYPE_HARDWARE, nullptr, flags, levels,
                                       ARRAYSIZE(levels), D3D11_SDK_VERSION, &m_Device, nullptr, &m_Context);
        if (FAILED(hr))
        {
            Log(L"D3D11CreateDevice fehlgeschlagen: 0x%08X", hr);
            return false;
        }

        ComPtr<ID3D11Multithread> mt;
        if (SUCCEEDED(m_Context.As(&mt))) mt->SetMultithreadProtected(TRUE);

        ComPtr<IDXGIDevice> dxgiDevice;
        m_Device.As(&dxgiDevice);
        winrt::com_ptr<::IInspectable> inspectable;
        hr = CreateDirect3D11DeviceFromDXGIDevice(dxgiDevice.Get(), inspectable.put());
        if (FAILED(hr)) return false;
        m_WinrtDevice = inspectable.as<wgd3d::IDirect3DDevice>();

        ComPtr<ID3DBlob> vsBlob, psBlob, err;
        hr = D3DCompile(kShaderSource, sizeof(kShaderSource) - 1, "blit", nullptr, nullptr, "VSMain", "vs_5_0", 0, 0, &vsBlob, &err);
        if (FAILED(hr)) { Log(L"VS-Kompilierung fehlgeschlagen"); return false; }
        hr = D3DCompile(kShaderSource, sizeof(kShaderSource) - 1, "blit", nullptr, nullptr, "PSMain", "ps_5_0", 0, 0, &psBlob, &err);
        if (FAILED(hr)) { Log(L"PS-Kompilierung fehlgeschlagen"); return false; }
        m_Device->CreateVertexShader(vsBlob->GetBufferPointer(), vsBlob->GetBufferSize(), nullptr, &m_Vs);
        m_Device->CreatePixelShader(psBlob->GetBufferPointer(), psBlob->GetBufferSize(), nullptr, &m_Ps);

        D3D11_SAMPLER_DESC sd{};
        sd.Filter = D3D11_FILTER_MIN_MAG_MIP_LINEAR;
        sd.AddressU = sd.AddressV = sd.AddressW = D3D11_TEXTURE_ADDRESS_CLAMP;
        sd.MaxLOD = D3D11_FLOAT32_MAX;
        m_Device->CreateSamplerState(&sd, &m_Sampler);
        return true;
    }

    bool Compositor::InitWindow(HINSTANCE instance)
    {
        HMONITOR physical = nullptr;
        if (!FindMonitor(m_Cfg.Physical, physical, m_PhysicalRect))
        {
            Log(L"Physischer Monitor %s nicht gefunden", m_Cfg.Physical.c_str());
            return false;
        }

        WNDCLASSEXW wc{};
        wc.cbSize = sizeof(wc);
        wc.lpfnWndProc = WndProc;
        wc.hInstance = instance;
        wc.hCursor = LoadCursor(nullptr, IDC_ARROW);
        wc.lpszClassName = L"MonivoxCompositor";
        RegisterClassExW(&wc);

        const int w = m_PhysicalRect.right - m_PhysicalRect.left;
        const int h = m_PhysicalRect.bottom - m_PhysicalRect.top;
        m_Hwnd = CreateWindowExW(WS_EX_TOPMOST | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE | WS_EX_NOREDIRECTIONBITMAP,
                                 wc.lpszClassName, L"Monivox", WS_POPUP,
                                 m_PhysicalRect.left, m_PhysicalRect.top, w, h,
                                 nullptr, nullptr, instance, nullptr);
        if (!m_Hwnd) return false;

        // Das Compositor-Fenster soll nie in Aufnahmen auftauchen (verhindert Rückkopplung).
        SetWindowDisplayAffinity(m_Hwnd, WDA_EXCLUDEFROMCAPTURE);

        ComPtr<IDXGIDevice> dxgiDevice;
        m_Device.As(&dxgiDevice);
        ComPtr<IDXGIAdapter> adapter;
        dxgiDevice->GetAdapter(&adapter);
        ComPtr<IDXGIFactory2> factory;
        adapter->GetParent(IID_PPV_ARGS(&factory));

        DXGI_SWAP_CHAIN_DESC1 desc{};
        desc.Width = w;
        desc.Height = h;
        desc.Format = DXGI_FORMAT_B8G8R8A8_UNORM;
        desc.SampleDesc.Count = 1;
        desc.BufferUsage = DXGI_USAGE_RENDER_TARGET_OUTPUT;
        desc.BufferCount = 2;
        desc.SwapEffect = DXGI_SWAP_EFFECT_FLIP_DISCARD;
        desc.AlphaMode = DXGI_ALPHA_MODE_IGNORE;
        HRESULT hr = factory->CreateSwapChainForHwnd(m_Device.Get(), m_Hwnd, &desc, nullptr, nullptr, &m_SwapChain);
        if (FAILED(hr))
        {
            Log(L"CreateSwapChainForHwnd fehlgeschlagen: 0x%08X", hr);
            return false;
        }
        factory->MakeWindowAssociation(m_Hwnd, DXGI_MWA_NO_ALT_ENTER);

        ComPtr<ID3D11Texture2D> backBuffer;
        m_SwapChain->GetBuffer(0, IID_PPV_ARGS(&backBuffer));
        m_Device->CreateRenderTargetView(backBuffer.Get(), nullptr, &m_Rtv);

        ShowWindow(m_Hwnd, SW_SHOWNOACTIVATE);
        RegisterHotKey(m_Hwnd, kHotkeyQuit, MOD_CONTROL | MOD_ALT | MOD_SHIFT | MOD_NOREPEAT, 'Q');
        SetTimer(m_Hwnd, kTimerClip, 250, nullptr);
        return true;
    }

    bool Compositor::InitViews()
    {
        // Rahmenlose Aufnahme anfordern (Windows 11). Für nicht paketierte Apps wird sie
        // automatisch gewährt; auf älteren Systemen schlägt der Aufruf fehl -> gelber Rahmen
        // wäre sichtbar, wird aber vom Compositor-Fenster nicht mit aufgenommen.
        try
        {
            if (winrt::Windows::Foundation::Metadata::ApiInformation::IsTypePresent(L"Windows.Graphics.Capture.GraphicsCaptureAccess"))
            {
                wgc::GraphicsCaptureAccess::RequestAccessAsync(wgc::GraphicsCaptureAccessKind::Borderless).get();
            }
        }
        catch (...) {}

        auto interop = winrt::get_activation_factory<wgc::GraphicsCaptureItem, IGraphicsCaptureItemInterop>();

        for (const auto& vc : m_Cfg.Views)
        {
            auto view = std::make_unique<View>();
            view->Cfg = vc;
            if (!FindMonitor(vc.Device, view->Monitor, view->MonitorRect))
            {
                Log(L"Quellmonitor %s nicht gefunden", vc.Device.c_str());
                return false;
            }

            HRESULT hr = interop->CreateForMonitor(view->Monitor, winrt::guid_of<wgc::GraphicsCaptureItem>(),
                                                   winrt::put_abi(view->Item));
            if (FAILED(hr))
            {
                Log(L"CreateForMonitor(%s) fehlgeschlagen: 0x%08X", vc.Device.c_str(), hr);
                return false;
            }

            view->PoolSize = view->Item.Size();
            view->Pool = wgc::Direct3D11CaptureFramePool::CreateFreeThreaded(
                m_WinrtDevice, wgd::DirectXPixelFormat::B8G8R8A8UIntNormalized, 2, view->PoolSize);

            View* raw = view.get();
            view->FrameToken = view->Pool.FrameArrived([this, raw](auto const& pool, auto const&) { OnFrame(*raw, pool); });

            view->Session = view->Pool.CreateCaptureSession(view->Item);
            view->Session.IsCursorCaptureEnabled(true);
            try { view->Session.IsBorderRequired(false); } catch (...) {}
            // TODO(Roadmap): Session.MinUpdateInterval (ab Windows 11 24H2 / SDK 26100) setzen,
            // damit die Aufnahme nicht auf ~60 fps begrenzt bleibt.
            view->Session.StartCapture();


            Log(L"Ansicht %s -> (%ld,%ld %ldx%ld)", vc.Device.c_str(), vc.Dest.left, vc.Dest.top,
                vc.Dest.right - vc.Dest.left, vc.Dest.bottom - vc.Dest.top);
            m_Views.push_back(std::move(view));
        }
        ComputeClipRect();
        return true;
    }

    // Maus-Bereich = Vereinigung aller Monitore AUSSER dem geparkten physischen Monitor.
    // Monivox parkt diesen ganz rechts außen, daher schließt die Vereinigung ihn vollständig aus,
    // während andere (nicht aufgeteilte) Monitore weiter normal erreichbar bleiben.
    struct ClipCollect { RECT Physical; RECT Union; bool Any; };

    BOOL CALLBACK CollectClipProc(HMONITOR, HDC, LPRECT rect, LPARAM param)
    {
        auto* c = reinterpret_cast<ClipCollect*>(param);
        if (EqualRect(rect, &c->Physical)) return TRUE;
        if (!c->Any) { c->Union = *rect; c->Any = true; }
        else UnionRect(&c->Union, &c->Union, rect);
        return TRUE;
    }

    void Compositor::ComputeClipRect()
    {
        ClipCollect c{ m_PhysicalRect, {}, false };
        EnumDisplayMonitors(nullptr, nullptr, CollectClipProc, reinterpret_cast<LPARAM>(&c));
        m_ClipRect = c.Union;
        RECT overlap{};
        if (!c.Any || IntersectRect(&overlap, &m_ClipRect, &m_PhysicalRect))
        {
            // Unerwartete Anordnung (physischer Monitor nicht außen) – lieber gar nicht begrenzen,
            // als die Maus auf falsche Bereiche einzusperren.
            Log(L"Physischer Monitor liegt nicht außen – Mausbegrenzung deaktiviert");
            m_Cfg.ClipCursor = false;
        }
    }

    void Compositor::OnFrame(View& view, wgc::Direct3D11CaptureFramePool const& pool)
    {
        auto frame = pool.TryGetNextFrame();
        if (!frame) return;

        auto size = frame.ContentSize();
        auto access = frame.Surface().as<::Windows::Graphics::DirectX::Direct3D11::IDirect3DDxgiInterfaceAccess>();
        ComPtr<ID3D11Texture2D> src;
        if (FAILED(access->GetInterface(IID_PPV_ARGS(&src)))) return;

        D3D11_TEXTURE2D_DESC srcDesc{};
        src->GetDesc(&srcDesc);
        UINT w = std::min<UINT>(static_cast<UINT>(size.Width), srcDesc.Width);
        UINT h = std::min<UINT>(static_cast<UINT>(size.Height), srcDesc.Height);
        if (w == 0 || h == 0) return;

        {
            // Reihenfolge immer: Context-Lock, dann View-Lock (wie in Render) – verhindert Deadlocks.
            std::lock_guard<std::mutex> ctx(m_ContextLock);
            std::lock_guard<std::mutex> lock(view.Lock);
            if (!view.Texture || view.TexWidth != w || view.TexHeight != h)
            {
                D3D11_TEXTURE2D_DESC desc{};
                desc.Width = w;
                desc.Height = h;
                desc.MipLevels = 1;
                desc.ArraySize = 1;
                desc.Format = DXGI_FORMAT_B8G8R8A8_UNORM;
                desc.SampleDesc.Count = 1;
                desc.Usage = D3D11_USAGE_DEFAULT;
                desc.BindFlags = D3D11_BIND_SHADER_RESOURCE;
                view.Texture.Reset();
                view.Srv.Reset();
                if (FAILED(m_Device->CreateTexture2D(&desc, nullptr, &view.Texture))) return;
                m_Device->CreateShaderResourceView(view.Texture.Get(), nullptr, &view.Srv);
                view.TexWidth = w;
                view.TexHeight = h;
            }

            D3D11_BOX box{ 0, 0, 0, w, h, 1 };
            m_Context->CopySubresourceRegion(view.Texture.Get(), 0, 0, 0, 0, src.Get(), 0, &box);
        }

        // Auflösung des Quellmonitors hat sich geändert -> Pool anpassen
        if (size.Width != view.PoolSize.Width || size.Height != view.PoolSize.Height)
        {
            view.PoolSize = size;
            pool.Recreate(m_WinrtDevice, wgd::DirectXPixelFormat::B8G8R8A8UIntNormalized, 2, size);
        }

        m_Dirty = true;
        SetEvent(m_FrameEvent);
    }

    void Compositor::Render()
    {
        std::lock_guard<std::mutex> ctx(m_ContextLock);

        const float black[4] = { 0, 0, 0, 1 };
        m_Context->OMSetRenderTargets(1, m_Rtv.GetAddressOf(), nullptr);
        m_Context->ClearRenderTargetView(m_Rtv.Get(), black);
        m_Context->IASetPrimitiveTopology(D3D11_PRIMITIVE_TOPOLOGY_TRIANGLELIST);
        m_Context->IASetInputLayout(nullptr);
        m_Context->VSSetShader(m_Vs.Get(), nullptr, 0);
        m_Context->PSSetShader(m_Ps.Get(), nullptr, 0);
        m_Context->PSSetSamplers(0, 1, m_Sampler.GetAddressOf());

        for (auto& view : m_Views)
        {
            std::lock_guard<std::mutex> lock(view->Lock);
            if (!view->Srv) continue;
            const RECT& d = view->Cfg.Dest;
            D3D11_VIEWPORT vp{ static_cast<float>(d.left), static_cast<float>(d.top),
                               static_cast<float>(d.right - d.left), static_cast<float>(d.bottom - d.top), 0.f, 1.f };
            m_Context->RSSetViewports(1, &vp);
            m_Context->PSSetShaderResources(0, 1, view->Srv.GetAddressOf());
            m_Context->Draw(3, 0);
        }

        ID3D11ShaderResourceView* nullSrv = nullptr;
        m_Context->PSSetShaderResources(0, 1, &nullSrv);

        HRESULT hr = m_SwapChain->Present(1, 0);
        if (hr == DXGI_ERROR_DEVICE_REMOVED || hr == DXGI_ERROR_DEVICE_RESET)
        {
            Log(L"Grafikgerät verloren (0x%08X) – Neustart nötig", hr);
            m_ExitCode = 4;
            PostQuitMessage(4);
        }
    }

    void Compositor::ApplyClip()
    {
        if (!m_Cfg.ClipCursor || m_Views.empty()) return;
        RECT current{};
        GetClipCursor(&current);
        if (!EqualRect(&current, &m_ClipRect)) ClipCursor(&m_ClipRect);
    }

    void Compositor::Shutdown()
    {
        for (auto& view : m_Views)
        {
            if (view->Pool) view->Pool.FrameArrived(view->FrameToken);
            if (view->Session) view->Session.Close();
            if (view->Pool) view->Pool.Close();
        }
        m_Views.clear();
        if (m_Cfg.ClipCursor) ClipCursor(nullptr);
        if (m_Hwnd)
        {
            UnregisterHotKey(m_Hwnd, kHotkeyQuit);
            DestroyWindow(m_Hwnd);
            m_Hwnd = nullptr;
        }
        if (m_FrameEvent)
        {
            CloseHandle(m_FrameEvent);
            m_FrameEvent = nullptr;
        }
    }

    LRESULT CALLBACK Compositor::WndProc(HWND hwnd, UINT msg, WPARAM wParam, LPARAM lParam)
    {
        switch (msg)
        {
        case WM_HOTKEY:
            if (wParam == kHotkeyQuit) PostQuitMessage(0);
            return 0;
        case WM_TIMER:
            if (wParam == kTimerClip && g_instance) g_instance->ApplyClip();
            return 0;
        case WM_DISPLAYCHANGE:
            Log(L"Displayänderung erkannt – beende mit Code 3");
            if (g_instance) g_instance->m_ExitCode = 3;
            PostQuitMessage(3);
            return 0;
        case WM_MOUSEACTIVATE:
            return MA_NOACTIVATE;
        case WM_NCHITTEST:
            return HTTRANSPARENT;
        default:
            return DefWindowProcW(hwnd, msg, wParam, lParam);
        }
    }

    int Compositor::Run(HINSTANCE instance)
    {
        g_instance = this;
        m_FrameEvent = CreateEventW(nullptr, FALSE, FALSE, nullptr);

        if (!InitD3D()) return 4;
        if (!InitWindow(instance)) return 2;
        if (!InitViews()) return 2;
        ApplyClip();
        Log(L"Compositor läuft mit %zu Ansichten", m_Views.size());

        MSG msg{};
        for (;;)
        {
            DWORD wait = MsgWaitForMultipleObjects(1, &m_FrameEvent, FALSE, 100, QS_ALLINPUT);
            while (PeekMessageW(&msg, nullptr, 0, 0, PM_REMOVE))
            {
                if (msg.message == WM_QUIT) return m_ExitCode != 0 ? m_ExitCode : static_cast<int>(msg.wParam);
                TranslateMessage(&msg);
                DispatchMessageW(&msg);
            }
            if (wait == WAIT_OBJECT_0 || m_Dirty.exchange(false))
            {
                Render();
            }
        }
    }
}

int WINAPI wWinMain(HINSTANCE instance, HINSTANCE, PWSTR, int)
{
    SetProcessDpiAwarenessContext(DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2);
    winrt::init_apartment(winrt::apartment_type::multi_threaded);

    int argc = 0;
    LPWSTR* argv = CommandLineToArgvW(GetCommandLineW(), &argc);
    if (argc < 2)
    {
        MessageBoxW(nullptr, L"Aufruf: MonivoxCompositor.exe <config-datei>", L"Monivox", MB_ICONINFORMATION);
        return 2;
    }

    std::wstring configPath = argv[1];
    std::wstring logPath = configPath + L".log";
    _wfopen_s(&g_log, logPath.c_str(), L"w, ccs=UTF-8");

    Config cfg;
    std::wstring error;
    if (!LoadConfig(configPath, cfg, error))
    {
        Log(L"Konfigurationsfehler: %s", error.c_str());
        return 2;
    }

    int code = 0;
    try
    {
        Compositor compositor(std::move(cfg));
        code = compositor.Run(instance);
    }
    catch (winrt::hresult_error const& e)
    {
        Log(L"WinRT-Fehler 0x%08X: %s", static_cast<unsigned>(e.code()), e.message().c_str());
        code = 4;
    }
    ClipCursor(nullptr);
    Log(L"Beendet mit Code %d", code);
    if (g_log) fclose(g_log);
    LocalFree(argv);
    return code;
}
