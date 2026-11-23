# AUDIT_PLAN.md — Discrete 全 repo 稽核盤點

> 產出日期：2026-11-23 · 基準：`dev` @ `6bea633`（working tree clean）
> 性質：**盤點與規劃**，不是稽核結果。本次只做了目錄、建置設定與核心模組的結構掃描，沒有修改任何程式碼，也刻意**不參考**先前的 audit 文件（`docs/audit-2026-08-13*.md`）。
> 下文「觀察到的訊號」是掃描時順手看到、**尚未驗證**的疑點，用來決定稽核優先順序，不能直接當成已確認的 bug。

---

## 0. Repo 全貌

| 區域 | 語言 / 技術 | 規模（行數，約） | 產物 |
|---|---|---|---|
| `dll/src/shared` + `main.cpp` | C++20 / MSVC / Win32 | ~17k（其中 `ExportAPI.cpp` 一檔 10.6k） | `Discrete-Mono.dll`、`Discrete-IL2CPP.dll` 共用部分 |
| `dll/src/mono` | C++ | ~4.6k | Mono backend（x64 + x86） |
| `dll/src/il2cpp` | C++ | ~8.5k | IL2CPP backend（僅 x64） |
| `dll/src/proxy` | C++（`/MT`、pre-CRT 限制） | ~0.7k | `version.dll` / `winmm.dll` proxy |
| `dll/rust` | Rust 2024（`iced-x86` 唯一 dep） | ~11.6k（含 tests ~2.4k） | `discrete_core` staticlib |
| `tools/metadata-cli`、`tools/r2-diff`、`tools/inject-x86` | C++ | ~4.4k / ~3.1k / ~0.3k | `metadata-cli.exe`、驗證 harness、x86 injector |
| `tools/verify`、`tools/zydis-oracle` | Python / Rust | ~1.2k / — | golden-hash 迴歸閘門 |
| `ui/UnityDumpUI.Core` | C# .NET 10（AOT） | ~16k | 平台無關的 service / protocol / exporter |
| `ui/UnityDumpUI` | C# Avalonia 12 + CommunityToolkit.Mvvm | ~26k + `.axaml` | `DiscreteUI.exe` |
| `ui/UnityDumpUI.Tests` | xUnit v3（MTP） | ~25k | C# 單元測試 |
| `scripts/*.lua` | CE Lua | ~2.7k | CE bridge（同時 embed 進 UI） |
| `tools/*.ps1` | PowerShell | 30+ 支 | 需要真實遊戲行程的 live 測試 / 診斷腳本 |
| 建置 / 發行 | `build.cmd`→`build.ps1`、CMake + Ninja、cargo、`release.yml` | — | `dist\`、`dist\x86\`、draft release |

**執行期資料流**

```
 [遊戲行程]                                          [獨立行程]
 proxy DLL / UI injector / inject-x86 ──► Discrete-{Mono|IL2CPP}.dll
                                            │  main.cpp → Discrete_Init → PipeServer
                                            │  ExportAPI::RegisterCoreHandlers（~77 個 RPC）
                                            │     ├─ Mono / IL2CPP backend（runtime 內省）
                                            │     ├─ Value/Group/Snapshot session
                                            │     └─ discrete_core（Rust，經 *Rs.cpp wrapper）
                                            ▼
                         \\.\pipe\DiscreteBfx  (newline-delimited JSON, 最多 4 個 instance)
                                            ▲                       ▲
                     DiscreteUI (PipeClient/BackendAdapter)   CE Lua bridge (scripts/*.lua)
                                            │
                                            └─► CE XML / CSX / AA script / SDK header 輸出
```

---

## 1. 功能區塊劃分（5 塊）

切分原則：每塊有**清楚的邊界介面**、可以**獨立稽核**（不必同時讀另一塊的實作），且風險性質相近，可以套用同一套檢查手法。

| # | 區塊 | 核心問題 | 對外邊界 |
|---|---|---|---|
| A | 行程內基礎設施與注入鏈 | 進到遊戲行程、活下來、乾淨離開 | DllMain / proxy export、Named Pipe、Logger 目錄結構 |
| B | Mono / IL2CPP Runtime 內省 backend | 讀對遊戲記憶體、呼叫對 runtime API | `Mono::*` / `IL2CPP::*` C++ 介面、`ThreadAttachGuard` |
| C | RPC 指令層與 Wire Contract | DLL 與 UI/Lua 兩端對協定的理解一致 | `MagicStrings.h` ⇄ `ProtocolModels.cs` ⇄ `docs/pipe-protocol.md` |
| D | Rust Core 與離線分析工具鏈 | FFI 安全、演算法正確、golden 閘門有效 | `include/discrete_core.h`、`*Rs.cpp` wrapper、golden JSON |
| E | 桌面 UI 與 CE 整合輸出 | UI 不卡、不洩漏、輸出的 CE 腳本正確 | `PipeClient` 之上的所有 C#、`scripts/*.lua`、輸出檔格式 |

建置 / 發行 / 文件屬於**橫切面**，在 §7 另列，不單獨成塊。

---

## 2. 區塊 A — 行程內基礎設施與注入鏈

### 範圍
- `dll/src/main.cpp`（DllMain、per-PID init mutex、log retention thread）
- `dll/src/proxy/{loader.cpp,loader.h}`、`proxy/version/*`、`proxy/winmm/*`（含 `.def`）
- `dll/src/shared/`：`BackendDetector`、`ModuleHelper`、`Logger`、`PipeServer`、`MainThreadDispatcher`、`ForegroundHook`、`Memory`（SEH 讀寫 / AOB）、`ScanCancel.h`、`RustCore.{h,cpp}`（log sink glue）
- `dll/src/mono/MonoMainThreadHook.*`、`dll/src/il2cpp/IL2CPPMainThreadHook.*`（MinHook detour `runtime_invoke`）
- `tools/inject-x86/main.cpp`
- UI 端注入：`ui/UnityDumpUI/Platform/{WindowsDllInjector,WindowsProxyInstaller}.cs`、`Core/Platform/IDllInjector.cs`、`IProxyInstaller.cs`、`ProxyDeployViewModel.cs`
- 迴歸工具：`tools/verify/proxy_precrt_gate.py`

### 職責
把 DLL 載入目標行程（三條路：proxy 自動載入 / UI 注入 / x86 helper）、確保每個 PID 只初始化一次、開 pipe server、提供主執行緒派工與安全記憶體存取，並在 FreeLibrary（eject）與行程結束兩種情境下正確收尾。

### 觀察到的訊號（待驗證）
1. **eject 生命週期矛盾**：`PipeServer.cpp:323-328` 註解寫「DLL unload only happens at game exit」，所以 per-client worker 用 `detach()` 且不 join；但 `main.cpp:187-221` 明確支援 `lpReserved == NULL` 的 FreeLibrary 路徑。若 eject 時仍有 detached worker 在跑 `ExportAPI` handler，程式碼頁可能在它底下被 unmap。
2. **文件與實作不符**：`PipeServer.h:44-45` 宣稱 `RegisterDisconnectHook` 具 idempotent（以 `std::function` target 位址比對），`PipeServer.cpp:410-414` 實際只有 `push_back`。
3. **過時註解**：`PipeServer.h:8-9`「single in-flight client / multi-client deferred」與 `PipeServer.cpp:258` `kMaxPipeInstances = 4` + per-client thread 矛盾；`dll/CMakeLists.txt:262`「x86 variants are NOT yet built」也已過時。
4. `main.cpp:77` 以 `bInitialOwner=TRUE` 建立 mutex（擁有者是 loader thread），`OnDetach` 的 `ReleaseMutex` 可能在別的 thread 執行，會失敗；要確認是否只是無害的雜訊。

### 稽核檢查清單
- **架構一致性**：三條注入路徑（proxy / UI / inject-x86）是否走同一套 backend 選擇與 single-init 邏輯；x64 與 x86 的分歧點是否都有 `DISCRETE_ARCH_*` 守住。
- **過時 API**：`CreateThread` 與 `std::thread` 混用的理由；MinHook 1.3.3 的使用方式；`EnumProcessModules` 改用 `K32*` / Toolhelp 的必要性。
- **邊界條件**：loader lock 下的所有呼叫（`Log::Init` 在 DllMain 內寫檔）；`Stop()` 的 bounded wait 逾時後 `g_stopEvent` 的生命週期；`ERROR_PIPE_BUSY` 無限 retry 迴圈；32-bit 遊戲 2 GB 位址空間下的 `bad_alloc`。
- **隱藏邏輯 bug**：SEH-leaf rule（`__try` 本體只能有單一呼叫 / 存取）逐一檢查；proxy pre-CRT rule 4 條逐一檢查（`Proxy_MarkCrtReady` 是否第一行、每個 thunk 的失敗值是否為該 export 的文件值、latch 是否為 `volatile LONG`、是否在 `LoadLibraryW` 期間持鎖）；`ForegroundHook` 卸載時 hook 是否確實移除。
- **驗證手段**：`proxy_precrt_gate.py --all`；`tools/Test-Handshake.ps1`、`Test-BridgeInject.ps1`；針對 eject 路徑做一次「掃描中 eject」的 live 測試。

---

## 3. 區塊 B — Mono / IL2CPP Runtime 內省 backend

### 範圍
- `dll/src/mono/*`（扣掉 MainThreadHook）：`MonoResolver`、`MonoDomain`、`MonoTypeParser`、`MonoClassWalker`、`MonoAddressFinder`（1.3k）、`MonoMethodFinder`、`MonoMethodInvoker`（0.7k）、`MonoFieldXref`、`MonoThreadAttach.h`、`MonoTypes.h`
- `dll/src/il2cpp/*`（扣掉 MainThreadHook 與 metadata parser 的 Rust wrapper）：`IL2CPPResolver`、`IL2CPPDomain`、`IL2CPPTypeParser`、`IL2CPPClassWalker`、`IL2CPPAddressFinder`（1.6k）、`IL2CPPMethodFinder`、`IL2CPPMethodInvoker`、`IL2CPPFieldXref`（1k）、`IL2CPPStaticFieldsOffset`、`IL2CPPClassPointerScanner`、`IL2CPPClassReferenceDiagnostic`、`IL2CPPLazyInitSlotScanner`、`IL2CPPMetadataRegistration`、`IL2CPPThreadAttach.h`、`IL2CPPTypes.h`、`IL2CPPChainDetector.h`
- `dll/src/shared/{ManagedLayout.h,MethodSignatureFormat.h}`
- 參考文件：`docs/mono-internals.md`、`docs/il2cpp-internals.md`、`docs/address-finder.md`、`docs/value-type-structs.md`

### 職責
透過 runtime export 與動態探測出的 offset 走訪 Domain → Assembly → Class → Field/Method、找出 instance、讀取 static 欄位、呼叫 managed method、做 field xref。這一塊的正確性直接決定所有上層資料是否可信。

### 觀察到的訊號（待驗證）
1. 兩個 backend 是**平行手寫**，不是共用抽象（例：`MonoAddressFinder` vs `IL2CPPAddressFinder`、`MonoMethodInvoker` vs `IL2CPPMethodInvoker` 的規模與結構幾乎一樣）。修 bug 時只修一邊的機率高。
2. `MonoTypeParser.cpp`（39 行）與 `IL2CPPTypeParser.cpp`（36 行）極小，可能是早期架構留下的殘骸（`docs/Archiutecture.md` 的架構圖仍把它們列為主要元件）。
3. IL2CPP 側的 `ClassReferenceDiagnostic` / `LazyInitSlotScanner` 標為「investigation helper」（`dll/CMakeLists.txt:184-195`），但編進正式 DLL 並對外開 RPC。

### 稽核檢查清單
- **架構一致性**：列出 Mono / IL2CPP 雙胞胎函式對照表，逐對比較邊界處理是否一致；確認「Unity offsets 必須動態驗證、不得 hardcode」在每個 offset 使用點都成立（`ManagedLayout.h`、`MetadataLayout.h`、`*Types.h`）。
- **過時 API**：已 deprecated 的 mono export（例如舊版 `mono_class_get_*` 變體）是否仍有 fallback；IL2CPP export 在 v24–v39 之間改名 / 消失的處理。
- **邊界條件**：x86 與 x64 的 pointer size、`MonoObject` header（0x10 vs 0x08）、`int32`/`int64`、`float`/`double`，所有 struct offset 需對照真實 runtime 資料；generic class / nested type / array-of-struct / `Dictionary<K,V>` 的 indirection chain 要追到底；null / 已被 GC 回收的 instance；超大 class 數量（60k+）。
- **隱藏邏輯 bug**：Mono thread-attach rule（直接呼叫 `r.class_get_*` 必須在 `ThreadAttachGuard` 內、helper 內已 attach 的不得再包一層）；`MethodInvoker` 的參數 boxing、例外物件處理與回傳值型別；SEH-leaf rule。
- **驗證手段**：`tools/Test-MonoWalk.ps1`、`Test-FindInstances*.ps1`、`Test-CallMethod.ps1`、`Test-WalkInstance.ps1`、`Test-V39RuntimeSweep.ps1`；依 `docs/test-matrix.md` 至少涵蓋 x86 Mono、x64 Mono、IL2CPP 各一款遊戲。

---

## 4. 區塊 C — RPC 指令層與 Wire Contract

### 範圍
- DLL 端：`dll/src/shared/ExportAPI.{h,cpp}`（10.6k 行；`RegisterCoreHandlers()` 從第 2000 行延伸到第 10507 行）、`MagicStrings.h`、`ValueScanSession.*`、`GroupScanSession.*`、`SnapshotSession.*`
- UI 端：`ui/UnityDumpUI.Core/Services/{PipeClient,ProtocolModels,ProtocolJson,RawJsonStringConverter,BackendAdapter}.cs`
- 契約文件：`docs/pipe-protocol.md`、`docs/snapshot-streaming-dedup.md`、`docs/group-scan-plan.md`

### 職責
定義並實作約 77 個 JSON-RPC 指令（handshake、class 走訪、instance 搜尋、value / group scan、snapshot streaming、method call、記憶體讀寫…），維護跨 RPC 的 session 狀態，並讓 UI 端反序列化模型與 DLL 端輸出完全吻合。

### 觀察到的訊號（待驗證）
1. **God function**：`RegisterCoreHandlers()` 約 8,500 行，以 `#if defined(DISCRETE_BACKEND_MONO)`（約第 3711–7037 行）/ `IL2CPP`（約第 7037–10500 行）分成兩大段，相同指令各寫一份（例：`CmdSnapshotBegin` Mono 版在第 4000 行、約 800 行，IL2CPP 版在第 8046 行；`CmdScanFieldValue` 在第 5467 行與第 9011 行）。這是兩端行為漂移的最高風險點。
2. **Magic string 規則破口**：以下指令直接用字串字面值，沒有走 `MagicStrings`：`"ping"`（2002）、`"lookupSymbol"`（3127）、`"listSymbols"`（3137）、`"getMetadataStats"`（6534、8762）、`"il2cppGetMetadataTypeFields"`（8503）、`"il2cppGetMetadataRegistration"`（8780）。
3. **併發假設不一致**：`PipeClient.cs:3-6` 假設 server「one-at-a-time」；實際上 server 最多 4 個 client 並行（UI + CE bridge），每個 client 各有 thread。全域 session（value scan / group scan / snapshot）與 `ScanCancel` 在兩個 client 同時操作時的行為需要確認。

### 稽核檢查清單
- **架構一致性**：產出「指令 × backend × 文件 × UI model」四方對照表：`MagicStrings.h` 有、但 handler 沒註冊的；handler 有、但 `pipe-protocol.md` 沒寫的；UI 呼叫了、但某 backend 沒實作的（handshake 的 capability 旗標是否正確反映）。
- **過時 API**：殘留的舊版指令 / 舊欄位名（有沒有 UI 已不呼叫的 handler）；`nlohmann::json` 用法（`at()` vs `operator[]`、`value()` 的預設值型別）。
- **邊界條件**：`args` 缺欄位 / 型別錯誤 / 負數 / 超大 count；超大回應（32-bit 下 `bad_alloc`、pipe buffer）；snapshot chunk 的 begin → chunk → end 狀態機（中途斷線、重複 begin、沒 end 就重連）；cancel 與 refine 的競態。
- **隱藏邏輯 bug**：雙胞胎 handler 逐對 diff（錯誤碼、預設值、單位、欄位名大小寫）；JSON 數值精度（64-bit 位址以 number 傳到 C# `long` / `ulong` 的路徑）；UI 端 `ProtocolModels` 的 `[JsonSerializable]` 是否涵蓋所有型別（AOT 下缺漏只會在 runtime 才爆）。
- **驗證手段**：靜態交叉比對（grep `MagicStrings::Cmd*` vs `RegisterHandler` vs `ProtocolModels`）；`tools/Test-ValueScanSession.ps1`、`Test-SnapshotAll.ps1`、`Test-MainThreadBurst.ps1`；做一次「UI 與 CE bridge 同時連線並同時掃描」的併發測試。

---

## 5. 區塊 D — Rust Core 與離線分析工具鏈

### 範圍
- `dll/rust/`：`src/{lib,ffi,aob,group_match,value}.rs`、`src/chain/*`（chain detector、x64 decode、MSVC sort 模擬）、`src/metadata/*`（`global-metadata.dat` parser v16–v39）、`src/xref/*`、`include/discrete_core.h`（手動同步）、`build.rs`、`RustCore.cmake`、`Cargo.toml` / `Cargo.lock`、`rust-toolchain.toml`
- C++ wrapper：`dll/src/shared/{AobPatternRs,GroupMatchRs,ValueRefineRs}.cpp` + 對應 `.h`；`dll/src/il2cpp/{MetadataParserRs,MetadataParserHostPath,IL2CPPChainDetectorRs,IL2CPPFieldXrefScanRs}.cpp`、`MetadataParser.h`、`MetadataLayout.h`
- 離線工具：`tools/metadata-cli/*`（PE 解析、registration finder、symbol pipeline，main.cpp 1.6k 行）、`tools/r2-diff/*`、`tools/verify/{metadata_golden,r2_golden}.py` + golden JSON、`tools/zydis-oracle/*`
- 規範：`docs/rust-core.md`、`docs/metadata-cli.md`、`docs/il2cpp-metadata-crosscheck.md`

### 職責
提供純演算法核心（metadata 解析、AOB pattern、group SDR matcher、value refine、IL2CPP static-field chain 偵測、field xref 解碼），同時給 DLL 與 `metadata-cli` 使用；用 golden-hash 閘門保證 C++ → Rust 移植後的行為不變。

### 觀察到的訊號（待驗證）
1. `include/discrete_core.h`（332 行）靠**手動**與 `ffi.rs` / `metadata/ffi.rs` 同步，沒有 cbindgen 或編譯期檢查，struct layout 漂移不會被抓到；在 i686 上尤其危險。
2. `metadata/parser.rs` 1.7k 行要涵蓋 v16–v39 共 20+ 個版本，但 CLAUDE.md 只列 v24/27/29/31/35–39 已實測；其他版本是「接受但未驗證」。
3. 真實檔案的 parser 測試必須設定 `DISCRETE_METADATA_FIXTURES` 或本機 `fixtures.local.txt` 才會跑，否則是 no-op（`build.ps1:686-701`）——CI 上很可能一直沒跑到。

### 稽核檢查清單
- **架構一致性**：`docs/rust-core.md` 的規則逐條核對：每個 `extern "C"` 是否在 `ffi::guard` 內、回傳 pointer 是否借用 handle 擁有的記憶體、bytes 以 `(ptr,len)` 傳遞且不做 UTF-8 驗證、遊戲記憶體只透過 `dc_read_fn` SEH shim 讀取、proxy DLL 絕不 link Rust。
- **過時 API**：已移除 legacy C++ 實作後殘留的 dead code / CMake 變數 / 註解（例：根 `CMakeLists.txt:144-152` 仍描述 Zydis）；`metadata-cli` 是否還有自己的一套 PE / metadata 邏輯與 Rust 版重複。
- **邊界條件**：metadata header 被截斷、offset/size 溢位、count × stride 乘法溢位（`usize` 在 i686 是 32-bit）、string table 未以 NUL 結尾、未知版本號；x64 decoder 遇到非法指令或跨頁 / 跨區段讀取。
- **隱藏邏輯 bug**：`discrete_core.h` 與 Rust `#[repr(C)]` struct 逐欄位對照（型別寬度、對齊、`bool` 大小）；handle 生命週期（double free、use-after-free 透過 C++ wrapper 的 RAII）；`msvc_sort.rs` 是否真的重現 MSVC `std::sort` 的不穩定順序。
- **驗證手段**：`.\build.cmd test`（x64 + i686 的 `cargo test`）；`metadata_golden.py` / `r2_golden.py` 搭配各版本 fixture；手動對 i686 印出 `size_of` / `offset_of` 與 C++ `sizeof` / `offsetof` 比對。

---

## 6. 區塊 E — 桌面 UI 與 CE 整合輸出

### 範圍
- App 殼：`ui/UnityDumpUI/{Program.cs,App.axaml*,app.manifest}`、`Views/*`（`MainWindow.axaml.cs` 2.1k 行）、`Controls/*`、`Resources/Strings/en.axaml`、`Services/*`（window state、hotkey）
- ViewModel：`ui/UnityDumpUI/ViewModels/*`（`SnapshotViewModel` 3.4k、`LiveInspectionViewModel` 2.6k、`MainWindowViewModel` 2.0k、`PivotViewModel` 1.7k、`TeleportViewModel` 1.0k…，以及 `*Engine.cs` 純邏輯）
- Core service（扣掉區塊 C 的 protocol 類）：`SnapshotStore`（SQLite）、`PlayerLocator`（1.9k）、`SceneEnumerator`、`TeleportService`、`UserSettingsStore`、`BookmarkStore`、`FileLogger`、`AppDataMigration`、各種 Scorer / Classifier
- CE 輸出：`Core/Services/Export/*`（`CeXmlExporter(.Live)`、`CsxExporter`、`SdkHeaderExporter`、`Il2cppHeaderBatchExporter`、`SymbolExporter`、5 個 `*AaScriptGenerator`）、`AobMakerBridge` / `AobMakerAppClient`
- CE Lua bridge：`scripts/*.lua`（其中 5 支 embed 進 `UnityDumpUI.Core.csproj:59-78`）、`BridgeInjector.cs`、`BridgeLuaResources.cs`
- 平台層：`ui/UnityDumpUI/Platform/{WindowsProcessEnumerator,SteamGameScanner}.cs`（injector 歸區塊 A）
- 測試：`ui/UnityDumpUI.Tests/*`
- 規範：`docs/ui-spec.md`、`docs/aot-pitfalls.md`、`docs/lua-logging.md`、`docs/export-formats.md`、`docs/ce-bridge-integration.md`

### 職責
提供使用者介面（瀏覽、搜尋、snapshot 比對、pivot、teleport、method call），把結果輸出成 CE 可直接使用的 table / script，並維護使用者狀態與 log。

### 觀察到的訊號（待驗證）
1. **Backend-agnostic 規則可能有破口**：`MainWindowViewModel.cs:1480` 判斷 `Backend2 == "IL2CPP"`、`MainWindow.axaml.cs:1379-1380` 依 backend 字首分支、`SnapshotViewModel.cs:1843-1858` 以 module 名稱子字串猜 backend（註解自承曾把 IL2CPP 遊戲誤判為 mono）。
2. **MVVM 不一致**：`MainWindow.axaml.cs` 2.1k 行 code-behind，邏輯可能散落在 View 與 VM 兩處。
3. **超大 ViewModel**：`SnapshotViewModel.cs`（3.4k）+ `.Group.cs`、`LiveInspectionViewModel.cs`（2.6k），不容易確認每條 RPC 都是 async、都不會卡住 UI thread。
4. `scripts/ce_table_bootstrap.lua` 沒有 embed 進 UI（其餘 5 支有），要確認是刻意的設計。

### 稽核檢查清單
- **架構一致性**：Core 不得含平台專屬程式碼（P/Invoke 必須走 `Core/Platform/I*` 介面）——`PipeClient.cs:97-100` 在 Core 直接 `DllImport kernel32`，需判定是否違規；UI 不得含 backend 專屬邏輯；所有 UI 字串是否都在 `en.axaml`。
- **過時 API**：Avalonia 11 → 12 遺留的寫法、`Dispatcher.UIThread` 用法；ReactiveUI 時代的殘留（文件仍寫 ReactiveUI，`UnityDumpUI.csproj:118` 註明已不使用）；`Microsoft.Data.Sqlite` / SQLitePCLRaw 3.x 的初始化方式。
- **邊界條件**：pipe 斷線時每個 VM 的狀態重置；大量資料（數十萬列 snapshot）下的記憶體與 DataGrid 虛擬化；DataGrid 欄寬規則（禁用 `*`，只能用 `Auto` + `MinWidth`，例外記在 `ui-spec.md`）；使用者狀態檔損毀 / 舊版格式；`%LOCALAPPDATA%\Discrete_Dumper` → `discrete` 的遷移。
- **隱藏邏輯 bug**：AOT / trimming 相容（任何反射、`JsonSerializer` 沒有用 source-gen context 的呼叫）；`async void`、未 await 的 Task、例外被吞；Single-instance mutex；所有 `*AaScriptGenerator` 產出的 `{$lua}` 區塊與 `scripts/*.lua` 是否遵守 `docs/lua-logging.md`（`print` 受 DEBUG 開關控制、錯誤一律輸出、成功且非 debug 時關閉 Lua Engine）；CE XML 的 pointer chain offset 是否與 x86 / x64 一致；Lua 對 CE API 的呼叫是否真的存在於 `celua.txt` / `monoscript.lua`（簽章與回傳慣例）。
- **驗證手段**：`.\build.cmd test`；`.\build.cmd ui publish`（AOT analyzer 必須零警告）；`tools/Test-BridgeInject.ps1`；把產出的 `.CT` 實際載入 CE 驗證。

---

## 7. 橫切面（不單獨成塊，各塊稽核時一併檢查）

| 項目 | 範圍 | 觀察到的訊號（待驗證） |
|---|---|---|
| 建置腳本 | `build.cmd`、`build.ps1`、根與 `dll/` 的 `CMakeLists.txt`、`RustCore.cmake` | `build.ps1:113` 的 `$SLN` 指向不存在的 `ui\Discrete.sln`（實際檔名是 `ui\UnityCEDumper.sln`），且變數未被使用。命名四套並存：Discrete / UnityCEDumper / UnityDumpUI / DiscreteUI。 |
| 相依套件釘版 | `Directory.Build.props`、各 `.csproj`、`tools/vendor_pins.json`、`sync_vendor.ps1`、`Cargo.lock` | `Directory.Build.props:56-60` 強調「必須精確釘版、不得用浮動範圍」，但 `CommunityToolkit.Mvvm 8.*`、`xunit.v3 3.*`、`xunit.runner.visualstudio 3.*` 都是浮動版本；`XunitV3Version=3.0.0` 屬性有定義，卻沒有被引用。 |
| 發行流程 | `.github/workflows/release.yml` | `release.yml:169-173`：tag 已有 release 時直接 `gh release edit --notes-file`，會把維護者改寫過的 notes 覆蓋回 raw commit list，與「release 維持 draft、notes 改寫成人看得懂的版本」規則衝突（若該 release 已發布，影響更大）。 |
| 身分防護 / hooks | `tools/check_no_local_paths.py`、`.githooks/pre-commit` | 確認 exit code 語意（1 = 洩漏、2 = 無法執行）在 hook 與 build 兩處一致。 |
| 文件漂移 | `docs/Archiutecture.md`（檔名本身拼錯）、`docs/plan.md`、`README*.md` | 架構圖仍寫 ReactiveUI、`Signatures`、`FieldExporter`、`Discrete.dll`，與現況不符。稽核時以程式碼為準，文件差異另列清單。 |
| 測試覆蓋 | 全域 | C++ DLL（區塊 A/B/C）**沒有任何離線單元測試**，只有需要真實遊戲行程的 `tools/Test-*.ps1`；C# 與 Rust 有單元測試。區塊 C 的雙胞胎 handler 是覆蓋率最低、風險最高的地方。 |

---

## 8. 建議稽核順序與產出

| 順序 | 區塊 | 理由 |
|---|---|---|
| 1 | **C — RPC 指令層** | 程式碼最集中（單一 10.6k 行檔案）、雙胞胎漂移風險最高、沒有離線測試；結果也決定 B 與 E 要優先看哪些路徑。 |
| 2 | **A — 基礎設施** | 出錯的後果最嚴重（遊戲崩潰 / 凍結）；eject 生命週期與多 client 併發要先釐清，C 的併發結論才站得住。 |
| 3 | **B — Runtime backend** | 依 C 產出的熱門指令清單，優先稽核被最多 handler 共用的 walker / finder。 |
| 4 | **D — Rust core** | 已有 golden 閘門與單元測試保護，風險相對低；重點放在 FFI 邊界與 i686。 |
| 5 | **E — UI 與 CE 輸出** | 規模最大但測試最多；用規則檢查（AOT、async、DataGrid、Lua logging、backend-agnostic）快速掃過。 |

**每個區塊的產出格式**（建議各自寫成 `docs/audit-<日期>-<區塊>.md`）：
1. 發現清單：`檔案:行號` · 類別（架構不一致 / 過時 API / 邊界條件 / 邏輯 bug）· 嚴重度 · 重現或觸發條件 · 建議修法
2. 本文 §2–§6「觀察到的訊號」逐條結案：確認 / 否決 / 降級，並附理由
3. 需要 live 驗證、但這次沒能驗證的項目，另外列出

**限制與前提**
- 修改程式碼前必須先完成該區塊的發現清單並經過確認；本文件本身不授權任何修改。
- 建置 / 測試一律透過 `.\build.cmd`（直接呼叫 `cmake` / `dotnet` 會缺少 VS Developer Shell 環境）。
- 涉及 offset / pointer size / 資料型別的結論，必須以真實 runtime 資料或格式規格為依據，不能從相鄰的 case 推論（x86 與 x64 都要驗證）。
