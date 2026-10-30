// MONIVOX Compositor
//
// Zeigt die Bilder virtueller Monitore auf einem physischen Monitor an – jeder Bereich in einem eigenen,
// randlosen Fenster. Optional bleibt ein "Direktbereich" frei: dort sieht man den echten Monitor
// (keine Zusatzlatenz, G-Sync/FreeSync und DRM-Videos funktionieren).
//
// Aufruf:  MonivoxCompositor.exe <config-datei>
//
// Konfiguration (eine Angabe pro Zeile, Koordinaten der Bereiche relativ zum physischen Monitor):
//   physical=\\.\DISPLAY1
//   view=\\.\DISPLAY3,0,0,1920,2160,Discord     Quellmonitor, X, Y, Breite, Höhe, Name
//   direct=1920,0,1920,2160,Spiel               optional: Direktbereich
//   panel=0,0                                   optional: Desktop-Ursprung des Monitorbilds (für die Maus)
//   frames=1                                    farbige Rahmen anfangs zeigen
//
// Maus-Brücke: Liegt die Maus auf einem verdeckten Teil des physischen Monitors, wird sie an die passende
// Stelle des virtuellen Monitors gesetzt – und umgekehrt an Bereichsgrenzen. Dafür werden nur Mausbewegungen
// passiv gelesen (Raw Input) und SetCursorPos benutzt: keine Hooks, nichts wird in andere Prozesse eingeschleust.
// Begrenzt ein Spiel die Maus selbst (ClipCursor), hält sich die Brücke heraus.
//
// Exit-Codes: 0 = normal beendet, 2 = Konfigurationsfehler, 3 = Displayänderung (neu aufbauen), 4 = Grafikfehler
// Tasten:  Strg+Alt+Shift+Q = Not-Aus,  Strg+Alt+Shift+F = Rahmen ein/aus

#include <windows.h>
#include <shellapi.h>
#include <d3d11_4.h>
#include <dxgi1_6.h>
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
#include <cmath>
#include <cstdio>
#include <fstream>
#include <memory>
#include <mutex>
#include <optional>
#include <string>
#include <thread>
#include <vector>

using Microsoft::WRL::ComPtr;
namespace wgc = winrt::Windows::Graphics::Capture;
namespace wgd = winrt::Windows::Graphics::DirectX;
namespace wgd3d = winrt::Windows::Graphics::DirectX::Direct3D11;

namespace
{
    constexpr int kHotkeyQuit = 1;
    constexpr int kHotkeyFrames = 2;
    constexpr UINT_PTR kTimerHousekeeping = 1;
    const wchar_t* kToggleFramesEvent = L"Global\\MONIVOX-ToggleFrames";

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

    // =======================================================================
    // Konfiguration
    // =======================================================================

    struct Area
    {
        RECT Dest{};          // relativ zum physischen Monitor
        std::wstring Name;
    };

    struct ViewConfig : Area
    {
        std::wstring Device;
        bool HideTaskbar = false;
    };

    struct Config
    {
        std::wstring Physical;
        std::vector<ViewConfig> Views;
        std::optional<Area> Direct;
        std::optional<POINT> Panel;
        bool Frames = false;
    };

    std::wstring Trim(std::wstring s)
    {
        const wchar_t* ws = L" \t\r\n\xFEFF";
        size_t b = s.find_first_not_of(ws);
        size_t e = s.find_last_not_of(ws);
        if (b == std::wstring::npos) return L"";
        return s.substr(b, e - b + 1);
    }

    // "x,y,w,h,Name mit, Kommas" -> Rechteck + Name
    bool ParseArea(const std::wstring& text, Area& area)
    {
        int v[4] = {};
        size_t pos = 0;
        for (int i = 0; i < 4; ++i)
        {
            size_t comma = text.find(L',', pos);
            std::wstring part = text.substr(pos, comma == std::wstring::npos ? std::wstring::npos : comma - pos);
            v[i] = _wtoi(part.c_str());
            if (comma == std::wstring::npos)
            {
                if (i < 3) return false;
                pos = text.size();
            }
            else pos = comma + 1;
        }
        if (v[2] <= 0 || v[3] <= 0) return false;
        area.Dest = { v[0], v[1], v[0] + v[2], v[1] + v[3] };
        area.Name = pos < text.size() ? Trim(text.substr(pos)) : L"";
        return true;
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

            if (key == L"physical") cfg.Physical = value;
            else if (key == L"hidetaskbar")
            {
                // Indizes der view-Zeilen, deren Taskleiste ausgeblendet wird, z. B. "0,2"
                size_t p = 0;
                while (p < value.size())
                {
                    size_t c = value.find(L',', p);
                    int idx = _wtoi(value.substr(p, c == std::wstring::npos ? std::wstring::npos : c - p).c_str());
                    if (idx >= 0 && idx < static_cast<int>(cfg.Views.size())) cfg.Views[idx].HideTaskbar = true;
                    if (c == std::wstring::npos) break;
                    p = c + 1;
                }
            }
            else if (key == L"frames") cfg.Frames = value != L"0";
            else if (key == L"panel")
            {
                POINT p{};
                if (swscanf_s(value.c_str(), L"%ld,%ld", &p.x, &p.y) == 2) cfg.Panel = p;
            }
            else if (key == L"direct")
            {
                Area a;
                if (!ParseArea(value, a)) { error = L"Ungültige direct-Zeile: " + line; return false; }
                cfg.Direct = a;
            }
            else if (key == L"view")
            {
                size_t comma = value.find(L',');
                if (comma == std::wstring::npos) continue;
                ViewConfig v;
                v.Device = Trim(value.substr(0, comma));
                if (!ParseArea(value.substr(comma + 1), v)) { error = L"Ungültige view-Zeile: " + line; return false; }
                cfg.Views.push_back(v);
            }
        }

        if (cfg.Physical.empty()) { error = L"'physical=' fehlt"; return false; }
        if (cfg.Views.empty() && !cfg.Direct) { error = L"Keine Bereiche"; return false; }
        return true;
    }

    // =======================================================================
    // Monitore
    // =======================================================================

    struct MonitorLookup
    {
        std::wstring Device;
        HMONITOR Handle = nullptr;
        RECT Rect{};
        RECT Work{};
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
            lookup->Work = info.rcWork;
            return FALSE;
        }
        return TRUE;
    }

    bool FindMonitor(const std::wstring& device, MonitorLookup& out)
    {
        out.Device = device;
        EnumDisplayMonitors(nullptr, nullptr, EnumMonitorProc, reinterpret_cast<LPARAM>(&out));
        return out.Handle != nullptr;
    }

    RECT Offset(RECT r, LONG dx, LONG dy) { return { r.left + dx, r.top + dy, r.right + dx, r.bottom + dy }; }
    bool Contains(const RECT& r, POINT p) { return p.x >= r.left && p.x < r.right && p.y >= r.top && p.y < r.bottom; }
    LONG W(const RECT& r) { return r.right - r.left; }
    LONG H(const RECT& r) { return r.bottom - r.top; }

    // =======================================================================
    // Shader: Vollbild-Dreieck
    // =======================================================================

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

    // =======================================================================
    // Ansicht = ein virtueller Monitor, angezeigt in einem eigenen Fenster
    // =======================================================================

    struct View
    {
        ViewConfig Cfg;
        MonitorLookup Source;
        RECT Screen{};           // Zielrechteck in Desktop-Koordinaten
        HWND Hwnd = nullptr;
        ComPtr<IDXGISwapChain1> SwapChain;
        ComPtr<ID3D11RenderTargetView> Rtv;

        wgc::GraphicsCaptureItem Item{ nullptr };
        wgc::Direct3D11CaptureFramePool Pool{ nullptr };
        wgc::GraphicsCaptureSession Session{ nullptr };
        winrt::event_token FrameToken{};
        winrt::Windows::Graphics::SizeInt32 PoolSize{};

        // Desktop Duplication (bevorzugt): liefert jedes Bild, das Windows für den Monitor zusammensetzt
        ComPtr<IDXGIOutput1> DupOutput;
        ComPtr<IDXGIOutputDuplication> Dup;
        std::thread DupThread;
        std::atomic<bool> StopDup{ false };
        bool UsesDda = false;

        std::mutex Lock;
        ComPtr<ID3D11Texture2D> Texture;
        ComPtr<ID3D11ShaderResourceView> Srv;
        UINT TexWidth = 0, TexHeight = 0;
        std::atomic<bool> Dirty{ false };
        ULONGLONG LastFrameMs = 0;
        std::atomic<unsigned> MaxGapMs{ 0 };
    };

    enum class AreaKind { Other, Virtual, Direct, Covered };

    struct Location
    {
        AreaKind Kind = AreaKind::Other;
        int View = -1;
    };

    class Compositor
    {
    public:
        explicit Compositor(Config cfg) : m_Cfg(std::move(cfg)) {}
        ~Compositor() { Shutdown(); }
        int Run(HINSTANCE instance);

    private:
        bool InitD3D();
        bool InitWindows(HINSTANCE instance);
        bool InitView(View& view, IGraphicsCaptureItemInterop* interop);
        void OnFrame(View& view, wgc::Direct3D11CaptureFramePool const& pool);
        bool StartDda(View& view);
        void DdaLoop(View& view);
        void StoreFrame(View& view, ID3D11Texture2D* src, UINT w, UINT h);
        void RenderDirty();
        void Shutdown();

        // Rahmen
        void SetFrames(bool on);
        void DrawFrames();

        // Arbeitsbereich (Direktbereich)
        void ApplyWorkArea();

        // Maus-Brücke
        void OnRawMouse(LONG dx, LONG dy, bool absolute);
        Location Locate(POINT p) const;
        std::optional<POINT> LayoutOf(const Location& loc, POINT p) const;
        std::optional<POINT> ToDesktop(POINT layout) const;
        bool ForeignClipActive() const;
        void Teleport(POINT p);

        static LRESULT CALLBACK WndProc(HWND, UINT, WPARAM, LPARAM);

        Config m_Cfg;
        MonitorLookup m_Physical;
        RECT m_DirectScreen{};
        RECT m_OriginalWork{};
        bool m_WorkAreaSet = false;
        POINT m_PanelOrigin{};
        bool m_PanelKnown = false;
        bool m_Mirrored = true;
        int m_ExitCode = 0;

        HWND m_Overlay = nullptr;   // Rahmen + Empfänger für Raw Input
        HWND m_Backdrop = nullptr;  // schwarzer Hintergrund (nur ohne Direktbereich)
        bool m_FramesOn = false;

        ComPtr<ID3D11Device> m_Device;
        ComPtr<ID3D11DeviceContext> m_Context;
        ComPtr<ID3D11VertexShader> m_Vs;
        ComPtr<ID3D11PixelShader> m_Ps;
        ComPtr<ID3D11SamplerState> m_Sampler;
        wgd3d::IDirect3DDevice m_WinrtDevice{ nullptr };

        std::vector<std::unique_ptr<View>> m_Views;
        HANDLE m_FrameEvent = nullptr;
        HANDLE m_ToggleEvent = nullptr;
        std::mutex m_ContextLock;

        POINT m_LastPos{};
        Location m_LastLoc;
        bool m_UseRawInput = false;

        // Taskleisten auf virtuellen Bereichen ausblenden
        std::vector<HWND> m_HiddenTaskbars;
        std::vector<HMONITOR> m_WorkFixed;
        void UpdateTaskbars();
        void RestoreTaskbars();
        int m_WorkAreaResets = 0;

        // Leistungsmessung (alle 10 s ins Protokoll)
        ULONGLONG m_StatsStart = 0;
        std::atomic<unsigned> m_StatFrames{ 0 };
        unsigned m_StatPresents = 0;
        double m_StatRenderMs = 0;
        void LogStats();
    };

    Compositor* g_instance = nullptr;

    // ------------------------------------------------------------------- D3D

    bool Compositor::InitD3D()
    {
        // Grafikkarte wählen, an der der physische Monitor hängt (vermeidet teure Kopien zwischen GPUs)
        ComPtr<IDXGIAdapter1> chosen;
        ComPtr<IDXGIFactory1> factory;
        if (SUCCEEDED(CreateDXGIFactory1(IID_PPV_ARGS(&factory))))
        {
            ComPtr<IDXGIAdapter1> adapter;
            for (UINT a = 0; !chosen && factory->EnumAdapters1(a, &adapter) != DXGI_ERROR_NOT_FOUND; ++a)
            {
                ComPtr<IDXGIOutput> output;
                for (UINT o = 0; adapter->EnumOutputs(o, &output) != DXGI_ERROR_NOT_FOUND; ++o)
                {
                    DXGI_OUTPUT_DESC od{};
                    if (SUCCEEDED(output->GetDesc(&od)) && _wcsicmp(od.DeviceName, m_Cfg.Physical.c_str()) == 0)
                    {
                        chosen = adapter;
                        DXGI_ADAPTER_DESC1 ad{};
                        adapter->GetDesc1(&ad);
                        Log(L"Grafikkarte: %s", ad.Description);
                        break;
                    }
                }
            }
        }

        D3D_FEATURE_LEVEL levels[] = { D3D_FEATURE_LEVEL_11_1, D3D_FEATURE_LEVEL_11_0 };
        HRESULT hr = D3D11CreateDevice(chosen.Get(), chosen ? D3D_DRIVER_TYPE_UNKNOWN : D3D_DRIVER_TYPE_HARDWARE, nullptr,
                                       D3D11_CREATE_DEVICE_BGRA_SUPPORT, levels, ARRAYSIZE(levels), D3D11_SDK_VERSION,
                                       &m_Device, nullptr, &m_Context);
        if (FAILED(hr)) { Log(L"D3D11CreateDevice: 0x%08X", hr); return false; }

        ComPtr<ID3D11Multithread> mt;
        if (SUCCEEDED(m_Context.As(&mt))) mt->SetMultithreadProtected(TRUE);

        ComPtr<IDXGIDevice> dxgiDevice;
        m_Device.As(&dxgiDevice);
        winrt::com_ptr<::IInspectable> inspectable;
        if (FAILED(CreateDirect3D11DeviceFromDXGIDevice(dxgiDevice.Get(), inspectable.put()))) return false;
        m_WinrtDevice = inspectable.as<wgd3d::IDirect3DDevice>();

        ComPtr<ID3DBlob> vs, ps, err;
        if (FAILED(D3DCompile(kShaderSource, sizeof(kShaderSource) - 1, "blit", nullptr, nullptr, "VSMain", "vs_5_0", 0, 0, &vs, &err))) return false;
        if (FAILED(D3DCompile(kShaderSource, sizeof(kShaderSource) - 1, "blit", nullptr, nullptr, "PSMain", "ps_5_0", 0, 0, &ps, &err))) return false;
        m_Device->CreateVertexShader(vs->GetBufferPointer(), vs->GetBufferSize(), nullptr, &m_Vs);
        m_Device->CreatePixelShader(ps->GetBufferPointer(), ps->GetBufferSize(), nullptr, &m_Ps);

        D3D11_SAMPLER_DESC sd{};
        sd.Filter = D3D11_FILTER_MIN_MAG_MIP_LINEAR;
        sd.AddressU = sd.AddressV = sd.AddressW = D3D11_TEXTURE_ADDRESS_CLAMP;
        sd.MaxLOD = D3D11_FLOAT32_MAX;
        m_Device->CreateSamplerState(&sd, &m_Sampler);
        return true;
    }

    // ------------------------------------------------------------------- Fenster

    bool Compositor::InitWindows(HINSTANCE instance)
    {
        bool found = false;
        for (int attempt = 0; attempt < 40 && !found; ++attempt)
        {
            found = FindMonitor(m_Cfg.Physical, m_Physical);
            if (!found) Sleep(250);
        }
        if (!found)
        {
            Log(L"Physischer Monitor %s nicht gefunden", m_Cfg.Physical.c_str());
            return false;
        }
        const LONG px = m_Physical.Rect.left, py = m_Physical.Rect.top;

        if (m_Cfg.Direct)
        {
            m_DirectScreen = Offset(m_Cfg.Direct->Dest, px, py);
            m_PanelOrigin = { px, py };
            m_PanelKnown = true;
        }
        else if (m_Cfg.Panel)
        {
            m_PanelOrigin = *m_Cfg.Panel;
            m_PanelKnown = true;
        }

        WNDCLASSEXW wc{};
        wc.cbSize = sizeof(wc);
        wc.lpfnWndProc = WndProc;
        wc.hInstance = instance;
        wc.hCursor = LoadCursor(nullptr, IDC_ARROW);
        wc.lpszClassName = L"MonivoxView";
        RegisterClassExW(&wc);

        wc.hbrBackground = static_cast<HBRUSH>(GetStockObject(BLACK_BRUSH));
        wc.lpszClassName = L"MonivoxBackdrop";
        RegisterClassExW(&wc);

        const DWORD exView = WS_EX_TOPMOST | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE | WS_EX_NOREDIRECTIONBITMAP;

        // Ohne Direktbereich: schwarzer Hintergrund für Lücken zwischen den Bereichen
        if (!m_Cfg.Direct)
        {
            m_Backdrop = CreateWindowExW(WS_EX_TOPMOST | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE, L"MonivoxBackdrop", L"",
                                         WS_POPUP, px, py, W(m_Physical.Rect), H(m_Physical.Rect), nullptr, nullptr, instance, nullptr);
            SetWindowDisplayAffinity(m_Backdrop, WDA_EXCLUDEFROMCAPTURE);
            ShowWindow(m_Backdrop, SW_SHOWNOACTIVATE);
        }

        ComPtr<IDXGIDevice> dxgiDevice;
        m_Device.As(&dxgiDevice);
        ComPtr<IDXGIAdapter> adapter;
        dxgiDevice->GetAdapter(&adapter);
        ComPtr<IDXGIFactory2> factory;
        adapter->GetParent(IID_PPV_ARGS(&factory));

        for (auto& view : m_Views)
        {
            view->Screen = Offset(view->Cfg.Dest, px, py);
            view->Hwnd = CreateWindowExW(exView, L"MonivoxView", view->Cfg.Name.c_str(), WS_POPUP,
                                         view->Screen.left, view->Screen.top, W(view->Screen), H(view->Screen),
                                         nullptr, nullptr, instance, nullptr);
            if (!view->Hwnd) return false;
            SetWindowDisplayAffinity(view->Hwnd, WDA_EXCLUDEFROMCAPTURE);

            DXGI_SWAP_CHAIN_DESC1 desc{};
            desc.Width = W(view->Screen);
            desc.Height = H(view->Screen);
            desc.Format = DXGI_FORMAT_B8G8R8A8_UNORM;
            desc.SampleDesc.Count = 1;
            desc.BufferUsage = DXGI_USAGE_RENDER_TARGET_OUTPUT;
            desc.BufferCount = 2;
            desc.SwapEffect = DXGI_SWAP_EFFECT_FLIP_DISCARD;
            desc.AlphaMode = DXGI_ALPHA_MODE_IGNORE;
            HRESULT hr = factory->CreateSwapChainForHwnd(m_Device.Get(), view->Hwnd, &desc, nullptr, nullptr, &view->SwapChain);
            if (FAILED(hr)) { Log(L"Swapchain: 0x%08X", hr); return false; }
            factory->MakeWindowAssociation(view->Hwnd, DXGI_MWA_NO_ALT_ENTER);
            ComPtr<ID3D11Texture2D> back;
            view->SwapChain->GetBuffer(0, IID_PPV_ARGS(&back));
            m_Device->CreateRenderTargetView(back.Get(), nullptr, &view->Rtv);
            ShowWindow(view->Hwnd, SW_SHOWNOACTIVATE);
        }

        // Rahmen-Overlay: klickdurchlässig, nicht in Aufnahmen sichtbar. Empfängt außerdem Raw Input.
        m_Overlay = CreateWindowExW(WS_EX_LAYERED | WS_EX_TRANSPARENT | WS_EX_TOPMOST | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE,
                                    L"MonivoxView", L"MONIVOX", WS_POPUP, px, py, W(m_Physical.Rect), H(m_Physical.Rect),
                                    nullptr, nullptr, instance, nullptr);
        SetWindowDisplayAffinity(m_Overlay, WDA_EXCLUDEFROMCAPTURE);

        RegisterHotKey(m_Overlay, kHotkeyQuit, MOD_CONTROL | MOD_ALT | MOD_SHIFT | MOD_NOREPEAT, 'Q');
        RegisterHotKey(m_Overlay, kHotkeyFrames, MOD_CONTROL | MOD_ALT | MOD_SHIFT | MOD_NOREPEAT, 'F');
        return true;
    }

    // ------------------------------------------------------------------- Aufnahme

    bool Compositor::InitView(View& view, IGraphicsCaptureItemInterop* interop)
    {
        // Frisch angelegte Monitore tauchen manchmal erst nach einigen hundert Millisekunden auf
        bool found = false;
        for (int attempt = 0; attempt < 40 && !found; ++attempt)
        {
            found = FindMonitor(view.Cfg.Device, view.Source);
            if (!found) Sleep(250);
        }
        if (!found)
        {
            Log(L"Quellmonitor %s nach 10 s nicht gefunden", view.Cfg.Device.c_str());
            return false;
        }
        if (StartDda(view))
        {
            Log(L"%s: Aufnahme per Desktop Duplication", view.Cfg.Device.c_str());
            return true;
        }
        Log(L"%s: Desktop Duplication nicht möglich – nutze Windows.Graphics.Capture", view.Cfg.Device.c_str());

        HRESULT hr = interop->CreateForMonitor(view.Source.Handle, winrt::guid_of<wgc::GraphicsCaptureItem>(), winrt::put_abi(view.Item));
        if (FAILED(hr)) { Log(L"CreateForMonitor: 0x%08X", hr); return false; }

        view.PoolSize = view.Item.Size();
        view.Pool = wgc::Direct3D11CaptureFramePool::CreateFreeThreaded(m_WinrtDevice, wgd::DirectXPixelFormat::B8G8R8A8UIntNormalized, 2, view.PoolSize);
        View* raw = &view;
        view.FrameToken = view.Pool.FrameArrived([this, raw](auto const& pool, auto const&) { OnFrame(*raw, pool); });
        view.Session = view.Pool.CreateCaptureSession(view.Item);
        view.Session.IsCursorCaptureEnabled(true);
        try { view.Session.IsBorderRequired(false); } catch (...) {}
        view.Session.StartCapture();
        return true;
    }

    void Compositor::OnFrame(View& view, wgc::Direct3D11CaptureFramePool const& pool)
    {
        auto frame = pool.TryGetNextFrame();
        if (!frame) return;
        auto size = frame.ContentSize();
        auto access = frame.Surface().as<::Windows::Graphics::DirectX::Direct3D11::IDirect3DDxgiInterfaceAccess>();
        ComPtr<ID3D11Texture2D> src;
        if (FAILED(access->GetInterface(IID_PPV_ARGS(&src)))) return;
        StoreFrame(view, src.Get(), static_cast<UINT>(size.Width), static_cast<UINT>(size.Height));
        if (size.Width != view.PoolSize.Width || size.Height != view.PoolSize.Height)
        {
            view.PoolSize = size;
            pool.Recreate(m_WinrtDevice, wgd::DirectXPixelFormat::B8G8R8A8UIntNormalized, 2, size);
        }
    }

    void Compositor::StoreFrame(View& view, ID3D11Texture2D* src, UINT width, UINT height)
    {
        D3D11_TEXTURE2D_DESC sd{};
        src->GetDesc(&sd);
        UINT w = std::min<UINT>(width, sd.Width);
        UINT h = std::min<UINT>(height, sd.Height);
        if (w == 0 || h == 0) return;
        {
            // Reihenfolge immer: Context-Lock, dann View-Lock (wie beim Zeichnen)
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
                desc.BindFlags = D3D11_BIND_SHADER_RESOURCE;
                view.Texture.Reset();
                view.Srv.Reset();
                if (FAILED(m_Device->CreateTexture2D(&desc, nullptr, &view.Texture))) return;
                m_Device->CreateShaderResourceView(view.Texture.Get(), nullptr, &view.Srv);
                view.TexWidth = w;
                view.TexHeight = h;
            }
            D3D11_BOX box{ 0, 0, 0, w, h, 1 };
            m_Context->CopySubresourceRegion(view.Texture.Get(), 0, 0, 0, 0, src, 0, &box);
        }
        ULONGLONG now = GetTickCount64();
        if (view.LastFrameMs != 0)
        {
            unsigned gap = static_cast<unsigned>(now - view.LastFrameMs);
            unsigned prev = view.MaxGapMs.load();
            while (gap > prev && !view.MaxGapMs.compare_exchange_weak(prev, gap)) {}
        }
        view.LastFrameMs = now;
        view.Dirty = true;
        m_StatFrames++;
        SetEvent(m_FrameEvent);
    }

    // ------------------------------------------------------------------- Desktop Duplication

    bool Compositor::StartDda(View& view)
    {
        ComPtr<IDXGIDevice> dxgiDevice;
        m_Device.As(&dxgiDevice);
        ComPtr<IDXGIAdapter> adapter;
        dxgiDevice->GetAdapter(&adapter);

        // Ausgang des virtuellen Monitors auf derselben Grafikkarte suchen
        ComPtr<IDXGIOutput> output;
        for (UINT o = 0; adapter->EnumOutputs(o, &output) != DXGI_ERROR_NOT_FOUND; ++o)
        {
            DXGI_OUTPUT_DESC od{};
            if (SUCCEEDED(output->GetDesc(&od)) && _wcsicmp(od.DeviceName, view.Cfg.Device.c_str()) == 0) break;
            output.Reset();
        }
        if (!output) return false;
        if (FAILED(output.As(&view.DupOutput))) return false;

        HRESULT hr = E_FAIL;
        ComPtr<IDXGIOutput5> out5;
        if (SUCCEEDED(view.DupOutput.As(&out5)))
        {
            const DXGI_FORMAT formats[] = { DXGI_FORMAT_B8G8R8A8_UNORM };
            hr = out5->DuplicateOutput1(m_Device.Get(), 0, 1, formats, &view.Dup);
        }
        if (FAILED(hr)) hr = view.DupOutput->DuplicateOutput(m_Device.Get(), &view.Dup);
        if (FAILED(hr))
        {
            Log(L"DuplicateOutput: 0x%08X", hr);
            view.DupOutput.Reset();
            return false;
        }
        view.UsesDda = true;
        View* raw = &view;
        view.DupThread = std::thread([this, raw] { DdaLoop(*raw); });
        return true;
    }

    void Compositor::DdaLoop(View& view)
    {
        // Höhere Priorität, damit Bilder auch unter Last pünktlich abgeholt werden
        SetThreadPriority(GetCurrentThread(), THREAD_PRIORITY_ABOVE_NORMAL);
        while (!view.StopDup)
        {
            if (!view.Dup)
            {
                // Zugriff verloren (z. B. Moduswechsel, UAC-Dialog) – neu verbinden
                Sleep(100);
                ComPtr<IDXGIOutput5> out5;
                HRESULT hr = E_FAIL;
                if (SUCCEEDED(view.DupOutput.As(&out5)))
                {
                    const DXGI_FORMAT formats[] = { DXGI_FORMAT_B8G8R8A8_UNORM };
                    hr = out5->DuplicateOutput1(m_Device.Get(), 0, 1, formats, &view.Dup);
                }
                if (FAILED(hr)) view.DupOutput->DuplicateOutput(m_Device.Get(), &view.Dup);
                continue;
            }

            DXGI_OUTDUPL_FRAME_INFO info{};
            ComPtr<IDXGIResource> resource;
            HRESULT hr = view.Dup->AcquireNextFrame(100, &info, &resource);
            if (hr == DXGI_ERROR_WAIT_TIMEOUT) continue;
            if (hr == DXGI_ERROR_ACCESS_LOST || hr == DXGI_ERROR_INVALID_CALL)
            {
                view.Dup.Reset();
                continue;
            }
            if (FAILED(hr))
            {
                Log(L"AcquireNextFrame: 0x%08X", hr);
                view.Dup.Reset();
                Sleep(200);
                continue;
            }
            // Nur neue Bildinhalte kopieren (reine Mauszeiger-Updates ohne Bild überspringen)
            if (info.LastPresentTime.QuadPart != 0 || info.AccumulatedFrames > 0)
            {
                ComPtr<ID3D11Texture2D> tex;
                if (SUCCEEDED(resource.As(&tex)))
                {
                    D3D11_TEXTURE2D_DESC d{};
                    tex->GetDesc(&d);
                    StoreFrame(view, tex.Get(), d.Width, d.Height);
                }
            }
            resource.Reset();
            view.Dup->ReleaseFrame();
        }
    }

    void Compositor::RenderDirty()
    {
        std::lock_guard<std::mutex> ctx(m_ContextLock);
        for (auto& view : m_Views)
        {
            if (!view->Dirty.exchange(false)) continue;
            std::lock_guard<std::mutex> lock(view->Lock);
            if (!view->Srv) continue;

            const float black[4] = { 0, 0, 0, 1 };
            m_Context->OMSetRenderTargets(1, view->Rtv.GetAddressOf(), nullptr);
            m_Context->ClearRenderTargetView(view->Rtv.Get(), black);
            D3D11_VIEWPORT vp{ 0, 0, static_cast<float>(W(view->Screen)), static_cast<float>(H(view->Screen)), 0, 1 };
            m_Context->RSSetViewports(1, &vp);
            m_Context->IASetPrimitiveTopology(D3D11_PRIMITIVE_TOPOLOGY_TRIANGLELIST);
            m_Context->IASetInputLayout(nullptr);
            m_Context->VSSetShader(m_Vs.Get(), nullptr, 0);
            m_Context->PSSetShader(m_Ps.Get(), nullptr, 0);
            m_Context->PSSetSamplers(0, 1, m_Sampler.GetAddressOf());
            m_Context->PSSetShaderResources(0, 1, view->Srv.GetAddressOf());
            m_Context->Draw(3, 0);
            ID3D11ShaderResourceView* none = nullptr;
            m_Context->PSSetShaderResources(0, 1, &none);

            // Sync 0: DWM setzt das Bild beim nächsten Bildaufbau ein – mehrere Fenster blockieren sich nicht.
            LARGE_INTEGER t0, t1, f;
            QueryPerformanceCounter(&t0);
            HRESULT hr = view->SwapChain->Present(0, 0);
            QueryPerformanceCounter(&t1);
            QueryPerformanceFrequency(&f);
            m_StatRenderMs += (t1.QuadPart - t0.QuadPart) * 1000.0 / f.QuadPart;
            m_StatPresents++;
            if (hr == DXGI_ERROR_DEVICE_REMOVED || hr == DXGI_ERROR_DEVICE_RESET)
            {
                Log(L"Grafikgerät verloren (0x%08X)", hr);
                m_ExitCode = 4;
                PostQuitMessage(4);
                return;
            }
        }
    }

    struct TaskbarScan { std::vector<HWND> Windows; };

    BOOL CALLBACK CollectTaskbars(HWND hwnd, LPARAM param)
    {
        wchar_t cls[64]{};
        GetClassNameW(hwnd, cls, 64);
        if (wcscmp(cls, L"Shell_SecondaryTrayWnd") == 0 || wcscmp(cls, L"Shell_TrayWnd") == 0)
            reinterpret_cast<TaskbarScan*>(param)->Windows.push_back(hwnd);
        return TRUE;
    }

    // Blendet die Taskleiste auf gewählten virtuellen Bereichen aus. Der Explorer zeigt sie bei manchen
    // Ereignissen wieder an, daher wird sekündlich nachgesehen. Der freie Platz wird dem Arbeitsbereich
    // zugeschlagen (einmalig, damit nicht ständig Änderungsmeldungen an alle Fenster gehen).
    void Compositor::UpdateTaskbars()
    {
        bool any = false;
        for (auto& v : m_Views) any |= v->Cfg.HideTaskbar;
        if (!any) return;

        TaskbarScan scan;
        EnumWindows(CollectTaskbars, reinterpret_cast<LPARAM>(&scan));
        for (HWND tb : scan.Windows)
        {
            HMONITOR mon = MonitorFromWindow(tb, MONITOR_DEFAULTTONULL);
            for (auto& v : m_Views)
            {
                if (!v->Cfg.HideTaskbar || v->Source.Handle != mon) continue;
                if (IsWindowVisible(tb))
                {
                    ShowWindow(tb, SW_HIDE);
                    if (std::find(m_HiddenTaskbars.begin(), m_HiddenTaskbars.end(), tb) == m_HiddenTaskbars.end())
                        m_HiddenTaskbars.push_back(tb);
                }
                if (std::find(m_WorkFixed.begin(), m_WorkFixed.end(), mon) == m_WorkFixed.end())
                {
                    m_WorkFixed.push_back(mon);
                    RECT full = v->Source.Rect;
                    SystemParametersInfoW(SPI_SETWORKAREA, 0, &full, SPIF_SENDCHANGE);
                }
            }
        }
    }

    void Compositor::RestoreTaskbars()
    {
        for (HWND tb : m_HiddenTaskbars)
            if (IsWindow(tb)) ShowWindow(tb, SW_SHOWNA);
        m_HiddenTaskbars.clear();
    }

    void Compositor::LogStats()
    {
        ULONGLONG now = GetTickCount64();
        if (m_StatsStart == 0) { m_StatsStart = now; return; }
        if (now - m_StatsStart < 10000) return;
        double secs = (now - m_StatsStart) / 1000.0;
        unsigned frames = m_StatFrames.exchange(0);
        unsigned maxGap = 0;
        for (auto& v : m_Views) maxGap = std::max(maxGap, v->MaxGapMs.exchange(0));
        Log(L"Leistung: %.1f Aufnahmen/s, %.1f Bilder/s, längste Pause %u ms, Present Ø %.2f ms, Maus-Brücke %s",
            frames / secs, m_StatPresents / secs, maxGap, m_StatPresents ? m_StatRenderMs / m_StatPresents : 0.0,
            m_UseRawInput ? L"Raw Input" : L"leicht");
        m_StatsStart = now;
        m_StatPresents = 0;
        m_StatRenderMs = 0;
    }

    // ------------------------------------------------------------------- Rahmen

    void Compositor::SetFrames(bool on)
    {
        m_FramesOn = on;
        if (on)
        {
            DrawFrames();
            ShowWindow(m_Overlay, SW_SHOWNOACTIVATE);
            SetWindowPos(m_Overlay, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
        }
        else
        {
            // Ausgeblendet ist das Overlay komplett weg – kein Einfluss auf Spiele im Direktbereich.
            ShowWindow(m_Overlay, SW_HIDE);
        }
        Log(L"Rahmen %s", on ? L"an" : L"aus");
    }

    void Compositor::DrawFrames()
    {
        const int w = W(m_Physical.Rect), h = H(m_Physical.Rect);
        BITMAPINFO bi{};
        bi.bmiHeader.biSize = sizeof(bi.bmiHeader);
        bi.bmiHeader.biWidth = w;
        bi.bmiHeader.biHeight = -h; // top-down
        bi.bmiHeader.biPlanes = 1;
        bi.bmiHeader.biBitCount = 32;
        bi.bmiHeader.biCompression = BI_RGB;

        HDC screen = GetDC(nullptr);
        HDC mem = CreateCompatibleDC(screen);
        void* bits = nullptr;
        HBITMAP bmp = CreateDIBSection(mem, &bi, DIB_RGB_COLORS, &bits, nullptr, 0);
        if (!bmp || !bits)
        {
            DeleteDC(mem);
            ReleaseDC(nullptr, screen);
            return;
        }
        HGDIOBJ oldBmp = SelectObject(mem, bmp);
        auto* px = static_cast<UINT32*>(bits);
        std::fill(px, px + static_cast<size_t>(w) * h, 0u);

        auto fill = [&](RECT r, UINT32 argb)
        {
            r.left = std::max<LONG>(0, r.left); r.top = std::max<LONG>(0, r.top);
            r.right = std::min<LONG>(w, r.right); r.bottom = std::min<LONG>(h, r.bottom);
            for (LONG y = r.top; y < r.bottom; ++y)
                std::fill(px + static_cast<size_t>(y) * w + r.left, px + static_cast<size_t>(y) * w + r.right, argb);
        };
        auto opaque = [&](RECT r)
        {
            r.left = std::max<LONG>(0, r.left); r.top = std::max<LONG>(0, r.top);
            r.right = std::min<LONG>(w, r.right); r.bottom = std::min<LONG>(h, r.bottom);
            for (LONG y = r.top; y < r.bottom; ++y)
                for (LONG x = r.left; x < r.right; ++x) px[static_cast<size_t>(y) * w + x] |= 0xFF000000u;
        };

        const int t = 3; // Rahmenstärke
        HFONT font = CreateFontW(-20, 0, 0, 0, FW_SEMIBOLD, FALSE, FALSE, FALSE, DEFAULT_CHARSET, OUT_DEFAULT_PRECIS,
                                 CLIP_DEFAULT_PRECIS, CLEARTYPE_QUALITY, DEFAULT_PITCH, L"Segoe UI");
        HGDIOBJ oldFont = SelectObject(mem, font);
        SetBkMode(mem, TRANSPARENT);
        SetTextColor(mem, RGB(255, 255, 255));

        auto frame = [&](RECT r, UINT32 rgb, const std::wstring& label)
        {
            UINT32 c = 0xFF000000u | rgb;
            fill({ r.left, r.top, r.right, r.top + t }, c);
            fill({ r.left, r.bottom - t, r.right, r.bottom }, c);
            fill({ r.left, r.top, r.left + t, r.bottom }, c);
            fill({ r.right - t, r.top, r.right, r.bottom }, c);

            SIZE sz{};
            GetTextExtentPoint32W(mem, label.c_str(), static_cast<int>(label.size()), &sz);
            RECT box{ r.left + t, r.top + t, r.left + t + sz.cx + 20, r.top + t + sz.cy + 10 };
            box.right = std::min(box.right, r.right - t);
            fill(box, c);
            RECT textRect{ box.left + 10, box.top + 5, box.right - 4, box.bottom };
            DrawTextW(mem, label.c_str(), -1, &textRect, DT_LEFT | DT_TOP | DT_SINGLELINE | DT_END_ELLIPSIS | DT_NOPREFIX);
            GdiFlush();
            opaque(box); // GDI schreibt kein Alpha – Beschriftung deckend machen
        };

        static const UINT32 palette[] = { 0x7C5CFF, 0x3BA0FF, 0xFFB020, 0xFF5C8A, 0x2EC4B6, 0xA3E635, 0xF472B6, 0xF97316 };
        for (size_t i = 0; i < m_Views.size(); ++i)
        {
            const auto& v = m_Views[i]->Cfg;
            wchar_t label[256];
            swprintf_s(label, L"%zu · %s · %ld×%ld · virtuell", i + 1, v.Name.c_str(), W(v.Dest), H(v.Dest));
            frame(v.Dest, palette[i % ARRAYSIZE(palette)], label);
        }
        if (m_Cfg.Direct)
        {
            wchar_t label[256];
            swprintf_s(label, L"★ %s · %ld×%ld · DIREKT – keine Latenz · DRM ✓", m_Cfg.Direct->Name.c_str(), W(m_Cfg.Direct->Dest), H(m_Cfg.Direct->Dest));
            frame(m_Cfg.Direct->Dest, 0x22C55E, label);
        }

        POINT src{ 0, 0 };
        POINT dst{ m_Physical.Rect.left, m_Physical.Rect.top };
        SIZE size{ w, h };
        BLENDFUNCTION blend{ AC_SRC_OVER, 0, 255, AC_SRC_ALPHA };
        UpdateLayeredWindow(m_Overlay, screen, &dst, &size, mem, &src, 0, &blend, ULW_ALPHA);

        SelectObject(mem, oldFont);
        DeleteObject(font);
        SelectObject(mem, oldBmp);
        DeleteObject(bmp);
        DeleteDC(mem);
        ReleaseDC(nullptr, screen);
    }

    // ------------------------------------------------------------------- Arbeitsbereich

    // Maximierte Fenster auf dem physischen Monitor sollen genau den Direktbereich füllen.
    void Compositor::ApplyWorkArea()
    {
        if (!m_Cfg.Direct) return;
        MonitorLookup now;
        if (!FindMonitor(m_Cfg.Physical, now)) return;
        if (!m_WorkAreaSet)
        {
            m_OriginalWork = now.Work;
            m_WorkAreaSet = true;
        }
        if (!EqualRect(&now.Work, &m_DirectScreen))
        {
            if (m_WorkAreaResets >= 3)
            {
                if (m_WorkAreaResets == 3) Log(L"Arbeitsbereich wird vom System zurückgesetzt – gebe auf (verhindert Ruckler)");
                m_WorkAreaResets = 4;
                return;
            }
            ++m_WorkAreaResets;
            RECT r = m_DirectScreen;
            SystemParametersInfoW(SPI_SETWORKAREA, 0, &r, SPIF_SENDCHANGE);
        }
    }

    // ------------------------------------------------------------------- Maus-Brücke

    Location Compositor::Locate(POINT p) const
    {
        for (size_t i = 0; i < m_Views.size(); ++i)
            if (Contains(m_Views[i]->Source.Rect, p)) return { AreaKind::Virtual, static_cast<int>(i) };
        if (Contains(m_Physical.Rect, p))
        {
            if (m_Cfg.Direct && Contains(m_DirectScreen, p)) return { AreaKind::Direct, -1 };
            return { AreaKind::Covered, -1 };
        }
        return { AreaKind::Other, -1 };
    }

    // Position im "Monitorbild" (Koordinaten des physischen Monitors), falls bestimmbar
    std::optional<POINT> Compositor::LayoutOf(const Location& loc, POINT p) const
    {
        switch (loc.Kind)
        {
        case AreaKind::Virtual:
        {
            const auto& v = *m_Views[loc.View];
            const RECT& s = v.Source.Rect;
            const RECT& d = v.Cfg.Dest;
            LONG x = d.left + static_cast<LONG>(std::floor((p.x - s.left) * static_cast<double>(W(d)) / W(s)));
            LONG y = d.top + static_cast<LONG>(std::floor((p.y - s.top) * static_cast<double>(H(d)) / H(s)));
            return POINT{ x, y };
        }
        case AreaKind::Direct:
        case AreaKind::Covered:
            return POINT{ p.x - m_Physical.Rect.left, p.y - m_Physical.Rect.top };
        default:
            if (!m_PanelKnown) return std::nullopt;
            return POINT{ p.x - m_PanelOrigin.x, p.y - m_PanelOrigin.y };
        }
    }

    // Monitorbild-Koordinate -> Desktop-Position, an der die Maus wirklich stehen muss
    std::optional<POINT> Compositor::ToDesktop(POINT l) const
    {
        const LONG pw = W(m_Physical.Rect), ph = H(m_Physical.Rect);
        if (l.x >= 0 && l.y >= 0 && l.x < pw && l.y < ph)
        {
            if (m_Cfg.Direct && Contains(m_Cfg.Direct->Dest, l))
                return POINT{ m_Physical.Rect.left + l.x, m_Physical.Rect.top + l.y };

            // Bereich, der den Punkt enthält – oder bei Lücken der nächstgelegene
            int best = -1;
            double bestDist = 1e18;
            RECT bestRect{};
            auto consider = [&](int index, const RECT& d)
            {
                LONG cx = std::clamp(l.x, d.left, d.right - 1), cy = std::clamp(l.y, d.top, d.bottom - 1);
                double dist = std::pow(cx - l.x, 2) + std::pow(cy - l.y, 2);
                if (dist < bestDist) { bestDist = dist; best = index; bestRect = d; }
            };
            for (size_t i = 0; i < m_Views.size(); ++i) consider(static_cast<int>(i), m_Views[i]->Cfg.Dest);
            if (m_Cfg.Direct) consider(-2, m_Cfg.Direct->Dest);
            if (best == -1) return std::nullopt;

            POINT c{ std::clamp(l.x, bestRect.left, bestRect.right - 1), std::clamp(l.y, bestRect.top, bestRect.bottom - 1) };
            if (best == -2) return POINT{ m_Physical.Rect.left + c.x, m_Physical.Rect.top + c.y };
            const auto& v = *m_Views[best];
            const RECT& s = v.Source.Rect;
            const RECT& d = v.Cfg.Dest;
            LONG x = s.left + static_cast<LONG>((c.x - d.left + 0.5) * W(s) / W(d));
            LONG y = s.top + static_cast<LONG>((c.y - d.top + 0.5) * H(s) / H(d));
            return POINT{ std::clamp(x, s.left, s.right - 1), std::clamp(y, s.top, s.bottom - 1) };
        }

        // Außerhalb des Monitorbilds: weiter auf einen echten Nachbarmonitor, falls dort einer ist
        if (!m_PanelKnown) return std::nullopt;
        POINT q{ m_PanelOrigin.x + l.x, m_PanelOrigin.y + l.y };
        HMONITOR mon = MonitorFromPoint(q, MONITOR_DEFAULTTONULL);
        if (!mon) return std::nullopt;
        Location loc = Locate(q);
        if (loc.Kind == AreaKind::Other) return q;
        if (loc.Kind == AreaKind::Direct) return q;
        return std::nullopt;
    }

    // Begrenzt ein anderes Programm (z. B. ein Spiel) die Maus, halten wir uns komplett heraus.
    bool Compositor::ForeignClipActive() const
    {
        RECT clip{};
        if (!GetClipCursor(&clip)) return false;
        RECT all{ GetSystemMetrics(SM_XVIRTUALSCREEN), GetSystemMetrics(SM_YVIRTUALSCREEN), 0, 0 };
        all.right = all.left + GetSystemMetrics(SM_CXVIRTUALSCREEN);
        all.bottom = all.top + GetSystemMetrics(SM_CYVIRTUALSCREEN);
        return !EqualRect(&clip, &all);
    }

    void Compositor::Teleport(POINT p)
    {
        SetCursorPos(p.x, p.y);
        m_LastPos = p;
        m_LastLoc = Locate(p);
    }

    void Compositor::OnRawMouse(LONG dx, LONG dy, bool absolute)
    {
        if (ForeignClipActive()) return;
        POINT p{};
        if (!GetCursorPos(&p)) return;
        Location loc = Locate(p);
        const POINT last = m_LastPos;
        const Location lastLoc = m_LastLoc;
        auto accept = [&] { m_LastPos = p; m_LastLoc = loc; };

        const LONG sx = (dx > 0) - (dx < 0), sy = (dy > 0) - (dy < 0);
        auto stepFrom = [&](const Location& from, POINT at) -> bool
        {
            auto l = LayoutOf(from, at);
            if (!l) return false;
            POINT next{ l->x + sx, l->y + sy };
            auto target = ToDesktop(next);
            if (!target) return false;
            if (from.Kind == AreaKind::Virtual && Contains(m_Views[from.View]->Source.Rect, *target)) return false;
            Teleport(*target);
            return true;
        };

        // 1) Maus steht auf einem verdeckten Teil des echten Monitors
        if (loc.Kind == AreaKind::Covered)
        {
            if (lastLoc.Kind == AreaKind::Virtual && !absolute)
            {
                // vom virtuellen Block über die Desktop-Anordnung "hinübergerutscht" -> im Monitorbild weiter
                if (!stepFrom(lastLoc, last)) Teleport(last);
                return;
            }
            if (!m_Cfg.Direct && lastLoc.Kind == AreaKind::Other && !absolute)
            {
                Teleport(last); // geparkter Monitor ist kein Ziel
                return;
            }
            POINT l{ p.x - m_Physical.Rect.left, p.y - m_Physical.Rect.top };
            if (auto target = ToDesktop(l)) Teleport(*target); else accept();
            return;
        }
        if (absolute) { accept(); return; }

        // 2) Unerlaubter Wechsel über die Desktop-Anordnung (z. B. vom virtuellen Block auf einen echten Monitor)
        bool illegal = false;
        if (lastLoc.Kind == AreaKind::Virtual && loc.Kind == AreaKind::Direct) illegal = true;
        else if (lastLoc.Kind == AreaKind::Virtual && loc.Kind == AreaKind::Other && (m_Cfg.Direct || !m_Mirrored)) illegal = true;
        else if (lastLoc.Kind == AreaKind::Virtual && loc.Kind == AreaKind::Virtual && loc.View != lastLoc.View && !m_Mirrored) illegal = true;
        else if (lastLoc.Kind != AreaKind::Virtual && loc.Kind == AreaKind::Virtual && (m_Cfg.Direct || !m_Mirrored)) illegal = true;

        if (illegal)
        {
            if (!stepFrom(lastLoc, last)) Teleport(last);
            return;
        }

        // 3) Maus stößt am Rand eines virtuellen Monitors an -> im Monitorbild weiter
        if (loc.Kind == AreaKind::Virtual && (sx != 0 || sy != 0))
        {
            const RECT& s = m_Views[loc.View]->Source.Rect;
            bool atEdge = (sx > 0 && p.x >= s.right - 1) || (sx < 0 && p.x <= s.left) ||
                          (sy > 0 && p.y >= s.bottom - 1) || (sy < 0 && p.y <= s.top);
            if (atEdge && stepFrom(loc, p)) return;
        }
        accept();
    }

    // ------------------------------------------------------------------- Ablauf

    void Compositor::Shutdown()
    {
        for (auto& view : m_Views)
        {
            view->StopDup = true;
            if (view->DupThread.joinable()) view->DupThread.join();
            view->Dup.Reset();
            if (view->Pool) view->Pool.FrameArrived(view->FrameToken);
            if (view->Session) view->Session.Close();
            if (view->Pool) view->Pool.Close();
            if (view->Hwnd) DestroyWindow(view->Hwnd);
        }
        RestoreTaskbars();
        m_Views.clear();
        if (m_WorkAreaSet)
        {
            SystemParametersInfoW(SPI_SETWORKAREA, 0, &m_OriginalWork, SPIF_SENDCHANGE);
            m_WorkAreaSet = false;
        }
        if (m_Overlay)
        {
            UnregisterHotKey(m_Overlay, kHotkeyQuit);
            UnregisterHotKey(m_Overlay, kHotkeyFrames);
            DestroyWindow(m_Overlay);
            m_Overlay = nullptr;
        }
        if (m_Backdrop) { DestroyWindow(m_Backdrop); m_Backdrop = nullptr; }
        if (m_FrameEvent) { CloseHandle(m_FrameEvent); m_FrameEvent = nullptr; }
        if (m_ToggleEvent) { CloseHandle(m_ToggleEvent); m_ToggleEvent = nullptr; }
    }

    LRESULT CALLBACK Compositor::WndProc(HWND hwnd, UINT msg, WPARAM wParam, LPARAM lParam)
    {
        auto* self = g_instance;
        switch (msg)
        {
        case WM_INPUT:
            if (self)
            {
                RAWINPUT ri{};
                UINT size = sizeof(ri);
                if (GetRawInputData(reinterpret_cast<HRAWINPUT>(lParam), RID_INPUT, &ri, &size, sizeof(RAWINPUTHEADER)) != static_cast<UINT>(-1) &&
                    ri.header.dwType == RIM_TYPEMOUSE)
                {
                    bool absolute = (ri.data.mouse.usFlags & MOUSE_MOVE_ABSOLUTE) != 0;
                    if (absolute || ri.data.mouse.lLastX != 0 || ri.data.mouse.lLastY != 0)
                        self->OnRawMouse(ri.data.mouse.lLastX, ri.data.mouse.lLastY, absolute);
                }
            }
            return DefWindowProcW(hwnd, msg, wParam, lParam);
        case WM_HOTKEY:
            if (wParam == kHotkeyQuit) PostQuitMessage(0);
            else if (wParam == kHotkeyFrames && self) self->SetFrames(!self->m_FramesOn);
            return 0;
        case WM_TIMER:
            if (wParam == kTimerHousekeeping && self)
            {
                // Maus z. B. per Touch/Programm auf verdeckten Bereich gesetzt -> korrigieren
                POINT p{};
                if (self->m_UseRawInput)
                {
                    // Maus z. B. per Touch/Programm auf verdeckten Bereich gesetzt -> korrigieren
                    if (!self->ForeignClipActive() && GetCursorPos(&p) && self->Locate(p).Kind == AreaKind::Covered) self->OnRawMouse(0, 0, true);
                }
                else
                {
                    self->OnRawMouse(0, 0, false); // leichte Positionsprüfung
                }
                static ULONGLONG lastWork = 0, lastTaskbar = 0;
                ULONGLONG now = GetTickCount64();
                if (now - lastWork > 5000) { lastWork = now; self->ApplyWorkArea(); }
                if (now - lastTaskbar > 1000) { lastTaskbar = now; self->UpdateTaskbars(); }
                self->LogStats();
            }
            return 0;
        case WM_DISPLAYCHANGE:
            Log(L"Displayänderung erkannt – beende mit Code 3");
            if (self) self->m_ExitCode = 3;
            PostQuitMessage(3);
            return 0;
        case WM_MOUSEACTIVATE:
            return MA_NOACTIVATE;
        default:
            return DefWindowProcW(hwnd, msg, wParam, lParam);
        }
    }

    int Compositor::Run(HINSTANCE instance)
    {
        g_instance = this;
        m_FrameEvent = CreateEventW(nullptr, FALSE, FALSE, nullptr);
        m_ToggleEvent = CreateEventW(nullptr, FALSE, FALSE, kToggleFramesEvent);

        if (!InitD3D()) return 4;
        for (const auto& vc : m_Cfg.Views)
        {
            auto v = std::make_unique<View>();
            v->Cfg = vc;
            m_Views.push_back(std::move(v));
        }

        try
        {
            if (winrt::Windows::Foundation::Metadata::ApiInformation::IsTypePresent(L"Windows.Graphics.Capture.GraphicsCaptureAccess"))
                wgc::GraphicsCaptureAccess::RequestAccessAsync(wgc::GraphicsCaptureAccessKind::Borderless).get();
        }
        catch (...) {}

        auto interop = winrt::get_activation_factory<wgc::GraphicsCaptureItem, IGraphicsCaptureItemInterop>();
        for (auto& v : m_Views)
            if (!InitView(*v, interop.get())) return 2;
        if (!InitWindows(instance)) return 2;

        // Liegen die virtuellen Monitore wie die Bereiche (gespiegelt), wechselt die Maus zwischen ihnen natürlich.
        for (auto& v : m_Views)
            if (W(v->Source.Rect) != W(v->Cfg.Dest) || H(v->Source.Rect) != H(v->Cfg.Dest)) m_Mirrored = false;

        // Raw Input wird nur gebraucht, wenn Mausübergänge umgerechnet werden müssen (Direktbereich oder
        // hochskalierte Bereiche). Sonst reicht eine leichte Positionsprüfung – so wenig Last wie möglich.
        m_UseRawInput = m_Cfg.Direct.has_value() || !m_Mirrored;
        if (m_UseRawInput)
        {
            RAWINPUTDEVICE rid{};
            rid.usUsagePage = 0x01; // Generic Desktop
            rid.usUsage = 0x02;     // Maus
            rid.dwFlags = RIDEV_INPUTSINK;
            rid.hwndTarget = m_Overlay;
            if (!RegisterRawInputDevices(&rid, 1, sizeof(rid))) { Log(L"Raw Input nicht verfügbar"); m_UseRawInput = false; }
        }
        SetTimer(m_Overlay, kTimerHousekeeping, m_UseRawInput ? 50 : 15, nullptr);

        ApplyWorkArea();
        SetFrames(m_Cfg.Frames);
        GetCursorPos(&m_LastPos);
        m_LastLoc = Locate(m_LastPos);
        Log(L"Compositor läuft: %zu virtuelle Bereiche%s", m_Views.size(), m_Cfg.Direct ? L" + Direktbereich" : L"");

        HANDLE handles[] = { m_FrameEvent, m_ToggleEvent };
        MSG msg{};
        for (;;)
        {
            DWORD wait = MsgWaitForMultipleObjects(2, handles, FALSE, 100, QS_ALLINPUT);
            while (PeekMessageW(&msg, nullptr, 0, 0, PM_REMOVE))
            {
                if (msg.message == WM_QUIT) return m_ExitCode != 0 ? m_ExitCode : static_cast<int>(msg.wParam);
                TranslateMessage(&msg);
                DispatchMessageW(&msg);
            }
            if (wait == WAIT_OBJECT_0 + 1) SetFrames(!m_FramesOn);
            RenderDirty();
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
        MessageBoxW(nullptr, L"Aufruf: MonivoxCompositor.exe <config-datei>", L"MONIVOX", MB_ICONINFORMATION);
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
    Log(L"Beendet mit Code %d", code);
    if (g_log) fclose(g_log);
    LocalFree(argv);
    return code;
}
