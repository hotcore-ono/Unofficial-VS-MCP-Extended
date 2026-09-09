using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using VsMcp.Extension.McpServer;
using VsMcp.Extension.Services;
using VsMcp.Shared;
using VsMcp.Shared.Protocol;

namespace VsMcp.Extension.Tools
{
    /// <summary>
    /// Extended 独自の Windows 標準ダイアログ（MessageBox / TaskDialog）自動化ツール群。
    /// 対象の発見・検証・キャプチャ・待機は Phase 4 の基盤（DebuggeeWindowResolver / UiWindowTools の internal helper）を再利用し、
    /// 標準ダイアログ固有の分類・情報取得・操作は StandardDialogResolver と Adapter に置く。
    /// 操作は標準コントロール ID を正本にし、画面座標や表示文字列には依存しない。MCP ツールハンドラ同士は直接呼ばない。
    /// </summary>
    public static class StandardDialogTools
    {
        /// <summary>UIA / Win32 の構造取得と操作に許す時間（秒）。</summary>
        private const int InspectTimeoutSeconds = 15;

        /// <summary>execute 後にダイアログが閉じるのを確認する上限（ミリ秒）。</summary>
        private const int ExecuteCloseWaitMs = 3000;

        /// <summary>execute 後の閉鎖確認のポーリング間隔（ミリ秒）。</summary>
        private const int ExecuteClosePollMs = 100;

        /// <summary>standard_dialog_wait の dialogType 既定値。</summary>
        private const string DialogTypeAny = "any";

        /// <summary>ツールをレジストリへ登録する。VsMcpPackage.RegisterTools から呼ばれる。</summary>
        /// <param name="InRegistry">登録先レジストリ。</param>
        /// <param name="InAccessor">DTE / UI スレッドアクセサ。</param>
        public static void Register(McpToolRegistry InRegistry, VsServiceAccessor InAccessor)
        {
            InRegistry.Register(
                new McpToolDefinition(
                    "standard_dialog_detect",
                    "[Windows UIA — desktop app being debugged] List the standard Windows dialogs (class #32770) currently shown by the debugged processes and classify each " +
                    "one from its window structure: 'messageBox' (user32 MessageBox: Button children with standard control IDs and Static text), 'taskDialog' " +
                    "(comctl32 TaskDialog: DirectUIHWND child) or 'unknownStandardDialog' (any other #32770, returned only with includeUnknown=true). " +
                    "Returns text { count, includeUnknown, dialogs[] } where each entry has dialogType plus the same fields as ui_list_windows (handle, handleHex, title, " +
                    "className, processId, ownerHandle, isVisible, isEnabled, isForeground, isModalCandidate, modalCandidateReason, bounds, dpi, monitor). " +
                    "Use the handle with standard_dialog_get_info / capture / execute / wait_closed.",
                    SchemaBuilder.Create()
                        .AddBoolean("includeUnknown", "Also return #32770 windows that are neither a MessageBox nor a TaskDialog (dialogType 'unknownStandardDialog'). Default: false")
                        .AddString("title", "Only return dialogs whose title matches this value (see titleMatch)")
                        .AddEnum("titleMatch", "Match mode for 'title': 'exact' (default, case-sensitive), 'contains' (case-insensitive substring), 'regex' (case-insensitive)",
                            new[] { "exact", "contains", "regex" })
                        .Build()),
                InArgs => StandardDialogDetectAsync(InAccessor, InArgs));

            InRegistry.Register(
                new McpToolDefinition(
                    "standard_dialog_get_info",
                    "[Windows UIA — desktop app being debugged] Return the structured content of one standard dialog of the debugged application by its HWND " +
                    "(from standard_dialog_detect or ui_list_windows). The handle is re-validated (IsWindow, top-level normalization, debugged-process check, class #32770) " +
                    "and classified again at call time. MessageBox: message (Static 0xFFFF), buttons with standard control IDs, defaultButton (DM_GETDEFID / BS_DEFPUSHBUTTON). " +
                    "TaskDialog: mainInstruction, content, footer, verificationText/verificationChecked, radioButtons, buttons with standard or custom IDs " +
                    "(UIA AutomationId CommandButton_<id>), defaultButton (BS_DEFPUSHBUTTON). Each button has id, action (logical name derived from the standard ID, " +
                    "null for custom IDs), text (observed caption, informational only), isDefault, isEnabled. Values are observed from the window; the original API flags " +
                    "(MB_* etc.) are never guessed. Unknown #32770 dialogs return dialogType 'unknownStandardDialog' with whatever Win32 buttons/text could be observed.",
                    SchemaBuilder.Create()
                        .AddInteger("handle", "HWND of the dialog (decimal, as returned by standard_dialog_detect 'handle')", required: true)
                        .Build()),
                InArgs => StandardDialogGetInfoAsync(InAccessor, InArgs));

            InRegistry.Register(
                new McpToolDefinition(
                    "standard_dialog_capture",
                    "[Windows UIA — desktop app being debugged] Capture a screenshot of one standard dialog of the debugged application by its HWND, together with its " +
                    "structured content. Same validation and classification as standard_dialog_get_info, then the same Windows.Graphics.Capture / PrintWindow pipeline as " +
                    "ui_capture_window_by_handle (works while Visual Studio has the foreground). Returns a text content { dialog: <StandardDialogInfo>, normalizedHandle, window, " +
                    "originalWidth, originalHeight, mimeType } followed by the image content.",
                    SchemaBuilder.Create()
                        .AddInteger("handle", "HWND of the dialog (decimal)", required: true)
                        .Build()),
                InArgs => StandardDialogCaptureAsync(InAccessor, InArgs));

            InRegistry.Register(
                new McpToolDefinition(
                    "standard_dialog_execute",
                    "[Windows UIA — desktop app being debugged] Press a button on a MessageBox or TaskDialog of the debugged application. Identify the button by the " +
                    "logical 'action' (ok, cancel, yes, no, retry, abort, ignore, tryAgain, continue, close, help — resolved to the standard control ID IDOK=1 … IDCONTINUE=11) " +
                    "or by 'buttonId' (exact control ID, needed for TaskDialog custom buttons; buttonId wins when both are given). Button captions in any language are never used " +
                    "to select the button. Before pressing, the handle is re-validated and the dialog re-classified; unknown #32770 dialogs, missing or disabled buttons are refused. " +
                    "MessageBox buttons are pressed via UIA InvokePattern, falling back to BM_CLICK and WM_COMMAND; TaskDialog buttons via TDM_CLICK_BUTTON, falling back to UIA " +
                    "InvokePattern and BM_CLICK — no screen coordinates and no foreground activation. Returns text { executed, dialogHandle, dialogType, buttonId, action, " +
                    "executionMethod, dialogClosed, closeWaitMs } (dialogClosed is checked for up to 3 s).",
                    SchemaBuilder.Create()
                        .AddInteger("handle", "HWND of the dialog (decimal)", required: true)
                        .AddEnum("action", "Logical button to press (mapped to the standard control ID). Ignored when buttonId is given",
                            StandardDialogResolver.StandardActions)
                        .AddInteger("buttonId", "Exact button control ID (standard ID or TaskDialog custom button ID). Takes precedence over action")
                        .Build()),
                InArgs => StandardDialogExecuteAsync(InAccessor, InArgs));

            InRegistry.Register(
                new McpToolDefinition(
                    "standard_dialog_wait",
                    "[Windows UIA — desktop app being debugged] Wait until a standard dialog (MessageBox / TaskDialog) of the debugged application appears. Polls the visible " +
                    "#32770 windows, classifies them, filters by dialogType ('any' = messageBox or taskDialog) and optional title / titleMatch, and resolves several matches " +
                    "with the same rules as ui_wait_for_window (single, else the foreground one, else the only visible-and-enabled one; still ambiguous → error listing the " +
                    "candidates). Returns text { found, elapsedMs, resolutionReason, dialog } with the same summary fields as standard_dialog_detect; found=false on timeout. " +
                    "timeoutMs default 10000, max 55000; pollIntervalMs default 200, min 50. Follow with standard_dialog_get_info / execute using dialog.handle.",
                    SchemaBuilder.Create()
                        .AddEnum("dialogType", "Kind of dialog to wait for. Default: 'any' (messageBox or taskDialog)", new[] { "any", "messageBox", "taskDialog" })
                        .AddString("title", "Optional title filter (see titleMatch)")
                        .AddEnum("titleMatch", "Match mode for 'title': 'exact' (default, case-sensitive), 'contains' (case-insensitive substring), 'regex' (case-insensitive)",
                            new[] { "exact", "contains", "regex" })
                        .AddInteger("timeoutMs", "Maximum time to wait in milliseconds (default: 10000, max: 55000)")
                        .AddInteger("pollIntervalMs", "Polling interval in milliseconds (default: 200, min: 50)")
                        .Build()),
                InArgs => StandardDialogWaitAsync(InAccessor, InArgs));

            InRegistry.Register(
                new McpToolDefinition(
                    "standard_dialog_wait_closed",
                    "[Windows UIA — desktop app being debugged] Wait until the standard dialog with the given HWND is closed. Same semantics as ui_wait_for_window_closed " +
                    "with a handle: closed when the HWND no longer exists ('windowDestroyed') or has been reused by another process ('handleReused'); a handle that is " +
                    "already invalid returns closed=true immediately ('alreadyClosed'). Returns text { closed, elapsedMs, handle, initialTitle, reason }; closed=false on timeout. " +
                    "timeoutMs default 10000, max 55000; pollIntervalMs default 200, min 50.",
                    SchemaBuilder.Create()
                        .AddInteger("handle", "HWND of the dialog to wait for (decimal)", required: true)
                        .AddInteger("timeoutMs", "Maximum time to wait in milliseconds (default: 10000, max: 55000)")
                        .AddInteger("pollIntervalMs", "Polling interval in milliseconds (default: 200, min: 50)")
                        .Build()),
                InArgs => StandardDialogWaitClosedAsync(InAccessor, InArgs));
        }

        // ------------------------------------------------------------------
        // ツールハンドラ
        // ------------------------------------------------------------------

        /// <summary>standard_dialog_detect の本体。可視の #32770 を分類して一覧を返す。</summary>
        /// <param name="InAccessor">DTE / UI スレッドアクセサ。</param>
        /// <param name="InArgs">ツール引数（includeUnknown / title / titleMatch）。</param>
        /// <returns>ダイアログ一覧、またはエラー。</returns>
        private static async Task<McpToolResult> StandardDialogDetectAsync(VsServiceAccessor InAccessor, JObject InArgs)
        {
            bool TheIncludeUnknown = InArgs.Value<bool?>("includeUnknown") ?? false;
            string TheQueryError = TryParseOptionalTitleQuery(InArgs, out WindowTitleQuery TheQuery);
            if (TheQueryError != null)
                return McpToolResult.Error(TheQueryError);

            HashSet<uint> TheProcessIds = await UiWindowTools.GetDebuggedProcessIdsAsync(InAccessor);
            if (TheProcessIds.Count == 0)
                return McpToolResult.Error(DebuggeeWindowResolver.NoDebuggedProcessMessage);

            List<ClassifiedDialog> TheDialogs = await FindDialogsAsync(TheProcessIds, TheQuery, DialogTypeAny, TheIncludeUnknown);
            return McpToolResult.Success(new
            {
                count = TheDialogs.Count,
                includeUnknown = TheIncludeUnknown,
                dialogs = TheDialogs.Select(TheDialog => BuildSummary(TheDialog.Window, TheDialog.DialogType)).ToList(),
            });
        }

        /// <summary>standard_dialog_get_info の本体。</summary>
        /// <param name="InAccessor">DTE / UI スレッドアクセサ。</param>
        /// <param name="InArgs">ツール引数（handle）。</param>
        /// <returns>StandardDialogInfo、またはエラー。</returns>
        private static async Task<McpToolResult> StandardDialogGetInfoAsync(VsServiceAccessor InAccessor, JObject InArgs)
        {
            ResolvedDialog TheResolved = await ResolveDialogByHandleAsync(InAccessor, InArgs);
            if (TheResolved.Error != null)
                return TheResolved.Error;
            return McpToolResult.Success(TheResolved.Info);
        }

        /// <summary>standard_dialog_capture の本体。構造化情報を取得したうえで共通キャプチャ経路へ渡す。</summary>
        /// <param name="InAccessor">DTE / UI スレッドアクセサ。</param>
        /// <param name="InArgs">ツール引数（handle）。</param>
        /// <returns>text（dialog + 画像メタ情報）+ image、またはエラー。</returns>
        private static async Task<McpToolResult> StandardDialogCaptureAsync(VsServiceAccessor InAccessor, JObject InArgs)
        {
            ResolvedDialog TheResolved = await ResolveDialogByHandleAsync(InAccessor, InArgs);
            if (TheResolved.Error != null)
                return TheResolved.Error;

            JObject ThePrefix = new JObject
            {
                ["dialog"] = JObject.FromObject(TheResolved.Info),
            };
            // 共通キャプチャ経路が IsWindow / PID 再照合 / IsIconic をやり直す（取得からキャプチャまでに閉じられた場合の競合対策）
            return await UiWindowTools.CaptureTopLevelWindowAsync(InAccessor, TheResolved.Info.Handle, ThePrefix);
        }

        /// <summary>
        /// standard_dialog_execute の本体。handle を再検証・再分類し、action（標準 ID へ解決）または buttonId で対象ボタンを決め、
        /// 存在・有効性を確認してから Adapter に押させる。unknown ダイアログは拒否する。
        /// </summary>
        /// <param name="InAccessor">DTE / UI スレッドアクセサ。</param>
        /// <param name="InArgs">ツール引数（handle / action / buttonId）。</param>
        /// <returns>実行結果、またはエラー。</returns>
        private static async Task<McpToolResult> StandardDialogExecuteAsync(VsServiceAccessor InAccessor, JObject InArgs)
        {
            string TheAction = InArgs.Value<string>("action");
            int? TheButtonId = InArgs.Value<int?>("buttonId");
            if (string.IsNullOrEmpty(TheAction) && !TheButtonId.HasValue)
                return McpToolResult.Error("Either 'action' or 'buttonId' must be provided.");

            ResolvedDialog TheResolved = await ResolveDialogByHandleAsync(InAccessor, InArgs);
            if (TheResolved.Error != null)
                return TheResolved.Error;

            StandardDialogInfo TheInfo = TheResolved.Info;
            string TheAvailable = DescribeButtons(TheInfo.Buttons);
            if (TheResolved.Adapter == null)
            {
                return McpToolResult.Error(
                    $"Dialog {TheInfo.Handle} ('{TheInfo.Title}') is an unknown standard dialog; standard_dialog_execute only presses buttons on dialogs it can classify " +
                    $"as messageBox or taskDialog. Observed buttons: {TheAvailable}");
            }

            // 対象ボタンの決定: buttonId が正本、無ければ action → 標準 ID → 無ければ観測 action 名（MB_OK の ID 2 = ok など）
            StandardDialogButtonInfo TheButton;
            if (TheButtonId.HasValue)
            {
                TheButton = TheInfo.Buttons.FirstOrDefault(TheCandidate => TheCandidate.Id == TheButtonId.Value);
                if (TheButton == null)
                    return McpToolResult.Error($"Button id {TheButtonId.Value} is not present on this {TheInfo.DialogType}. Available buttons: {TheAvailable}");
            }
            else
            {
                int? TheStandardId = StandardDialogResolver.ResolveActionId(TheAction);
                if (!TheStandardId.HasValue)
                    return McpToolResult.Error($"Unknown action '{TheAction}'. Expected one of: {string.Join(", ", StandardDialogResolver.StandardActions)}");

                TheButton = TheInfo.Buttons.FirstOrDefault(TheCandidate => TheCandidate.Id == TheStandardId.Value)
                    ?? TheInfo.Buttons.FirstOrDefault(TheCandidate => string.Equals(TheCandidate.Action, TheAction, StringComparison.OrdinalIgnoreCase));
                if (TheButton == null)
                    return McpToolResult.Error($"Action '{TheAction}' is not available on this {TheInfo.DialogType}. Available buttons: {TheAvailable}");
            }

            if (!TheButton.IsEnabled)
                return McpToolResult.Error($"Button id {TheButton.Id} ('{TheButton.Text}') on dialog {TheInfo.Handle} is disabled.");

            string TheMethod;
            try
            {
                TheMethod = await RunWithTimeoutAsync(() => TheResolved.Adapter.Execute(TheInfo, TheButton, TheResolved.Structure));
            }
            catch (TimeoutException TheException)
            {
                return McpToolResult.Error(TheException.Message);
            }
            if (TheMethod == null)
                return McpToolResult.Error($"Failed to press button id {TheButton.Id} on dialog {TheInfo.Handle}: every execution method was rejected by the target.");

            // 押した結果としてダイアログが閉じたかを短時間だけ確認する（閉じない設計のボタンもあるので Error にはしない）
            Stopwatch TheStopwatch = Stopwatch.StartNew();
            bool TheIsClosed = false;
            IntPtr TheDialogHandle = new IntPtr(TheInfo.Handle);
            while (TheStopwatch.ElapsedMilliseconds < ExecuteCloseWaitMs)
            {
                if (!NativeMethods.IsWindow(TheDialogHandle))
                {
                    TheIsClosed = true;
                    break;
                }
                NativeMethods.GetWindowThreadProcessId(TheDialogHandle, out uint TheCurrentProcessId);
                if (TheCurrentProcessId != TheInfo.ProcessId)
                {
                    TheIsClosed = true;
                    break;
                }
                await Task.Delay(ExecuteClosePollMs);
            }

            return McpToolResult.Success(new
            {
                executed = true,
                dialogHandle = TheInfo.Handle,
                dialogType = TheInfo.DialogType,
                buttonId = TheButton.Id,
                action = TheButton.Action,
                executionMethod = TheMethod,
                dialogClosed = TheIsClosed,
                closeWaitMs = TheStopwatch.ElapsedMilliseconds,
            });
        }

        /// <summary>standard_dialog_wait の本体。Phase 4 と同じポーリング・候補解決・タイムアウト形式で標準ダイアログの出現を待つ。</summary>
        /// <param name="InAccessor">DTE / UI スレッドアクセサ。</param>
        /// <param name="InArgs">ツール引数（dialogType / title / titleMatch / timeoutMs / pollIntervalMs）。</param>
        /// <returns>text（found / elapsedMs / resolutionReason / dialog）、またはエラー。</returns>
        private static async Task<McpToolResult> StandardDialogWaitAsync(VsServiceAccessor InAccessor, JObject InArgs)
        {
            string TheDialogType = InArgs.Value<string>("dialogType") ?? DialogTypeAny;
            if (!IsKnownDialogTypeFilter(TheDialogType))
                return McpToolResult.Error($"Unknown dialogType: '{TheDialogType}'. Expected one of: any, messageBox, taskDialog");

            string TheQueryError = TryParseOptionalTitleQuery(InArgs, out WindowTitleQuery TheQuery);
            if (TheQueryError != null)
                return McpToolResult.Error(TheQueryError);
            UiWindowTools.ParseWaitOptions(InArgs, out int TheTimeoutMs, out int ThePollIntervalMs);

            Stopwatch TheStopwatch = Stopwatch.StartNew();
            HashSet<uint> TheProcessIds = await UiWindowTools.GetDebuggedProcessIdsAsync(InAccessor);
            if (TheProcessIds.Count == 0)
                return McpToolResult.Error(DebuggeeWindowResolver.NoDebuggedProcessMessage);

            string TheDescription = $"{TheDialogType} standard dialog" + (TheQuery != null ? " with " + TheQuery.Describe() : string.Empty);
            while (true)
            {
                List<ClassifiedDialog> TheDialogs = await FindDialogsAsync(TheProcessIds, TheQuery, TheDialogType, false);
                if (TheDialogs.Count > 0)
                {
                    CandidateResolution TheResolution = DebuggeeWindowResolver.ResolveCandidates(TheDialogs.Select(TheDialog => TheDialog.Window).ToList());
                    if (!TheResolution.IsResolved)
                    {
                        return McpToolResult.Error(DebuggeeWindowResolver.DescribeAmbiguity(
                            TheResolution, TheDescription, "Pick one and call standard_dialog_get_info / standard_dialog_execute with its 'handle'."));
                    }

                    ClassifiedDialog TheMatched = TheDialogs.First(TheDialog => TheDialog.Window.Handle == TheResolution.Resolved.Handle);
                    return McpToolResult.Success(new
                    {
                        found = true,
                        elapsedMs = TheStopwatch.ElapsedMilliseconds,
                        resolutionReason = TheResolution.Reason,
                        dialog = BuildSummary(TheMatched.Window, TheMatched.DialogType),
                    });
                }

                if (TheStopwatch.ElapsedMilliseconds >= TheTimeoutMs)
                    break;
                await Task.Delay(ThePollIntervalMs);

                TheProcessIds = await UiWindowTools.GetDebuggedProcessIdsAsync(InAccessor);
                if (TheProcessIds.Count == 0)
                    return McpToolResult.Error("Debugging stopped while waiting for the dialog.");
            }

            return McpToolResult.Success(new
            {
                found = false,
                elapsedMs = TheStopwatch.ElapsedMilliseconds,
                resolutionReason = (string)null,
                dialog = (object)null,
            });
        }

        /// <summary>standard_dialog_wait_closed の本体。Phase 4 の handle 版 close-wait をそのまま使う。</summary>
        /// <param name="InAccessor">DTE / UI スレッドアクセサ。</param>
        /// <param name="InArgs">ツール引数（handle / timeoutMs / pollIntervalMs）。</param>
        /// <returns>text（closed / elapsedMs / handle / initialTitle / reason）、またはエラー。</returns>
        private static async Task<McpToolResult> StandardDialogWaitClosedAsync(VsServiceAccessor InAccessor, JObject InArgs)
        {
            long? TheHandle = InArgs.Value<long?>("handle");
            if (!TheHandle.HasValue)
                return McpToolResult.Error("Parameter 'handle' is required (use the 'handle' value returned by standard_dialog_detect)");
            if (!UiWindowTools.IsHandleInRange(TheHandle.Value))
                return McpToolResult.Error($"Window handle {TheHandle.Value} is out of range for a window handle.");

            UiWindowTools.ParseWaitOptions(InArgs, out int TheTimeoutMs, out int ThePollIntervalMs);
            return await UiWindowTools.WaitForHandleClosedAsync(InAccessor, TheHandle.Value, TheTimeoutMs, ThePollIntervalMs);
        }

        // ------------------------------------------------------------------
        // 共通 helper
        // ------------------------------------------------------------------

        /// <summary>分類済みダイアログ（ウィンドウ情報 + dialogType）。</summary>
        private sealed class ClassifiedDialog
        {
            public WindowInfo Window { get; set; }
            public string DialogType { get; set; }
        }

        /// <summary>handle から解決したダイアログ一式。Error が非 null なら他は未設定。</summary>
        private sealed class ResolvedDialog
        {
            public StandardDialogInfo Info { get; set; }
            public IStandardDialogAdapter Adapter { get; set; }
            public StandardDialogStructure Structure { get; set; }
            public McpToolResult Error { get; set; }
        }

        /// <summary>
        /// handle 引数を Phase 4 の検証（範囲 → IsWindow → GA_ROOT → PID 再照合）に通し、#32770 であることを確認してから
        /// 構造取得と分類を行う。get_info / capture / execute の入口で毎回やり直す（古い HWND での誤操作を防ぐ）。
        /// </summary>
        /// <param name="InAccessor">DTE / UI スレッドアクセサ。</param>
        /// <param name="InArgs">ツール引数（handle）。</param>
        /// <returns>解決結果。</returns>
        private static async Task<ResolvedDialog> ResolveDialogByHandleAsync(VsServiceAccessor InAccessor, JObject InArgs)
        {
            long? TheHandle = InArgs.Value<long?>("handle");
            if (!TheHandle.HasValue)
                return new ResolvedDialog { Error = McpToolResult.Error("Parameter 'handle' is required (use the 'handle' value returned by standard_dialog_detect)") };
            if (!UiWindowTools.IsHandleInRange(TheHandle.Value))
                return new ResolvedDialog { Error = McpToolResult.Error($"Window handle {TheHandle.Value} is out of range for a window handle.") };

            HashSet<uint> TheProcessIds = await UiWindowTools.GetDebuggedProcessIdsAsync(InAccessor);
            string TheHandleError = DebuggeeWindowResolver.ValidateAndNormalizeWindowHandle(TheHandle.Value, TheProcessIds, out IntPtr TheNormalized);
            if (TheHandleError != null)
                return new ResolvedDialog { Error = McpToolResult.Error(TheHandleError) };

            long TheNormalizedHandle = TheNormalized.ToInt64();
            WindowInfo TheWindow = await UiWindowTools.FindWindowInfoAsync(TheProcessIds, TheNormalizedHandle);
            if (TheWindow == null)
                return new ResolvedDialog { Error = McpToolResult.Error($"Window handle {TheNormalizedHandle} disappeared before it could be inspected.") };
            if (!StandardDialogResolver.IsDialogClass(TheWindow))
            {
                return new ResolvedDialog
                {
                    Error = McpToolResult.Error(
                        $"Window handle {TheNormalizedHandle} ('{TheWindow.Title}') is not a standard dialog: class '{TheWindow.ClassName}', expected '{StandardDialogResolver.DialogClassName}'."),
                };
            }

            try
            {
                return await RunWithTimeoutAsync(() =>
                {
                    StandardDialogStructure TheStructure = StandardDialogResolver.Inspect(TheNormalized);
                    StandardDialogInfo TheInfo = StandardDialogResolver.BuildInfo(TheWindow, TheStructure, out IStandardDialogAdapter TheAdapter);
                    return new ResolvedDialog { Info = TheInfo, Adapter = TheAdapter, Structure = TheStructure };
                });
            }
            catch (Exception TheException)
            {
                return new ResolvedDialog { Error = McpToolResult.Error($"Failed to inspect dialog {TheNormalizedHandle}: {TheException.Message}") };
            }
        }

        /// <summary>可視の #32770 を列挙・分類し、dialogType フィルタと任意のタイトル条件で絞る。</summary>
        /// <param name="InProcessIds">デバッグ中プロセス ID の集合。</param>
        /// <param name="InQuery">タイトル条件（null なら無条件）。</param>
        /// <param name="InDialogTypeFilter">any / messageBox / taskDialog。</param>
        /// <param name="InIncludeUnknown">unknownStandardDialog も含めるか（any のときだけ意味を持つ）。</param>
        /// <returns>分類済みダイアログ一覧（Z 順）。</returns>
        private static Task<List<ClassifiedDialog>> FindDialogsAsync(HashSet<uint> InProcessIds, WindowTitleQuery InQuery, string InDialogTypeFilter, bool InIncludeUnknown)
        {
            return Task.Run(() =>
            {
                List<ClassifiedDialog> TheDialogs = new List<ClassifiedDialog>();
                foreach (WindowInfo TheWindow in DebuggeeWindowEnumerator.EnumerateTopLevelWindows(InProcessIds, false))
                {
                    if (!StandardDialogResolver.IsDialogClass(TheWindow))
                        continue;
                    if (InQuery != null && !DebuggeeWindowResolver.IsMatched(TheWindow, InQuery))
                        continue;

                    string TheType;
                    try
                    {
                        TheType = StandardDialogResolver.SelectAdapter(StandardDialogResolver.Inspect(new IntPtr(TheWindow.Handle)))?.DialogType
                            ?? StandardDialogResolver.TypeUnknown;
                    }
                    catch
                    {
                        continue; // 列挙中に閉じられたダイアログ
                    }

                    if (!IsTypeAccepted(TheType, InDialogTypeFilter, InIncludeUnknown))
                        continue;
                    TheDialogs.Add(new ClassifiedDialog { Window = TheWindow, DialogType = TheType });
                }
                return TheDialogs;
            });
        }

        /// <summary>dialogType フィルタの判定。any は messageBox / taskDialog（includeUnknown なら unknown も）。</summary>
        /// <param name="InType">分類結果。</param>
        /// <param name="InFilter">any / messageBox / taskDialog。</param>
        /// <param name="InIncludeUnknown">unknown を含めるか。</param>
        /// <returns>対象なら true。</returns>
        private static bool IsTypeAccepted(string InType, string InFilter, bool InIncludeUnknown)
        {
            if (string.Equals(InFilter, DialogTypeAny, StringComparison.OrdinalIgnoreCase))
                return InType != StandardDialogResolver.TypeUnknown || InIncludeUnknown;
            return string.Equals(InType, InFilter, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>dialogType 引数が受け付ける値か。</summary>
        /// <param name="InFilter">引数値。</param>
        /// <returns>any / messageBox / taskDialog なら true。</returns>
        private static bool IsKnownDialogTypeFilter(string InFilter)
        {
            return string.Equals(InFilter, DialogTypeAny, StringComparison.OrdinalIgnoreCase)
                || string.Equals(InFilter, StandardDialogResolver.TypeMessageBox, StringComparison.OrdinalIgnoreCase)
                || string.Equals(InFilter, StandardDialogResolver.TypeTaskDialog, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>任意の title / titleMatch 引数を検索条件にする。title が無ければ null（エラーなし）。</summary>
        /// <param name="InArgs">ツール引数。</param>
        /// <param name="OutQuery">検索条件。</param>
        /// <returns>エラーメッセージ。正常なら null。</returns>
        private static string TryParseOptionalTitleQuery(JObject InArgs, out WindowTitleQuery OutQuery)
        {
            OutQuery = null;
            string TheTitle = InArgs.Value<string>("title");
            if (string.IsNullOrEmpty(TheTitle))
                return null;
            return DebuggeeWindowResolver.TryParseTitleQuery(TheTitle, InArgs.Value<string>("titleMatch"), null, out OutQuery);
        }

        /// <summary>detect / wait の戻り値用の要約（dialogType + ui_list_windows と同じウィンドウ項目）。</summary>
        /// <param name="InWindow">ウィンドウ情報。</param>
        /// <param name="InDialogType">分類結果。</param>
        /// <returns>匿名オブジェクト。</returns>
        private static object BuildSummary(WindowInfo InWindow, string InDialogType)
        {
            return new
            {
                dialogType = InDialogType,
                handle = InWindow.Handle,
                handleHex = InWindow.HandleHex,
                title = InWindow.Title,
                className = InWindow.ClassName,
                processId = InWindow.ProcessId,
                ownerHandle = InWindow.OwnerHandle,
                isVisible = InWindow.IsVisible,
                isEnabled = InWindow.IsEnabled,
                isForeground = InWindow.IsForeground,
                isModalCandidate = InWindow.IsModalCandidate,
                modalCandidateReason = InWindow.ModalCandidateReason,
                bounds = InWindow.Bounds,
                dpi = InWindow.Dpi,
                monitor = InWindow.Monitor,
            };
        }

        /// <summary>エラー文用にボタン一覧を "id=6 action=yes text='はい(Y)'" 形式で並べる。</summary>
        /// <param name="InButtons">ボタン一覧。</param>
        /// <returns>説明文。無ければ "(none)"。</returns>
        private static string DescribeButtons(IEnumerable<StandardDialogButtonInfo> InButtons)
        {
            List<string> TheParts = InButtons
                .Select(TheButton => $"id={TheButton.Id} action={TheButton.Action ?? "(custom)"} text='{TheButton.Text}'{(TheButton.IsEnabled ? string.Empty : " disabled")}")
                .ToList();
            return TheParts.Count == 0 ? "(none)" : string.Join(", ", TheParts);
        }

        /// <summary>UIA / Win32 の処理をバックグラウンドで実行し、応答が無い場合は TimeoutException にする（Router の 60 秒より先に返す）。</summary>
        /// <typeparam name="T">戻り値の型。</typeparam>
        /// <param name="InFunc">実行する処理。</param>
        /// <returns>処理の戻り値。</returns>
        private static async Task<T> RunWithTimeoutAsync<T>(Func<T> InFunc)
        {
            Task<T> TheTask = Task.Run(InFunc);
            Task TheFinished = await Task.WhenAny(TheTask, Task.Delay(TimeSpan.FromSeconds(InspectTimeoutSeconds)));
            if (TheFinished != TheTask)
                throw new TimeoutException($"Standard dialog inspection timed out after {InspectTimeoutSeconds} seconds. The target application may not be responding.");
            return await TheTask;
        }
    }
}
