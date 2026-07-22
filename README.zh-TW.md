# discrete — Unity Cheat Engine Dumper

[![Phase 30](https://img.shields.io/badge/phase-30_(memory_%26_speed)_shipped-success)](docs/plan.md)
[![Backends](https://img.shields.io/badge/backends-Mono_x64_%2B_IL2CPP_x64-blue)](docs/test-matrix.md)
[![AOT](https://img.shields.io/badge/UI-Avalonia_12_AOT_(0%2F0_warnings)-purple)]()
[![License](https://img.shields.io/badge/license-GPL--3.0-blue)](LICENSE)

> 即時 Unity dumper + cheat-table 產生器 + 可從 Lua 呼叫 managed methods。
> C++ DLL backend + Avalonia 12 AOT UI + Cheat Engine Lua bridge。
>
> **語言**: [English](README.md) · 繁體中文 (本檔)

---

## 它能做什麼

把單一 backend DLL 注入 Unity 遊戲後,可以:

- **即時 class browser** — 列出每個 assembly → class → field/method,
  從 Mono runtime tables 或 IL2CPP 的 `global-metadata.dat`
  (metadata v24 → v39 — 涵蓋到 Unity 6.1 的每個版本,含 v38 的 section-header
  改版) 解析。**完全不需要遊戲原始碼**。
- **即時 instance walker** — 找到 heap 上某 class 的所有 live
  instance,inline 展開 pointer / array 欄位,以 250 ms / 1 s
  速度 watch 數值變化。
- **反向 address lookup** (`whatIsAt`) — 把 CE pointer-scan 找出的
  任意位址貼進來,辨識出它屬於哪個 managed instance + class +
  declared field。`findInstances` 的反向操作。
- **Method overload picker** (`findMethodInstances`) — 列出某方法名
  的每個 overload,回傳其 native address (Mono JIT / IL2CPP AOT
  pointer) + signature。
- **AOB AA-script generator** — 給定一個 overload,產生 Cheat Engine
  Auto-Assembler script,內含版本穩定的 AOB pattern + `registersymbol`
  區塊。多 overload 方法會為每個 signature 各產生一個 symbol,同一份
  script 可同時 handle 全部 overload。
- **AOBMaker direct-push** — 當 AOBMaker CE plugin 已載入,UI 可把
  產生的 AA script 直接推到 CE 的 address list 變成 memory record
  (省掉剪貼簿來回)。
- **`callMethod` RPC** (Phase 19) — 呼叫任意 managed method
  (Mono JIT 或 IL2CPP AOT entry),含 marshal 過的 primitive / string
  / null / address 參數 + typed return value + managed exception
  捕獲。三種使用面:
  1. **DLL pipe RPC** — `\\.\pipe\DiscreteBfx`,JSON over named pipe。
  2. **UI Method Call dialog** — 在 Method Overloads 視窗右鍵任一
     overload row → "Call method…"。
  3. **CE Lua bridge** — `dofile [...]\discrete\unity_invoke.lua`
     後 `callUnityMethod(asm, ns, class, method, args, opts)`。
- **CE XML / CSX / SDK header export** — 把當前 class 匯出成 Cheat
  Table structure、Structure Dissect entry,或 C++ header。
  Live mode 會嵌入真實 instance 位址,所以 .CT 檔案可開箱即用。
- **Proxy DLL deploy** — `version.dll` / `winmm.dll` / `dxgi.dll`
  side-by-side 安裝器,給直接 inject 不方便的遊戲用。批次 install
  panel 可掃 Steam 遊戲庫並選擇要 deploy 哪些。
- **Offline metadata CLI** — `metadata-cli.exe` 可獨立 parse
  `global-metadata.dat` (不需開遊戲),適合做 RE 探勘。給遊戲安裝
  目錄、甚至只給 Steam 遊戲名稱即可,檔案位置它自己找。

看 [docs/plan.md](docs/plan.md) — **單一窗口 / 唯一真相來源**:目前 build 狀態、目前 phase、**集中 Open Backlog / TODO**、最近批次、完整 phase ledger、cross-PC workflow。已完成 phase 的長篇寫作 + 舊批次摘要在 [docs/archive/](docs/archive/)。

## 架構

```
                  ┌─────────────────────────┐
                  │ Cheat Engine + AOBMaker │
                  └──────────┬──────────────┘
                             │  \\.\pipe\AOBMakerCEBridge
                             ▼
                  ┌─────────────────────────┐
                  │  DiscreteUI.exe (AOT)   │  Avalonia 12,single-file
                  │  • Assembly browser     │  Win-only (Skia + HarfBuzz)
                  │  • Live inspector       │
                  │  • Address Lookup tab   │
                  │  • Method Overloads     │
                  │  • Method Call dialog   │
                  │  • Proxy Deploy panel   │
                  └──────────┬──────────────┘
                             │  \\.\pipe\DiscreteBfx (JSON over pipe)
                             ▼
                  ┌─────────────────────────┐
                  │  Discrete-Mono.dll   │  ← 注入 Unity x64 遊戲
                  │  / -IL2CPP.dll          │
                  │                         │
                  │  • backend detector     │
                  │  • domain / class walk  │
                  │  • method invoker       │  ← Phase 19 (callMethod)
                  │  • AOB-pattern decoder  │  ← Phase 18 (手刻 x64 LDE)
                  │  • address finder       │  ← Phase 17 (whatIsAt)
                  └─────────────────────────┘
                             ▲
                             │ runtime / metadata APIs
                             │ (mono_class_get_methods,
                             │  il2cpp_class_get_field_at_index, ...)
                  ┌─────────────────────────┐
                  │   Unity 遊戲 (target)   │
                  │  Mono.dll / GameAsm.dll │
                  └─────────────────────────┘
```

CE Lua bridge scripts (`unity_dump.lua`、`unity_symbols.lua`、
`unity_invoke.lua`) 直接從 CE Lua console 對同一個
`\\.\pipe\DiscreteBfx` 講話 — headless workflow 完全不需要 UI。

## 快速開始

### 環境需求

- **Windows 10 / 11** (x64)。Linux / macOS 不支援。
- **.NET SDK 10** — build UI / tests 用。
- **Visual Studio 2022 Build Tools** (或完整版 VS 的 C++ workload)
  — build DLL 用。
- **CMake 3.20+**、**Ninja** (有裝 VS 的話 build script 會自動找到)。
- **Cheat Engine 7.5+** — Lua bridge 用。

### Build

```powershell
# 完整 release publish (DLL + UI AOT + tests + metadata-cli) → dist/
.\build.cmd publish clean

# 只 build UI (快速 iteration; self-contained single-file)
.\build.cmd ui

# 只 build IL2CPP DLL / Mono DLL
.\build.cmd il2cpp
.\build.cmd mono

# xUnit v3 tests
.\build.cmd test
```

產出落在 `dist/`:

```
DiscreteUI.exe                    38 MB (Native AOT, single-file)
Discrete-Mono.dll              ~545 KB
Discrete-IL2CPP.dll            ~570 KB
metadata-cli.exe                  ~70 KB
version.dll / winmm.dll           ~130 KB each (proxy DLLs)
unity_*.lua / utils.lua           CE Lua bridge scripts
libSkiaSharp.dll / ...            Avalonia native dependencies
```

### 使用 (UI 路徑)

```
1. 啟動目標 Unity 遊戲。
2. 跑 dist\DiscreteUI.exe。
3. 按 "Pick & inject…",依名稱或 PID 過濾,按 Inject & Connect。
4. 瀏覽 assemblies → classes;右鍵 class → "Inspect (find instances)…"。
5. 右鍵 method → "Call method… (pick overload)" 即可呼叫。
```

### 使用 (CE-only,不開 UI)

```
1. 啟動目標 Unity 遊戲。
2. 開 Cheat Engine → File → Open Process → 選遊戲。
3. 開 Lua console (Ctrl+Alt+L),執行:
   dofile([[C:\Program Files\Cheat Engine\discrete\unity_dump.lua]])
4. DLL 會被注入並啟動 pipe server。然後:
   dofile([[...\discrete\unity_invoke.lua]])
   local r = callUnityMethod("Assembly-CSharp", "MainGame", "PLY_WORK",
                             "AddGrowHpMax", { argInt32(10) })
   print(r.ok, r.returnValue)
```

Bridge script 安裝:`.\build.cmd deploy` (publish 後),或手動把
`dist\unity_*.lua` + `dist\Discrete-*.dll` 複製到
`<CE-install>\discrete\`。詳細 layout 看 [scripts/README.md](scripts/README.md)。

## 已驗證遊戲

七款遊戲跨兩個 backend + 我們 ship 的所有 metadata version — 完整列表
看 [docs/test-matrix.md](docs/test-matrix.md)。SaGa F2R (IL2CPP v29)、
BongoCat (Mono bdwgc)、Farthest Frontier (IL2CPP v31)、Hollow Knight:
Silksong (IL2CPP v31)、Songs of Silence (IL2CPP v39, Unity 6.1) 等。

## 文件對照

| 檔案 | 內容 |
|---|---|
| [plan.md](docs/plan.md) | **單一窗口** — 目前狀態、Open Backlog / TODO、最近批次、phase ledger (0 → 32)、cross-PC workflow — 從這裡開始 |
| [archive/](docs/archive/) | 已完成 phase 的長篇寫作 + 舊 session hand-offs + 舊批次摘要 |
| [Archiutecture.md](docs/Archiutecture.md) | 規則與不變量 (rule of one backend、AOT mandate、單一 instance、async everywhere…) |
| [pipe-protocol.md](docs/pipe-protocol.md) | DLL ↔ UI 線上協議 (每個指令、每個 shape) |
| [test-matrix.md](docs/test-matrix.md) | 已驗證遊戲 + 工具版本 |
| [address-finder.md](docs/address-finder.md) | Phase 17 設計 — `whatIsAt` 反向 lookup spec |
| [lua-invoke.md](docs/lua-invoke.md) | Phase 19 設計 — `callMethod` spec、marshaling 規則、error code 列表 |
| [il2cpp-metadata-crosscheck.md](docs/il2cpp-metadata-crosscheck.md) | 我們的 IL2CPP layout 對 upstream Unity source 的 audit |
| [lessons-learned.md](docs/lessons-learned.md) | 工程上踩過的雷 |
| [proxy-dlls.md](docs/proxy-dlls.md) | `version.dll` / `winmm.dll` / `dxgi.dll` proxy + ProxyDeployWindow |
| [metadata-cli.md](docs/metadata-cli.md) | `metadata-cli.exe` 獨立工具 |
| [export-formats.md](docs/export-formats.md) | CE XML / CSX / SDK header 格式 |
| [mono-internals.md](docs/mono-internals.md) | Mono backend 細節 |
| [il2cpp-internals.md](docs/il2cpp-internals.md) | IL2CPP backend 細節 |
| [dll-spec.md](docs/dll-spec.md) | DLL ABI + 模組對照 + lifecycle |
| [ui-spec.md](docs/ui-spec.md) | UI 架構 (ViewModels、windows、bridges) |
| [technical-notes.md](docs/technical-notes.md) | 雜項工程筆記 |
| [CONTRIBUTING.md](CONTRIBUTING.md) | Build / test / 寫碼慣例 / PR 風格 |

## 授權

採用 **GNU General Public License v3.0**（GPL-3.0）授權。
完整條款請見 [LICENSE](LICENSE)。

## 致謝

- **Cheat Engine** — Eric "Dark Byte" Heijnen + 整個 CE 社群。
- **Avalonia 12** — UI 框架 (Native AOT 支援的關鍵)。
- **CommunityToolkit.Mvvm** — source-generator MVVM (零 reflection)。
- **nlohmann/json** — DLL 端 JSON。
- **xunit.v3** — UI test runner。
- **BepInEx / MelonLoader / Il2CppInterop** 專案 — non-uniform
  `runtime_invoke` marshaling 的參考實作 (詳見
  [lessons-learned.md](docs/lessons-learned.md))。
- `vendor/libil2cpp/` 下的四份 Unity metadata-version source — 並排
  保留以便 offset cross-check (詳見
  [il2cpp-metadata-crosscheck.md](docs/il2cpp-metadata-crosscheck.md))。
