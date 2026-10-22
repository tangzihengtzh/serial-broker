#include "broker.hpp"
#include <commctrl.h>
#include <algorithm>
#include <chrono>
#include <deque>
#include <sstream>

namespace {
constexpr UINT timer_id = 1;
enum Id { port_id=101, baud_id, tcp_id, tcp_enabled_id, dtr_id, rts_id, log_id, refresh_id,
          start_id, stop_id, policy_id, disconnect_id, clients_id,
          monitor_id, clear_id, pause_id, hex_id };
struct Entry { std::string text; bool serial; bool tx; sb::Bytes bytes; };
struct App {
    HWND window{};
    HFONT font{}, mono{};
    int dpi = 96;
    std::vector<HWND> labels;
    HWND port{}, baud{}, tcp{}, tcp_enabled{}, dtr{}, rts{}, log{}, refresh{}, start{}, stop{},
         policy{}, disconnect{}, clients{}, monitor{}, clear{}, pause{}, hex{}, status{};
    std::mutex queue_mutex;
    std::deque<Entry> entries;
    std::size_t queued = 0, dropped = 0;
    sb::Broker broker;
    std::thread worker;
    std::atomic_bool busy{false};
    bool needs_cleanup = false, stopping = false, want_close = false, policy_dropdown = false;
    unsigned ticks = 0;
    App() : broker([this](auto text) { enqueue({std::move(text),false,false,{}}); },
                   [this](bool tx, auto bytes) { enqueue({sb::timestamp(),true,tx,std::move(bytes)}); }) {}
    ~App() { if (worker.joinable()) worker.join(); broker.stop(); DeleteObject(font); DeleteObject(mono); }
    int s(int n) const { return MulDiv(n,dpi,96); }
    void enqueue(Entry entry) {
        std::lock_guard lock(queue_mutex);
        const auto cost = entry.text.size() + entry.bytes.size();
        if (queued + cost > 512*1024 || entries.size() >= 2048) { ++dropped; return; }
        queued += cost; entries.push_back(std::move(entry));
    }
    HWND control(const wchar_t* type, const wchar_t* text, DWORD style, int id = 0) {
        auto handle = CreateWindowExW(wcscmp(type,L"EDIT") == 0 ? WS_EX_CLIENTEDGE : 0,type,text,
            WS_CHILD | WS_VISIBLE | style,0,0,0,0,window,reinterpret_cast<HMENU>(static_cast<INT_PTR>(id)),
            GetModuleHandleW(nullptr),nullptr);
        if (!handle) throw std::runtime_error("Cannot create window control");
        SendMessageW(handle,WM_SETFONT,reinterpret_cast<WPARAM>(font),TRUE);
        return handle;
    }
    void create() {
        dpi = static_cast<int>(GetDpiForWindow(window));
        font = CreateFontW(-s(14),0,0,0,FW_NORMAL,FALSE,FALSE,FALSE,DEFAULT_CHARSET,0,0,CLEARTYPE_QUALITY,0,L"Microsoft YaHei UI");
        mono = CreateFontW(-s(13),0,0,0,FW_NORMAL,FALSE,FALSE,FALSE,DEFAULT_CHARSET,0,0,CLEARTYPE_QUALITY,0,L"Consolas");
        for (const auto text : {L"物理串口",L"波特率 / 8N1",L"Telnet 端口",L"日志目录",L"写入策略",L"客户端会话",L"串口监视（RX / TX，转义控制字符）"})
            labels.push_back(control(L"STATIC",text,0));
        port = control(L"COMBOBOX",L"",CBS_DROPDOWN | WS_VSCROLL | WS_TABSTOP,port_id);
        baud = control(L"COMBOBOX",L"",CBS_DROPDOWN | WS_VSCROLL | WS_TABSTOP,baud_id);
        for (const auto rate : {L"9600",L"19200",L"38400",L"57600",L"115200",L"230400",L"460800",L"921600",L"1500000"})
            SendMessageW(baud,CB_ADDSTRING,0,reinterpret_cast<LPARAM>(rate));
        SetWindowTextW(baud,L"115200");
        tcp = control(L"EDIT",L"7000",ES_NUMBER | WS_TABSTOP,tcp_id);
        tcp_enabled = control(L"BUTTON",L"启用本机 Telnet",BS_AUTOCHECKBOX | WS_TABSTOP,tcp_enabled_id);
        SendMessageW(tcp_enabled,BM_SETCHECK,BST_CHECKED,0);
        dtr = control(L"BUTTON",L"DTR",BS_AUTOCHECKBOX | WS_TABSTOP,dtr_id);
        rts = control(L"BUTTON",L"RTS",BS_AUTOCHECKBOX | WS_TABSTOP,rts_id);
        wchar_t path[32768]{}; GetModuleFileNameW(nullptr,path,32768);
        const auto logs = std::filesystem::path(path).parent_path() / L"logs";
        log = control(L"EDIT",logs.c_str(),ES_AUTOHSCROLL | WS_TABSTOP,log_id);
        refresh = control(L"BUTTON",L"刷新串口",WS_TABSTOP,refresh_id);
        start = control(L"BUTTON",L"启动代理",WS_TABSTOP,start_id);
        stop = control(L"BUTTON",L"停止代理",WS_TABSTOP,stop_id);
        policy = control(L"COMBOBOX",L"",CBS_DROPDOWNLIST | WS_VSCROLL | WS_TABSTOP,policy_id);
        for (const auto text : {L"全部只读",L"单写入者（首个发送者独占）",L"共享写入（默认，用户负责命令冲突）"})
            SendMessageW(policy,CB_ADDSTRING,0,reinterpret_cast<LPARAM>(text));
        SendMessageW(policy,CB_SETCURSEL,static_cast<WPARAM>(sb::Policy::shared),0);
        disconnect = control(L"BUTTON",L"断开选中会话",WS_TABSTOP,disconnect_id);
        clients = control(L"LISTBOX",L"",LBS_NOTIFY | WS_BORDER | WS_VSCROLL | WS_TABSTOP,clients_id);
        monitor = control(L"EDIT",L"",ES_MULTILINE | ES_READONLY | ES_AUTOVSCROLL | ES_AUTOHSCROLL | WS_VSCROLL | WS_HSCROLL,monitor_id);
        SendMessageW(monitor,EM_SETLIMITTEXT,250000,0);
        SendMessageW(monitor,WM_SETFONT,reinterpret_cast<WPARAM>(mono),TRUE);
        clear = control(L"BUTTON",L"清空监视",WS_TABSTOP,clear_id);
        pause = control(L"BUTTON",L"暂停显示",BS_AUTOCHECKBOX | WS_TABSTOP,pause_id);
        hex = control(L"BUTTON",L"十六进制",BS_AUTOCHECKBOX | WS_TABSTOP,hex_id);
        status = control(L"STATIC",L"未启动。先关闭直接占用物理串口的会话，再启动代理。",0);
        enumerate(); layout(); SetTimer(window,timer_id,100,nullptr); update();
    }
    void place(HWND h, int x, int y, int w, int hgt) { MoveWindow(h,s(x),s(y),s(w),s(hgt),TRUE); }
    void layout() {
        RECT rect{}; GetClientRect(window,&rect);
        const int width = MulDiv(rect.right,96,dpi), height = MulDiv(rect.bottom,96,dpi);
        place(labels[0],16,16,110,22); place(port,16,40,130,240); place(refresh,156,40,100,28);
        place(labels[1],276,16,140,22); place(baud,276,40,130,250);
        place(labels[2],426,16,130,22); place(tcp,426,40,90,27);
        place(tcp_enabled,530,40,170,27); place(dtr,710,40,65,27); place(rts,780,40,65,27);
        place(labels[3],16,82,100,22); place(log,112,80,width-350,27);
        place(start,width-224,80,100,29); place(stop,width-114,80,100,29);
        place(labels[4],16,124,90,22); place(policy,112,120,370,200);
        place(disconnect,498,120,150,29);
        place(labels[5],16,162,160,22); place(clients,16,186,300,height-240);
        place(labels[6],332,162,380,22); place(clear,width-320,156,92,28);
        place(pause,width-216,156,100,28); place(hex,width-110,156,100,28);
        place(monitor,332,186,width-348,height-240); place(status,16,height-42,width-32,32);
    }
    static std::wstring text(HWND h) {
        const int n = GetWindowTextLengthW(h); std::wstring result(static_cast<std::size_t>(n)+1,L'\0');
        GetWindowTextW(h,result.data(),n+1); result.resize(n); return result;
    }
    void enumerate() {
        auto previous = text(port);
        std::vector<wchar_t> names(65536);
        std::vector<std::wstring> ports;
        if (QueryDosDeviceW(nullptr,names.data(),static_cast<DWORD>(names.size()))) {
            for (const wchar_t* p = names.data(); *p; p += wcslen(p)+1) {
                const std::wstring name(p);
                if (name.size() > 3 && name.substr(0,3) == L"COM" && name.find_first_not_of(L"0123456789",3) == std::wstring::npos)
                    ports.push_back(name);
            }
        }
        std::sort(ports.begin(),ports.end(),[](const auto& a, const auto& b) { return wcstoul(a.c_str()+3,nullptr,10) < wcstoul(b.c_str()+3,nullptr,10); });
        SendMessageW(port,CB_RESETCONTENT,0,0);
        for (const auto& p : ports) SendMessageW(port,CB_ADDSTRING,0,reinterpret_cast<LPARAM>(p.c_str()));
        if (previous.empty()) previous = ports.empty() ? L"COM3" : ports.front();
        SetWindowTextW(port,previous.c_str());
    }
    static DWORD number(const std::wstring& value, DWORD low, DWORD high) {
        if (value.empty() || value.find_first_not_of(L"0123456789") != std::wstring::npos) throw std::runtime_error("Enter a valid unsigned integer");
        const auto n = std::stoull(value);
        if (n < low || n > high) throw std::runtime_error("Number outside supported range");
        return static_cast<DWORD>(n);
    }
    void launch_start() {
        if (busy || needs_cleanup) return;
        try {
            sb::Options options; options.port = text(port);
            std::transform(options.port.begin(),options.port.end(),options.port.begin(),[](wchar_t c) { return static_cast<wchar_t>(towupper(c)); });
            options.baud = number(text(baud),50,4000000);
            options.tcp_enabled = SendMessageW(tcp_enabled,BM_GETCHECK,0,0) == BST_CHECKED;
            if (options.tcp_enabled) options.tcp_port = static_cast<unsigned short>(number(text(tcp),1,65535));
            options.dtr = SendMessageW(dtr,BM_GETCHECK,0,0) == BST_CHECKED;
            options.rts = SendMessageW(rts,BM_GETCHECK,0,0) == BST_CHECKED;
            options.log_root = text(log);
            if (options.log_root.empty()) throw std::runtime_error("Log directory cannot be empty");
            busy = true; needs_cleanup = true; stopping = false;
            worker = std::thread([this,options] {
                try { broker.start(options); }
                catch (const std::exception& e) { enqueue({sb::timestamp()+" START FAILED: "+e.what()+"\r\n",false,false,{}}); }
                busy = false;
            });
            update();
        } catch (const std::exception& e) { MessageBoxW(window,sb::wide(e.what()).c_str(),L"配置错误",MB_OK | MB_ICONERROR); }
    }
    void launch_stop() {
        if (busy) return;
        if (worker.joinable()) worker.join();
        busy = true; stopping = true;
        worker = std::thread([this] { broker.stop(); busy = false; }); update();
    }
    std::uint64_t selected() const {
        const auto index = SendMessageW(clients,LB_GETCURSEL,0,0);
        if (index == LB_ERR) return 0;
        return static_cast<std::uint64_t>(SendMessageW(clients,LB_GETITEMDATA,static_cast<WPARAM>(index),0));
    }
    void update() {
        const auto snap = broker.snapshot();
        const bool editable = !busy && !needs_cleanup && !want_close;
        for (auto h : {port,baud,tcp,tcp_enabled,dtr,rts,log,refresh,start}) EnableWindow(h,editable);
        EnableWindow(stop,!busy && needs_cleanup && !want_close);
        for (auto h : {policy,disconnect}) EnableWindow(h,!busy && snap.running && !want_close);
        // Do not reset the highlighted item while the user is choosing a policy.
        if (!policy_dropdown && !SendMessageW(policy,CB_GETDROPPEDSTATE,0,0) &&
            SendMessageW(policy,CB_GETCURSEL,0,0) != static_cast<LRESULT>(snap.policy))
            SendMessageW(policy,CB_SETCURSEL,static_cast<WPARAM>(snap.policy),0);
        auto selection = selected();
        SendMessageW(clients,WM_SETREDRAW,FALSE,0); SendMessageW(clients,LB_RESETCONTENT,0,0);
        for (const auto& c : snap.clients) {
            const auto line = sb::wide(std::string(c.writer ? "[TX] " : "[RO] ")+c.name);
            const auto index = SendMessageW(clients,LB_ADDSTRING,0,reinterpret_cast<LPARAM>(line.c_str()));
            SendMessageW(clients,LB_SETITEMDATA,static_cast<WPARAM>(index),static_cast<LPARAM>(c.id));
            if (c.id == selection) SendMessageW(clients,LB_SETCURSEL,static_cast<WPARAM>(index),0);
        }
        SendMessageW(clients,WM_SETREDRAW,TRUE,0); InvalidateRect(clients,nullptr,TRUE);
        const auto message = busy ? (stopping ? L"正在停止，等待 I/O 取消…" : L"正在启动…") :
            snap.running ? L"运行中。MobaXterm 使用 Telnet 连接 127.0.0.1；Agent 使用 serialctl。" : L"未启动。启动时独占物理串口，默认共享写入。";
        const auto summary = std::wstring(message)+L"  RX="+std::to_wstring(snap.rx)+L"  TX="+std::to_wstring(snap.tx);
        SetWindowTextW(status,summary.c_str());
    }
    static std::string render(const Entry& e, bool hexadecimal) {
        if (!e.serial) return e.text;
        std::string out = e.text+(e.tx ? " [TX] " : " [RX] ");
        constexpr char digits[] = "0123456789ABCDEF";
        for (auto c : e.bytes) {
            if (hexadecimal) { out += digits[c >> 4]; out += digits[c & 15]; out += ' '; }
            else if (c == '\r') out += "\\r";
            else if (c == '\n') out += "\\n";
            else if (c == '\t') out += "\\t";
            else if (c == '\\') out += "\\\\";
            else if (c >= 32 && c < 127) out += static_cast<char>(c);
            else { out += "\\x"; out += digits[c >> 4]; out += digits[c & 15]; }
        }
        return out+"\r\n";
    }
    void tick() {
        if (!busy && worker.joinable()) { worker.join(); if (stopping) needs_cleanup = false; }
        if (!busy && want_close) {
            if (needs_cleanup) launch_stop();
            else { DestroyWindow(window); return; }
        } else if (!busy && needs_cleanup && !broker.snapshot().running) launch_stop();
        std::deque<Entry> pending; std::size_t lost;
        { std::lock_guard lock(queue_mutex); pending.swap(entries); queued = 0; lost = dropped; dropped = 0; }
        std::string rendered;
        if (lost) rendered = "[Monitor dropped "+std::to_string(lost)+" entries; forwarding is independent]\r\n";
        const bool paused = SendMessageW(pause,BM_GETCHECK,0,0) == BST_CHECKED;
        const bool hexadecimal = SendMessageW(hex,BM_GETCHECK,0,0) == BST_CHECKED;
        for (const auto& e : pending) if (!paused || !e.serial) rendered += render(e,hexadecimal);
        if (!rendered.empty()) {
            if (rendered.size() > 150000) rendered = "[Display batch truncated]\r\n" + rendered.substr(rendered.size()-150000);
            auto output = sb::wide(rendered);
            const int length = GetWindowTextLengthW(monitor);
            if (length + static_cast<int>(output.size()) > 200000) {
                const int trim = (std::max)(length/2,length+static_cast<int>(output.size())-200000);
                SendMessageW(monitor,EM_SETSEL,0,trim); SendMessageW(monitor,EM_REPLACESEL,FALSE,reinterpret_cast<LPARAM>(L""));
            }
            SendMessageW(monitor,EM_SETSEL,static_cast<WPARAM>(-1),static_cast<LPARAM>(-1));
            SendMessageW(monitor,EM_REPLACESEL,FALSE,reinterpret_cast<LPARAM>(output.c_str()));
            SendMessageW(monitor,EM_SCROLLCARET,0,0);
        }
        if (++ticks % 5 == 0) update();
    }
    void command(int id, int notification) {
        if (id == policy_id) {
            if (notification == CBN_DROPDOWN) policy_dropdown = true;
            else if (notification == CBN_SELENDOK ||
                     (notification == CBN_SELCHANGE && !policy_dropdown)) {
                const auto value = SendMessageW(policy,CB_GETCURSEL,0,0);
                if (value >= 0 && value <= 2) broker.set_policy(static_cast<sb::Policy>(value));
                update();
            } else if (notification == CBN_CLOSEUP) {
                policy_dropdown = false;
                update();
            }
            // Focus, open and highlight notifications must not overwrite the user's selection.
            return;
        }
        if (id == start_id) launch_start();
        else if (id == stop_id) launch_stop();
        else if (id == refresh_id) enumerate();
        else if (id == clear_id) SetWindowTextW(monitor,L"");
        else if (id == disconnect_id) broker.disconnect(selected());
        if (id != clear_id && id != pause_id && id != hex_id) update();
    }
};
LRESULT CALLBACK procedure(HWND window, UINT message, WPARAM wparam, LPARAM lparam) {
    auto* app = reinterpret_cast<App*>(GetWindowLongPtrW(window,GWLP_USERDATA));
    if (message == WM_NCCREATE) {
        app = static_cast<App*>(reinterpret_cast<CREATESTRUCTW*>(lparam)->lpCreateParams);
        app->window = window; SetWindowLongPtrW(window,GWLP_USERDATA,reinterpret_cast<LONG_PTR>(app));
    }
    if (!app) return DefWindowProcW(window,message,wparam,lparam);
    try {
        switch (message) {
        case WM_CREATE: app->create(); return 0;
        case WM_SIZE: if (app->monitor) app->layout(); return 0;
        case WM_GETMINMAXINFO: {
            auto* info = reinterpret_cast<MINMAXINFO*>(lparam); info->ptMinTrackSize = {app->s(1050),app->s(650)}; return 0;
        }
        case WM_COMMAND: app->command(LOWORD(wparam),HIWORD(wparam)); return 0;
        case WM_TIMER: app->tick(); return 0;
        case WM_CLOSE: app->want_close = true; app->update(); return 0;
        case WM_DESTROY: KillTimer(window,timer_id); PostQuitMessage(0); return 0;
        }
    } catch (const std::exception& e) { MessageBoxW(window,sb::wide(e.what()).c_str(),L"SerialBroker",MB_OK | MB_ICONERROR); }
    return DefWindowProcW(window,message,wparam,lparam);
}
}
int WINAPI wWinMain(HINSTANCE instance, HINSTANCE, PWSTR, int show) {
    SetProcessDPIAware();
    INITCOMMONCONTROLSEX common{sizeof(common),ICC_STANDARD_CLASSES}; InitCommonControlsEx(&common);
    App app;
    app.dpi = static_cast<int>(GetDpiForSystem());
    WNDCLASSW type{}; type.lpfnWndProc = procedure; type.hInstance = instance;
    type.hCursor = LoadCursorW(nullptr,IDC_ARROW); type.hIcon = LoadIconW(nullptr,IDI_APPLICATION);
    type.hbrBackground = reinterpret_cast<HBRUSH>(COLOR_BTNFACE+1); type.lpszClassName = L"SerialBroker.Main";
    if (!RegisterClassW(&type)) return 1;
    const auto window = CreateWindowExW(0,type.lpszClassName,L"SerialBroker — 串口代理",WS_OVERLAPPEDWINDOW,
        CW_USEDEFAULT,CW_USEDEFAULT,app.s(1160),app.s(780),nullptr,nullptr,instance,&app);
    if (!window) return 1;
    ShowWindow(window,show);
    MSG message{};
    while (GetMessageW(&message,nullptr,0,0) > 0) {
        if (!IsDialogMessageW(window,&message)) { TranslateMessage(&message); DispatchMessageW(&message); }
    }
    return 0;
}
