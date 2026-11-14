# discrete — Unity Cheat Engine Dumper

[![Backends](https://img.shields.io/badge/backends-Mono_x64%2Fx86_%2B_IL2CPP_x64-blue)](docs/test-matrix.md)
[![License](https://img.shields.io/badge/license-GPL--3.0-blue)](LICENSE)
[![Built with Claude Code](https://img.shields.io/badge/Built%20with-Claude%20Code-CC785C?logo=claude)](https://claude.ai/code)

即時 Unity dumper 與 cheat-table 產生器:注入遊戲的 C++ DLL、Avalonia UI,以及
Cheat Engine Lua bridge。

**語言**: [English](README.md) · 繁體中文(本檔)

## 功能

- **Class browser** — assembly → class → field / method,即時讀取 Mono runtime
  或 IL2CPP 的 `global-metadata.dat`(v24–v39,涵蓋到 Unity 6.1)。
- **Instance inspector** — 找出 class 的即時 instance,就地展開 pointer 與陣列,
  數值自動更新,也能寫回欄位。
- **位址反查** — 貼上任一位址(例如 CE pointer scan 的結果),得到它所屬的
  managed 物件、class 與欄位。
- **Value search、snapshot、class pivot** — 可依 class 縮小範圍的 CE 式掃描、
  snapshot 差異比對、跨 instance 欄位表。
- **Methods** — 列出各 overload 的 native 位址、產生以 AOB 為基礎的
  Auto-Assembler 腳本,或直接呼叫 managed method。
- **匯出** — CE cheat-table XML、Structure Dissect 檔、C++ header;live 模式會
  嵌入實際位址。
- **CE Lua bridge** — 在 CE 的 Lua console 使用同一套 pipe API,不需開 UI。
- **Proxy DLL** — 給不方便注入的遊戲用的 `version.dll` / `winmm.dll` loader,
  附 Steam library 批次安裝。
- **離線 metadata CLI** — `metadata-cli.exe` 不需啟動遊戲即可解析
  `global-metadata.dat`。

## 下載

預先建置好的 Windows 版本放在
[Releases](https://github.com/bbfox0703/discrete-public/releases) 頁面:
self-contained,不需安裝 .NET runtime。執行檔未簽章,第一次執行時 Windows
SmartScreen 或防毒軟體可能會警告。

## 使用

**透過 UI**

1. 啟動 Unity 遊戲。
2. 執行 `DiscreteUI.exe` → **Pick & inject…** → 選擇遊戲 → **Inject & Connect**。
3. 瀏覽 assembly 與 class;在 class 上按右鍵 → **Inspect (find instances)…**。

**只用 Cheat Engine**

1. 把 `.lua` 腳本與 `Discrete-*.dll` 複製到 `<CE 安裝目錄>\discrete\`
   (build 後執行 `.\build.cmd deploy` 會自動完成)。
2. CE attach 到遊戲,開啟 Lua console(Ctrl+Alt+L)後執行:

```lua
dofile([[C:\Program Files\Cheat Engine\discrete\unity_dump.lua]])     -- 注入 DLL
dofile([[C:\Program Files\Cheat Engine\discrete\unity_invoke.lua]])
local r = callUnityMethod("Assembly-CSharp", "MainGame", "PLY_WORK",
                          "AddGrowHpMax", { argInt32(10) })
print(r.ok, r.returnValue)
```

CT 製作流程見 [USAGE.md](USAGE.md),Lua API 見
[scripts/README.md](scripts/README.md)。

## 從原始碼建置

需求:Windows 10/11 x64、Visual Studio(C++ workload,已內含 CMake 與
Ninja)、.NET SDK 10、Python 3(建置時的檢查用),Lua bridge 需要
Cheat Engine 7.5+。

```powershell
.\sync_vendor.ps1            # 抓取鎖定版本的 C++ 依賴(nlohmann/json、MinHook、Zydis)
.\build.cmd publish clean    # DLL + Native AOT UI + 測試 + metadata-cli -> dist\
.\build.cmd /?               # 所有 target(ui、dll、mono、il2cpp、test、deploy……)
```

一律透過 `build.cmd` 建置;直接呼叫 `cmake` / `dotnet` 會缺少 VS 開發環境。
x86 產出在 `dist\x86\`。

## 運作方式

```
Cheat Engine (+ Lua bridge) ─┐
                             ├─ \\.\pipe\DiscreteBfx (JSON) ─► Discrete-Mono.dll / Discrete-IL2CPP.dll
DiscreteUI.exe ──────────────┘                                  注入 Unity 遊戲,
                                                                讀取 Mono / IL2CPP runtime 資料
```

UI 不直接接觸遊戲;Mono / IL2CPP 的所有差異都包在 pipe protocol 後面。

## 已驗證遊戲

SaGa F2R、BongoCat、Farthest Frontier、Hollow Knight: Silksong、Songs of
Silence 等,涵蓋兩種 backend 與所有支援的 metadata 版本。完整清單見
[docs/test-matrix.md](docs/test-matrix.md)。

## 文件

| | |
|---|---|
| [USAGE.md](USAGE.md) | 給 cheat-table 製作者的使用指南 |
| [CONTRIBUTING.md](CONTRIBUTING.md) | 建置、測試、慣例、發布流程 |
| [docs/plan.md](docs/plan.md) | 開發狀態、待辦與 phase 紀錄 |
| [docs/Archiutecture.md](docs/Archiutecture.md) | 架構規則與不變條件 |
| [docs/pipe-protocol.md](docs/pipe-protocol.md) | DLL ↔ UI 通訊協定 |
| [docs/](docs/) | 其他:backend 內部、匯出格式、proxy DLL、lessons learned |

## 授權

[GNU General Public License v3.0](LICENSE)。

## 致謝

Cheat Engine(Eric "Dark Byte" Heijnen 與社群)· Avalonia ·
CommunityToolkit.Mvvm · nlohmann/json(MIT)· MinHook(BSD-2-Clause)·
Zydis(MIT)· xunit.v3 · BepInEx、MelonLoader、Il2CppInterop 提供
`runtime_invoke` marshaling 參考 · 上游 libil2cpp 原始碼供 layout 交叉比對。
