# Code Audit Report

## Block A — 行程內基礎設施與注入鏈（static review，2026-11-23，基準 `dev` @ `6bea633`）

> 範圍：`dll/src/main.cpp`、`dll/src/proxy/*`、`dll/src/shared/{BackendDetector,ModuleHelper,Logger,PipeServer,MainThreadDispatcher,ForegroundHook,Memory,RustCore,ScanCancel}`、`dll/src/{mono,il2cpp}/*MainThreadHook.*`、`tools/inject-x86/main.cpp`、`ui/.../Platform/{WindowsDllInjector,WindowsProxyInstaller}.cs`、`Core/Platform/{IDllInjector,IProxyInstaller}.cs`、`ProxyDeployViewModel.cs`、`tools/verify/proxy_precrt_gate.py`、`tools/check_no_local_paths.py`（+ `.githooks/pre-commit`、`build.ps1` 呼叫端）。
> 跨界只讀了 Block A 的邊界介面：`ExportAPI.cpp:10507-10646`（`Discrete_Init/Shutdown/StartPipe/StopPipe`）與 `scripts/unity_dump.lua:84-102`（確認實際出貨的 teardown 是 `Discrete_Shutdown`、不做 FreeLibrary）。
> 全部為靜態推論，沒有 live 驗證；標「待驗證」者推論鏈中有一環需要實機確認。本節沒有修改任何程式碼。

### Critical

（無。下列最嚴重的兩項都需要非預設操作或資源耗盡才會觸發，列為 High。）

### High

**A-H1 · FreeLibrary（`lpReserved == NULL`）收尾路徑在 loader lock 下必然卡住或在卸載後回跳進已 unmap 的程式碼**
- 位置：`dll/src/main.cpp:175-183`、`dll/src/main.cpp:214-221`；`dll/src/shared/PipeServer.cpp:174-201`、`:231-232`、`:342-394`、`:438-503`；`dll/src/mono/MonoMainThreadHook.cpp:166-185`；`dll/src/il2cpp/IL2CPPMainThreadHook.cpp:138-159`
- 成因：
  1. `OnDetach → Discrete_Shutdown → PipeServer::Stop` 在 DllMain 內、持有 loader lock 時執行。執行緒結束（`LdrShutdownThread` 送 `DLL_THREAD_DETACH`）需要拿 loader lock，所以 `Stop()` 對 acceptor 的 `WaitForSingleObject(..., 2000)`（`:465`）在這條路徑上**一定**逾時，2 秒白等，再 `detach()`。
  2. 更嚴重的是每個連線中的 client worker：`ServeClient` 結尾的 `monitor.join()`（`:232`）要等 monitor thread 結束，而 monitor thread 結束同樣卡在 loader lock → worker 永遠到不了 `g_activeClients.fetch_sub`（`:393`）→ `Stop()` 的 drain（`:483-485`）逾時、刻意洩漏 `g_stopEvent` 後返回 → DllMain 返回、模組被 unmap → loader lock 釋放後 monitor 結束，worker 的 `join()` 返回到**已 unmap 的 `ServeClient`** → access violation，遊戲崩潰。只要 FreeLibrary 當下有任何 client 連著（例如 UI 開著）就會發生。
  3. 即使沒有 loader lock，`fetch_sub`（`:393`）之後 worker 仍在執行本模組的程式碼（lambda epilogue、`std::thread` 的 `_Invoke` trampoline），`Stop()` 一看到 0 就可能讓 FreeLibrary 繼續，存在一段卸載競態窗口。
  4. `MainThreadHook::Uninstall` 在 loader lock 下 `MH_DisableHook`（SuspendThread 所有執行緒）並 Sleep 等 `g_inDetour` 歸零；若主執行緒正在 detour 內等 loader lock，2000 次後照樣 `MH_RemoveHook` 並卸載，主執行緒回到已 unmap 的 detour。
  5. `PipeServer.cpp:323-328` 的註解宣稱「DLL unload only happens at game exit」，與 `main.cpp:187-208` 明確處理 FreeLibrary 路徑、`Stop()` 自己的註解（`:476-482`）互相矛盾 —— 這正是 audit-2026-11-23-plan.md §2 訊號 1。出貨的 Lua bridge 已經刻意不做 FreeLibrary（`scripts/unity_dump.lua:84-91`），但 CE 的「unload module」、Process Hacker、第三方 injector 都能觸發這條路徑。
- 修法：既然唯一支援的 teardown 是 `Discrete_Shutdown`（模組保持 mapped），就在 `OnAttach` 成功 claim 後用 `GetModuleHandleExW(GET_MODULE_HANDLE_EX_FLAG_PIN | ..._FROM_ADDRESS)` 把模組 pin 住，讓外部 FreeLibrary 不能真的卸載；`DLL_PROCESS_DETACH` 一律直接 `return TRUE`，不要在 DllMain 內做任何 join / drain / MinHook 操作。若日後確實需要 eject，改成提供 `Discrete_Unload` export：在 remote thread 上（不持 loader lock）先跑 `Discrete_Shutdown`，再以 `FreeLibraryAndExitThread` 結束；每個 client worker / monitor 建立時各自持一份 `GetModuleHandleEx` 參考，並以 `FreeLibraryAndExitThread` 收尾（比照 `main.cpp:142-156` 的 retention thread 作法），讓「計數歸零」和「離開模組程式碼」成為同一個動作。

**A-H2 · `ScanForValue` / `ScanForString` 宣告 `noexcept`，但內部會配置記憶體、開 thread；在 32-bit 遊戲中失敗會 `std::terminate` 直接殺掉遊戲**
- 位置：`dll/src/shared/Memory.cpp:807-808`、`:920-921`（`noexcept`）；`dll/src/shared/Memory.cpp:503`、`:525-528`（`RunParallelScan`）；`dll/src/shared/Memory.h:190-191`、`:245-246`
- 成因：`FilterScanRegions`、`BuildScanChunks`、`perChunk(chunks.size())`、`out.reserve` 都可能丟 `bad_alloc`；`pool.emplace_back(worker)` 在位址空間吃緊時（x86 下最多 `hc-2`、上限 16 條 thread，每條預留 1 MB stack）會丟 `std::system_error`。例外一旦離開 `noexcept` 函式就是 `std::terminate`。另外，`RunParallelScan` 第 N 條 thread 建立失敗時，前面已經 joinable 的 `std::thread` 在 `pool` 解構時也會觸發 `std::terminate`，`ScanForPointer` 雖然沒有 `noexcept` 也一樣逃不掉。audit-2026-11-23-plan.md 提到的「32-bit 遊戲 2 GB 位址空間 `bad_alloc`」就落在這裡。
- 修法：拿掉兩個函式的 `noexcept`，或在函式本體外包 `try { … } catch (...) { 回傳空結果 + 在 stats 標記失敗 }`；`RunParallelScan` 的建 thread 迴圈要捕捉例外，先 join 已經啟動的 thread，再退回單執行緒（`worker()` inline）繼續掃描。

### Medium

**A-M1 · 某個 client 斷線時，可能取消**另一個** client 正在跑的 scan**
- 位置：`dll/src/shared/PipeServer.cpp:166-201`（特別是 `:171-172`、`:178-186`）；`dll/src/shared/ScanCancel.h:55-58`
- 成因：每個 client 的 disconnect monitor 都用全域的 `g_scanActive > 0` 當「該不該 probe」的條件，而不是「**本 client** 是否有 scan 在跑」。client A（UI）在 value scan 時，client B（CE bridge）只要正在跑一個非 scan 的長 handler（例如 `findFieldReferences`、經 MainThreadDispatcher 的 `callMethod`），主迴圈就不會讀 pipe，於是 B 斷線只能由 B 的 monitor 發現 → `RequestCancel()` → A 的 scan 被取消，必然發生。即使 B 閒置，monitor 剛醒來、主迴圈還沒設 `monitorStop` 的短暫窗口內也可能誤觸。`:171-172` 的註解（「scan 進行中主執行緒不做 pipe IO」）在多 client 下不成立。
- 修法：monitor 只在「本 worker thread 持有 `ScanCancel::Scope`」時才 probe（由 Scope 設一個 per-thread / per-client 旗標，monitor 讀它）；或者把 cancel 改成 per-client token（Scope 記錄擁有者，`RequestCancel(owner)` 只對同一個 owner 生效）。

**A-M2 · `ReadLine` 會丟掉同一次 `ReadFile` 中 `'\n'` 之後的位元組**
- 位置：`dll/src/shared/PipeServer.cpp:85-130`（`:116-122`）
- 成因：遇到第一個 `'\n'` 就 `return true`，`buf` 裡剩下的資料（下一個 request 的前半或整段）沒有保留到下一次呼叫。byte-mode pipe 會合併寫入；只要 client 在上一個回應之前送出第二行（pipelining，或在同一條連線上送 `cancelScan`），第二個 request 就會靜默消失，client 永遠等不到它的回應。
- 修法：每個連線維持一個跨呼叫的 receive buffer（例如 `ServeClient` 擁有的 `std::string pending`），`ReadLine` 先從 `pending` 切行，切不出來才再 `ReadFile`。

**A-M3 · Named pipe 缺少遠端拒絕與 instance 擁有權保護，而且 pipe 名稱是全機共用**
- 位置：`dll/src/shared/PipeServer.cpp:263-268`；`dll/src/shared/MagicStrings.h:9`
- 成因：`CreateNamedPipeW` 沒有帶 `PIPE_REJECT_REMOTE_CLIENTS`，安全描述子是 `nullptr`（default DACL）。這條 pipe 提供任意記憶體讀寫和 managed method 呼叫，在 SMB 可達的情況下，同帳號或系統管理員的認證就能從網路連進來。另外沒有 `FILE_FLAG_FIRST_PIPE_INSTANCE`，名稱又固定是 `\\.\pipe\DiscreteBfx`：同時注入兩個遊戲時，兩個行程會在同一個名稱下各建 instance，UI 連到哪一個不可預期；任何同使用者的行程也能搶先建立這個名稱來冒充 DLL。
- 修法：flags 加上 `PIPE_REJECT_REMOTE_CLIENTS`；用明確的 SDDL（只允許目前使用者 + SYSTEM）建立 `SECURITY_ATTRIBUTES`；第一個 instance 加 `FILE_FLAG_FIRST_PIPE_INSTANCE`，失敗就記錄「名稱已被佔用」；長期考慮把 PID 放進 pipe 名稱（需要 UI / Lua 端配合，屬 Block C / E）。

**A-M4 · 公開 export 繞過 per-PID single-init 閘門**
- 位置：`dll/src/main.cpp:94-103`；`dll/src/shared/ExportAPI.cpp:10507-10509`、`:10628-10632`
- 成因：`TryClaimSingleInit` 只在 DllMain 裡把關。第二份 DLL（例如 proxy 載入遊戲目錄那份，UI 又注入 `dist\` 那份；路徑不同，所以是兩個模組）雖然跳過 init，但它的 `Discrete_Init` / `Discrete_StartPipe` export 仍然可以呼叫（CE bridge 會明確呼叫 `Discrete_StartPipe`，CE 依符號名解析時可能解析到第二份）。結果是同一行程內有第二個 pipe server（同名 instance）、第二套 handler table，之後還可能有第二個 `runtime_invoke` MinHook detour（每個模組各帶一份 MinHook 狀態，會互相疊加）。
- 修法：把 claim 結果存進模組層級的旗標（例如 `g_ownsInit`）；`Discrete_Init` / `Discrete_StartPipe` 在 `!g_ownsInit` 時直接回傳錯誤碼並寫 log。

**A-M5 · Proxy 的 backend 偵測 5 秒就放棄，而且不留任何 log**
- 位置：`dll/src/proxy/loader.cpp:108-123`、`:101-131`
- 成因：proxy 在行程啟動的極早期就被載入，只輪詢 50 × 100 ms。舊版 Unity 啟動時有「Screen resolution dialog」，dialog 在 mono / GameAssembly 載入**之前**出現、等使用者操作；慢速磁碟或啟動器也常超過 5 秒。逾時後執行緒回傳 2 就結束，什麼都沒記錄，使用者看到的症狀是「proxy 裝好了但永遠連不上」。回傳 1 / 3（找不到目錄、`LoadLibraryW` 失敗，包含 x86 IL2CPP 遊戲找不到 `Discrete-IL2CPP.dll`）也一樣沒有 log。
- 修法：改用 `LdrRegisterDllNotification`（ntdll，動態解析）在 mono / GameAssembly 載入時觸發，或改成無上限、指數退避的輪詢；三種失敗回傳前都 `Log::Init()` 並寫一行原因。

**A-M6 · Memory.cpp 的掃描 helper 違反 SEH-leaf rule，把 `bad_alloc` 當成「讀取失敗」吞掉**
- 位置：`dll/src/shared/Memory.cpp:229-284`（`SehScanRegion` 的 `__try` 內有迴圈和 `matches.push_back`）、`:791-802`（`SehDispatchScanChunk` → `ScanChunk` 的 `push_back`）、`:905-915`（`SehScanStringChunk` → `SehMemcmpProbe` 的 `push_back`）
- 成因：`docs/Archiutecture.md` §Rules 規定 `__try` 本體只能是單一 call / 單一記憶體存取。這三處的 `__except(EXCEPTION_EXECUTE_HANDLER)` 會連 C++ 例外（`0xE06D7363`，例如 32-bit 下 vector 擴容的 `bad_alloc`）一起攔下：結果被靜默截斷、算成 `readFailureBytes`（`ScanForPointer` 甚至不計），例外物件也不會被釋放。
- 修法：讓 `__try` 只包「讀一段遊戲記憶體到本地 buffer」這個 leaf（例如每次 `memcpy` 一個 4–64 KB 視窗到 stack/thread-local buffer），比對與 `push_back` 移到 `__try` 外；或者改用 exception filter，只對 `EXCEPTION_ACCESS_VIOLATION` / `EXCEPTION_IN_PAGE_ERROR` 回傳 `EXECUTE_HANDLER`，其他一律 `CONTINUE_SEARCH`。

**A-M7 · `MainThreadHook::Uninstall` 的 detour drain 有進入競態，而且實際上限是約 31 秒而不是 2 秒**
- 位置：`dll/src/mono/MonoMainThreadHook.cpp:87`、`:94-101`、`:166-185`；`dll/src/il2cpp/IL2CPPMainThreadHook.cpp:65`、`:71-75`、`:138-159`
- 成因：
  1. 執行緒已經跳進 `HookedRuntimeInvoke`、但還沒執行 `g_inDetour.fetch_add`（`:87`）時，`Uninstall` 若剛好看到 0，就會 `MH_RemoveHook` 釋放 trampoline，並把 `g_orig_runtime_invoke` 設成 `nullptr`（非 atomic 讀寫）。該執行緒接著不是呼叫已釋放的 trampoline，就是走 `nullptr` 分支、回傳 NULL 卻沒有真正呼叫 Unity（`:94-101`）。窗口很小，但結果是崩潰或靜默吃掉一次 managed 呼叫。
  2. 2000 × `Sleep(1)` 在預設 15.6 ms 計時器解析度下最長約 31 秒；`scripts/utils.lua` 對 `Discrete_Shutdown` 只給 10 秒。逾時後照樣 remove，而且 log 仍寫「hook removed」，看不出逾時。
- 修法：用 `GetTickCount64()` 做真正的時間上限，逾時時寫 WARN 並**跳過** `MH_RemoveHook`（只 disable、保留 trampoline，模組本來就不卸載）；`g_orig_runtime_invoke` 改成 `std::atomic` 並且不要清成 `nullptr`（trampoline 洩漏遠比 NULL 路徑安全）。

**A-M8 · `Discrete_Shutdown` 的收尾順序讓卡在 `SubmitAndWait` 的 worker 拖垮 pipe drain**
- 位置：`dll/src/shared/ExportAPI.cpp:10608-10626`（邊界）；`dll/src/shared/PipeServer.cpp:483-503`；`dll/src/shared/MainThreadDispatcher.cpp:190-206`、`:337-338`
- 成因：`PipeServer::Stop()` 先 drain client worker（上限 2 秒），`MainThreadDispatcher::Shutdown()`（會 abort 排隊中的工作）卻在最後才跑。若 worker 正在 `SubmitAndWait`（遊戲失焦暫停、loading 中，或 `timeout_ms < 0` 時是 INFINITE），drain 必定逾時 → `g_stopEvent` 被洩漏 → 之後重新 `Start()` 時，舊 worker 會讀到新的 event（見 A-L6）。
- 修法：`Stop()` 設好 `g_running = false` 並 signal 之後，在 drain 之前先呼叫 `MainThreadDispatcher::Shutdown()`（或拆出一個只負責 abort pending 的 `AbortPending()`）；`Discrete_Shutdown` 中 hook 的 uninstall 順序維持不變。

**A-M9 · Proxy installer 的 Detect 與 Install 對「第三方 proxy 共存」的判斷互相矛盾**
- 位置：`ui/UnityDumpUI/Platform/WindowsProxyInstaller.cs:58-73`、`:86-88`、`:207-212`；`ui/UnityDumpUI/ViewModels/ProxyDeployViewModel.cs:164-170`
- 成因：`InstallSync` 刻意允許「別人的 `version.dll` + 我們的 `winmm.dll`」共存（`:207-210`），但 `DetectSync` 只要兩個檔案同時存在就回報錯誤、`Kind = None`（`:65-70`）；而且 Detect 判斷 kind 時只看 `File.Exists`，沒有用 `IsOurDll`，所以第三方的 `version.dll` 會被當成「已安裝、版本過舊」。後果：剛安裝成功的遊戲立刻顯示錯誤或未安裝；「Update all deployed」會對第三方 proxy 執行 Install，然後被拒絕，回報失敗。
- 修法：Detect 對兩個檔案都先做 `IsOurDll`，只把「我們的」算進 `Kind`；兩個都是我們的才回報錯誤；第三方檔案另外放進 `ForeignProxyPresent` 之類的欄位給 UI 顯示。

**A-M10 · ProxyDeploy 視窗：批次作業進行中仍然可以按 Refresh，會在 `foreach` 中途改動集合**
- 位置：`ui/UnityDumpUI/ViewModels/ProxyDeployViewModel.cs:101-110`（`Games.Clear()` / `Add`）、`:207`（`foreach (… in Games)` 中間有 `await`）；`ui/UnityDumpUI/Views/ProxyDeployWindow.axaml:26-27`（Refresh 只綁 `!IsScanning`）
- 成因：`RunBulkAsync` 在 `await` 之間列舉 `Games`；使用者在批次期間按 Refresh（或批次開始時 scan 剛好完成），`Games` 被清空重建 → 下一次 `MoveNext` 丟 `InvalidOperationException`。這個例外在 `RunBulkAsync` 內沒有 catch，會一路傳到 `AsyncRelayCommand`，預設會在 UI thread 重新拋出。
- 修法：Refresh 的 `IsEnabled` 改綁 `!IsScanning && !IsBusy`，`RefreshAsync` 開頭也檢查 `IsBusy`；`RunBulkAsync` 改成列舉快照（`foreach (var row in Games.ToList())`）。

**A-M11 · 字串掃描的 AVX2 fast-path 在 alignment 不整除 32 時，scalar tail 會偏離掃描格點**
- 位置：`dll/src/shared/Memory.cpp:867-891`（`SehMemcmpProbe`）；`:924-927`
- 成因：AVX2 迴圈結束時 `i` 是 32 的倍數，scalar 迴圈直接從這個 `i` 開始、每次加 `stride`。`req.alignment` 是 3、5、6、7、64… 這類不整除 32 的值時，tail 掃的是 `32, 32+s, …`，不在 `0, s, 2s…` 的格點上 → 漏掉格點上的命中，同時回報格點外的假命中；結果還會隨 CPU 有沒有 AVX2 而不同。
- 修法：SIMD 迴圈結束後先把 `i` 進位到下一個 `stride` 的倍數（`i = (i + stride - 1) / stride * stride`），再進 scalar 迴圈；或者 `32 % stride != 0` 時直接停用 SIMD 路徑。

**A-M12 · `check_no_local_paths.py` 的 `SESSION_DIR_PATTERN` 有災難性回溯風險，可能讓每次 build 卡住（待驗證）**
- 位置：`tools/check_no_local_paths.py:83-84`；呼叫端 `build.ps1:389`
- 成因：`(?:[^\s:*?"<>|]{0,120}SEP)*?` 中的字元類別**沒有排除路徑分隔字元**，所以一段「X:/」後面跟著很多 `/`、又沒有 `.claude/projects` 的字串，可以用指數多種方式切成各次重複；比對失敗時 Python `re` 會全部試過一遍。identity guard 在每次 `build.cmd` 開頭都會跑，任何被追蹤的檔案只要有一行這種長路徑（log fixture、minified JSON），就可能讓 build 卡死。（這次無法在環境中實際計時，請用 `"C:/" + "a/"*25 + "x"` 驗證。）
- 修法：把字元類別改成 `[^\s:*?"<>|\\/]{0,120}`（排除分隔字元），讓每次重複的切法唯一，複雜度回到線性。

### Low

**A-L1 · per-PID mutex 用 `bInitialOwner=TRUE`，`ReleaseMutex` 由別的 thread 呼叫必然失敗（確認 AUDIT 訊號 4：無害雜訊）**
- 位置：`dll/src/main.cpp:77`、`:179`
- 成因：mutex 的擁有者是 loader thread；`OnDetach` 通常在其他 thread 執行，`ReleaseMutex` 回傳 `ERROR_NOT_OWNER`。閘門只依賴 `ERROR_ALREADY_EXISTS`，所以功能不受影響；owner thread 結束後 mutex 變成 abandoned，也沒有人等它。
- 修法：改用 `bInitialOwner=FALSE` 並刪掉 `ReleaseMutex`，只保留 `CloseHandle`。

**A-L2 · 文件與實作不一致 / 過時註解（含 AUDIT 訊號 2、3）**
- 位置與內容：
  - `PipeServer.h:44-45`：宣稱 `RegisterDisconnectHook` 具 idempotent，但 `PipeServer.cpp:410-414` 只有 `push_back`（`std::function` 也沒辦法比對 target）。
  - `PipeServer.h:8-9`：「single in-flight client / multi-client deferred」，與 `kMaxPipeInstances = 4` + per-client thread 不符。
  - `PipeServer.h:65`：「blocks until stopped」，實際是有上限的等待，而且可能 detach。
  - `PipeServer.cpp:323-328`：與 `Stop()` 的 drain 互相矛盾（見 A-H1）；`:171-172`：見 A-M1。
  - `PipeServer.cpp:14-15`：重複 `#include <thread>`。
  - `dll/CMakeLists.txt:262` 與 `dll/src/proxy/loader.h:16-18`：「x86 not yet built」，但 x86 proxy 已經出貨（`WindowsProxyInstaller.cs:89-94`）。
  - `main.cpp:63-64`：說 proxy 也會跑同一個 `OnAttach`，實際上 proxy 有自己的 DllMain。
  - `Logger.h:18-20`：說 proxy 只在 worker thread 寫 log，但 `loader.cpp:219-225` 會在 DllMain 失敗時寫。
  - `ForegroundHook.h:35-38`：說 subclass 會保留，但 `ForegroundHook.cpp:349` 會還原。
  - `MainThreadDispatcher.h:22`：說 `Shutdown()` 會 uninstall hooks，實際沒有；`:50-53` 的註解寫 32 ms，常數卻是 1000 ms。
  - `Memory.h:183-185`：說掃描是「single-threaded」、用 `SafeReadBytes`，實際是平行 + `__try` chunk。
- 修法：逐條改註解；`RegisterDisconnectHook` 若要 idempotent，就改成回傳 token / 以字串 key 註冊。

**A-L3 · `Discrete_StopPipe` → `Discrete_StartPipe` 會讓 disconnect hooks 永久消失**
- 位置：`dll/src/shared/PipeServer.cpp:505-509`；`dll/src/shared/ExportAPI.cpp:10628-10636`
- 成因：`Stop()` 一律 `ClearDisconnectHooks()`，但 `Discrete_StopPipe` 不會清掉 `g_initialized`，之後的 `StartPipe` 也就不會重新 `Discrete_Init`，GC drain 與「最後一個 client 離開時丟棄 session」的 hook 都不會再註冊。repo 內沒有人呼叫 `Discrete_StopPipe`（dead export），所以列為 Low。
- 修法：把 `ClearDisconnectHooks()` 從 `Stop()` 移到 `Discrete_Shutdown`；或者直接移除 `Discrete_StopPipe` export。

**A-L4 · `WriteLine` 為了附加 `'\n'` 複製整份回應**
- 位置：`dll/src/shared/PipeServer.cpp:53-55`
- 成因：大型回應（snapshot / scan）的尖峰記憶體變成兩倍，在 32-bit 下更容易 `bad_alloc`（會被 worker 的 catch 吃掉，client 被斷線）。
- 修法：讓 `serialized` 直接 `push_back('\n')` 後以 move 傳入，或分成兩次 `WriteFile`。

**A-L5 · `ReadLine` 超過 1 MiB 時直接斷線，不回錯誤**
- 位置：`dll/src/shared/PipeServer.cpp:123-126`
- 成因：client 只看到 broken pipe，分不出是 request 太大還是 DLL 掛了。
- 修法：先回一個 `-32600 "request too large"` 再斷線，並寫 log。

**A-L6 · Stop / Start 循環的邊界狀況**
- 位置：`dll/src/shared/PipeServer.cpp:285`、`:276-281`、`:425-436`、`:470`
- 成因：`ConnectNamedPipe` 用的 event 沒檢查 `CreateEventW` 失敗（NULL event 會導致 wait 立刻失敗、迴圈空轉）；4 個 slot 都滿時每 500 ms 寫一行 log；acceptor 被 `detach()` 之後若又 `Start()`，`g_running` 重新變成 true，舊 acceptor 也會繼續跑（同時有兩個 acceptor），洩漏的舊 `g_stopEvent` 被新的覆蓋後，舊 worker 會等在新 event 上；`std::thread` 建構失敗時 `g_running` 停在 true、event 洩漏，例外還可能從 DllMain 傳出去。
- 修法：檢查 event 建立結果；log 加節流；`Start()` 在偵測到上一輪 acceptor 還活著時拒絕啟動；把 `std::thread` 建構放進 try 並回滾狀態。

**A-L7 · `ScanCancel::RequestCancel` 的 check-then-store 有 TOCTOU**
- 位置：`dll/src/shared/ScanCancel.h:55-58`
- 成因：讀到 `g_scanActive > 0` 之後、`store(true)` 之前，最後一個 Scope 可能已經離開並清掉旗標，結果留下一個「已 arm」的旗標。下一個 outermost Scope 進場時會清掉，所以只影響沒有持有 Scope 的 polling 端（註解說這種情況只是 advisory）。
- 修法：用 CAS 迴圈，或在 Scope dtor 最後一位離開時再清一次（先 store false、再檢查 count）。

**A-L8 · 注入器的成功判定與權限**
- 位置：`ui/UnityDumpUI/Platform/WindowsDllInjector.cs:31`、`:48`、`:113-122`
- 成因：x64 HMODULE 的低 32 位元在 64 KB 粒度的 ASLR 下約有 1/65536 機率剛好是 0，會被誤判成載入失敗；`0x1F0FFF` 是 XP 時代的 `PROCESS_ALL_ACCESS`，要求的權限遠多於需要（對受保護的行程更容易失敗）；沒有檢查 DLL 的 PE machine 是否與目標 bitness 相符（錯配時只會看到 `LoadLibraryW returned NULL`）。
- 修法：失敗時再用 `EnumProcessModulesEx` / Toolhelp 確認模組是否已經載入；權限只要求 `CREATE_THREAD | VM_OPERATION | VM_WRITE | VM_READ | QUERY_INFORMATION`（與 `inject-x86` 的 `kAccess` 一致）；注入前讀 DLL 的 PE machine 並與 `IsWow64Process` 結果比對。

**A-L9 · `inject-x86` 把 `IsWow64Process` 失敗回報成「目標是 x64」，非 ASCII 路徑會截斷 stdout**
- 位置：`tools/inject-x86/main.cpp:89-98`、`:139-146`、`:125-126`
- 成因：API 失敗（存取被拒）時回傳 false → exit 2 → UI 顯示「ProcessPicker 誤判 bitness」，誤導使用者；`wprintf` 在 C locale 下遇到非 ASCII 的 DLL 路徑會在該字元停止輸出（僅影響 log）。
- 修法：API 失敗時改走 exit 1，並帶 `GetLastError`；印路徑前先設定 `_setmode(_fileno(stdout), _O_U8TEXT)`，或改成手動轉 UTF-8 輸出。

**A-L10 · Proxy 的小問題**
- 位置：`dll/src/proxy/loader.cpp:141`、`:169`（`noexcept` 函式內建 `std::wstring`，`bad_alloc` 會 `terminate`）；`dll/src/proxy/version/version.cpp:68-75`、`dll/src/proxy/winmm/winmm.cpp:131-138`（`static fn_t fn` 跨執行緒非 atomic 讀寫，在 x86/x64 上是良性 data race，但形式上屬於 UB）；`dll/src/proxy/winmm/winmm.cpp:142-242`（只轉發 UnityPlayer.dll 用到的子集，遊戲另外帶的 middleware 若 import `waveOutSetVolume` / `waveOutPause` / `midiStream*` / `mixer*` 等，會出現「找不到程序進入點」而無法啟動；待驗證）。
- 修法：`SystemDllPath` 改用固定大小的 `wchar_t` buffer；`fn` 改成 `std::atomic<fn_t>`（constant-init，符合 Pre-CRT rule 3 的精神）並用 relaxed load/store；建一份常見 middleware 的 winmm import 清單，用 `dumpbin /imports` 對照補齊。

**A-L11 · Logger 跨模組併寫與 DllMain 內的例外風險**
- 位置：`dll/src/shared/Logger.cpp:60`、`:167-173`、`:321-323`
- 成因：proxy、backend 和「被跳過 init 的第二份 DLL」各有一份 Logger（各自的 `g_mutex`），卻寫同一個 `current.log`；CRT 的 append 是「seek 到檔尾 + write」，不是 atomic，跨模組可能互相覆寫；一方開著檔案時另一方 rotate 會遇到 sharing violation（程式已註明會留到下次重試）。`fs::temp_directory_path()` 用的是會丟例外的 overload，而且是在 DllMain 的 `Log::Init` 裡呼叫。
- 修法：用 `CreateFileW(FILE_APPEND_DATA, FILE_SHARE_READ|WRITE|DELETE)` + `WriteFile` 取代 `ofstream`（核心保證 append 是 atomic，rename 也不會被擋）；改用 `temp_directory_path(ec)` 這個 overload。

**A-L12 · Magic string：`LogLoadedModules` 直接寫 `L"init"`**
- 位置：`dll/src/main.cpp:59`
- 修法：改成 `MagicStrings::LogCategoryInit`。

**A-L13 · `AOBScan` / `AOBScanModule` 是 dead code，而且沒有 SEH 保護**
- 位置：`dll/src/shared/Memory.cpp:158-186`；`Memory.h:49-57`
- 成因：repo 內沒有任何呼叫者；`AOBScanModule` 直接讀整個 `SizeOfImage`，遇到 `PAGE_NOACCESS` 的 section（packer / anti-tamper）會讓遊戲崩潰。之後若有人重新啟用，就是現成的地雷。
- 修法：刪除，或改成透過 Rust `aob` + `SafeReadBytes` 的版本。

**A-L14 · SEH 讀取會吞掉 `STATUS_GUARD_PAGE_VIOLATION`**
- 位置：`dll/src/shared/Memory.cpp:66-82`、`:118-143`
- 成因：`IsReadableSeq` 的 thread-local region cache 和 `VirtualQuery` 之後的 TOCTOU，都可能讓 `memcpy` 碰到新出現的 guard page。`EXCEPTION_EXECUTE_HANDLER` 會吃掉一次性的 guard 例外，guard 屬性因此被移除；若那是其他 thread 的 stack guard，該 thread 之後就無法擴充 stack。
- 修法：exception filter 只接受 `EXCEPTION_ACCESS_VIOLATION` / `EXCEPTION_IN_PAGE_ERROR`；遇到 guard page 時用 `VirtualProtect` 把 `PAGE_GUARD` 補回去再回傳失敗。

**A-L15 · `g_scanThreadCap` 是全域狀態，多個 client 同時掃描時互相覆寫**
- 位置：`dll/src/shared/Memory.cpp:597-607`；`Memory.h:97-108`
- 成因：註解假設「scans are serialized in practice」，但 pipe 允許 4 個 client 並行；一個 scope 結束時把 cap 歸零，另一個「強制單執行緒」的 scan 中途就變回全平行（正好違背這個開關要對付 anti-tamper 的目的）。
- 修法：把 cap 存在 thread-local，或作為 `RunParallelScan` 的參數傳入。

**A-L16 · `SubmitAndWait` 的 inline main-thread 路徑沒有設定 `t_inside_work_item`**
- 位置：`dll/src/shared/MainThreadDispatcher.cpp:289-301`
- 成因：在主執行緒上 inline 執行的 closure 裡，`ThreadAttachGuard` 會判斷「不在 drain 中」而照樣做 attach / detach，正是 build #54 的問題型態；目前沒有實際會走到的路徑。
- 修法：inline 執行期間比照 `Drain()` 設定並還原 `t_inside_work_item`。

**A-L17 · Proxy deploy 的批次作業會作用在被 filter 隱藏的已選列；安裝不是原子操作**
- 位置：`ui/UnityDumpUI/ViewModels/ProxyDeployViewModel.cs:138-156`、`:207-209`；`ui/UnityDumpUI/Platform/WindowsProxyInstaller.cs:204-223`
- 成因：選取狀態跨 filter 保留，Install / Uninstall 列舉的是 `Games` 而不是 `FilteredGames`；複製到一半失敗（例如遊戲在執行、檔案被鎖）會留下新舊版本混雜的 DLL，而另一個 proxy 已經先被刪除；`_scanCts` 從未 Dispose。
- 修法：批次作業只處理 `FilteredGames`（或在確認對話框中列出隱藏的列）；先複製到 `*.tmp`，全部成功後再 `File.Move(overwrite)`；替換 `_scanCts` 前先 Dispose 舊的。

**A-L18 · Identity guard 的覆蓋與語意**
- 位置：`tools/check_no_local_paths.py:113-124`、`:249`、`:307-317`、`:330`；`.githooks/pre-commit:21-26`；`build.ps1:389-401`
- 成因：
  1. `runtime_identifiers()` 用 word-bounded 比對 `USERNAME` / `COMPUTERNAME` / `USERDOMAIN` / home 目錄名稱；遇到常見字（例如 Linux runner 的 home 名稱 `runner` 會命中 `xunit.runner.visualstudio`，或 `admin`、`build`、網域名 `CORP`）就產生假的 exit 1，而且整個 build 會被擋下。
  2. `--list` 標榜「do not exit 1」，但 commit message 有命中時仍回傳 1，而且會提前 return，同時存在的檔案命中不會印出。
  3. 只掃 working tree 與未 push 的 commit **message**，不掃未 push 的 commit **內容**：先 commit 洩漏、再只在 working tree 修掉，gate 會通過，push 後就公開了。
  4. 確認 AUDIT §7 的訊號：exit code 語意在 `build.ps1:394-401` 是一致的（1 = 洩漏、其他非 0 = 無法執行）；但 `.githooks/pre-commit` 只檢查 Lua 的 CRLF，**沒有**跑 identity guard，所以 hook 端不存在一致性問題，也就不存在 commit 時的防線。另外 hook 的 `for f in $files` 遇到含空白的檔名會拆錯。
- 修法：identifier 加上最短長度 / 常見字 deny-list，並允許用環境變數 opt-out 單一 identifier；`--list` 在所有分支都回傳 0；加掃 `git diff @{push}..HEAD`（或 `--branches --not --remotes -p`）的新增行；在 pre-commit 中以 `--staged` 模式呼叫 checker（掃 index 內容）；hook 改用 `git diff -z` + `while IFS= read -r -d ''`。

**A-L19 · `proxy_precrt_gate.py --all` 在兩個 proxy 都沒 build 時回報 PASS**
- 位置：`tools/verify/proxy_precrt_gate.py:330-334`、`:370-375`
- 成因：「not built」只印 `[SKIP]`，不計入 failures，最後印 `PASS` 並 exit 0；一個什麼都沒測的閘門看起來跟通過一樣（同檔對 bitness 不符的處理反而有計為失敗，兩者不一致）。
- 修法：沒有任何 proxy 被測到時回傳非 0，或至少印「NOTHING TESTED」並 exit 2。

**A-L20 · Disconnect hook 在每次 client 斷線都觸發 full GC（跨模組觀察）**
- 位置：契約在 `dll/src/shared/PipeServer.h:26-45`；hook 本體在 `dll/src/shared/ExportAPI.cpp:10536-10556`、`:10560-10577`（Block C）
- 成因：CE bridge 的設計是「短連線 burst」，每次 burst 斷線都會在遊戲裡跑一次 `mono_gc_collect(-1)` / `il2cpp_gc_collect(0)`（log 顯示約 50–200 ms），也就是每次 bridge 操作都造成一次可感知的卡頓。
- 修法：GC drain 只在 `ActiveClientCount() == 1`（最後一個 client 離開）時執行，或者只在該 client 跑過標記為「重量級」的 RPC 後才執行。

### audit-2026-11-23-plan.md §2「觀察到的訊號」結案

| # | 訊號 | 結論 |
|---|---|---|
| 1 | eject 生命週期矛盾 | **確認並升級** → A-H1。不只是「worker 可能還在跑 handler」：在 FreeLibrary 路徑上，`monitor.join()` 在 loader lock 下必然卡住，只要有任何 client 連著就會回跳到已 unmap 的程式碼。出貨的 Lua teardown 不走這條路，所以不是 Critical。 |
| 2 | `RegisterDisconnectHook` idempotent 文件不符 | **確認**（A-L2）。實際後果由 `Stop()` 的 `ClearDisconnectHooks()` 擋掉，但這又衍生出 A-L3。 |
| 3 | 過時註解（`PipeServer.h:8-9`、`CMakeLists.txt:262`） | **確認**（A-L2），另外補了 7 處。 |
| 4 | mutex `bInitialOwner=TRUE` / 跨 thread `ReleaseMutex` | **確認但無害**（A-L1）。 |

### 需要 live 驗證、這次沒能驗證的項目
- A-H1：對已注入、UI 連線中的遊戲以外部工具 FreeLibrary，確認會崩潰；確認在 DllMain 中等待 acceptor 必然吃滿 2 秒（init log 會出現 `worker did not exit within the timeout`）。
- A-M1：UI 跑 value scan 時，讓 CE bridge 在一個長 handler 中途斷線，確認 UI 的 scan 收到 `cancelled`。
- A-M2：同一條連線連續寫兩行 request 不等回應（`tools/Test-Handshake.ps1` 改一下即可），確認第二行沒有回應。
- A-M5：用一款有 resolution dialog 的 Unity 5.x 遊戲，在 dialog 停留超過 5 秒，確認 proxy 不會載入 backend。
- A-M7：在 x86 Mono 遊戲中計時 `Discrete_Shutdown`（`tools/smoke/stall_probe.py`）。
- A-M12：用 `"C:/" + "a/"*25 + "x"` 計時 `SESSION_DIR_PATTERN.search`。
- A-L10：對常見 Unity middleware（CRI、Wwise、FMOD standalone、SDL2）做 `dumpbin /imports … | findstr winmm`，與 `winmm.def` 比對。

---

## Block B — Mono / IL2CPP Runtime 內省 backend（static review，2026-11-23，基準 `dev` @ `6bea633`）

> 範圍：`dll/src/mono/*`（不含 `MonoMainThreadHook.*`）、`dll/src/il2cpp/*`（不含 `IL2CPPMainThreadHook.*`、`MetadataParserRs.cpp`、`MetadataParserHostPath.cpp`、`IL2CPPChainDetectorRs.cpp`、`IL2CPPFieldXrefScanRs.cpp`，這些屬 Block A / D）、`dll/src/shared/{ManagedLayout.h,MethodSignatureFormat.h}`。
> 跨界只做了兩次 grep：`ExportAPI.cpp` 對 `DiagnoseClassReferences` / `ResolveInstanceLookup` 的呼叫點（確認 AUDIT 訊號 3 與 array 分支是否可達），以及 `docs/address-finder.md` 中是否記載 generic instance 的限制（沒有記載）。
> 全部為靜態推論，沒有 live 驗證；標「待驗證」者的推論鏈中有一環依賴 runtime 內部配置，需要實機確認。本節沒有修改任何程式碼。

### Critical

（無。最嚴重的兩項會產生「看起來成功、其實錯誤」的結果，但不會讓遊戲崩潰，列為 High。）

### High

**B-H1 · x86 Mono：heap walker 與 `WhatIsAt` 用 `& 7` 過濾 vtable 指標，會丟掉所有「有實作 interface 的類別」的 instance（待驗證）**
- 位置：`dll/src/mono/MonoAddressFinder.cpp:706`（`WhatIsAt`：`(vtCandidate & 7u) != 0`）、`:839`（`SehSweepRegionLoad`：`(qw & 7u) != 0`，`FindAllInstances` `:1007` 與 `ReverseLookupIndex::Build` `:1229` 共用）
- 成因：這個過濾假設「MonoVTable* 一定 8-byte 對齊」，但這個假設只在 x64 成立。Mono 的 `mono_class_create_runtime_vtable` 對有 interface 的類別（`interface_offsets_count > 0`）會在 vtable **前面**放一張 IMT：`imt_table_bytes = sizeof(gpointer) * MONO_IMT_SIZE`（`MONO_IMT_SIZE = 19`），`vt = (MonoVTable*)((char*)alloc + imt_table_bytes)`。mempool 的配置是 8-byte 對齊（`MEM_ALIGN 8`）；在 x86 上 `19 × 4 = 76`，76 ≡ 4 (mod 8)，所以這些類別的 vtable 位址一定是 4 mod 8。x64 則是 `19 × 8 = 152` ≡ 0，因此不受影響。結果是在 32-bit Mono 遊戲（例如 活俠傳）上，所有陣列（`T[]` 都實作 `IList<T>`）、`List<T>`、`Dictionary<K,V>`，以及實作任何 interface 的遊戲類別（`ISerializationCallbackReceiver`、`IPointerClickHandler`、`IComparable`…），在 snapshot / findInstances liveOnly 以外的路徑 / value scan 的 reverse lookup 中都會消失，而且不會有任何錯誤訊息。這也可能是 x86 上「false positive 減少」的部分原因：真正的 instance 被一起濾掉了。（`klass & 7` 的檢查沒有問題，MonoClass 由 mempool 配置，本身是 8-byte 對齊。）
- 修法：vtable 的對齊檢查改成 `qw & (alignof(void*) - 1)`（x86 為 `& 3`）。只放寬 vtable 這一項；instance slot 的 `instAddr & 7`（BDWGC granule 在 32-bit 也是 8 bytes）維持不變。後面已經有 `vtable[0] ∈ classSet` 與 `vtable+2*ptr == rootDomain` 兩道結構驗證，false positive 不會因此明顯增加。
- 驗證：在 x86 Mono 遊戲中以 `whatIsAt` 查一個已知的 `List<T>` instance，印出它的 header qword，確認其 `% 8 == 4`。

**B-H2 · classSet 只有 TypeDef（沒有 generic instance、沒有 array），而且命中範圍不受 instance 大小限制：`WhatIsAt` / `ReverseLookupIndex` 會把 hit 歸到「隔壁的物件」**
- 位置：classSet 來源為 `MonoAddressFinder.cpp:562-573` + `MonoClassWalker.cpp:204-211`（只對 `0x02xxxxxx` TypeDef token 呼叫 `mono_class_get`），以及 `IL2CPPAddressFinder.cpp:709-720` + `IL2CPPClassWalker.cpp:245-254`（`il2cpp_image_get_class` 只回傳 TypeDef）；backward scan 在 `MonoAddressFinder.cpp:696-760`、`IL2CPPAddressFinder.cpp:947-975`；index lookup 在 `MonoAddressFinder.cpp:1331-1352`、`IL2CPPAddressFinder.cpp:1542-1565`；`ResolveFieldAt` 的 nearest 分支在 `MonoAddressFinder.cpp:386-399`、`IL2CPPAddressFinder.cpp:437-450`
- 成因：
  1. `List<Foo>`、`Dictionary<K,V>`、`Foo[]` 等物件，header 指向的是 runtime 產生的 inflated / array class，不在 classSet 裡。backward scan 會跳過它們的 header，繼續往回找 4 KB（上限 16 KB），直到碰到前面某個非泛型物件，然後回報 `found = true`，`instance` 與 `className` 都是那個鄰居。`fieldOffset` 超出鄰居的實際大小時，`ResolveFieldAt` 仍會填 `nearestField` + 很大的 delta。interval-tree index 取的也是「≤ addr 的最近 base」，症狀相同。整條路徑都沒有用 instance size 驗證命中範圍（`mono_object_get_size` 已經綁定，但沒有被使用，見 `MonoResolver.h:129-136`）。
  2. 同一個原因，`FindAllInstances`（兩個 backend 都是）永遠找不到 generic instantiation 的物件，snapshot 也會缺少這一整類資料。
  3. IL2CPP 只對 value-type 元素的陣列補了 `WhatIsAtAcceptingArrays`；Mono 完全沒有對應的 fallback（雙胞胎不對稱）。
- 修法：
  1. **範圍檢查**：命中後計算物件大小（Mono 用 `mono_object_get_size`，或 `mono_class_instance_size`；IL2CPP 用 `il2cpp_class_instance_size`，陣列另外加 `max_length × element size`），`fieldOffset >= size` 時視為未命中並繼續往回找，或直接回 `no_klass_in_range`。
  2. **補 classSet**：header 不在 set 中時，用純記憶體讀取驗證「klass 的 image 在已知 image 集合內」（Mono：`vtable→klass→image`；IL2CPP：`klass→image`，offset 0），並檢查 generic_class / rank 欄位；驗證通過後放進一個 per-scan 的延伸 set。
  3. 在 `docs/address-finder.md` 記錄「generic instance / array 的覆蓋範圍」。

### Medium

**B-M1 · `CallMethod` 不比對 arg kind 與形式參數型別，型別錯配時會讓 runtime 把 native 位址當成 managed object 使用（Mono / IL2CPP 共通）**
- 位置：`dll/src/mono/MonoMethodInvoker.cpp:470-591`（`formalType` 只在錯誤分支被 `(void)`，`:570`、`:576`）、`:667-685`；`dll/src/il2cpp/IL2CPPMethodInvoker.cpp:475-601`、`:682-700`
- 成因：`MarshalArg` 只看 `arg.kind` 決定 `params[i]`。例如形式參數是 reference type（`System.Object`、`Foo`），呼叫端卻送 Int32：`params[i] = &slots[i]`，runtime 會把 C++ heap 上的 8-byte slot 當成 MonoObject / Il2CppObject，去讀它的 vtable，或在 managed code 裡 write barrier / 寫入欄位。反過來，形式參數是 struct、送的卻是 `Address`，runtime 會從任意位址複製 struct。形式參數是 >8 bytes 的 struct、卻送 scalar kind 時，trampoline 會讀出 8-byte slot 之外的記憶體。外層的 SEH 只能接住 AV，接不住靜默的記憶體破壞；而且 AV 是在 runtime frame 內被吞掉的（見 B-M6）。
- 修法：用已經解析出的 `m.paramTypes[i]` 做白名單比對：primitive 名稱必須對應同寬度的 kind；`System.String` 只接受 `String` / `Null`；reference type 只接受 `Address` / `Null`；值型別（用 `class_is_valuetype(class_from_type(param))` 判斷）只接受 scalar 或 `ValueStruct`，並檢查 `bytes.size()` 是否等於 `class_value_size`。不符合就回 `arg_type_mismatch`。

**B-M2 · 呼叫前配置的 managed string 參數沒有 GC root，可能在 `runtime_invoke` 之前就被回收（Mono / IL2CPP 共通）**
- 位置：`MonoMethodInvoker.cpp:573-581`、`:661-662`；`IL2CPPMethodInvoker.cpp:583-591`、`:676-677`
- 成因：`mono_string_new` / `il2cpp_string_new` 回傳的指標只存在 `std::vector`（C++ heap）裡。BDWGC 只掃描已 attach thread 的 stack 與暫存器，不掃 CRT heap。有兩個以上的 string 參數時，第二次 `string_new` 就可能觸發 GC，把第一個 string 回收掉；遊戲其他 thread 觸發的 GC 也一樣。之後 `runtime_invoke` 傳入的就是 dangling pointer。
- 修法：每個 string 都用 `mono_gchandle_new(obj, TRUE)` / `il2cpp_gchandle_new(obj, true)` 取得 handle，invoke 完再 `*_gchandle_free`（需要在 Resolver 加 optional bind）；或者把指標陣列放在 stack 上（固定上限的 local array），而不是 `std::vector`。

**B-M3 · 沒有 opt-in 的值型別回傳會被回報成「暫時性 boxed 物件的位址」；enum 回傳完全沒有解碼；與標頭文件不一致**
- 位置：`MonoMethodInvoker.cpp:456-458`、`IL2CPPMethodInvoker.cpp:456-458`（fallthrough → `Kind::Address`）；文件 `MonoMethodInvoker.h:90-94`、`IL2CPPMethodInvoker.h:94`（寫的是「legacy shape (Unsupported + note)」）；`CallReturn::Kind::Bool`（`MonoMethodInvoker.h:58`）從未被產生（`Boolean` 回傳的是 `SignedInt`）
- 成因：`DecodeReturnValue` 只認得 primitive 名稱與 `System.String`。enum（`returnType` 是 `MyNs.MyEnum`）、`DateTime`、`Nullable<T>`、所有 struct，在 `wantStructReturn == false` 時都會走到「reference type」分支，回傳 boxed 暫存物件的位址。這個物件在 guard detach 之後隨時可能被 GC 回收，UI / Lua 卻可能把它當成可以 walk 的 instance。
- 修法：先用 `object_get_class(boxed)` + `class_is_valuetype` 判斷；值型別而且沒有 opt-in 時回 `Unsupported`（與文件一致）；enum 用 `class_enum_basetype`（Mono）或 `value__` 欄位型別（IL2CPP）解碼成整數；`Kind::Bool` 要嘛使用，要嘛刪除。

**B-M4 · `ResolveStaticFieldAddress` 對 const（literal）與 thread-static 欄位回傳 `ok = true` 和錯誤的位址；兩個 backend 的 offset 型別不一致**
- 位置：`dll/src/mono/MonoClassWalker.cpp:437-463`；`dll/src/il2cpp/IL2CPPClassWalker.cpp:491-513`；型別在 `MonoClassWalker.h:22`（`int offset`）與 `IL2CPPClassWalker.h:24`（`std::size_t offset`）；同樣的計算也出現在 `IL2CPPFieldXref.cpp:224`、`:283`
- 成因：只檢查 `Static (0x10)`。`const` 欄位的旗標是 `Static|Literal`，但沒有 static storage：Mono 的 offset 是 -1 或 0，IL2CPP 是 0，所以結果不是 `base - 1`，就是與第一個真正的 static 欄位重疊（讀到別的欄位的值）。`[ThreadStatic]` 欄位在兩個 runtime 的 offset 都是 -1（IL2CPP 的 `THREAD_STATIC_FIELD_OFFSET`）：Mono 回 `base + (-1)`，IL2CPP 經 `size_t` sign-extend 後同樣是 `base - 1`。同一個值在兩邊的 `FieldEntry.offset` 會是 `-1` 與 `0xFFFFFFFFFFFFFFFF`，上層若做 `base + f.offset`，兩個 backend 的行為不同。
- 修法：Literal（`0x40`）一律回 `ok = false, reason = "literal"`（值改用 `EnumerateLiteralFields` / `field_get_data` 取得）；offset 為 -1（或 `>= 0x7FFFFFFF`）時回 `reason = "thread_static"`；兩邊的 `FieldEntry.offset` 統一成 `std::int32_t`（runtime 本來就是 int32），並約定 -1 是 sentinel。

**B-M5 · `SehNameLooksSane` 只接受可列印 ASCII，名稱含非 ASCII 字元的類別會讓 `ReadClassInfo` / `ProbeKlassValid` 整個失敗**
- 位置：`MonoClassWalker.cpp:139-153`（`:146`）、`:290`、`:399-408`；`IL2CPPClassWalker.cpp:166-180`（`:173`）、`:372`、`:219-228`
- 成因：`c < 0x20 || c > 0x7E` 一律判為 garbage。runtime 回傳的類別名稱與 image 名稱是 UTF-8，混淆器（ConfuserEx 一類會產生 Unicode 名稱）、使用中文或日文識別字的遊戲、全形字元的 assembly 名稱，都會讓 `ReadClassInfo` 回 `ok = false`。結果 Inspector / walkInstance 對這些類別完全不能用，而且呼叫端看到的是「class invalid」，不是「名稱含特殊字元」。
- 修法：接受合法的 UTF-8 multibyte 序列（≥ 0x80 的 byte 依 UTF-8 規則驗證 lead / continuation），只拒絕 C0 控制字元與不合法的序列；512 bytes 的上限與「讀取時受 SEH 保護」都保留。（待驗證：找一款名稱經混淆或含 CJK 字元的遊戲實測。）

**B-M6 · SEH leaf 在 runtime 呼叫內吞掉例外：持有中的 runtime lock 不會被釋放，C++ 例外也會被當成 AV 吞掉**
- 位置：代表案例 `dll/src/mono/MonoDomain.cpp:30-37` + `:40-66`；同一種模式出現在所有 `Seh*` leaf（例如 `MonoClassWalker.cpp:78-131`、`IL2CPPClassWalker.cpp:33-38`、`:98-156`、`MonoMethodInvoker.cpp:81-98`、`IL2CPPMethodInvoker.cpp:77-94`）
- 成因：
  1. `mono_assembly_foreach` 會在持有 `mono_assemblies_lock` 的狀態下呼叫 `OnAssembly`，而 `OnAssembly` 裡有 `std::string` 建構與 `push_back`（可能丟 `bad_alloc`，x86 下尤其容易）。`__except(EXECUTE_HANDLER)` 會把 C++ 例外（`0xE06D7363`）一起攔下，從 runtime frame 中直接 unwind 出來，assemblies lock 永遠不會被釋放。之後遊戲本身的 `Assembly.Load`（或下一次 `EnumerateAssemblies`）就會永久卡住，症狀是遊戲凍結而不是崩潰，更難診斷。
  2. 同理，`class_get_fields` / `class_vtable` / `runtime_invoke` 在 runtime 內部持有 loader lock / class init lock / GC lock 時發生 AV，被 leaf 接住之後 lock 同樣會洩漏。這個 SEH 設計的前提「出錯就回 null、繼續執行」只對不持鎖的 accessor 成立。
- 修法：(a) `OnAssembly` 改成 `noexcept`，在 lambda 內 `try { … } catch (...) { 設旗標 }`，不要讓任何 C++ 例外穿過 runtime frame；(b) 所有 leaf 的 filter 改成只接受 `EXCEPTION_ACCESS_VIOLATION` / `EXCEPTION_IN_PAGE_ERROR`（與 A-M6 / A-L14 共用同一個 filter helper）；(c) 對會取得 runtime lock 的呼叫（`class_get_fields`、`class_vtable`、`assembly_foreach`、`runtime_invoke`）在 SEH 命中後設定全域的「runtime 可能已損壞」旗標並寫 WARN，之後拒絕 heavy RPC，提示使用者重啟遊戲，而不是靜默繼續。

**B-M7 · `IL2CPPMetadataRegistration` 直接解參考模組記憶體，沒有任何 SEH 保護**
- 位置：`dll/src/il2cpp/IL2CPPMetadataRegistration.cpp:77-98`（`ListInitDataSections` 直接讀 PE header）、`:201-213`（掃描迴圈對 section 做 raw `memcpy`）、`:117-157`（`TestCandidate` 讀 `p` 時沒有保護）、`:289`（`GetTypeTag` 直接讀 `g_state.types[typeIndex]`）
- 成因：同一個 backend 中，`ClassPointerScanner`、`LazyInitSlotScanner`、`FieldXref` 都已經用 `SafeRead*` 防範 anti-cheat 把頁面拔掉（註解反覆提到「AC page-strip」），只有這個 scanner 直接讀 `.data` / `.rdata`（可能上百 MB）。只要有一頁被設成 `PAGE_NOACCESS` / guard，pipe worker 就會 AV，遊戲崩潰。
- 修法：比照 `IL2CPPClassPointerScanner.cpp:112-150`，以 1 MB 的 `SafeReadBytes` chunk 複製到本地 buffer 再比對（chunk 之間保留 `kRegBytes` 的重疊）；PE header 改用 `Memory::SafeRead`；`GetTypeTag` 改用 `SafeReadBytes(types + idx)`。

**B-M8 · Mono `findFieldReferences` 只比對 FieldDef token：泛型類別的欄位（以及跨 assembly 的存取）一律 0 筆，結果卻標成 `exact`**
- 位置：`dll/src/mono/MonoFieldXref.cpp:284-286`（`tok == targetToken`）、`:16-18`（v1 範圍註解）；`MonoFieldXref.h:7-10`（宣稱「Exact, no false positives」）
- 成因：ECMA-335 規定，參考**泛型型別**的欄位時，IL operand 必須是 MemberRef（`0x0A`，parent 為 TypeSpec），即使存取發生在同一個類別內也一樣。所以 `Singleton<T>.instance`、`Pool<T>._items` 這類欄位，在同一個 assembly 內的所有讀寫都是 MemberRef，walker 會回傳 0 筆，`truncated = false`，與「欄位沒有被使用」無法區分。標頭只說明了跨 assembly 的限制，沒有提到這一點。
- 修法：遇到 `0x0A` token 時，用 `mono_field_from_token(image, tok, &klass, NULL)`（`MONO_API`，需要加 optional bind）解析出 `MonoClassField*`；如果 parent 是 generic instance，取它的 generic definition 與欄位名稱，再和目標欄位比對；至少在目標類別是泛型時，於結果中加上 `note`。

**B-M9 · IL2CPP field xref 的 `FindEnclosing` 沒有上界，其他 image 的程式碼命中會被歸給目標 image 的方法，而且標成 `exact`**
- 位置：`dll/src/il2cpp/IL2CPPFieldXref.cpp:170-181`、`:466-470`（emitHit）、`:690-695`（v2 chain）；與註解 `:10-16`、`:453` 宣稱的「其他 assembly 的 hit 會被濾掉」不符
- 成因：method index 只收錄目標 image 的方法。`upper_bound - 1` 會回傳「位址 ≤ ip 的最後一個目標方法」，只要 ip 大於目標 image 的最小方法位址就一定有結果。AOT 碼在 `GameAssembly.dll` 中並不是依 assembly 連續排列（泛型共享碼、其他 assembly 的方法都可能夾在中間），所以跨 image 的命中幾乎都會被安插到一個不相干的目標方法上。`droppedNoEnclose` 只會計到「位址低於最小方法」的那一小部分。
- 修法：用 `RtlLookupFunctionEntry`（x64 `.pdata`）取得命中點所在函式的精確起點，再以「起點 == 某個 index entry 的 address」做精確比對，不相等就算 not enclosed；或者把 method index 擴大到所有 image，並保留每筆 entry 的 image，最後再過濾。

**B-M10 · IL2CPP heap walker 的 thread pool：建立失敗會 `std::terminate`；`FindAllInstances` 不能取消，也不尊重「Parallel」開關**
- 位置：`dll/src/il2cpp/IL2CPPAddressFinder.cpp:1291`（`ScanThreadCount()`，與 `:1436` 的 `EffectiveScanThreads()` 不一致）、`:1312-1319`、`:1455-1462`（`pool.emplace_back` 沒有 try）、`:1299-1310`（worker 不檢查 `ScanCancel`）；Mono 端 `MonoAddressFinder.cpp:971-1080`（同樣沒有 cancel 檢查）
- 成因：(1) 與 A-H2 相同的模式：第 N 條 thread 建立時丟出 `std::system_error`，已經 joinable 的 `std::thread` 在 `pool` 解構時會 `std::terminate`，直接殺掉遊戲。(2) `FindAllInstances`（snapshot 的 heap walk，GB 級）兩個 backend 都不檢查 `ScanCancel`；UI 取消或斷線後，worker 仍會跑到結束，期間持續佔用記憶體頻寬。(3) IL2CPP 的 `FindAllInstances` 使用 `ScanThreadCount()`，使用者為了避開 anti-tamper 而關掉 Parallel 時，snapshot 路徑仍然是全平行。
- 修法：建立 thread 的迴圈加上 try/catch，失敗時先 join 已啟動的 thread，再在目前的 thread 上 inline 跑 `worker`；兩個 backend 的 `FindAllInstances` 在 chunk / region 迴圈內檢查 `ScanCancel::IsCancelRequested()`，並在結果中標記 `cancelled`；IL2CPP 改用 `EffectiveScanThreads()`。

**B-M11 · Mono `WhatIsAt` 與 `ReverseLookupIndex` / `FindAllInstances` 的驗證規則不同，「byte-identical」的宣稱不成立**
- 位置：`dll/src/mono/MonoAddressFinder.cpp:708-735`（backward scan：只做 `vtable[0] ∈ classSet` + 名稱 probe，**沒有** domain 檢查）、`:1248-1253`（index：有 `vtable+2*ptr == rootDomain` 檢查）、`:745-758`（把未經 domain 驗證的配對寫進全域 memo）；文件 `MonoAddressFinder.h:120-124`
- 成因：任何「第一個 qword 指向『開頭是 MonoClass*』之結構」的指標都會通過 `WhatIsAt`。例如指向 `MonoType`（`MONO_TYPE_CLASS` 的 `data.klass` 就在 offset 0）、`RuntimeTypeHandle` 等。同一個位址，value scan 走 index 時查不到，fallback 到 `WhatIsAt` 時卻得到一個假的 instance。全域 `vtableToKlass` memo 因此混入未驗證的條目（`FindAllInstances` 有刻意避開，見 `:912-916`，但 `WhatIsAt` 自己會繼續使用這些條目）。
- 修法：把 domain 結構檢查抽成共用的 `IsPlausibleVTable(qw, &klass)`，三條路徑都呼叫它；memo 只存放通過完整驗證的配對。

### Low

**B-L1 · resolver / offset discovery 的「失敗即永久」與「無限重試」兩種極端並存（雙胞胎漂移）**
- 位置：`IL2CPPResolver.cpp:18`、`:138-141`（`std::call_once`）；`IL2CPPStaticFieldsOffset.cpp:36`、`:167-169`；`IL2CPPAddressFinder.cpp:468-543`（`g_ecOnce`）；`IL2CPPMetadataRegistration.cpp:54`、`:274-277`；`IL2CPPLazyInitSlotScanner.cpp:28`、`:370-373`；對照 `MonoResolver.cpp:192-206`
- 成因：Mono 端特地修掉了「`call_once` 在第一次**呼叫**就鎖定結果」的 bug（註解寫得很清楚），IL2CPP 端的 resolver 與四個 discovery 仍然是 `call_once`：如果在 runtime 還沒初始化完成時被觸發（static_fields 都還是 null、class cache 還是空的），失敗結果會保留到行程結束，`scan` RPC 也救不回來。反過來，Mono 只要缺少任何一個 required export，**每一次** `GetResolver()` 都會重跑 `DoResolve`：重寫 `g_resolver`（包含 `std::wstring moduleName`），而其他 thread 正在沒有鎖的狀態下讀它（data race），同時每次都寫「missing export」log（log 洪水）。
- 修法：統一成「成功才鎖定，失敗時以節流的方式重試」（例如記錄上次嘗試的 tick，間隔 ≥ 1 秒才重試）；Mono 的 `DoResolve` 先在 local `Resolver` 上完成，再在鎖內整體 swap，讀取端也改成拿一份 snapshot（或 `std::shared_ptr<const Resolver>`）。

**B-L2 · `ResolveInstanceLookup` 的陣列分支對「boxed enum」誤判；對真正的陣列則永遠到不了（實質 dead code）**
- 位置：`MonoAddressFinder.cpp:467-509`；`IL2CPPAddressFinder.cpp:828-870`
- 成因：兩個 runtime 都把 enum 類別的 `element_class` 設為其底層整數型別的類別，所以 `elemClass != k` 對 enum 成立。boxed enum 物件（enum 被存進 `object` 欄位、`Dictionary<object, …>` 等情況）會被回報成 `"[0]"`、`array-element`，`len=` 是從錯誤的 offset 讀出來的。真正的陣列 class 不在 classSet（見 B-H2），而 `ExportAPI` 沒有直接呼叫 `ResolveInstanceLookup`（grep 確認），所以這個分支實際上只會在 enum 上觸發。
- 修法：改成判斷 rank（Mono：`mono_class_get_rank`；IL2CPP：`il2cpp_class_get_rank`，或在 `il2cpp_class_is_enum` / `mono_class_is_enum` 為真時排除）；與 B-H2 的 classSet 擴充一起處理，讓陣列真的能走到這個分支。

**B-L3 · Dead code / 只寫不讀的狀態**
- `IL2CPPLazyInitSlotScanner.cpp:401-491`：`g_mapOnce`、`g_typeDefMap`、`BuildTypeDefMap()` 完全沒有呼叫者（grep 確認）。
- `IL2CPPLazyInitSlotScanner.cpp:559-577` + `IL2CPPFieldXref.cpp:605-617`：v3.1 的 `ResolveTypeInfoSlotClass` 只在 slot「已經是 heap 指標」時才回傳 class，但 v3.0（`:594-603`）剛讀過同一個 slot 並且比對失敗，所以 v3.1 實際上不可能新增命中（只差兩次讀取之間的時間窗口）。
- `MonoAddressFinder.cpp:136-146`：`ContainsKlass` / `ContainsVtable` 沒有呼叫者；`vtableSet`、`g_minVtable/g_maxVtable` 只用於 `GetClassRangeStats`。`:1091-1096` 在迴圈中對**未排序**的 `vtableSet` 做 `binary_search`（結果靠最後的 sort + unique 修正）；`g_minVtable` 在 Build 時是 -1、在 Invalidate 時是 0，兩處更新規則（`:750` 與 `:1095`）不一致。
- Resolver 綁定了但從未使用：`mono_domain_get`（而且是 **required**，`MonoResolver.cpp:69`，缺少它會讓整個 Mono backend 停擺）、`string_chars` / `string_length`（兩邊都是）、`mono_object_get_size`（B-H2 的修法可以用上）。
- 修法：刪除或接上；`mono_domain_get` 改用 `optBind`。

**B-L4 · `IsKnownNotArray` 的上限語意與註解相反，而且容器沒有比照 heap-leak 模式**
- 位置：`IL2CPPAddressFinder.cpp:565-588`、`:628`
- 成因：註解說「滿了之後，未知值一律視為 non-array（never hangs）」，實作卻是 `RememberNotArray` 回 false 後呼叫端直接忽略，下一次遇到同一個值又會重新 probe（VirtualQuery），正是註解想避免的 300 秒情境。`g_notArrayKlass`（最多 524K 個 node）是一般的 static 物件，行程結束時會逐一解構，也就是 Cache 註解中提到的 shutdown 卡頓。
- 修法：加一個 `std::atomic<bool> g_notArrayFull`，滿了之後 `LooksLikeValueTypeElementArray` 對未知值直接回 false；容器改成 heap-leaked。

**B-L5 · chunk 讀取失敗時只前進 4 KB，會跳過壞頁**之前**的正常頁**
- 位置：`IL2CPPClassPointerScanner.cpp:126-135`；`IL2CPPLazyInitSlotScanner.cpp:233-237`；`IL2CPPClassReferenceDiagnostic.cpp:150-155`
- 成因：`SafeReadBytes` 一次讀 1 MB，只要其中任何一頁不可讀就整段失敗；`pos += 4096` 之後再讀 1 MB 仍然包含同一個壞頁。壞頁之前（最多將近 1 MB）的正常頁永遠不會被掃到，而且會白白重試約 256 次。
- 修法：失敗時把這個 chunk 改成逐頁（4 KB）讀取，只跳過真正讀不到的頁。

**B-L6 · IL2CPP 判斷 enum 只看 parent 的簡單名稱是否為 `"Enum"`**
- 位置：`IL2CPPClassWalker.cpp:402-407`；`IL2CPPAddressFinder.cpp:253-258`
- 成因：沒有檢查 namespace，使用者自己定義在其他 namespace、名為 `Enum` 的類別的子類別會被誤判；Mono 端則用 `class_is_enum`（雙胞胎行為不同）。
- 修法：加上 `class_get_namespace(parent) == "System"`，或綁定 `il2cpp_class_is_enum`（v24 以後的 runtime 都有 export）。

**B-L7 · IL2CPP instance field xref 的兩個 recall 缺口**
- 位置：`IL2CPPFieldXref.cpp:851`、`:914`
- 成因：(1) 宣告類別是 **struct** 時，`this` 是 unboxed 指標，AOT 碼的 disp 是 `offset - kObjectHeader`，scanner 卻用含 header 的 offset，所以會全部漏掉，同時產生 false positive。(2) 透過 `FindClass` 的 `.` → nested fallback（ns = `Ns.Outer`、name = `Inner`）解析出來的巢狀類別，method index 裡記錄的是 `ns = "Ns"`、`className = "Outer/Inner"`，`m.ns != ns` 會讓宣告類別的方法數變成 0，靜默回傳 0 筆。
- 修法：值型別時 disp 減去 header；比對改用 `ce.klass == target.klass`（在 `MethodIndexEntry` 中記錄 `Il2CppClass*`），不要比字串。

**B-L8 · 兩個 backend 的實作細節漂移**
- `IL2CPPMethodInvoker.cpp:218`、`:222`、`:298`：仍然寫死 `0x10` / `0x14` / `0x18`；Mono 端已改用 `ManagedLayout`（`MonoMethodInvoker.cpp:227`、`:231`、`:307`）。
- `IL2CPPClassWalker.cpp:449`（`method_get_flags` 直接呼叫）、`:91`、`:486`、`:550`、`:556`；`MonoClassWalker.cpp:316-319`、`:378-383`；`MonoFieldXref.cpp:205-224`、`:371-380`；`IL2CPPFieldXref.cpp:220-233`：同一份檔案裡有的 accessor 包了 SEH、有的沒有，與 `IL2CPPClassWalker.cpp:24-30` 的政策註解不符。
- `IL2CPP::ResolveStaticBaseAddress`（`IL2CPPClassWalker.cpp:522-527`）沒有 attach guard，Mono 版（`MonoClassWalker.cpp:478-483`）有；`ReadClassInfo` 只有 Mono 有 `forceSetup`。
- 修法：IL2CPP 的 invoker 改用 `ManagedLayout`；accessor 統一走 `SehCall`。

**B-L9 · 方法解析：只取第一個 overload、先 JIT 再判斷是否歧義**
- 位置：`MonoClassWalker.cpp:503-523`（`CompileMethodByName`）、`IL2CPPClassWalker.cpp:545-559`（`GetMethodPointerByName`）；`MonoMethodInvoker.cpp:199-205`；`MonoMethodFinder.cpp:191-201`
- 成因：前兩者遇到 overload 時靜默回傳第一個符合的方法；`ResolveOverload` 會先對每個候選呼叫 `mono_compile_method`，之後才因為 `ambiguous_method` 放棄（多做一次 JIT，產生副作用）。`FindMethodInstances` 只排除 abstract，不排除 open generic method / generic type definition 的方法；在舊版 Mono 上，`mono_compile_method` 失敗時可能用 `mono_raise_exception` 丟出 managed exception，而不是回傳 NULL（待驗證）。
- 修法：歧義判斷放在 JIT 之前；跳過 `is_generic` / generic container 的方法；overload API 回傳 `ambiguous` 狀態。

**B-L10 · `MethodSignatureFormat` 的 humanSuffix 碰撞**
- 位置：`dll/src/shared/MethodSignatureFormat.h:30-42`
- 成因：`System.Int32[]` 不在對照表中，所以變成 `Int32Arr` 而不是 `intArr`（與 scalar 的 keyword 規則不一致）；`Ns.Outer`1/Inner<…>` 先在 `<` 截斷、再在 `` ` `` 截斷，結果只剩 `Outer`，同一個泛型外層類別下的不同巢狀型別會得到相同的 suffix；`List<int>` / `List<string>` 都變成 `List`。CE 符號名稱可能因此重複。
- 修法：先剝掉 `[]` 再查表；先處理 `/` 取最後一段，再處理 `` ` `` 與 `<`；泛型引數縮寫後併入 suffix（例如 `List_int`）。

**B-L11 · 小型邊界問題**
- `MonoFieldXref.cpp:124-127`：`switch` 的 `count * 4` 在 x86 的 `size_t` 下可能溢位，只影響畸形 IL 的對齊。
- `MonoFieldXref.cpp:305`、`IL2CPPFieldXref.cpp:492`：`limit == 0` 時第一筆就 truncated（上層已驗證，屬防禦性問題）。
- `MonoAddressFinder.cpp:630` vs `:681-683`、`IL2CPPAddressFinder.cpp:908` vs `:932-934`：`scannedFrom` 與 `rangeStart` 在接近位址 0 時用不同的條件計算，buffer index 與 `p` 會錯位（實際上碰不到）。
- `MonoMethodInvoker.cpp:605-626`、`IL2CPPMethodInvoker.cpp:615-645`：提早返回的錯誤路徑沒有填 `elapsedMs`，也沒有寫 log。

**B-L12 · 過時 / 錯誤的註解**
- `IL2CPPFieldXref.h:30-37`：說 v1 不偵測寫入（`0x89`），但 `IL2CPPFieldXref.cpp:54-61` 已經把它分類為 `write`。
- `MonoMethodFinder.h:29`：`declaringClass` 寫的是「Outer/Inner form」，實作（`MonoMethodFinder.cpp:106-113`、`IL2CPPMethodFinder.cpp:113-120`）只取葉節點名稱，沒有走 nesting chain，巢狀類別的 overload 因此無法直接 round-trip 回 `FindClass`。
- `MonoDomain.cpp:18-19`：「Pipe worker is single-threaded today」，與多 client 的現況不符。
- `IL2CPPAddressFinder.h:155-157`、`MonoAddressFinder.h:120-124`：宣稱兩條路徑結果 byte-identical（Mono 端不成立，見 B-M11）。
- 修法：逐條改正；`declaringClass` 改用 `DescribeClass` 產生 `Outer/Inner` 形式。

### Mono ⇄ IL2CPP 雙胞胎函式對照

| 功能 | Mono | IL2CPP | 差異 / 對應發現 |
|---|---|---|---|
| Resolver | `MonoResolver.cpp:192-206`：失敗時重試 | `IL2CPPResolver.cpp:138-141`：`call_once` | 失敗語意相反 → B-L1 |
| Domain | `GetRootDomain`（root domain） | `GetDomain`（`il2cpp_domain_get`） | Mono 的 statics / vtable 一律以 root domain 計算；Editor 的 child domain 不在支援範圍（未驗證） |
| Assembly 列舉 | `mono_assembly_foreach` + callback（`MonoDomain.cpp:30-66`） | `domain_get_assemblies` 陣列（`IL2CPPDomain.cpp:62-89`） | Mono 的 callback 在 runtime 持鎖時執行 C++ → B-M6；只有 Mono 有 `BuildImageMap` |
| `TypeName` | `MonoTypeParser.cpp:26-37` | `IL2CPPTypeParser.cpp:23-34` | 一致 |
| `EnumerateClasses` | TypeDef table + `mono_class_get`（含 `token`） | `image_get_class`，2018.x 走 metadata fallback | 兩者都不含 generic instance → B-H2 |
| `FindClass` | `MonoClassWalker.cpp:215-270` | `IL2CPPClassWalker.cpp:301-352` | 邏輯一致 |
| `ReadClassInfo` | 有 `forceSetup`；enum 用 `class_is_enum`；`offset` 是 `int` | 沒有 `forceSetup`；enum 看 parent 名稱；`offset` 是 `size_t`；`MethodEntry.pointer` | → B-M4、B-L6 |
| `KlassLooksValid` / `SehNameLooksSane` | `:139-173` | `:166-202` | 同樣只接受 ASCII → B-M5 |
| Static 位址 | `class_vtable` + `vtable_get_static_field_data` | `class_get_static_field_data`（不會觸發 class init） | 兩者都不排除 literal / thread-static → B-M4 |
| JIT / AOT 位址 | `CompileMethodByName`（會觸發 JIT） | `GetMethodPointerByName` / `ReadMethodPointer`（`MethodInfo+0`） | 都只取第一個 overload → B-L9 |
| `FindMethodInstances` | `MonoMethodFinder.cpp:74-208` | `IL2CPPMethodFinder.cpp:83-197` | 結構一致；Mono 的 param names 依賴 `param_count` export |
| `CallMethod` | `MonoMethodInvoker.cpp:595-718` | `IL2CPPMethodInvoker.cpp:605-733` | 結構逐行一致；IL2CPP 寫死 offset（B-L8）；共同問題 B-M1、B-M2、B-M3 |
| `WhatIsAt` | header = vtable，lazy memo，**沒有** domain 檢查 | header = klass，每次 probe 都取 `cache().mu`，另有 `AcceptingArrays` | → B-H1（x86）、B-H2、B-M11 |
| `FindAllInstances` | 單執行緒 + `Sleep(1)` + domain 檢查 | 平行、`ScanThreadCount()` | 兩者都不能取消 → B-M10 |
| `ReverseLookupIndex::Build` | 單執行緒，可取消 | 平行、`EffectiveScanThreads()`，可取消 | thread 建立失敗 → B-M10 |
| `EnumerateInstanceFields` / struct 成員命名 | `NameStructComponent`（舊結構） | 抽出 `NameValueStructMember`，與 array 共用 | 行為等價；Mono 沒有 array 版本 |
| Field xref | IL walk，只比對 FieldDef token | RIP v1 + chain v2 + lazy-slot v3 + instance 啟發式 | → B-M8、B-M9、B-L7、B-L3 |

### audit-2026-11-23-plan.md §3「觀察到的訊號」結案

| # | 訊號 | 結論 |
|---|---|---|
| 1 | 兩個 backend 平行手寫，容易只修一邊 | **確認**。已列出實際漂移：Resolver 失敗語意（B-L1）、WhatIsAt 的驗證規則（B-M11）、IL2CPP 寫死 offset（B-L8）、enum 判斷（B-L6）、`FindAllInstances` 的 thread 數來源（B-M10）、array fallback 只有 IL2CPP 有（B-H2）。也有反例：`ResolveInstanceLookup` / `CallMethod` 幾乎逐行對稱，所以 B-M1～B-M3 是**兩邊一起錯**。 |
| 2 | `MonoTypeParser.cpp` / `IL2CPPTypeParser.cpp` 是殘骸 | **否決**。兩者都是 `TypeName()` 的實作，被 ClassWalker、AddressFinder、MethodFinder、Invoker 大量使用，只是很薄的 wrapper，不是 dead code。（`docs/Archiutecture.md` 的描述屬文件漂移，不在本區塊範圍內，沒有查證。） |
| 3 | `ClassReferenceDiagnostic` / `LazyInitSlotScanner` 是 investigation helper，卻編進正式 DLL 並開放 RPC | **部分確認**。`LazyInitSlotScanner` 是正式 `findFieldReferences` v3 路徑的一部分（`IL2CPPFieldXref.cpp:575`），不能拿掉，但其中有 dead code（B-L3）。`ClassReferenceDiagnostic` 確實透過 `CmdDiagnoseClassReferences` 對外開放（`ExportAPI.cpp:7500`），會掃描整個 heap（最多三種指標）；建議改成 DEBUG 開關或 handshake capability 才開放。 |

### 稽核檢查清單補充結論
- **Unity offset 寫死**：`Il2CppClass::static_fields` 與 `element_class` 是動態探測的（正確）。寫死的部分有：`MonoVTable::domain = +2*ptr`（`MonoAddressFinder.cpp:1048`、`:1250`，legacy Mono 2.x 與 Unity 2017+ 的 Mono 都成立）、`Il2CppType` 的 tag 在 `+10`（`IL2CPPMetadataRegistration.cpp:111`、`:293`，僅 x64）、`MethodInfo::methodPointer = +0`、`ManagedLayout.h` 中的 object / string / array / exception 版面（以 `sizeof(void*)` 計算，x86 / x64 都正確；Mono x64 的 `max_length` 實際上是 `uint32 + padding`，以 `uintptr_t` 讀取時只是依賴 padding 為 0）。真正錯在 bitness 的只有 B-H1。
- **Thread-attach rule**：所有直接呼叫 resolver 的 helper 都自己開了 guard，重入時由 depth counter 吸收，沒有發現 nested attach / detach。只有一點例外：`IL2CPP::ResolveStaticBaseAddress` 沒有開 guard，依賴呼叫端（B-L8）。
- **SEH-leaf rule**：leaf 本身都只包單一呼叫，但 filter 一律是 `EXECUTE_HANDLER`，callback 型的 runtime API（`assembly_foreach`）會讓 C++ 程式碼落在 `__try` 的範圍內 → B-M6。

### 需要 live 驗證、這次沒能驗證的項目
- B-H1：x86 Mono 遊戲中，對一個 `List<T>` / `T[]` instance 呼叫 `whatIsAt`，記錄 header qword `% 8`；比較修正前後 `FindAllInstances` 的 `vtablesResolved` 數量。
- B-H2：對 `List<T>._items` 內部、`Dictionary` entries 內部的位址呼叫 `whatIsAt`，確認回報的是鄰近的非泛型物件；確認 snapshot 中沒有任何 generic instantiation 的類別。
- B-M2：用兩個 string 參數的方法，在遊戲高頻配置的場景中重複 `callMethod` 數百次，觀察參數內容是否損毀。
- B-M5：一款名稱經混淆（Unicode）或使用 CJK 識別字的遊戲，對該類別呼叫 `getClassInfo`。
- B-M6：在 x86 Mono 上製造記憶體壓力，讓 `EnumerateAssemblies` 期間發生 `bad_alloc`，確認之後遊戲的 `Assembly.Load` 會卡住（風險高，建議用測試專案）。
- B-M8：對 `Singleton<T>` 類的泛型 static 欄位執行 Mono `findFieldReferences`，確認結果為 0 筆。
- B-M9：對一個被 `Assembly-CSharp` 以外的 assembly 存取的 static 欄位執行 IL2CPP `findFieldReferences`，以 `hitAddress` 在 CE 中反查實際所屬的函式。
- B-L9：在 Unity 5.x legacy `mono.dll` 上對 open generic method 呼叫 `findMethodInstances`，確認 `mono_compile_method` 是回傳 NULL 還是丟出例外。

---

## Block C — RPC 指令層與 Wire Contract（static review，2026-11-23，基準 `dev` @ `6bea633`）

> 範圍：`dll/src/shared/ExportAPI.{h,cpp}`（重點：共用 handler 2000–3710、Mono 段 3711–7036、IL2CPP 段 7037–10500、檔案開頭的 decode / dictionary / streaming decoder helper）、`MagicStrings.h`、`{Value,Group}ScanSession.*`、`SnapshotSession.*`；UI 端 `ui/UnityDumpUI.Core/Services/{PipeClient,ProtocolModels,ProtocolJson,RawJsonStringConverter,BackendAdapter}.cs`；`docs/pipe-protocol.md` 只做指令名稱的覆蓋率比對。
> 跨界只讀了邊界：`PipeServer.cpp:132-234`（envelope 格式與 `id` 回填）、`MainThreadDispatcher.cpp:336-393`（確認 `callMethod` 用 `[&]` 捕捉 stack 變數，timeout 時是否安全；結論：安全，不列）、`ScanCancel.h`、`Memory.cpp` 中 `IsCancelRequested` 的 poll 點、`IL2CPPClassWalker.h:24` / `MonoResolver.h:43` / `IL2CPPResolver.h:37`（`offset` 的型別），以及 `LiveInspectionViewModel.cs:1482-1491`（確認 Inspector 對 `walkArray` 傳入哪些參數）。
> 全部是靜態推論，沒有 live 驗證；標「待驗證」的項目，推論鏈中有一環依賴 runtime 的實際行為。本節沒有修改任何程式碼。

### Critical

（無。最嚴重的三項會讓 UI 拿到錯誤資料或整個請求失敗，但不會讓遊戲崩潰，也不會寫入錯誤位址，所以列為 High。）

### High

**C-H1 · `[ThreadStatic]` 欄位的 offset 以 `SIZE_MAX` 傳到 UI 的 `int` 欄位，整個 `getClassInfo` / `dumpClassInfo` / `searchSymbols` 回應反序列化失敗（待驗證） — ✅ FIXED (2026-11-26)**
- 位置：DLL 端 `dll/src/shared/ExportAPI.cpp:3819`（Mono `getClassInfo`）、`:3935`（Mono `dumpClassInfo`）、`:5016`（Mono `searchSymbols`）、`:7190`（IL2CPP `getClassInfo`）、`:7319`（IL2CPP `searchSymbols`），以及 IL2CPP `dumpClassInfo` 的同一寫法；型別來源 `dll/src/il2cpp/IL2CPPClassWalker.h:24`（`std::size_t offset`）、`dll/src/il2cpp/IL2CPPResolver.h:37`（`il2cpp_field_get_offset` 回傳 `size_t`）、`dll/src/mono/MonoResolver.h:43`（`mono_field_get_offset` 回傳 `int`，存進 `size_t` 時會做 sign-extend）；UI 端 `ui/UnityDumpUI.Core/Services/ProtocolModels.cs:163`（`FieldEntry.Offset` 是 `int`）、`:587`（`SearchSymbolMatch.Offset` 是 `int?`）、`:458`（`WhatIsAtField.Offset`）
- 成因：IL2CPP 的 thread-static 欄位 offset 是 `THREAD_STATIC_FIELD_OFFSET = -1`（int32），`il2cpp_field_get_offset` 轉成 `size_t` 後變成 `0xFFFFFFFFFFFFFFFF`。Mono 的 special-static 欄位在 class layout 時也被設成 `offset = -1`，經 `int → size_t` 變成 `SIZE_MAX`（x86 上是 `4294967295`）。這些 handler 都直接輸出 `{"offset", f.offset}`，JSON 裡就是 `18446744073709551615`；System.Text.Json 把它塞進 `int` 時會丟 `JsonException`，整個 envelope 無法解析。後果：
  1. 瀏覽任何含 `[ThreadStatic]` 欄位的類別都會失敗（mscorlib 的 `System.Threading.Thread`、UniTask、Newtonsoft 等常見函式庫都有）。
  2. `dumpClassInfo` 是一次 RPC 回傳整批類別，所以**整批**都會失敗，IL2CPP header / SDK 匯出會整個中斷。
  3. `PipeClient.SendAsync` 不會包裝 `JsonException`（`PipeClient.cs:224-225`），呼叫端看到的是一般例外，而不是 `PipeProtocolException`。
  Snapshot 路徑已經因為同樣的問題把 `SnapshotFieldEntry.Offset` 改成 `ulong`（`ProtocolModels.cs:1243-1248` 的註解），但這幾個 DTO 沒有一起改。
- 修法：DLL 端在輸出 offset 前統一正規化：`static` 欄位的 offset 若是 `(size_t)-1`（Mono 是 `(int)-1`）就輸出 `-1`，或者省略 `offset` 並加上 `"threadStatic": true`；最好集中成一個 `FieldOffsetToJson()` helper，讓兩個 backend 共用。UI 端把 `FieldEntry.Offset` / `SearchSymbolMatch.Offset` / `WhatIsAtField.Offset` 改成 `long`，作為第二道防線。
- 驗證：對 mscorlib 的 `System.Threading.Thread` 呼叫 `getClassInfo`，查看原始回應中的 `offset` 值。

**C-H2 · `walkArray` 指定 element class 時，用 `class_value_size` 覆寫了**參考型別**的 stride，第 1 個以後的元素全部讀錯位址（待驗證） — ✅ FIXED (2026-11-26)**
- 位置：`dll/src/shared/ExportAPI.cpp:2957-2961`（IL2CPP）、`:2984-2988`（Mono）、`:2995-2996`、`:3012-3019`、`:3049`；呼叫端 `ui/UnityDumpUI/ViewModels/LiveInspectionViewModel.cs:1482-1491`、`ui/UnityDumpUI.Core/Services/Export/CeXmlExporter.Live.cs:939-943`
- 成因：只要 element class 解析成功，就無條件執行 `elemSize = class_value_size(k)`。Mono 的 `mono_class_value_size` 實作是 `instance_size - sizeof(MonoObject)`，**不檢查**是否為 value type（原始碼中的 `g_assert(klass->valuetype)` 已被註解掉）；IL2CPP 的 `Class::GetValueSize` 也是同樣的公式。因此對參考型別（例如 `Enemy[]`），`isPointer = !elemIsValueType = true`，但 `elemSize` 是物件欄位的總大小（例如 0x30），而不是 pointer size。`:3012` 的 pointer-size fallback 只在 `elemSize == 0` 時才會生效，所以 `slotAddr = dataAddr + i * 0x30` 只有 `[0]` 正確，其餘的元素都讀到別的指標中間。Inspector 展開陣列時一律會帶入 element triple（`elemSpec` 只要型別名稱可以拆就有值），所以只要 element class 剛好在 `TargetAssembly` 中解析得到，就會觸發。CE XML live export 用同一份結果產生 pointer chain，輸出的 cheat table 也會是錯的。
- 修法：只在 `elemIsValueType == true` 時採用 `class_value_size`；參考型別一律用 `ManagedLayout::kPointerSize`。也就是把 `:2957-2961` / `:2984-2988` 改成 `if (elemIsValueType && r.class_value_size) …`，並把判斷移到 `class_is_valuetype` 之後。
- 驗證：在 Inspector 中展開一個 `SomeUserClass[]` 欄位，比較 `walkArray` 回傳的 `element_size` 與第 2、3 個元素的 `ptr` 是否和 CE 看到的一致。

**C-H3 · IL2CPP `getEnumValues` 的 FieldDefault 解碼和 `il2cppGetMetadataTypeFields` 不一致：在 v24/v27（Unity 2018–2020）的遊戲上，所有 ≤32-bit enum 的值都會解錯 — ✅ FIXED (2026-11-26)**
- 位置：`dll/src/shared/ExportAPI.cpp:10041-10068`（`getEnumValues` 的 `decodeTyped` 一律用 compressed-signed）；對照 `:8544-8598`（`il2cppGetMetadataTypeFields`：`metadataVersion <= 27` 用 raw LE，v29+ 才用 0xFE/0xFF sentinel + compressed）
- 成因：雙胞胎漂移。`:8544-8553` 的註解已記錄 FF VI（Unity 2019.4）的實際證據：v24/v27 的整數 literal 是 raw little-endian，但這個修正只套用到 `il2cppGetMetadataTypeFields`。`getEnumValues` 仍然對 raw LE 的 blob 做 ECMA compressed 解碼，例如 `01 00 00 00` 會被解成 `enc=1 → 0 - 0x40 = -64`。在 v29+ 上，又因為沒有處理 `0xFE` / `0xFF` sentinel，值為 `int.MaxValue` / `MinValue` 的成員會回報 `decode_error`。`MethodCallWindow` 會用這份清單產生 enum ComboBox，使用者選了某個名稱，實際送進 `callMethod` 的卻是錯誤的整數，而且畫面上看不出來。
- 修法：把 `:8556-8691` 的 `decodeByTag`（含版本分支與 sentinel 處理）抽成檔案層級的函式，讓兩個 handler 共用；`getEnumValues` 依底層型別對應到 type tag（I1=0x04 … U4=0x09）後呼叫它。順便刪除 `:10006-10036` 重複的 `decodeCompressedSigned`。

### Medium

**C-M1 · 長時間執行的 handler 沒有 `ScanCancel::Scope`：既不能取消，又會被其他 client 的 cancel 靜默截斷**
- 位置：`dll/src/shared/ExportAPI.cpp:4000`、`:4182`、`:4208`（Mono `snapshotBegin`：fast path 用 `ScanForPointer`、全抓用 `FindAllInstances` + 數分鐘的 decode）、`:8142`（IL2CPP `snapshotBegin`）、`:5307` / `:8860`（`findInstances`）、`:5426` / `:8970`（`listFieldValuesAcrossInstances`）；poll 點在 `dll/src/shared/Memory.cpp:510`（`RunParallelScan`）、`dll/src/il2cpp/IL2CPPAddressFinder.cpp:1447`、`dll/src/mono/MonoAddressFinder.cpp:1210`；規範在 `dll/src/shared/ScanCancel.h:19-20`
- 成因：整個檔案只有 6 個 handler 開了 Scope（`scanFieldValue` / `beginValueScan` / `beginGroupScan` × 2 個 backend）。其他 heap walk 共用 `RunParallelScan` / `FindAllInstances`，而它們會 poll **全域**的 `g_scanCancel`。結果：
  1. 這些 handler 單獨執行時 `g_scanActive == 0`，`RequestCancel()` 是 no-op，所以 UI 的 Cancel 和 disconnect monitor 都停不住（Mono `snapshotBegin` 可能跑數分鐘）。
  2. 如果另一個 client（CE bridge 或 UI 的另一條 pipe）同時在做 value scan，而那個 scan 被取消，旗標就會讓這些沒有 Scope 的 heap walk 中途 `break`，回傳**被截斷、卻標示為完整**的結果（snapshot 少了一批 instance、`findInstances` 少了一批 match）。這直接違反 `ScanCancel.h:19-20`「沒有 Scope 的 poll 端絕不能發布部分結果」。
- 修法：替 `snapshotBegin`（兩個 backend）、`findInstances`、`listFieldValuesAcrossInstances`、`findFieldReferences`、`diagnoseClassReferences` 開 Scope，並在回傳前檢查 `IsCancelRequested()`，取消時回 `{"ok":false,"cancelled":true}`。長期做法是把 cancel 改成 per-client token（Scope 記錄 owner，`RequestCancel(owner)`），和 A-M1 一起處理。

**C-M2 · DLL 會送出 `"id": null` 的錯誤 envelope，但 UI 的 `RpcEnvelope.Id` 是不可為 null 的 `int`，錯誤訊息會變成 `JsonException`**
- 位置：`dll/src/shared/PipeServer.cpp:212`（JSON 解析失敗）、`:226`（`response.dump` 失敗，例如 32-bit 下的超大 snapshot chunk 遇到 `bad_alloc`）；`ui/UnityDumpUI.Core/Services/ProtocolModels.cs:21`；`ui/UnityDumpUI.Core/Services/PipeClient.cs:224-236`
- 成因：`BuildError(nullptr, …)` 輸出 `"id":null`，STJ 無法把 null 轉成 `int`，於是丟 `JsonException`，DLL 特地準備的 `-32603 response serialization failed: …` 訊息完全看不到；`SendAsync` 也沒有把 `JsonException` 轉成文件宣稱的 `PipeProtocolException`。附帶問題：`id` 不一致時只丟例外、不做重新同步，如果真的發生（例如 A-M2 的資料遺失），這條連線之後的每個請求都會不一致。
- 修法：UI 端把 `RpcEnvelope.Id` 改成 `int?`；`Id == null && Error != null` 時視為「這個請求的錯誤」，直接拋出包含 `Error.Message` 的 `PipeProtocolException`；`Deserialize` 外包 `try/catch (JsonException)` 並轉成 `PipeProtocolException`；`id` 不一致時呼叫 `DisposeStreamsAsync()` 並觸發 `Disconnected`，強制重新連線。DLL 端的 `:226` 應該回填原始請求的 `id`，因為 `Dispatch` 已經知道它。

**C-M3 · `PipeClient` 沒有任何逾時；請求卡住時 `DisconnectAsync` / `DisposeAsync` 也會被 `_gate` 卡住**
- 位置：`ui/UnityDumpUI.Core/Services/PipeClient.cs:203`（`ReadLineAsync()` 不帶 token、不設逾時）、`:272-277`、`:279-284`（Disconnect / Dispose 都要先 `_gate.WaitAsync()`）、`:3-6`（過時註解：「one client at a time / one-at-a-time on the server」）；DLL 端可能無限期卡住的路徑：`dll/src/shared/ExportAPI.cpp:6960-6961`（`main_thread_timeout_ms` 不做 clamp，負值就是 INFINITE）、C-M1 中不能取消的 handler
- 成因：`SendAsync` 為了避免 response 錯位，刻意在寫入後不再理會取消，這個設計本身合理；但這也代表一個卡住的 handler 會讓 `_gate` 永遠被佔住。使用者按 Disconnect 會等 `_gate`，結果什麼都不會發生；如果 app 關閉時同步等待 `DisposeAsync`，整個 UI 會停在那裡。interactive pipe 上的 `callMethod`（mainThread）、`whatIsAt`、`getMetadataStats` 都走這條路。
- 修法：`DisconnectAsync` / `DisposeAsync` 不要等 `_gate`，改成直接 `_pipe.Dispose()`（讓進行中的 `ReadLineAsync` 丟 `ObjectDisposedException` / `IOException`，現有的 catch 會轉成 `PipeDisconnectedException`），再取 gate 清掉欄位；`SendAsync` 加上可設定的「每個指令的讀取逾時」，逾時就拆掉連線，而不是放棄讀取（重新連線可以避免 response 錯位）。DLL 端 `callMethod` 要 clamp `main_thread_timeout_ms`，例如限制在 [1, 60000]。更新 `:3-6` 的註解。

**C-M4 · Snapshot 的 `walkArrayElementsInline` 對 struct / enum 陣列用 pointer stride 讀取，把資料標成 `ptr`，還可能把 struct 的位元組交給 Layer-B capturer**
- 位置：`dll/src/shared/ExportAPI.cpp:2701-2718`、`:2725`、`:2735-2750`（與 `:2654-2659` 的註解不符：註解說會依 width hint 輸出 hex blob，實作完全沒有 width hint 參數）
- 成因：`knownElemSize` 只認得 primitive，其他一律 `isPointer = true`、stride 為 `sizeof(uintptr_t)`。`Vector3[]`（12 bytes）、自訂 struct 陣列、`MyEnum[]`（int32）都會依錯誤的 stride 讀取：enum 陣列在 x64 上每次跨 8 bytes，`n` 個元素會讀到陣列尾端之後最多 `4n` bytes（SEH 擋得住，但資料是垃圾）；struct 陣列的每個元素都顯示成無意義的 `ptr`。開啟 `arrayElementFields`（x64 預設是 blanket）時，這些「ptr」還會被送進 `CaptureElementFields` → `ClassOfObject`，雖然有 `knownIlKlasses_` / `klassToDescIdx` 擋著，但這只是靠運氣。`walkArray`（`:2933-3019`）有 element-class 解析，這條 snapshot 路徑卻沒有，又是一個雙胞胎漂移。
- 修法：呼叫端（`DecodeOneClass` / Mono `decodeOneClass`）已經有 `FieldEntry`，可以用 `class_get_element_class` 取得 element class，再用 `class_is_valuetype` + `class_value_size` 算出正確的 stride 和 `isPointer`（enum 用底層型別解碼），以參數傳入 `walkArrayElementsInline`；無法判斷時輸出 `arrayError: "unknown_element_layout"`，不要猜。

**C-M5 · IL2CPP streaming snapshot：instance 位址在 Begin 擷取、在 Chunk 才解碼，中間沒有重新驗證；decoder 不是 idempotent，而且在全域 mutex 內執行 GC 和 `Sleep(500)`**
- 位置：`dll/src/shared/ExportAPI.cpp:1121-1194`（`DecodeOneClass` 直接讀 `instAddr + offset`）、`:813-875`（`DecodeRange`；`:863-872` 的 `gc_collect(0)` + `::Sleep(500)`）、`:8367-8392`（整段在 `SessionManager::View` 內，也就是持有全域 `mu_`）；`dll/src/shared/SnapshotSession.h:181-189`
- 成因：
  1. Phase 30b 把 decode 延後到 chunk，大型遊戲從 Begin 到最後一個 chunk 可能相隔數分鐘，物件可能已被 GC 回收，記憶體再配給其他類別的物件。目前不檢查 header 就直接用原本類別的欄位 layout 解碼，結果是靜默的錯誤值，snapshot diff 會把它當成「欄位變化」。Mono 在 Begin 內立即解碼，沒有這個時間差（又一個雙胞胎差異）。
  2. `decodedInstances_` / `instanceDecodeBudget_` / `elementsProcessed_` 等計數器會隨每次 `DecodeRange` 累加。UI 若重送同一個 cursor（例如 chunk 反序列化失敗後重試），統計會重複計算、budget 會被提早耗盡，而最後一個 chunk 的 `drained_` 只會觸發一次。
  3. 最後一個 chunk 的 `gc_collect` 和 500 ms sleep 都在 `SessionManager` 的全域 mutex 內，這段期間其他 client 的 `snapshotBegin` / `snapshotEnd`，以及 disconnect hook 的 `DropAll()`（`:10596-10603`，在 worker thread 上執行），都會被一起擋住。
- 修法：(1) 在 `DecodeOneClass` 的 instance 迴圈中，先 `SafeReadBytes(instAddr) == klass` 再解碼，不符合就跳過，並累計 `staleInstances` 寫進 `decodeStats`。(2) 記住「已解碼到的最大 cursor」，重送已解碼過的範圍時只解碼、不累加統計和 budget；或者明確拒絕倒退的 cursor。(3) `View` 只用來取出 decoder 的 `shared_ptr`（session 內改用 `shared_ptr` 持有），decode / GC / sleep 都在鎖外執行。

**C-M6 · `searchSymbols` 單一 assembly 模式下 `truncated` 永遠是 false（兩個 backend 都一樣）**
- 位置：`dll/src/shared/ExportAPI.cpp:4934-4938`、`:5019-5020`、`:5038-5039`、`:5045`（Mono）；`:7269-7273`、`:7322-7323`、`:7342-7343`、`:7349`（IL2CPP）；UI 契約在 `ui/UnityDumpUI.Core/Services/ProtocolModels.cs:556-567`
- 成因：單一 assembly 時 `globalCap == perAsmLimit`，命中上限時一定先執行 `isAsmCapped()` → `thisAsmCapped = true; break`，`matches.size() >= globalCap` 那一行永遠輪不到；外層迴圈也沒有下一個 assembly 可以補設。所以回應是 `truncated:false` + `truncated_assemblies:[asm]`。這與 DTO 文件「單一 assembly 模式請看 `Truncated`，`truncated_assemblies` 會是空的」正好相反，UI 會把被截斷的結果當成完整結果顯示。
- 修法：在 `if (thisAsmCapped) truncatedAssemblies.push_back(...)` 之後加上 `if (!multiAsm && thisAsmCapped) truncated = true;`，或者在單一 assembly 模式下不要用 per-asm 預算判斷。兩個 backend 一起改，並補一個 C# 端測試，鎖住 DTO 的語意。

**C-M7 · 錯誤有兩種管道（envelope `error` 與 result 內的 `"error"` 字串），而部分 UI DTO 沒有 `Error` 欄位，錯誤與截斷狀態會被靜默丟掉**
- 位置：`ui/UnityDumpUI.Core/Services/BackendAdapter.cs:1317-1324`（`ThrowIfError` 只檢查 envelope）；`ProtocolModels.cs:109-113`（`GetAssembliesResult` 沒有 `Error`）、`:480-490`（`ClassRangeStatsResult` 沒有 `Error`）、`:1530-1546`（`SnapshotClassEntry` 沒有 `truncated`）；DLL 端 `dll/src/shared/ExportAPI.cpp:3714-3719`、`:7072-7077`（`getAssemblies` 回 `{"error":…,"assemblies":[]}`）、`:988-993`、`:4457-4464`（budget 截斷的類別帶 `"truncated":"memory_budget"|"instance_budget"`）
- 成因：DLL 的 handler 幾乎都用「result 內含 `error` 字串」回報業務錯誤，只有例外才走 envelope。UI 的 DTO 是否宣告 `Error` 屬性全看當初誰寫的：runtime 尚未 resolve 時，`getAssemblies` 在 UI 上看起來就是「0 個 assembly」，沒有任何原因；snapshot 中被 budget 截斷的類別也看起來和「沒有 instance 的類別」一模一樣（`truncated` 這個 key 直接被 STJ 忽略）。
- 修法：定一條規則並寫進 `pipe-protocol.md`：handler 層級的失敗一律有 `result.error`，UI 每個 result DTO 都要宣告 `Error`（可以抽成 `IResultWithError` 介面，由 `BackendAdapter` 統一檢查），或者反過來讓 `PipeServer::Dispatch` 把 `result.error` 提升成 envelope error。`SnapshotClassEntry` 加上 `[JsonPropertyName("truncated")] string? Truncated`，讓 UI 能標示被截斷的類別。

**C-M8 · 數值掃描的值在 UI 端一律是 `double`：64-bit 整數失去精度，小數對整數型別被靜默截斷**
- 位置：`ui/UnityDumpUI.Core/Services/BackendAdapter.cs:996-1008`（`BeginValueScanAsync(double value, double? valueTo)` → `Dbl(...)`）、`:1084-1096`（refine）、`ProtocolModels.cs:881-883`（`GroupSlotRequest.Value` / `ValueTo` 是 `double?`）；DLL 端 `dll/src/shared/ExportAPI.cpp:1397`、`:1414`（`static_cast<int64_t/uint64_t>(double)`）
- 成因：大於 2^53 的 `Int64` / `UInt64`（例如 `DateTime.Ticks` 約 6.3e17、Steam ID）經過 `double` 之後尾數會被捨入，exact scan 永遠找不到。DLL 端的 `PackJsonValueToBytes` 在 JSON 為浮點數時直接截斷（int32 掃 `3.7` 會變成 `3`，不會拒絕）；超出範圍的 double 轉整數在 C++ 中是 UB（例如 `1e30`、NaN），之後的 range 檢查也沒有意義。DLL 其實已經支援字串形式的數值（`:1398-1407`），只是 UI 沒有用。
- 修法：UI 端整數 dataType 改送字串（`"123456789012345678"`）或 `long` / `ulong` 的 JSON number（`JsonSerializer.SerializeToElement(long, ...Int64)`），只有 float / double 才送 double；DLL 端在 `asI64` / `asU64` 中，遇到浮點 JSON 時先檢查 `std::isfinite`、確認 `trunc(d) == d`，並且落在目標範圍內，否則回 false。

### Low

**C-L1 · Magic string 規則破口（確認 AUDIT 訊號 2）與 dead 常數**
- 位置：字面值 `"ping"`（`ExportAPI.cpp:2002`）、`"lookupSymbol"`（`:3127`）、`"listSymbols"`（`:3137`）、`"getMetadataStats"`（`:6534`、`:8762`）、`"il2cppGetMetadataTypeFields"`（`:8503`）、`"il2cppGetMetadataRegistration"`（`:8780`）；`MagicStrings.h:54-56`、`:64` 的 `CmdExportCeXml` / `CmdExportCsx` / `CmdExportSdkHeader` / `CmdIL2CPPGetGenericInstances` 沒有任何 handler，repo 中也沒有引用（只剩 `docs/Archiutecture.md:365-384` 還在描述）；`MagicStrings.h:3`、`:41` 說「mirror in C# UI/Constants.cs」，但 UI 端沒有任何指令常數，`BackendAdapter` 全部用字面值（`:127`、`:180`、`:1024` 等），而 `PipeFor` 的路由表（`:49-56`）也是用字串比對。
- 修法：補上 `CmdPing` / `CmdLookupSymbol` / `CmdListSymbols` / `CmdGetMetadataStats` / `CmdIL2CPPGetMetadataTypeFields` / `CmdIL2CPPGetMetadataRegistration`；刪除 4 個 dead 常數，並同步更新 `Archiutecture.md`；UI 端新增 `PipeCommands` 靜態類別，讓 `BackendAdapter` 和 `BulkCommands` 都引用它。

**C-L2 · `pipe-protocol.md` 與程式碼的覆蓋率落差，以及其他文件 / 註解漂移**
- 位置與內容：
  - `docs/pipe-protocol.md` 完全沒有出現（grep 0 筆）的已註冊指令：`scanFieldValue`、`beginValueScan`、`refineValueScan`、`endValueScan`、`cancelScan`、`beginGroupScan`、`refineGroupScan`、`queryGroupCandidates`、`endGroupScan`、`getDispatcherStats`、`dumpClassInfo`、`setKeepForeground`、`getKeepForeground`、`diagnoseClassReferences`、`dumpLazyInitSlots`、`il2cppGetMetadataRegistration`、`getMetadataStats`（共 17 個；它們的契約目前只寫在 `MagicStrings.h` 的註解裡）。
  - `MagicStrings.h:233-237` 說 class-scoped `scanFieldValue` 要傳 `ns` / `class`，但 `resolveClassFromArgs`（`ExportAPI.cpp:3773-3775`、`:7152-7154`）讀的是 `namespace` / `name`（`tools/Test-ScanFieldValue.ps1:143-145` 也用後者）。照文件呼叫一定得到「missing 'assembly' or 'name' arg」。
  - `MagicStrings.h:98-99`：「19a scope: IL2CPP, static methods only … no string args」已過時（現在兩個 backend 都支援 instance / string / value-struct）；`:285-287`：「Auto-cleanup on pipe disconnect」實際上只在最後一個 client 離開時才會發生（`ExportAPI.cpp:10596-10603`）。
  - `BackendAdapter.cs:1214-1217`：XML doc 說 hard-cap 是 1024 / 256，實作是 8192 / 4096（`ExportAPI.cpp:4012-4017`、`:8051-8055`）。
  - `BackendAdapter.cs:585-587`：說 address 參數可以是「decimal integer」，但 `parseAddr` 一律用 16 進位解析字串（見 C-L4）。
  - `ExportAPI.cpp:2993-2994` 的註解宣稱「otherwise treat as ref by default below」，實際上不會成立（見 C-H2）。
- 修法：逐條修正；`pipe-protocol.md` 補上這 17 個指令的 request / response 章節（可以直接搬 `MagicStrings.h` 中的註解，再讓 `MagicStrings.h` 只保留指向文件的連結）。

**C-L3 · 次要的雙胞胎漂移（AUDIT 訊號 1 的實例）**
- `findInstances`：IL2CPP 預設排除 metadata arena 並 `skipMapped=true`（`ExportAPI.cpp:8854-8861`），Mono 直接 `ScanForPointer(vt, limit, &stats)`（`:5307`），不排除 `MEM_MAPPED`，但 Mono snapshot 的 fast path 自己又加了 `skipMapped=true`（`:4182-4184`，註解說這樣是為了「和 wide walk 結果一致」）。同一個類別在 `findInstances` 和 snapshot 中的 instance 集合因此不同。
- `listFieldValuesAcrossInstances` 兩個 backend 都**沒有**套用 `filterMatches`（`:5426`、`:8970`），而 `findInstances` 預設 `validate=true`。對同一個類別，「列出所有 instance」和「列出所有 instance 的某個欄位」得到的 instance 集合不同（後者含有 stale 的 stack slot）。
- `liveOnly` 只有 Mono 實作（`:5276-5308`），IL2CPP 會靜默忽略。
- IL2CPP `snapshotBegin` 在 `GetDomain() == nullptr` 時不建立 decoder，`plan` 也沒有被消耗（`:8236-8256`）。chunk 端會改走 legacy slicer（`:8381-8391`），而 `s.classes` 是空的，所以第一個 chunk 就 `done:true`、0 個類別，但 Begin 回報的 `totalClasses` 是 plan 的大小。這種情況應該在 Begin 直接回錯誤。
- Mono `walkInstance` 在 `ThreadAttachGuard` 外直接呼叫 `r.class_is_valuetype(k)`（`:6334-6336`），違反 Mono thread-attach rule（直接呼叫 `r.class_*` 必須在 guard 內）；IL2CPP 版本同樣位置（`:9814-9816`）無妨。
- Mono `getMetadataStats`（`:6530-6550`）仍然遵循「刻意不開 outer guard」的舊說法，每次連線都對每個 assembly 做一次 attach / detach；`searchSymbols` / `dumpClassInfo` / snapshot 已經改成「一個 outer guard」才是正確作法（`:4946-4956`）。這個 handler 在每次 connect 時都會執行（`BackendAdapter.cs:192-207`）。
- 修法：Mono `findInstances` / `listFieldValuesAcrossInstances` 傳入 `skipMapped=true`；`listFieldValuesAcrossInstances` 呼叫 `filterMatches`（至少做 validate）；IL2CPP `liveOnly` 回 `live_walk_unavailable:true`；`snapshotBegin` 在 domain 為 null 時回錯誤；把 `class_is_valuetype` 移進現有的 guard；`getMetadataStats` 外層加一個 `ThreadAttachGuard`，並更新註解。

**C-L4 · `parseAddr` 太寬鬆：接受尾端垃圾字元、在 x86 上靜默截斷、十進位字串會被當成 16 進位**
- 位置：`dll/src/shared/ExportAPI.cpp:2405-2425`
- 成因：`_strtoui64` 遇到第一個非 hex 字元就停，`"0x1234zz"` 會被當成 `0x1234`；超過 64 bit 時飽和成 `ULLONG_MAX`；`static_cast<uintptr_t>` 在 x86 Mono 上會把 `0x1_2345_6789` 截成 `0x23456789`。`writeMemory` / `setInstanceField` 因此可能寫到使用者沒有輸入的位址。負的 JSON 整數也會被直接 cast。
- 修法：要求整個字串都被吃完（`*end == '\0'`），檢查 `errno == ERANGE`，並在 `val > UINTPTR_MAX` 時回 false；只接受非負整數；文件寫明「字串一律是 hex」。

**C-L5 · 參數驗證的邊界**
- `refineValueScan`：`limit > 50000` 時被**重設成 5000**，而不是 clamp 到 50000（`ExportAPI.cpp:2098-2099`）；`refineGroupScan` / `queryGroupCandidates` / `beginGroupScan` 的 limit 處理方式相同（`:2208-2209`、`:2347-2348`、`:6160-6161`）。
- `queryGroupCandidates`：負的 `offset` 經 `static_cast<size_t>` 變成極大值，`offset + limit` 會溢位，`truncated` 的計算也跟著錯（`:2346`、`:2357`、`:2368`）。
- `walkArray`：`elementSize` 沒有上限，`std::vector<std::uint8_t> buf(elemSize)` 最多會配置 4096 次（`:3013-3014`、`:3065`）；32-bit 遊戲中可能造成 `bad_alloc` 或大量記憶體壓力。
- `walkInstance` 的 `dictLimit` 沒有上限（`:6292-6295`、`:9789-9792`），而且巢狀 dictionary 會遞迴 4 層，回應可能大到無法控制。
- `setInstanceField`：`uint*` 型別接受負數並 wrap（`-1` 會寫入 `0xFFFF…`，`:3393-3397`）；`char` 取 UTF-8 的第一個 byte，而不是 code point（`:3428-3430`）；float / double 分支在 `is_number()` 之後的兩個 `else if` 永遠執行不到（`:3462-3464`、`:3470-3472`）。`callMethod` 的 `char` 也是同樣問題（`:6919-6924`）。
- `snapshotBegin`：`structMemberCap` 沒有 clamp（`:4244-4245`、`:8190-8191`）；`memHeadroomMB` 是負數時，經 `static_cast<uint64_t>` 變成極大值後才乘上 1 MiB（`:4070-4071`），只是剛好被可用記憶體上限擋住。
- `PipeServer::Dispatch` 遇到 `"args": null` 時直接把 null 傳給 handler（`PipeServer.cpp:155`），handler 中的 `args.value(...)` 會丟 `type_error 306`，結果變成 `-32000`，而不是「沒有參數」。
- 修法：一律 clamp 到上限；`offset` 用 `int64` 讀取並拒絕負值；`elementSize` 上限設為 4096；`dictLimit` 上限設為 4096；`uint*` 拒絕負數；`char` 改用 UTF-8 → UTF-16 解碼取第一個 code unit；`Dispatch` 在 `args` 為 null 或不是 object 時改成 `json::object()`。

**C-L6 · Session 管理的語意與資源**
- 位置：`dll/src/shared/ValueScanSession.h:76-95`、`:124`；`GroupScanSession.h:87-106`；`SnapshotSession.h:181-189`；`ValueScanSession.cpp:27`、`GroupScanSession.cpp:20`、`SnapshotSession.cpp:26`；`ExportAPI.cpp:4274`、`:4829`
- 成因：
  1. Session 沒有 owner，id 是全域遞增的小整數。任何 client 都能 refine 或 end 別人的 session（CE bridge 和 UI 共用同一個 id 空間），而且 `ActiveClientCount() > 1` 的判斷在兩個 client 同時斷線時，兩邊都會看到 2，於是都不會 drop。
  2. 「5 分鐘自動過期」只在下一次 `Begin` 時檢查，沒有人再 Begin 就不會回收（最後一個 client 離開時的 `DropAll` 只能緩解一部分）。
  3. Mono snapshot session 同時保留 `plan`（全部 instance 位址）和 `classes`（全部已解碼的 JSON）直到 End；chunk 是 `classes.push_back(s.classes[i])` 深複製，而且在全域 mutex 內進行。32-bit 遊戲中的尖峰記憶體因此是「完整 snapshot × 2 個 chunk」再加上 plan。
- 修法：session 記錄建立者的 client id（由 PipeServer 傳入 handler context），refine / end 時驗證；可以加一個背景 timer，或在每次 RPC 時順便檢查過期；Mono session 在 decode 完成後 `plan.clear(); plan.shrink_to_fit();`，chunk 改成先在鎖內取出 shared payload 的參考，再到鎖外複製。

**C-L7 · 32-bit OOM 閘門與 build id 的小問題**
- 位置：`dll/src/shared/ExportAPI.cpp:125-132`、`:4052-4053`、`:8088-8089`；`dll/src/shared/SnapshotSession.cpp:100-104`；`ExportAPI.cpp:1873-1874`、`:4217-4221`
- 成因：
  1. 拒絕門檻是固定的 3 GB private usage，只適用於「WOW64 + LARGEADDRESSAWARE」；非 LAA 的舊 32-bit 遊戲上限是 2 GB，這個閘門永遠不會觸發。
  2. `ComputeGameBuildId` 找不到模組時會改用 host EXE，所以永遠不會回傳空字串；`if (id.empty())` 的 legacy `mono.dll` fallback 是 dead code，Unity < 5.4 的遊戲實際上 hash 的是 EXE。
- 修法：門檻改成以 `GlobalMemoryStatusEx().ullTotalVirtual` 的某個比例計算（例如 75%）；`ComputeGameBuildId` 在找不到指定模組時回傳空字串，把 fallback 決定權交給呼叫端（handshake 與 snapshot 需要一起改，才能維持兩邊一致）。

**C-L8 · `PipeClient` / `BackendAdapter` 的小問題**
- `SnapshotChunkAsync` 在 `Send` 回傳、`_gate` 已釋放之後，才讀取 `PipeFor("snapshotChunk").LastReadMs` 等欄位（`BackendAdapter.cs:1287-1290`）。bulk pipe 是共用的，這段期間如果有其他 bulk RPC（例如 `findInstances`）完成，telemetry 就會被覆蓋。`PipeClient.cs:58-60` 宣稱「single-flight, so plain fields are safe right after the call」並不成立。修法：讓 `SendAsync` 回傳一個 telemetry struct，或把數值放在 envelope 外的 out 參數。
- `CancelScanAsync` 每次都新開一條連線（`BackendAdapter.cs:1036-1047`），斷線時會觸發 DLL 的 full GC disconnect hook（見 A-L20），而且 `cancelScan` 沒有目標 id，會連 CE bridge 的 scan 一起取消（見 C-M1）。
- Handshake 沒有 capability 清單（`ExportAPI.cpp:1904-1950`），UI 只能用 `backend` 字串判斷 `dumpIl2cppSymbols` / `diagnoseClassReferences` / `monoCompileMethod` 等只有單一 backend 才有的指令，這和「UI 必須 backend-agnostic」的規則相衝突（Block E 的訊號 1 部分源自這裡）。修法：handshake 加上 `"commands": [...]`，內容就是 `g_handlers` 的 key。
- `SnapshotFieldEntry.Offset` 是 `ulong`（`ProtocolModels.cs:1248`），但 struct member 的 `offset` 是有號的 `relOff`（`ExportAPI.cpp:302`、`:543`）。遇到 torn class 而算出負值時，整個 chunk 會反序列化失敗（待驗證，正常 class 不會出現負值）。修法：DLL 端在 `relOff < 0` 時跳過該 member，或 UI 端改成 `long`。

### audit-2026-11-23-plan.md §4「觀察到的訊號」結案

| # | 訊號 | 結論 |
|---|---|---|
| 1 | God function / 雙胞胎漂移風險最高 | **確認**。已找到的實際漂移：IL2CPP 內部的 enum 解碼（C-H3，同一個 backend 內兩份實作就已經不同）、snapshot 陣列解碼 vs `walkArray`（C-M4）、streaming vs decode-in-Begin（C-M5）、`findInstances` / `listFieldValues` 的過濾與 region 規則（C-L3）、`liveOnly` 只有 Mono、array fallback 只有 IL2CPP（B-H2）。另外也有「兩邊一起錯」的情況：`searchSymbols` 的 `truncated`（C-M6）、thread-static offset（C-H1）。建議把 per-field / per-array 的 decode 抽成以 backend trait 參數化的模板，而不是繼續維持兩份 800 行的 handler。 |
| 2 | Magic string 規則破口 | **確認並擴大**（C-L1）。除了原先列出的 6 處，另外有 4 個沒有 handler 的 dead 常數，而且 UI 端完全沒有指令常數。 |
| 3 | 併發假設不一致 | **確認**。`PipeClient.cs:3-6` 的註解已經過時（C-M3）。DLL 端的全域 session 與 cancel 都沒有 owner（C-L6、C-M1）；snapshot decode 在全域 mutex 內執行 GC 和 sleep（C-M5）。`callMethod` 的 `[&]` 捕捉在 timeout 時是否安全也查證過了：`SubmitAndWait` 在 `Running` 狀態下會等到 closure 結束才 unwind（`MainThreadDispatcher.cpp:365-376`），**不構成**問題。 |

### 四方對照表（摘要；只列有落差的列）

| 指令 | Mono | IL2CPP | UI 呼叫 | `pipe-protocol.md` | 備註 |
|---|---|---|---|---|---|
| `exportCeXml` / `exportCsx` / `exportSdkHeader` / `il2cppGetGenericInstances` | — | — | — | —（只有 `Archiutecture.md`） | dead 常數（C-L1） |
| `monoCompileMethod` / `monoGetVTable` | ✓ | — | — | ✓ | 只有 tools / Lua 使用 |
| `dumpIl2cppSymbols` | — | ✓ | ✓ | ✓ | Mono 上回 `-32601`；handshake 沒有 capability（C-L8） |
| `diagnoseClassReferences` / `dumpLazyInitSlots` / `il2cppGetMetadataRegistration` | — | ✓ | — | ✗ | 只有 tools 使用；文件缺漏 |
| `il2cppGetMetadataTypeFields` / `il2cppGetMetadataStrings` | — | ✓ | — | ✓ | 字面值註冊（前者） |
| value / group scan 系列（9 個）、`getDispatcherStats`、`dumpClassInfo`、`set/getKeepForeground`、`getMetadataStats` | ✓ | ✓ | ✓ | ✗ | 契約只存在於 `MagicStrings.h` 的註解（C-L2） |
| `lookupSymbol` / `listSymbols` | 共用 | 共用 | — | ✓ | 字面值註冊；Lua / tools 使用 |
| `scan` | 共用 | 共用 | — | ✓ | UI 不呼叫；目前只有清除 class-range cache 的用途 |

UI（`BackendAdapter.cs`）實際呼叫的 38 個指令，在對應的 backend 上都有 handler（`dumpIl2cppSymbols` 只存在於 IL2CPP，由 UI 自行依 backend 判斷）。`ProtocolJson.cs` 的 `[JsonSerializable]` 涵蓋了 `BackendAdapter` 用到的每一個 `RpcEnvelope<T>`，沒有發現 AOT 缺漏；真正的風險在於 DTO 欄位型別與 DLL 輸出不一致（C-H1、C-M2、C-L8）。

### 需要 live 驗證、這次沒能驗證的項目
- C-H1：對 mscorlib 的 `System.Threading.Thread`（Mono 與 IL2CPP 各一款）呼叫 `getClassInfo`，確認原始回應中的 `offset` 是否為 `18446744073709551615` / `4294967295`，以及 UI 是否丟出 `JsonException`；對含有該類別的 assembly 執行 `dumpClassInfo`。
- C-H2：在 Inspector 中展開 `UserClass[]`，比較 `element_size` 與 pointer size，並用 CE 核對 `[1]`、`[2]` 的指標。
- C-H3：在 Unity 2019 / 2020 的 IL2CPP 遊戲上，對同一個 enum 分別呼叫 `getEnumValues` 和 `il2cppGetMetadataTypeFields`，比較兩邊的值。
- C-M1：UI 執行 `beginValueScan` 時，從 CE bridge 送 `cancelScan`，同時在另一條連線執行 `findInstances(limit=4096)`，確認 `findInstances` 回傳的 match 數量明顯變少，卻沒有任何截斷標記。
- C-M5：在大型 IL2CPP 遊戲上執行 snapshot，Begin 完成後停留數分鐘（期間觸發 scene 切換）再拉 chunk，統計 header 與 klass 不符的 instance 比例。
- C-M8：對 `long` 型別的 `DateTime.Ticks` 欄位做 exact value scan，確認找不到。

## Block D — Rust Core、FFI 與離線工具鏈（static review，2026-11-23，基準 `dev` @ `6bea633`）

> 範圍：`dll/rust/`（`src/{lib,ffi,aob,group_match,value}.rs`、`src/chain/{mod,decode,msvc_sort}.rs`、`src/metadata/{mod,ffi,layout,parser}.rs`、`src/xref/mod.rs`、`include/discrete_core.h`、`build.rs`、`RustCore.cmake`、`Cargo.toml` / `Cargo.lock`、`rust-toolchain.toml`）；C++ wrapper `dll/src/shared/{AobPatternRs,GroupMatchRs,ValueRefineRs}.cpp`、`dll/src/il2cpp/{MetadataParserRs,MetadataParserHostPath,IL2CPPChainDetectorRs,IL2CPPFieldXrefScanRs}.cpp`、`MetadataParser.h`；離線工具 `tools/metadata-cli/{SymbolPipeline,PEParser,CodeGenModulesFinder,CMakeLists}`（`RegistrationFinder.cpp` 只略讀）、`tools/verify/metadata_golden.py`；建置 / 釘版 / 發行 `build.cmd`、`build.ps1`、根與 `dll/` 的 `CMakeLists.txt`、`sync_vendor.ps1`、`tools/vendor_pins.json`、`ui/Directory.Build.props`、`ui/global.json`、各 `.csproj` 的 `PackageReference`、`.github/workflows/release.yml`。
> 跨界只讀了邊界：`ExportAPI.cpp:3223-3258`（`getMethodAobPattern`，確認 AOB builder 在 x86 Mono DLL 上也會跑）、`:8695-8704`、`:10104-10108`（`FindFieldDefault` 呼叫端）、`IL2CPPFieldXref.cpp:897-929`（確認 method range 已被夾到 8 KiB）、`RustCore.cpp:36-65`（log sink / SEH shim）。
> `chain/decode.rs`、`chain/apx_map4.rs`、`xref/apx_mem.rs` 是經 oracle 驗證的 Zydis 對照表，本次只檢查 FFI 邊界、索引越界與 panic 路徑，沒有逐條重驗指令語意。`tools/zydis-oracle`、`tools/r2-diff` 不出貨，未審。
> 全部為靜態推論，沒有 live 驗證；標「待驗證」者推論鏈中有一環需要實機確認。本節沒有修改任何程式碼。

### Critical

（無。沒有找到會讓遊戲崩潰或破壞記憶體的 FFI 缺陷：每個 `extern "C"` 都在 `ffi::guard` 內，回傳的指標都借用 handle 擁有的記憶體，遊戲記憶體只經 `dc_read_fn` 讀取。）

### High

**D-H1 · AOB pattern builder 是 x86-64 解碼器，卻直接套用在 32-bit Mono 遊戲的 JIT 程式碼上，產生錯誤的 pattern**
- 位置：`dll/rust/src/aob.rs:22-24`（模組說明宣稱 x64 / x86 DLL 結果一致）、`:83-86`、`:350-357`（`0x40-0x4F` 一律當 REX）、`:131-135`（`A0-A3` moffs 固定 8 bytes）、`:148-150`（`B8+r` imm32 不萬用）、`:414-423`（`mod=00 rm=101` 當 RIP-relative）；`dll/src/shared/AobPatternRs.cpp:59-70`；呼叫端 `dll/src/shared/ExportAPI.cpp:3223-3258`（共用區段，Mono x86 DLL 也會註冊這個 handler）
- 成因：解碼器寫死 64-bit mode，C ABI 也沒有 mode 參數，所以 x86 Mono DLL 會把 32-bit 指令當成 64-bit 解碼：
  1. 在 32-bit mode 中，`0x40-0x4F` 是單一 byte 的 `INC` / `DEC r32`，這裡被當成 REX 吃掉，和下一條指令黏成一條，後續的長度與萬用位置全部錯位。
  2. `A1` / `A3`（`mov eax,[abs32]` / `mov [abs32],eax`）是 x86 Mono JIT 讀寫 static 欄位的常見寫法。在 32-bit mode 中 moffs 是 4 bytes，這裡吃 8 bytes，會吞掉下一條指令的前 4 bytes。
  3. x86 程式碼把絕對位址直接放在 `imm32` 中（`mov r32, imm32`、`push imm32`、`mov [m], imm32` 載入 vtable 或 static 位址），這裡只萬用相對跳躍與 RIP-relative disp32，絕對位址會被當成字面值。這些位址每次執行都會變，pattern 到下一個 session 就掃不到了。`rip_rel_count` 這個名稱在 x86 也沒有意義。
  結果是 32-bit Mono 遊戲（例如 Legend of Mortal）匯出的 `aobscanmodule` 腳本經常失效，而 `decoderBailed` 並不會亮起，使用者無從得知。
- 修法：`dc_aob_build_pattern` 加一個 `mode` 參數（32 / 64），由 `AobPatternRs.cpp` 依 `sizeof(void*)` 傳入；32-bit mode 中 `0x40-0x4F` 改成 1-byte opcode、moffs 改成 4 bytes、`mod=00 rm=101` 改成「絕對 disp32」（照樣萬用，但改記在另一個計數器）。另外加一個選項：x86 模式下，數值落在任何已載入模組或 heap 範圍內的 `imm32` 一律萬用。在 32-bit mode 完成之前，至少要在 x86 build 的回應中加上 `"arch":"x86","decoder":"x64-only"` 警示，UI 再據此提示使用者。之後重新錄製 `r2_golden.json` 中的 AOB 部分，並補上 32-bit 測試向量。

### Medium

**D-M1 · Chain detector 和 RIP xref scan 碰到不可讀的 page 時，會連帶丟掉它前面最多 1 MiB 的可讀程式碼**
- 位置：`dll/rust/src/chain/mod.rs:395-409`；`dll/rust/src/xref/mod.rs:44-45`、`:140-150`
- 成因：每個 chunk 用一次 `reader.read(sec_va+pos, 1 MiB+overlap)` 整塊讀取，任何一頁讀取失敗就整塊失敗，接著 `pos += 4096` 再讀一次「新的」1 MiB。只要壞頁還落在新的 chunk 裡，讀取就會繼續失敗；每失敗一次，`pos` 推進的那 4 KiB 都沒有被掃描。所以單一壞頁（anti-cheat 把 `.text` 部分頁面設成 `PAGE_NOACCESS`，正是註解「AC-stripped page」描述的情境）會讓壞頁之前、同一個 chunk 範圍內的所有可讀程式碼都被跳過，而不只是壞頁本身。每個壞頁還會觸發最多 256 次、每次接近 1 MiB 的 SEH 讀取。這個行為和 legacy C++ 一樣（逐行移植），所以 golden 檢查抓不到。
- 修法：整塊讀取失敗時，改成在同一個 chunk 內以 4 KiB 為單位逐頁讀取：可讀的頁照常掃描（頁與頁之間保留 overlap），只跳過真正讀不到的頁，`failed_reads` 改成以頁為單位計數。也可以先用 `VirtualQuery` 把 section 切成可讀的區段再掃描。改完之後要重新錄製 `r2_golden.json`。

**D-M2 · `discrete_core.h` 與 Rust `#[repr(C)]` 沒有任何編譯期對照，struct layout 漂移不會被抓到（audit-2026-11-23-plan.md 訊號 1）**
- 位置：`dll/rust/include/discrete_core.h:68-127`、`:186-196`、`:239-305`；`dll/rust/src/metadata/ffi.rs:41-133`；`dll/rust/src/chain/mod.rs:68-90`；唯一的 layout 測試在 `aob.rs:1206-1214`、`xref/tests.rs:326-329`
- 成因：`lib.rs:24-25` 明訂「手動同步、不用 cbindgen」，但兩邊都沒有補上檢查：C++ 端完全沒有 `static_assert(sizeof/offsetof(Dc*))`；Rust 端只測了 `DcAobResult` 和 4 個 `DcXref*`，其餘 metadata 的 7 個 row struct（`DcTypeDef` 中 `uint16_t` 與 `int32_t` 交錯，靠隱含 padding 對齊）、`DcChainMatch`、`DcChainStats` 都沒有測試。這次逐欄位人工比對了 14 個 struct 與約 40 個函式簽章，x86 / x64 **目前都一致**；但只要任何一邊加了欄位或調換順序，就會在執行期靜默讀到錯誤資料，i686 的 padding 又和 x64 不同。
- 修法：Rust 端對每個 `#[repr(C)]` 用 `const _: () = assert!(offset_of!(..) == ..)` 寫死 x86 / x64 兩組數值；C++ 端在對應的 `*Rs.cpp` 加上同一組 `static_assert(offsetof(DcTypeDef, field_count) == ...)`。成本更低的作法是讓 Rust 匯出一個 `dc_layout_probe(DcLayout* out)`，回傳各 struct 的 size / offset，`RustCore::Init()` 再和 C++ 的 `sizeof` / `offsetof` 比對，不一致就寫 log 並停用 Rust 路徑。

**D-M3 · v38 / v39 的可變寬度 index 推斷只驗證 TypeDef[0]，method / parameter / image / field 表的 stride 錯了也不會被發現**
- 位置：`dll/rust/src/metadata/parser.rs:734-751`（`type_index_size` 由 params 的 row size 推出，推不出時靜默保留 4）、`:746-750`（`get_index_size` 的邊界）、`:873-909`（`validate_type_table` 只看 TypeDef[0]）、`:1012-1021`、`:1173-1182`、`:1287-1296`、`:1373-1382`（各表的 count 都是 `size / stride` 取整，不檢查餘數）、`:1214-1250`、`:1397-1409`
- 成因：v38 的 TypeDefinition stride 是 `68 + 3·TIS + GCIS`，MethodDefinition 是 `24/20 + TDIS + TIS (+PIS) + GCIS`，Image 是 `32 + 2·TDIS`，各自依賴不同的寬度組合。`validate_type_table` 只能抓到 TIS / GCIS 錯誤；TDIS / PIS 錯了（例如某張表的 row count 剛好等於 255 / 65535，而 `get_index_size` 的 `<=` 和 runtime 實際規則差一，或 IL2CPP 未來調整規則），method 名稱、參數、image 範圍就會整批錯位，`loaded()` / `tables_loaded` 卻仍然是 true。`metadata-cli` 的符號輸出、`il2cppGetMetadata*` 系列 RPC 會照單全收。v38 header 中每個 section 都有 `count` 欄位，可以直接驗證，但目前只有 parameters 讀了它。
- 修法：v38+ 載入時，對每張表檢查 `section.size == section.count * stride`，再加上 `size % stride == 0`；不一致就把該表的 count 設成 0、寫 log，並在 `getMetadataStats` 回報 `tablesDegraded`。`type_index_size` 推不出來（`inferred ∉ {1,2,4}`）時也要寫 log，不能靜默沿用 4。

**D-M4 · Golden-hash 閘門沒有接進 build 或 CI；`cargo test` 也不是在出貨的編譯設定下執行（audit-2026-11-23-plan.md 訊號 3）**
- 位置：`build.ps1:685-709`（`Run-RustTests` 只跑 `cargo test`）、`:840-845`（`All` 的順序是 DLL → UI → Test → MetadataCli，跑測試時 `r2-dump` / `xref-dump` 還沒建置）；`.github/workflows/release.yml:83-88`；`dll/rust/src/metadata/tests.rs:1288-1294`（沒有 fixture 時直接 `return`，沒有任何輸出）；`dll/rust/src/ffi.rs:162-168`；`dll/rust/RustCore.cmake:38-50`
- 成因：
  1. `metadata_golden.py` / `r2_golden.py` 只能手動執行，`build.ps1` 和 `release.yml` 都沒有呼叫。CI 也沒有 fixture，所以 R1 / R2「行為不變」的保證在發行流程中從來沒有被檢查過。
  2. `real_metadata_fixtures` 沒有 fixture 時會靜默通過，在測試報告中和「真的跑過」無法區分。
  3. `ffi.rs:163-165` 的註解說這個測試「證明這個 target + CRT 組合的 unwinding 可行」，但 `Run-RustTests` 跑的是 dev profile、動態 CRT、使用者自己的 `RUSTFLAGS`。出貨的 Publish 是 release + LTO + `codegen-units=1` + `+crt-static`（由 `RustCore.cmake` 用 `CARGO_ENCODED_RUSTFLAGS` 強制指定），兩者不是同一個組合。
- 修法：`Run-RustTests` 再跑一輪 `cargo test --release`，並設定 `CARGO_ENCODED_RUSTFLAGS=-Ctarget-feature=+crt-static`（Publish 模式必跑）。`real_metadata_fixtures` 沒有 fixture 時用 `eprintln!` 印出 `SKIPPED`，`build.ps1` 也在 summary 中標示出來。`All` 改成先跑 `Build-MetadataCli` 再跑 `Run-Tests`；有 fixture 時（`DISCRETE_METADATA_FIXTURES` 或 `fixtures.local.txt`）自動執行兩支 golden 腳本，exit 1 當成失敗、exit 2（沒有 fixture）當成警告。

**D-M5 · `release.yml` 遇到已存在的 release 時，會用 raw commit list 覆蓋維護者改寫過的 notes，並替換已發布 release 的 assets（§7 訊號確認）**
- 位置：`.github/workflows/release.yml:159-173`（`gh release edit --notes-file`、`gh release upload --clobber`）、`:114-157`
- 成因：「Re-run jobs」（例如重跑上傳失敗的 job）或從網頁先建立 release 再推 tag，都會走 `gh release view` 成功的分支，接著：(a) 維護者依 CONTRIBUTING 規則改寫的 notes 被 raw commit list 覆蓋；(b) 這個分支沒有 `--draft`，如果 release 已經發布，`--clobber` 會直接替換公開的 zip 和 `.sha256`，已下載的使用者手上的雜湊就對不上了。這與 CLAUDE.md「Releases stay drafts with rewritten notes」的規則正面衝突。
- 修法：release 已存在時，先檢查 `gh release view --json isDraft,body`。已發布就直接 fail（「tag already published; delete + re-tag」）；是 draft 但 body 已被改寫（不是 workflow 產生的樣板，例如本來就沒有 `## Changes since` 標記），只上傳 assets、不動 notes。`gh release create` 本身維持現狀。

**D-M6 · `metadata-cli` 的 method → `methodPointers[]` 用「global method index − image.methodStart」定位，而 runtime 用的是 token RID（待驗證）**
- 位置：`tools/metadata-cli/SymbolPipeline.cpp:241-253`；`dll/rust/src/metadata/parser.rs:1429-1464`（`methodStart` = 該 image 所有 type `methodStart` 的最小值）；`tools/metadata-cli/SymbolPipeline.h:11-15`
- 成因：IL2CPP（v24.2+）是用 `codeGenModule->methodPointers[GetTokenRowId(method.token) - 1]` 解析方法指標，Il2CppDumper 也是這樣做。這裡的公式要成立，必須「image 的方法在 metadata 中連續排列，而且排列順序和 MethodDef token 的順序相同」。只要任一條件不成立（例如 type 在 metadata 中的順序和 TypeDef token 順序不同、方法數為 0 的 type 夾在中間、某些 image 的 method 區段不連續），整個 image 之後的方法都會對到錯誤的位址，而輸出的 `.dd64` / symbol 檔看起來完全正常。
- 修法：改成 `localIdx = (m.token & 0x00FFFFFF) - 1`；過渡期間兩個公式都算，不一致時計數並寫進 `PipelineResult`（例如 `methodsIndexMismatch`），用 fixture 確認之後再移除舊公式。

**D-M7 · 出貨 UI 的 NuGet 相依仍有浮動版本，而且沒有 lock file；SDK 也是浮動的（§7 訊號確認）**
- 位置：`ui/UnityDumpUI/UnityDumpUI.csproj:119`（`CommunityToolkit.Mvvm 8.*`）；`ui/UnityDumpUI.Tests/UnityDumpUI.Tests.csproj:84-85`（`xunit.v3 3.*`、`xunit.runner.visualstudio 3.*`）；`ui/Directory.Build.props:20`（`XunitV3Version=3.0.0` 定義了但沒有任何地方引用，是 dead property）、`:56-60`（同一個檔案明文禁止浮動版本）；`ui/global.json:3-4`（`rollForward: latestFeature`）；`.github/workflows/release.yml:62-65`（`dotnet-version: '10.0.x'`）；整個 `ui/` 沒有 `RestorePackagesWithLockFile`
- 成因：`CommunityToolkit.Mvvm` 是 source generator，它產生的程式碼會被 AOT 編譯進出貨的 `DiscreteUI.exe`。8.x 的任何 minor 版本都可能改變產生的程式碼（以及 trimming / AOT 警告）。同一個 tag 在不同日期重新建置，可能得到不同的 binary，`TreatWarningsAsErrors` 也可能突然讓 release 失敗。測試端的 `3.*` 同理。`vendor_pins.json`、`Cargo.lock --locked`、`rust-toolchain.toml` 都已精確釘版，只有 .NET 這一層例外。
- 修法：把這三個 `PackageReference` 改成精確版本（`xunit.v3` 改用 `$(XunitV3Version)`，或刪掉這個 dead property）；在 `ui/Directory.Build.props` 啟用 `RestorePackagesWithLockFile=true`，CI 加上 `RestoreLockedMode=true`；`global.json` 改成 `rollForward: latestPatch`，`setup-dotnet` 改用 `global-json-file: ui/global.json`。

### Low

**D-L1 · `Cargo.toml` 宣告 `rust-version = "1.85"`，但程式碼用了 Rust 1.88 才穩定的 let-chains**
- 位置：`dll/rust/Cargo.toml:17`；`dll/rust/src/chain/decode.rs:547`、`:703`、`:896-897`（`if … && let Some(..) = …`）
- 成因：MSRV 宣告過時。實際建置有 `rust-toolchain.toml`（1.100.0）保護，但 `rust-version` 會誤導 `cargo` 的 MSRV 檢查和讀者。
- 修法：改成實際的最低版本（≥ 1.88），或直接和 toolchain pin 保持一致。

**D-L2 · 「v16–v39 accepted」的實際語意是「只有 24/27/29/31–39 能解析型別表」；log 訊息誤導，還有 dead 分支（audit-2026-11-23-plan.md 訊號 2）**
- 位置：`dll/rust/src/metadata/parser.rs:521-531`（`type_def_sizeof` 只涵蓋 24/27/29/31–39）、`:564`、`:1212`（method 的 `30` 分支，因為 type 表永遠不會載入而成為 dead code）、`:630-641`、`:673-684`（v16–23/25/26/28/30/32–34 都印「shares v31's layout … accepting」）
- 成因：v16–v23、v25、v26、v28、v30 會 `loaded()==true`，但 `tables_loaded==false`（只有字串），log 卻說「與 v31 共用 layout，已接受」，使用者看到的是「metadata 載入成功、0 個 type」。v32–v34 走 v29 / v31 的 layout，從未用實際檔案驗證過。CLAUDE.md 的「v16–v39 accepted」也沒有說清楚這一點。
- 修法：soft warning 分成兩種：「strings-only（不支援型別表）」與「未驗證、比照 v31 解析」。`getMetadataStats` 加上 `typeTablesSupported`。刪掉 `30` 分支，或者有意支援 v30 時同時把它加進 `type_def_sizeof`。CLAUDE.md 的描述改成「v16–v39 accepted (strings); type tables for v24/27/29/31–39」。

**D-L3 · Rust 端大部分配置是 infallible，OOM 時會 abort 整個遊戲；legacy C++ 在這裡會丟 `bad_alloc`，可以被 pipe server 接住**
- 位置：`dll/rust/src/chain/mod.rs:378`、`:388`、`:468`；`dll/rust/src/xref/mod.rs:172`、`:190`、`:255`；`dll/rust/src/metadata/parser.rs:1129`、`:1271`、`:1355`、`:988-990`；`dll/rust/src/metadata/ffi.rs:519-522`；`dll/rust/src/ffi.rs:67-70`（`format!`）
- 成因：Rust 配置失敗會走 `handle_alloc_error` → abort，`catch_unwind` 接不住。只有 1 MiB 掃描 buffer、metadata 檔案 buffer 和 `group_match` 有用 `try_reserve`；結果向量、`HashSet`、`format!` 都沒有。x64 IL2CPP 遊戲碰到的機率很低（所以列為 Low），但這和 `group_match.rs:21-26`「never aborts the host on OOM」的設計精神不一致。
- 修法：結果向量改用 `try_reserve` 逐步成長，失敗時截斷並在結果中標記 `truncated`；`ffi::log` 中的 `format!` 在 panic 路徑上改用固定大小的 stack buffer。

**D-L4 · `Parser::Get()` 在 process 生命週期內只載入一次：失敗不重試，成功則常駐整個檔案**
- 位置：`dll/src/il2cpp/MetadataParserHostPath.cpp:27-34`；`dll/rust/src/metadata/parser.rs:33-34`、`:363-364`；`dll/rust/src/metadata/ffi.rs:27-33`、`:513-528`
- 成因：magic static 第一次呼叫時如果 `BackendDetector` 還解析不到 data dir，或檔案暫時被鎖住，之後所有 metadata RPC 都會永遠回「not loaded」，直到遊戲重啟。成功時則把整個 `global-metadata.dat`（上限 256 MiB）複製一份放在遊戲 process 裡，另外 `decoded_strings` 會快取所有 sanitize 過的字串，直到 process 結束。C++ wrapper 拿到字串後會立即複製（`MetadataParserRs.cpp:259-263`），所以這個快取只是為了讓指標在「回傳到複製之間」保持有效。
- 修法：`Get()` 改成「失敗後允許在下次呼叫時重試」（用 mutex 保護的 `std::optional<Parser>`，不要用 magic static）；檔案改用 `CreateFileMapping` 唯讀映射，不要整份複製。如果維持複製，`try_decode_string_default` 可以改成由呼叫端提供 buffer 的 API，省掉這個永久快取。

**D-L5 · `scan_rip` 在 chunk 邊界會重複回報，REX 前綴之後的 6-byte 形式會被歸到前一個 byte（待驗證呼叫端是否去重）**
- 位置：`dll/rust/src/xref/mod.rs:159-193`
- 成因：(a) chunk 的最後一個 byte 如果是 REX，前一個 chunk 會靠 overlap 回報 7-byte 形式（ip = p−1）；下一個 chunk 的 `p == 0` 不檢查 `buf[p-1]`，又會回報一次 6-byte 形式（ip = p），兩筆指向同一個 target。(b) 任何 6-byte 候選只要前一個 byte 剛好是 `0x40-0x4F`（上一條指令的 imm / disp），就會被跳過，改成以 ip−1 的 7-byte 形式回報，而 ip−1 可能落在前一個 method 的範圍內。行為和 legacy 相同，所以 golden 檢查抓不到。
- 修法：確認 `IL2CPPFieldXref` 的歸屬與去重邏輯；如果沒有去重，就以 `(ip, opcode)` 去重，並在 7-byte 形式命中時，額外驗證 `buf[p]` 是否確實在前一條指令的結尾之後（實際上無法得知時，兩種 ip 都保留，交給 method 歸屬去判斷）。

**D-L6 · Rust 的長時間掃描無法取消**
- 位置：`dll/rust/src/chain/mod.rs:390-478`；`dll/rust/src/xref/mod.rs:138-196`、`:211-260`；`include/discrete_core.h:266-269`、`:313-325`
- 成因：chain detector 與 RIP xref 會逐 MiB 掃描整個 `GameAssembly.dll`（可達數百 MB），C ABI 沒有 cancel 參數，`ScanCancel::RequestCancel()` 或 client 斷線都停不下來，worker thread 會一直佔著直到掃描完成。
- 修法：C ABI 加一個 `const volatile int32_t* cancel` 參數（或 `dc_cancel_fn` callback），每個 chunk 開始時檢查一次；取消時回傳的 handle 帶 `cancelled=1`，C++ 端再對應到既有的 `ScanCancel` 語意。

**D-L7 · 非建置類 target 也會遞增 build number，`dist\build_number.txt` 和 `dist\` 中的 binary 對不起來**
- 位置：`build.ps1:431-438`（只要沒有 `-NoBumpBuildNumber`，任何 target 都會 bump）、`:769-771`、`:823-826`、`:848`
- 成因：`build test`、`build deploy`、`build deploy-scripts`、`build ui` 都會讓 `build_number.txt` 加 1，接著 `Copy-CompanionArtifacts` 把新號碼複製到 `dist\`（`deploy` 還會再複製到 CE 目錄），但 DLL 仍是上一個號碼。之後回報問題時，用 build number 對照 binary 就會對錯。
- 修法：只有 `DLL` / `ProxyDLL` / `MetadataCli` / `UI` / `All` 才 bump；`Test` / `Deploy*` 一律當成 `-NoBumpBuildNumber`。

**D-L8 · 過時的變數、註解與文件**
- `build.ps1:113`：`$SLN` 指向不存在的 `ui\Discrete.sln`（實際是 `ui\UnityCEDumper.sln`），而且沒有任何地方使用（§7 訊號確認）。修法：刪除。
- `CMakeLists.txt:144-152`、`tools/metadata-cli/CMakeLists.txt:31-33`：仍以「避免 `/external:I` 洩漏到 Zydis vendor 子目錄」作為理由，但 Zydis 已經移除。修法：改寫成現況（只剩 MinHook）。
- `tools/metadata-cli/SymbolPipeline.h:11-15`：描述「每個 method 呼叫 `FindImageContainingMethod`」，實作（`SymbolPipeline.cpp:202-219`）改成以 image 為主走訪。修法：同步註解。
- `dll/rust/src/metadata/layout.rs:40-43`：宣稱 `vendor/Il2CppDumperV35plus` 「pinned at bbd9c0c2」，並引用 `MetadataClass.cs` 的行號，但 `sync_vendor.ps1:80-89` 讓 reference clone 跟著上游 tip 更新，行號會漂移。修法：在 `sync_vendor.ps1` 把這個 clone 釘在 `bbd9c0c2`，或在註解中說明行號以該 commit 為準。

**D-L9 · `release.yml` 的供應鏈與權限強化**
- 位置：`.github/workflows/release.yml:25-26`（整個 job 都有 `contents: write`）、`:37`、`:63`、`:106`（`actions/*@vN` 浮動 tag）、`:57`（`rustup self update` 沒有釘版）
- 成因：build 步驟會執行 repo 腳本、clone 第三方 repo、下載 crate，這些步驟都持有可寫 release 的 token；actions 使用可移動的 major tag。這和專案其他地方「精確釘版」的政策不一致。
- 修法：actions 改用 commit SHA 釘版；拆成兩個 job：`build`（`contents: read`，產出 artifact）與 `release`（`contents: write`，只下載 artifact 並呼叫 `gh`）。`rustup self update` 改成檢查版本，不足時才更新。

**D-L10 · `metadata_golden.py --record` 會直接覆蓋既有的 golden entry，不做任何比對**
- 位置：`tools/verify/metadata_golden.py:359-368`
- 成因：如果有人在回歸發生後執行 `--record`（例如以為只是新增 fixture），同一個檔案雜湊下的舊 golden 會被新雜湊直接蓋掉，閘門就失效了，而 diff 只會顯示 JSON 變更。
- 修法：`--record` 預設只新增 key 不存在的 entry；要覆蓋既有 entry 時，必須加上 `--rebaseline` 並印出哪些 check 改變了。`r2_golden.py` 這次沒有檢查是否有同樣的問題，請一併確認。

**D-L11 · `CodeGenModulesFinder` 的小問題**
- 位置：`tools/metadata-cli/CodeGenModulesFinder.cpp:86-91`、`:109`、`:239-257`
- 成因：(a) `Il2CppCodeGenModule::methodPointerCount` 是 `uint32_t`，後面接 4 bytes padding，這裡用 `u64` 讀取，等於假設 padding 一定是 0（MSVC 的常數初始化資料通常如此，但沒有保證）。(b) `off = cursor; continue;` 之後，迴圈本身還會再 `off += 8`，跳過一個 slot；目前只會跳過剛驗證失敗的那一格，無害，但很脆弱。(c) `moduleCount` 不含中間的 null slot，不等於實際的陣列長度。
- 修法：只讀 4 bytes 的 count；在迴圈內改成 `off = cursor - 8`，或把迴圈改成 while；另外回報 `arraySlots`（含 null）。

**D-L12 · `FindFieldDefault` / `FindTypeByName` / `FindImageContainingMethod` 每次呼叫都是 O(N) 線性掃描**
- 位置：`dll/rust/src/metadata/parser.rs:1100-1109`、`:1476-1487`、`:1642-1694`；呼叫端 `ExportAPI.cpp:8695-8704`、`:10104-10108`
- 成因：每個欄位都要掃一次整張 FieldDefaults 表（大型遊戲有數萬列）；`FindImageContainingMethod` 每次都要重新走訪每個 image 的所有 type。`metadata-cli/main.cpp:989-992` 自己就記錄了這會跑上數小時。
- 修法：仿照 `declaring_type_map`，用 `OnceLock` 延遲建立 `fieldIndex → row`、`(ns,name) → typeIdx`、`methodIdx → imageIdx` 三個索引；回傳值不變，golden 也不受影響。

### audit-2026-11-23-plan.md §5「觀察到的訊號」結案

| # | 訊號 | 結論 |
|---|---|---|
| 1 | `discrete_core.h` 靠手動同步，layout 漂移抓不到 | **確認**（D-M2）。人工比對結果：14 個 `#[repr(C)]` struct 與約 40 個 `extern "C"` 簽章目前和 header 完全一致，x86 / x64 的 padding 也一致；calling convention（`extern "C"` = cdecl）與 C++ 端的 function pointer 型別相符。風險在於「以後」，而不是「現在」。 |
| 2 | v16–v39 中多數版本只是「接受但未驗證」 | **確認並釐清**（D-L2、D-M3）。v16–23、25、26、28、30 只有字串，不會解析型別表；v32–34 比照 v29 / v31 的 layout 但沒有實檔驗證；v38 / v39 的可變寬度推斷缺少逐表驗證，是實際風險最高的部分。 |
| 3 | 真實檔案的 parser 測試在 CI 上是 no-op | **確認並擴大**（D-M4）。不只 `cargo test` 的 fixture 測試是 no-op，兩支 golden 腳本在 build 和 CI 上也都從來沒有執行過；`cargo test` 也不是在出貨的編譯設定下執行。 |

### 稽核檢查清單補充結論
- **`docs/rust-core.md` 規則逐條核對**：所有 `extern "C"` 本體都在 `ffi::guard` 內（`ffi.rs`、`metadata/ffi.rs`、`aob.rs`、`group_match.rs`、`value.rs`、`chain/mod.rs`、`xref/mod.rs` 逐一確認）；`DcBytes` 都指向 handle 擁有的 file buffer、`decoded_strings`，或 static 記憶體；位元組字串不做 UTF-8 驗證（只有 log 路徑用 `from_utf8_lossy`）；遊戲記憶體只透過 `Reader`（`SafeReadShim`）讀取，`aob.rs` 讀的是 C++ 已經複製出來的 buffer；proxy DLL 沒有 link `discrete_core`（`dll/CMakeLists.txt:264-303`）；`Cargo.toml` 設定 `panic = "unwind"`（兩個 profile 都是），`RustLogSink` 是 `noexcept` 並自行吞掉例外，C++ 例外不會穿越 Rust frame。
- **handle 生命週期**：`ChainResultHandle`、`ResultHandle<>`、`Parser` 的 move-only 語意都正確（拷貝建構已 `delete`，move 會把來源清空），所有例外路徑上都會呼叫 `dc_*_free`；沒有發現 double free 或 use-after-free。
- **i686 上的 `usize` 溢位**：parser 的邊界一律用 `u64` 計算，檔案上限 256 MiB，各表的 offset / size 都是 `i32` 且已檢查 `in_bounds`，所以 `idx * stride` 在 32-bit 上不會溢位；`group_match` / `aob` 的長度乘法都有用 `checked_*` 或事先 bound。
- **`msvc_sort.rs`**：逐函式對照了 MSVC STL 的 introsort 結構（insertion ≤ 32、ninther、heap fallback），沒有發現差異；是否逐 move 完全一致需要靠 `r2_golden` 驗證。它只是為了重現 legacy 的「同一個 hit IP 保留哪一筆」，不影響正確性。

### 需要 live / fixture 驗證、這次沒能驗證的項目
- D-H1：在 32-bit Mono 遊戲上，對含有 static 欄位存取的 method 呼叫 `getMethodAobPattern`，重啟遊戲後用 CE 的 `aobscanmodule` 驗證是否還能命中；同時檢查 `A1` / `A3` 與 `0x40-0x4F` 附近的萬用位置有沒有錯位。
- D-M1：找一款 anti-cheat 會把 `GameAssembly.dll` 部分頁面設成 `PAGE_NOACCESS` 的 IL2CPP 遊戲，比較 `findFieldReferences` / chain 掃描的 `failedReads` 與命中數，再用逐頁讀取的實驗版本比對。
- D-M3：找到（或合成）一個 typeDefinitions / genericContainers / parameters 的 count 正好落在 255 / 256 或 65535 / 65536 的 v38 / v39 檔案，比對 method 名稱與 Il2CppDumper 的輸出。
- D-M6：對 2–3 款 v24.2+ 遊戲執行 `metadata-cli symbols`，同時計算 token-RID 與 index-offset 兩種公式，統計不一致的數量。
- D-L5：檢查 `IL2CPPFieldXref.cpp` 是否對 RIP 候選以 ip 去重，並在 1 MiB 邊界附近製造一筆命中做驗證。

## Block E — 桌面 UI 與 CE 整合輸出：ViewModel、exporter、Lua bridge（static review，2026-11-23，基準 `dev` @ `6bea633`）

> 範圍：`ui/UnityDumpUI.Core/Services/Export/*`（`CeXmlExporter(.Live)`、`CsxExporter`、`SdkHeaderExporter`、`UnityTypeMap`、`ExportOptions`、`AaLuaLoader`、5 個 `*AaScriptGenerator`）；`BridgeInjector.cs`、`BridgeLuaResources.cs`；`scripts/{utils,unity_dump,unity_symbols,unity_invoke,mono_registersymbol_helpers,ce_table_bootstrap}.lua`；ViewModel 以 export / 連線 / 背景迴圈相關路徑為主：`AssemblyBrowserViewModel`（export 與 class 載入）、`LiveInspectionViewModel`（export、array 展開、auto-refresh）、`MethodOverloadsViewModel`（AA 產生參數）、`MainWindowViewModel`（pipe 重建、settings debounce、symbol export gate）、`SnapshotViewModel`（`GuessBackend`、capture 尾段）。其餘 ViewModel 只用 pattern 掃描（`async void` / `.Result` / `.Wait()` / fire-and-forget / CTS 生命週期），沒有逐行讀。
> 跨界只讀了邊界：`ExportAPI.cpp:2077-2081`、`:2462-2467`（`formatHex` 一律 `0x%016llX`）、`:3824-3831`（Mono `getClassInfo` 的 method 沒有 `address`）、`:6885`（`callMethod` 的 `args` 非陣列時視為空）；`PipeClient.cs:104`、`:163`（`_gate.WaitAsync(ct)`）；CE 的 `celua.txt`、`autorun/monoscript.lua`（`mono_enumImages`、`mono_method_getSignature`、`libmono.monopipe` / 全域 `monopipe` 相容層）。
> `SymbolExporter`、`Il2cppHeaderBatchExporter`、`AobMakerAppClient`、`PlayerLocator`、`SnapshotStore`、`Views/*` code-behind 這次沒有審。全部為靜態推論，沒有 live 驗證，也沒有修改任何程式碼。

### Critical

（無。沒有找到會讓遊戲或 UI 崩潰、或破壞使用者資料的缺陷；`UserSettingsStore.SaveAsync` 是 `.tmp` + `File.Move` 的原子寫入，被 debounce 取消也不會損毀設定檔。）

### High

**E-H1 · Live CE XML export 在 x86 讀 `List<T>._items` 固定讀 8 bytes，32-bit Mono 遊戲上每個非空的 `List<T>` 都匯出成「walk failed」的空群組 — ✅ FIXED (2026-11-26)**
- 位置：`ui/UnityDumpUI.Core/Services/Export/CeXmlExporter.Live.cs:906-918`（`ReadMemoryAsync(..., 8, ct)` + `ParseLeQword`）、`CeXmlExporter.cs:222-235`（`ParseLeQword` 要求剛好 16 個 hex 字元、組成 64-bit 值）；對照 `ui/UnityDumpUI/ViewModels/LiveInspectionViewModel.cs:1458-1479`（Inspector 已改成讀 `ptrSize` bytes，註解記錄了同一個 bug：活俠傳 Mono x86 `StatConvertCollectionData._list`）
- 成因：`itemsOff` 已經依 `options.PointerSize` 算成 `2*ptr`（x86 為 +0x8），但讀取長度仍寫死 8。x86 的 `List<T>` 在 +0x8 是 4-byte 的 `_items`，+0xC 是 `_size`，所以 `ParseLeQword` 得到 `_items | (_size << 32)`。只要 list 非空，`arrayAbs` 就是一個超過 4 GiB 的假位址，`WalkArrayAsync` 失敗，整個 list 只剩下標著 `(walk failed)` 的空群組（`walk is null` 分支）。`_size == 0` 時剛好正確，所以小型測試抓不到。Inspector 修過這個 bug，exporter 這份雙胞胎程式碼沒有跟著修。
- 修法：讀取長度改成 `ptr`，解析改成依實際 byte 數做 little-endian 組合（可直接共用 `LiveInspectionViewModel.ParseLittleEndianHexQword` 的語意，或讓 `ParseLeQword` 接受 4 / 8 bytes）；`CeXmlExporterLiveTests` 補一個 `PointerSize = 4`、`_size != 0` 的案例。

**E-H2 · Template 模式（Assembly Browser 的預設路徑）把 enum / 自訂 value struct 欄位當成 pointer 解參照，collection 的元素位址也是錯的**
- 位置：`ui/UnityDumpUI.Core/Services/Export/UnityTypeMap.cs:81-85`（未知型別一律 `Pointer`）；`CeXmlExporter.cs:418-503`（`!shape.IsPointer` 以外全部走 `Offsets=[0]` 群組）、`:474-492`（collection 元素 `+i*elemSize`、不跳過 array header、不解參照）；`CsxExporter.cs:283-318`（同一個元素模型）；觸發條件 `CeXmlExporter.cs:69-71` + `ui/UnityDumpUI/ViewModels/AssemblyBrowserViewModel.cs:104`（`CeXmlBaseAddress` 預設是 `"INSTANCE_ADDRESS"`，不是 hex → 一律退回 template 模式）
- 成因：
  1. getClassInfo 給的 enum 欄位型別是 `Game.WeaponType` 這類名稱，`UnityTypeMap.Resolve` 不認得就回傳 pointer shape，於是 enum / 自訂 struct 欄位被輸出成 `<Offsets><Offset>0</Offset></Offsets>` 的群組。CE 會把欄位裡的整數值當成指標去解參照，drilldown 後的 `value__` 子節點顯示的是不可讀的位址。遊戲 class 中 enum 欄位非常常見。
  2. Collection drilldown 把元素放在「解參照後的 array / List 物件 + `i*ComputeStructSize(child)`」：沒有加上 SZARRAY header（x64 0x20 / x86 0x10）；`List<T>` 沒有再經過 `_items` 解參照；reference 型別元素實際上是 pointer slot（stride = pointer size），這裡卻把 class 的 field 跨度（含物件 header）當成 stride，而且沒有逐元素解參照。`IsCollectionType` 還把 `HashSet` / `IEnumerable` / `IList` 也當成 array 處理。
  Live 模式（`CeXmlExporter.Live.cs`）的這三點都已經處理正確（`IsValueTypeClass`、`dataOff`、`_items` 群組），template 模式沒有跟上。
- 修法：template 模式比照 `EmitMetadataFieldAsync`（`Live.cs:1385-1456`）先 `GetClassInfoAsync` 解析欄位型別的 parent：`System.Enum` → 依 underlying 型別輸出 leaf，`System.ValueType` → 不解參照的 inline 群組。Collection 改用 live 模式的結構：outer deref → （List）`_items` deref → `dataOff + i*ptr` 的 slot（reference 元素再 `Offsets=[0]`）。沒有 pointer size 時，template 模式至少要在 root 描述註明「x64 layout」。`CsxExporter` 同步修改。

### Medium

**E-M1 · CE XML / CSX exporter 吞掉所有例外（包括取消），按 Cancel 或被新的 export 取代時，會把截斷的表格當成成功結果複製到剪貼簿**
- 位置：`CeXmlExporter.cs:169-172`、`:456`；`CeXmlExporter.Live.cs:277`、`:431`、`:451`、`:531`、`:917`、`:946`、`:1251`、`:1286`；`CsxExporter.cs:254`（全部是 bare `catch { }`，`Export/` 底下只有 `Il2cppHeaderBatchExporter` 會檢查 `ct`）；呼叫端 `AssemblyBrowserViewModel.cs:81`、`:544-578`、`:650-683`，`LiveInspectionViewModel.cs:1741`、`:1769-1819`、`:1849-1894`
- 成因：`CancelExport()` 或下一次 export 的 `_exportCts?.Cancel()` 取消 token 之後，後續每一個 RPC 都會在 `PipeClient._gate.WaitAsync(ct)`（`PipeClient.cs:104`、`:163`）立刻丟出 `OperationCanceledException`，但 exporter 的 bare catch 把它當成「這個節點解析失敗」吞掉，繼續遞迴。結果是 exporter 很快跑完並回傳一份只剩空群組的 XML。呼叫端的 `catch (OperationCanceledException)` 永遠收不到，所以會執行 `CopyToClipboardAsync(xml)`，並顯示 `copied N lines`。被取代的舊 export 也可能比新的那個晚寫入剪貼簿，覆蓋掉正確的結果。另外 `finally { IsExporting = false; }` 不分新舊，舊 export 結束時會把新 export 的忙碌狀態清掉（`AssemblyBrowserViewModel.cs:574-577`），被取代的 CTS 也從來沒有 `Dispose`。
- 修法：所有 `catch { }` 改成 `catch (Exception ex) when (ex is not OperationCanceledException)`，並在每次遞迴 / 迴圈開頭 `ct.ThrowIfCancellationRequested()`。呼叫端在 `await` 之後比對自己的 CTS 是否仍是 `_exportCts`，不是就不要寫剪貼簿，也不要動 `IsExporting`；被取代的 CTS 要 `Dispose`。

**E-M2 · Address Lookup AA 把 `System.String` 欄位當成 inline UTF-16 讀，沒有解參照**
- 位置：`ui/UnityDumpUI.Core/Services/Export/AddressLookupAaScriptGenerator.cs:177-184`（`Offsets = new[] { f.Offset }`）、`:213`（`"System.String" => CeValueType.UnicodeString`）
- 成因：managed string 欄位存的是指向 `String` 物件的指標。child record 解析到的是欄位本身的位址（`[symbol] + offset`），CE 會把指標的 8 個 byte 當成 UTF-16 文字顯示成亂碼。正確的位置是 `[[symbol]+offset] + 2*ptr + 4`。
- 修法：String 欄位改成兩層 offset `{ f.Offset, charsOff }`（CE 的 offset 是最內層放最後，要依 `AaScriptChildRecord` 的順序慣例排列），`charsOff` 依 pointer size 取 0x14 / 0xC。同一處的 `_ => Qword` fallback 在 x86 也應該是 4 bytes（併入 E-L4）。

**E-M3 · `ce_table_bootstrap.lua` 仍用 `injectDLL(...) == nil` 判斷失敗，與 CLAUDE.md 明列的 CE 回傳慣例相反**
- 位置：`scripts/ce_table_bootstrap.lua:257-260`；對照 `scripts/unity_dump.lua:159-174`（已修正並寫明原因）；backend 偵測 `ce_table_bootstrap.lua:196-205`
- 成因：`injectDLL` 失敗時回傳 `false`，不是 `nil`（`celua.txt`、CLAUDE.md §Cheat Engine）。這個 Mode-B 範本是給 CT 發布者複製的，失敗時不會 `error()`，而是繼續 `dofile` bridge，最後由 `unity_dump.lua` 從 CE 目錄再嘗試注入一次，錯誤訊息會指向錯誤的檔案位置。同一個檔案的 backend 偵測只認 `gameassembly.dll` / `mono-2.0-bdwgc.dll` / 完整等於 `mono.dll`，少了 `utils.lua:196-205` 支援的 `userassembly.dll`、`mono-2.0-sgen.dll`、`mono-2.0-boehm.dll`；而且 `name == "mono.dll"` 碰到 `enumModules` 回傳完整路徑時永遠不成立。
- 修法：改成 `if not injectDLL(dllPath) then error(...) end`；backend 偵測直接沿用 `utils.lua` 的 `M.detectBackend()`（basename + 同一份名單）。

**E-M4 · CE Lua bridge 的 pipe RPC 在 CE 主執行緒上無逾時地阻塞讀取**
- 位置：`scripts/unity_symbols.lua:159-161`、`scripts/unity_invoke.lua:302-307`、`ui/UnityDumpUI.Core/Services/Export/BootstrapAaScriptGenerator.cs:192-196`（bootstrap 的 handshake）
- 成因：AA `{$lua}` 區塊在 CE 的 GUI thread 上執行。`io.open` 的開檔失敗有 15×200 ms 的重試加 pump，但開檔成功之後 `f:read("*l")` 沒有任何逾時。DLL 的 handler 卡住（例如 `callMethod` 的 `main_thread=true` 遇到遊戲暫停或最小化、`findMethodInstances` 觸發 JIT、game thread 在 loader lock 中）時，Cheat Engine 會整個凍結，使用者沒有任何方法恢復。`utils.lua:161-171` 對 `executeCodeEx` 已經明確寫出「timeout 必須有限，否則 CE 會凍結」這條規則，但 pipe 路徑沒有套用。
- 修法：pipe I/O 改成在 `createThread` 中執行，主執行緒以 `sleep + pump` 輪詢，並設上限（例如 10 s，`callMethod` 依 `main_thread_timeout_ms` 加上餘裕）。逾時就 `error()`，並讓 DLL 端 `CancelIo`（關閉 handle）。至少 bootstrap 的 smoke handshake 可以直接改成 `pcall` 包住一個有逾時的版本。

**E-M5 · DLL-free 的 signature-match 註冊不檢查參數順序與個數，而且在 Mono 上可能永遠比對不到 primitive 參數（後者待驗證）**
- 位置：`ui/UnityDumpUI.Core/Services/Export/MonoRegisterSymbolAaScriptGenerator.cs:152-160`（有參數一律走 `BySignatureMatch`）；`scripts/mono_registersymbol_helpers.lua:585-603`（`string.find(sig, kw, 1, true)` 逐一做子字串比對）；參數來源 `ui/UnityDumpUI/ViewModels/MethodOverloadsViewModel.cs:382-390`
- 成因：
  1. `sigContains` 只檢查「每個 token 都出現在 signature 裡」。`(Int32, String)` 與 `(String, Int32)` 這兩個 overload 的 token 集合相同，`SigExcludes`（只收「別的 overload 有、這個沒有」的 token）也無法區分，結果會註冊第一個列舉到的 overload，而且不會報錯。`Foo(Int32)` 與 `Foo(Int32, Int32)` 同理，`SigExcludes` 是空的。子字串比對也會讓 `System.Int32` 命中 `System.Int32[]`。
  2. （待驗證）CE 的 Mono 分支由 MonoDataCollector 以 `mono_signature_get_desc` 產生 signature 字串，Mono 對 primitive 輸出的是 `int` / `single` / `string` 這類簡稱。`ParamTypes` 傳的是 `System.Int32`，所以凡是含有 primitive / string 參數的 overload，在 Mono 上都比對不到（`monoscript.lua:4387-4432` 只有 IL2CPP 分支會逐一讀取完整 typename）。`discrete_mono_registerSymbolEx` 有 typename → signature 的 fallback，但 generator 在有參數時並不呼叫它。
- 修法：有參數時改呼叫 `discrete_mono_registerSymbolEx`（逐位、比對個數與尾段名稱，`_discrete_typename_match`），再補一張 `System.Int32 ↔ int` 等 primitive 別名表。`BySignatureMatch` 只當最後的 fallback，命中多個 overload 時要 `eprint`，不能取第一個。

**E-M6 · Assembly Browser 切換 class 後沒有清掉 `_currentChain`，載入完成前或載入失敗時，export 輸出的是上一個 class**
- 位置：`ui/UnityDumpUI/ViewModels/AssemblyBrowserViewModel.cs:282-291`（只清 Fields / Methods）、`:436-442`（第一層 `getClassInfo` 失敗直接 `return`）、`:458`（只有成功時才指派）、`:690-691`（`CanExport` 只看 `SelectedClass`）、`:702`、`:537`、`:618`
- 成因：`OnSelectedClassChanged` 之後 export 指令立即可用，但 `_currentChain` 仍是舊 class 的資料。在 RPC 往返期間（大型 class 加上 ShowInherited 可能需要數秒），或新 class 的 `getClassInfo` 回傳錯誤時，`Export CE XML` / `Copy CE XML` / `Export .h` 匯出的都是舊 class，檔名也會用舊 class 的名稱，使用者只看得到新的 selection。註解（`:685-689`）把這個情況描述成「rare race window、no-op safely」，但實際上不是 no-op，而是輸出錯誤的 class。
- 修法：`OnSelectedClassChanged` 中立即把 `_currentChain` 清成空 list，並通知各 export 指令的 `CanExecute`；`CanExport` 改成 `_currentChain.Count > 0 && 最後一筆是 SelectedClass`。

**E-M7 · SDK header 的 struct 內容與它自己的 offset 註解不一致，無法直接拿來存取記憶體**
- 位置：`ui/UnityDumpUI.Core/Services/Export/SdkHeaderExporter.cs:53-97`；`UnityTypeMap.cs:61-85`
- 成因：在 `#pragma pack(push, 1)` 下依序輸出欄位，但 (a) 沒有輸出物件 header（第一個欄位實際位於 +0x10 / +0x8），也沒有在欄位之間補 padding，所以 C struct 中的 offset 是「前面欄位大小的累加」，不是 `// +0xNN` 註解寫的值；(b) enum / 自訂 struct 一律寫成 `void*`（8 bytes），Vector3 等型別使用了沒有定義的型別名稱；(c) 繼承鏈上的同名欄位（`ShowInherited` 時很常見）會產生重複 member，編譯失敗。
- 修法：依 `f.Offset` 排序，在欄位之間插入 `uint8_t _pad_XXXX[n]`，並從 header 大小開始；未知 value type 輸出 `uint8_t name[size]`，size 不明時只輸出註解。重複的名稱加上 `_<Declaring>` 後綴；可以再輸出 `static_assert(offsetof(...) == 0x..)`，讓問題在編譯期就被發現。

### Low

**E-L1 · `AobAaScriptGenerator` 的 `[DISABLE] {$lua}` 沒有重設 `_G.DISCRETE_HAD_ERROR`**
- 位置：`ui/UnityDumpUI.Core/Services/Export/AobAaScriptGenerator.cs:381-397`；對照 `MonoRegisterSymbolAaScriptGenerator.cs:180-189`（已寫明原因並補上）
- 成因：違反 `docs/lua-logging.md` 與 `AaLuaLoader.AppendHadErrorReset` 的「每個 [ENABLE] / [DISABLE] 都要重設」約定。只要之前有一次 [ENABLE] 失敗，之後每次 disable 都會讀到殘留的 `true`，Lua Engine 就一直不會關閉。
- 修法：在 `if syntaxcheck then return end` 之後呼叫 `AaLuaLoader.AppendHadErrorReset(sb)`。

**E-L2 · 產生的 AA 在前置條件失敗時用 `print + return`，CE 仍會把該項目勾成「已啟用」**
- 位置：`AaLuaLoader.cs:33-37`、`:43-47`；`MonoRegisterSymbolAaScriptGenerator.cs:105-110`、`:115-119`；`AobAaScriptGenerator.cs:343-347`；`AddressLookupAaScriptGenerator.cs:112-116`；`CallMethodAaScriptGenerator.cs:166-171`
- 成因：`{$lua}` 區塊 `return nil` 不會讓 AA 失敗，checkbox 照樣打勾，但 symbol 根本沒有註冊；相依於這個 symbol 的其他腳本之後才會失敗，看不出根因。`AddressLookup` 的 bridge 檢查更是多餘：它的 `{$asm}`（`alloc` / `dq`）完全不需要 bridge，卻要求先載入 bridge，並在沒有載入時把旗標設成錯誤。
- 修法：前置條件失敗時改用 `error('[Discrete] ...')`，讓 CE 拒絕啟用（錯誤訊息仍會顯示）；`AddressLookup` 移除 bridge 檢查。

**E-L3 · `CallMethodAaScriptGenerator` 的參數字面值邊界**
- 位置：`CallMethodAaScriptGenerator.cs:265`（`'\0'` → `"\\0"`）、`:200-203`、`:191-198`
- 成因：(a) Lua 的 `\ddd` 會讀取最多 3 位十進位數字，NUL 後面若緊接數字字元（例如 `"a\0" + "12"`），就會變成 `\012`（換行字元）；(b) `double.TryParse` 接受 `NaN` / `Infinity`，`ToString("R")` 輸出的 `NaN` / `Infinity` 在 Lua 中是未定義的全域變數，會變成 `argFloat(nil)`；(c) 數值解析失敗時靜默代入 0，例如使用者在 Int32 欄位輸入 `abc`，產生的腳本會以 0 呼叫 method。
- 修法：NUL 改成 `\000`；非有限值輸出 `0/0`、`1/0`、`-1/0`；解析失敗時 `Build` 直接丟出例外（或回傳錯誤）給 VM 顯示，不要產生腳本。

**E-L4 · x86 目標上多處仍寫死 8-byte pointer**
- 位置：`UnityTypeMap.cs:61-62`、`:91-92`（`IntPtr` / pointer = 8）；`CeXmlExporter.cs:89`、`:371`（root 群組 `"8 Bytes"`）、`:580`（`ComputeStructSize`）；`CsxExporter.cs:179-180`、`:300`（`Bytesize: 8`、`AutoCreateStructsize`）；`AddressLookupAaScriptGenerator.cs:209-214`（`IntPtr` / 預設 `Qword`）；`ExportOptions.cs:18`（預設 8）與 `AssemblyBrowserViewModel.cs:136-152`（`CurrentExportOptions` 沒有帶入 `PointerSize`，只能靠 `RenderLiveAsync` 用 root walk 修正）
- 成因：live 模式已經在 leaf 層級修正（`Live.cs:122-129`），template / CSX / SDK / AddressLookup 沒有。x86 遊戲上，`IntPtr` 欄位以 8 bytes 讀取會與下一個欄位重疊，CSX 的結構大小也會被高估。
- 修法：`UnityTypeMap.Resolve` 加上 `pointerSize` 參數；Browser 的 `CurrentExportOptions` 帶入 `GamePointerSize`（與 Inspector 的 `ResolveExportOptions` 一致）。

**E-L5 · CE 匯出的 `VariableType` / `Vartype` 用了 CE 不認得的 `12 Bytes` / `16 Bytes` / `24 Bytes`（待驗證）**
- 位置：`UnityTypeMap.cs:64-71`；使用處 `CeXmlExporter.cs:426-429`（template leaf）、`CsxExporter.cs:173`
- 成因：CE 的 variable type 只有 `Byte` / `2 Bytes` / `4 Bytes` / `8 Bytes` / `Float` / `Double` / `String` / `Array of byte` 等固定名稱。`Vector3` 等 value type 在 template 模式會被輸出成一個未知型別的 leaf，CE 可能以預設型別讀取，或拒絕載入整筆 entry。
- 修法：value struct 改成展開為 x / y / z 的 `Float` 子項（live 模式已經這樣做），或輸出 `Array of byte` 加上 `<ByteLength>`。

**E-L6 · XML 跳脫沒有過濾 XML 1.0 不允許的控制字元**
- 位置：`CeXmlExporter.cs:648-664`、`CsxExporter.cs:392-408`
- 成因：混淆過的 IL2CPP 名稱，或 live 模式描述中附帶的 `f.String` 值（`Live.cs:101-102`、`:1042-1043`，也就是遊戲中的字串內容），可能含有 `\u0001`、孤立的 surrogate 等字元。這類字元會讓 CE 的 XML parser 拒絕整份表格。
- 修法：`< 0x20`（tab / LF / CR 除外）與孤立的 surrogate 一律替換成 `?` 或 `\uXXXX` 文字。

**E-L7 · Lua bridge 內嵌的 JSON 編解碼不完整**
- 位置：`scripts/unity_symbols.lua:135`（`quote()` 只跳脫 `"`，不處理 `\` 與控制字元）、`:84`（`\n` / `\uXXXX` 被解成字面字元）、`:119-121`（無法辨識的字元會讓 `#m` 對 nil 取長度）、`:160-161`（`resp == nil` 時直接 `parseValue(nil)`）；`scripts/unity_invoke.lua:117-136`（`😀` 這類 surrogate pair 會解成 CESU-8）、`:235-238`（`inf` / `nan` 輸出 `inf` / `-nan`，不是合法的 JSON）
- 成因：兩支腳本各自維護一份 parser，修補的程度也不同（`unity_invoke.lua` 已經處理了大部分轉義，`unity_symbols.lua` 沒有）。
- 修法：抽出一份共用的 JSON 小模組（例如放進 `utils.lua` 或成為獨立的 embedded 檔案），兩支腳本共用；至少讓 `unity_symbols.lua` 的 `rpc` 在 `resp == nil` 時給出明確的錯誤。

**E-L8 · 錯誤只在 DEBUG 時才輸出，違反 lua-logging「錯誤一律輸出」的規則**
- 位置：`scripts/unity_dump.lua:92-102`（`discreteShutdown` 失敗只呼叫 `U.warn`，受 DEBUG 控制）、`BootstrapAaScriptGenerator.cs:245-251`（`pcall(discreteShutdown)` 的回傳值被丟掉）；`scripts/unity_symbols.lua:412-419`（`sigFilter` 命中多個 overload 時只有 `warn`，然後註冊第一個）
- 成因：`Discrete_Shutdown` 逾時或失敗時，[DISABLE] 會清掉 `discrete_bridgeLoaded` 並靜默關閉 Lua Engine，使用者以為 pipe 已經停止；選錯 overload 也不會有任何提示。
- 修法：這兩處都改用 `U.err` / `err`；bootstrap 的 [DISABLE] 檢查 `discreteShutdown()` 的回傳值，失敗時設定 `DISCRETE_HAD_ERROR`。

**E-L9 · `ce_table_bootstrap.lua` 的 lua-logging 細節**
- 位置：`scripts/ce_table_bootstrap.lua:112-120`、`:218`、`:130`
- 成因：`bootstrap()` / `teardown()` 開頭沒有重設 `DISCRETE_HAD_ERROR`，同一個 CE session 中只要之前有一次失敗，之後就永遠不會關閉 Lua Engine；`os.execute('mkdir ...')` 每次都會閃出一個 console 視窗。
- 修法：入口呼叫 `rawset(_G, "DISCRETE_HAD_ERROR", false)`；建立目錄改用 CE 的 `createDir`（如果 `celua.txt` 有）或 `pcall(io.open)` 探測。

**E-L10 · Backend-agnostic 規則的破口（audit-2026-11-23-plan.md 訊號 1）**
- 位置：`ui/UnityDumpUI/ViewModels/MainWindowViewModel.cs:1480`（symbol export 以 `Backend2 == "IL2CPP"` 控制）；`ui/UnityDumpUI/Views/MainWindow.axaml.cs:1378-1380`（以 backend 字首判斷 `supportsSignatureMatch`，實際效果等同「有連線」）；`ui/UnityDumpUI/ViewModels/SnapshotViewModel.cs:1845-1860`（`GuessBackend` 優先採用 handshake 的值，但 fallback 仍先比對 `mono` 子字串，正是註解自承的誤判來源）
- 成因：UI 直接依 backend 字串做功能分支，與「所有 Mono / IL2CPP 差異都藏在 pipe 協定之後」的規則不符。三處都只影響功能可用性或標籤，不影響資料正確性，所以列為 Low。
- 修法：handshake 加上 capability 旗標（例如 `stableModuleRelativeMethods`），UI 改以旗標控制；`supportsSignatureMatch` 改成 `IsConnected`；`GuessBackend` 的 fallback 先比對 `gameassembly`，或只採用 handshake / DB 中記錄的值。

**E-L11 · 早期版本遺留的 dead code 與過時註解**
- `CsxExporter.cs:87-100`、`:190-202`：同步的 `Render` → `EmitElements` 路徑完全沒有用到 `childRenderer` / `options` / `lookup` / `visited` 這四個參數（dead parameter）。
- `CeXmlExporter.cs:237-247`：兩段連續的 `<summary>`，第一段描述的方法（回傳 primitive hint 的版本）已經不存在。
- `MonoRegisterSymbolAaScriptGenerator.cs:101-121`：手動內嵌了一份與 `AaLuaLoader.AppendSelfLoad` 相同的 loader，兩份未來容易漂移。
- `UnityTypeMap.cs:4-5`、`SdkHeaderExporter.cs:2`：引用的 `docs/Architecture.md` 不存在（實際檔名是 `docs/Archiutecture.md`）。
- `UnityTypeMap.cs:128-129`：對 `Dictionary`2<K,V>` 等多參數泛型「recurse on inner」，會把 `"K,V"` 整段當成型別名稱拆解，產生無意義的 `getClassInfo` 查詢（只會失敗然後被吞掉）。
- `CeXmlExporter.Live.cs:826`：`pair.Index ?? 0`。舊版 DLL 沒有回傳 `index` 時，所有 pair 都會疊在 slot 0，應該改成退回 pointer leaf。
- `docs/ui-spec.md:277`：Trajectory 星號欄位的行號寫的是 `MainWindow.axaml:3801`，實際在 `:3804`。
- 修法：刪除 dead 參數與重複的 loader，改成共用 `AaLuaLoader`；修正文件連結與行號；`ExtractElementClass` 遇到多參數泛型時回傳 null。

### audit-2026-11-23-plan.md §6「觀察到的訊號」結案

| # | 訊號 | 結論 |
|---|---|---|
| 1 | Backend-agnostic 規則可能有破口 | **確認、降級為 Low**（E-L10）。三處都只控制功能可用性或標籤；`SnapshotViewModel.GuessBackend` 已改為優先採用 handshake 的值，module 子字串比對只剩 fallback。真正會影響 CE 輸出的 backend 差異（pointer size、List / array header）反而**沒有**透過 handshake 一致地傳下去（E-H1、E-L4）。 |
| 2 | `MainWindow.axaml.cs` 2.1k 行 code-behind | **本次未結案**。只讀了 `:1360-1400` 的 `MethodOverloadsViewModel` 建構；code-behind 不在這次「ViewModel / exporter / Lua bridge」的範圍內。 |
| 3 | 超大 ViewModel 是否每條 RPC 都 async | **大致否決**。`ViewModels/` 全域掃描沒有 `async void`、`.Result`、`.Wait()`、`GetAwaiter().GetResult()`；CPU 密集的 engine 都包在 `Task.Run` 中；auto-refresh 迴圈以 generation 計數器防止重入，並透過 `Dispatcher.UIThread.InvokeAsync` 回到 UI thread 修改集合（`LiveInspectionViewModel.cs:1958-2026`），沒有發現 UI thread 阻塞。實際問題是取消語意與結果歸屬（E-M1、E-M6）。 |
| 4 | `ce_table_bootstrap.lua` 沒有 embed 進 UI | **確認為刻意設計**：這是給 CT 發布者複製的 Mode-B 範本（檔頭 `:1-58`），`BridgeLuaResources.LoadOrder` 不需要它。不過這份範本本身帶有 E-M3 的 `injectDLL` 回傳值 bug。 |

### 稽核檢查清單補充結論
- **Lua 對 CE API 的呼叫**：抽查 `executeCodeEx(callmethod, timeout, address, ...)`、`targetIs64Bit`、`findTableFile`、`getLuaEngine`、`synchronize`、`registerSymbol(name, addr, donotsave)`、`unregisterSymbol`、`createTimer(owner)`、`getAddressSafe`、`processMessagesPaintOnly`、`mono_enumImages(onImage)`（callback 形式）、`mono_image_findClass`，簽章都與 `celua.txt` / `monoscript.lua` 一致。`mono_registersymbol_helpers.lua` 依賴的全域 `monopipe` 是 `monoscript.lua:546` 的「backward compatibility」別名，只有在主執行緒連線時才會設定；AA `{$lua}` 在主執行緒執行，所以目前可以運作，但這是相容層，建議改成檢查 `libmono and libmono.monopipe`。`getLuaEngine()` 在 engine 不存在時會先建立再關閉，無害。
- **`*AaScriptGenerator` 的 `{$lua}` 是否遵守 lua-logging**：5 個 generator 中，`Bootstrap`、`MonoRegisterSymbol`、`AddressLookup` 完全符合；`Aob` 的 [DISABLE] 漏了重設（E-L1）；`CallMethod` 依文件刻意不關閉 Lua Engine，符合規則。
- **CE XML 的 pointer chain offset 在 x86 / x64 是否一致**：live 模式的 String（`2*ptr` / `2*ptr+4`）、array data（優先使用 DLL 回傳的 `data_address`）、`_items` offset 都依 pointer size 計算，唯一的漏洞是 `_items` 的讀取長度（E-H1）；template 模式的 array / value type 模型本身就是錯的（E-H2），與 bitness 無關。
- **DataGrid 星號寬度規則**：`MainWindow.axaml:3804`、`MethodCallWindow.axaml:184` 是僅有的兩處星號欄位，都已列在 `docs/ui-spec.md` 的例外清單中（行號有漂移，見 E-L11）。
- **AOT**：這次讀過的 Core / VM 程式碼沒有反射式 JSON 或 `Type.GetType`；`BridgeLuaResources` 使用 `GetManifestResourceStream`，AOT 安全。

### 需要 live 驗證、這次沒能驗證的項目
- E-H1：在 32-bit Mono 遊戲（例如活俠傳）對含有非空 `List<T>` 欄位的 instance 執行 Inspector「Copy CE XML」，確認 `_items` 群組顯示 `(walk failed)`；修正後再驗證元素位址。
- E-H2：在 Browser 以預設的 `INSTANCE_ADDRESS` 匯出一個含 enum 與 `T[]` 欄位的 class，在 CE 中把 root 換成真實 instance 位址，確認 enum 欄位被解參照、陣列元素位址錯位。
- E-M4：在 `callMethod main_thread=true` 時把遊戲最小化或暫停，從 CE 的 CallMethod AA 觸發，確認 CE 是否凍結。
- E-M5：在 Mono 遊戲中以 CE 的 `mono_method_getSignature` 印出一個 `Foo(int, string)` 的 signature 字串，確認 primitive 的輸出是 `int` 還是 `System.Int32`；再對 `(Int32, String)` / `(String, Int32)` 這組 overload 產生 register AA，確認註冊到哪一個。
- E-L5：把 template 模式產生的含 Vector3 欄位的 `.CT` 載入 CE，確認 `12 Bytes` 被解讀成什麼型別，或是否被拒絕。

