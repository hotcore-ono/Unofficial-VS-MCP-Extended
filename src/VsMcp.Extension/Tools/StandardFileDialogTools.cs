using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Automation;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using VsMcp.Extension.McpServer;
using VsMcp.Extension.Services;
using VsMcp.Shared;
using VsMcp.Shared.Protocol;

namespace VsMcp.Extension.Tools
{
    /// <summary>
    /// Extended 独自の標準 File Dialog（IFileDialog: Open / Save / Folder）自動化ツール群。
    /// 対象の発見・検証・キャプチャ・待機は Phase 4/5 の基盤（UiWindowTools の internal helper、DebuggeeWindowResolver、
    /// StandardDialogResolver.Inspect）を再利用し、File Dialog 固有の分類・情報取得・操作は StandardFileDialogResolver と Adapter に置く。
    /// 操作の正本は UIA のパターンと Control ID（確定 = 1、キャンセル = 2）で、表示文字列・画面座標・SendKeys は使わない。
    /// </summary>
    public static class StandardFileDialogTools
    {
        /// <summary>UIA / Win32 の構造取得と操作に許す時間（秒）。</summary>
        private const int InspectTimeoutSeconds = 15;

        /// <summary>confirm / cancel 後にダイアログが閉じるのを確認する上限（ミリ秒）。</summary>
        private const int CloseWaitMs = 3000;

        /// <summary>閉鎖確認のポーリング間隔（ミリ秒）。</summary>
        private const int ClosePollMs = 100;

        /// <summary>ツールをレジストリへ登録する。VsMcpPackage.RegisterTools から呼ばれる。</summary>
        /// <param name="InRegistry">登録先レジストリ。</param>
        /// <param name="InAccessor">DTE / UI スレッドアクセサ。</param>
        public static void Register(McpToolRegistry InRegistry, VsServiceAccessor InAccessor)
        {
            string[] TheModes = { "any", "open", "save", "folder" };

            // Extended (Phase 10): 登録は DiagnosticToolRunner を通し、tool.start / tool.end と相関 ID を付ける（schema・戻り値・エラー文は不変）
            DiagnosticToolRunner.Register(InRegistry,
                new McpToolDefinition(
                    "standard_file_dialog_detect",
                    "[Windows UIA — desktop app being debugged] List the standard Windows file dialogs (IFileDialog: Open File, Save File, Folder picker) currently shown by " +
                    "the debugged processes. A #32770 window is recognized as a file dialog from its structure (DUIViewWndClassName view host, address bar / shell view, and the " +
                    "standard Confirm=1 / Cancel=2 buttons) and its mode from the file-name control (Open: Edit 1148, Save: Edit 1001 under the DirectUI host, Folder: Edit 1152); " +
                    "anything else is 'unknownFileDialog' and returned only with includeUnknown=true. Returns text { count, includeUnknown, mode, dialogs[] } with dialogType, mode and " +
                    "the same window fields as ui_list_windows. Use the handle with standard_file_dialog_get_info / set_filename / select / confirm / cancel / capture / wait_closed.",
                    SchemaBuilder.Create()
                        .AddBoolean("includeUnknown", "Also return IFileDialog windows whose mode could not be classified (dialogType 'unknownFileDialog'). Default: false")
                        .AddEnum("mode", "Only return dialogs of this mode. Default: 'any'", TheModes)
                        .AddString("title", "Only return dialogs whose title matches this value (see titleMatch)")
                        .AddEnum("titleMatch", "Match mode for 'title': 'exact' (default, case-sensitive), 'contains' (case-insensitive substring), 'regex' (case-insensitive)",
                            new[] { "exact", "contains", "regex" })
                        .Build()),
                InArgs => DetectAsync(InAccessor, InArgs));

            DiagnosticToolRunner.Register(InRegistry,
                new McpToolDefinition(
                    "standard_file_dialog_get_info",
                    "[Windows UIA — desktop app being debugged] Return the structured state of one standard file dialog of the debugged application by its HWND: dialogType, mode, " +
                    "window fields, currentFolderDisplay (address bar text) and currentFolderPath (the rooted path taken from it, null if none), fileName (file-name Edit value), " +
                    "selectedItems (items selected in the shell view: name, path, isFolder, automationId, isSelected), fileTypeFilter / fileTypeFilterIndex (Open/Save), and " +
                    "confirmButton / cancelButton (control IDs 1 / 2 with caption and enabled state). The handle is re-validated (IsWindow, top-level, debugged process, #32770, " +
                    "file dialog structure) at call time; values that cannot be observed are null rather than guessed.",
                    SchemaBuilder.Create()
                        .AddInteger("handle", "HWND of the file dialog (decimal, from standard_file_dialog_detect)", required: true)
                        .Build()),
                InArgs => GetInfoAsync(InAccessor, InArgs));

            DiagnosticToolRunner.Register(InRegistry,
                new McpToolDefinition(
                    "standard_file_dialog_capture",
                    "[Windows UIA — desktop app being debugged] Capture a screenshot of one standard file dialog of the debugged application by its HWND together with its " +
                    "structured state (same as standard_file_dialog_get_info). Same validation and the same Windows.Graphics.Capture / PrintWindow pipeline as " +
                    "ui_capture_window_by_handle. Returns text { dialog: <StandardFileDialogInfo>, normalizedHandle, window, originalWidth, originalHeight, mimeType } followed by the image.",
                    SchemaBuilder.Create()
                        .AddInteger("handle", "HWND of the file dialog (decimal)", required: true)
                        .Build()),
                InArgs => CaptureAsync(InAccessor, InArgs));

            DiagnosticToolRunner.Register(InRegistry,
                new McpToolDefinition(
                    "standard_file_dialog_set_filename",
                    "[Windows UIA — desktop app being debugged] Set the file-name (or folder-name) text of a standard file dialog of the debugged application. The dialog is " +
                    "re-validated and re-classified, the file-name Edit is located by its control ID for the detected mode (Open 1148 / Save 1001 / Folder 1152), the value is set " +
                    "through the UIA ValuePattern (falling back to WM_SETTEXT) and read back. No keystrokes or screen coordinates are used. Returns text { handle, mode, fileName, " +
                    "method }. Unknown dialogs (no file-name control located) are refused.",
                    SchemaBuilder.Create()
                        .AddInteger("handle", "HWND of the file dialog (decimal)", required: true)
                        .AddString("fileName", "Text to put into the file-name box (a file name, or a full path to navigate)", required: true)
                        .Build()),
                InArgs => SetFileNameAsync(InAccessor, InArgs));

            DiagnosticToolRunner.Register(InRegistry,
                new McpToolDefinition(
                    "standard_file_dialog_select",
                    "[Windows UIA — desktop app being debugged] Select one item (file or folder) in the shell view of a standard file dialog of the debugged application by " +
                    "'name' (case-insensitive exact match on the displayed name) or 'path' (case-insensitive match on currentFolderPath + name). Uses the UIA SelectionItemPattern " +
                    "of the list item and reads the selection back. Exactly one item must match: no match or several matches return an error listing the items. Returns text " +
                    "{ handle, mode, item, method, selectedItems }.",
                    SchemaBuilder.Create()
                        .AddInteger("handle", "HWND of the file dialog (decimal)", required: true)
                        .AddString("name", "Displayed name of the item to select (e.g. 'alpha.txt' or 'SampleFolder')")
                        .AddString("path", "Absolute path of the item to select (currentFolderPath + name)")
                        .Build()),
                InArgs => SelectAsync(InAccessor, InArgs));

            DiagnosticToolRunner.Register(InRegistry,
                new McpToolDefinition(
                    "standard_file_dialog_confirm",
                    "[Windows UIA — desktop app being debugged] Press the confirm button (control ID 1: Open / Save / Select Folder, whatever its caption) of a standard file dialog " +
                    "of the debugged application via UIA InvokePattern (falling back to BM_CLICK). The dialog is re-validated first and a disabled button is refused. Only the " +
                    "first confirm is performed: an overwrite / other follow-up prompt is NOT answered automatically — handle it with standard_dialog_detect / execute. " +
                    "Returns text { executed, handle, mode, buttonId, method, dialogClosed, closeWaitMs }.",
                    SchemaBuilder.Create()
                        .AddInteger("handle", "HWND of the file dialog (decimal)", required: true)
                        .Build()),
                InArgs => PressButtonAsync(InAccessor, InArgs, StandardFileDialogResolver.ConfirmControlId));

            DiagnosticToolRunner.Register(InRegistry,
                new McpToolDefinition(
                    "standard_file_dialog_cancel",
                    "[Windows UIA — desktop app being debugged] Press the cancel button (control ID 2) of a standard file dialog of the debugged application via UIA InvokePattern " +
                    "(falling back to BM_CLICK). The dialog is re-validated first. Returns text { executed, handle, mode, buttonId, method, dialogClosed, closeWaitMs }.",
                    SchemaBuilder.Create()
                        .AddInteger("handle", "HWND of the file dialog (decimal)", required: true)
                        .Build()),
                InArgs => PressButtonAsync(InAccessor, InArgs, StandardFileDialogResolver.CancelControlId));

            DiagnosticToolRunner.Register(InRegistry,
                new McpToolDefinition(
                    "standard_file_dialog_wait",
                    "[Windows UIA — desktop app being debugged] Wait until a standard file dialog of the debugged application appears, optionally filtered by mode " +
                    "(open / save / folder / any) and title. Same polling, ambiguity rules and timeout format as ui_wait_for_window / standard_dialog_wait: returns text " +
                    "{ found, elapsedMs, resolutionReason, dialog }, found=false on timeout; several matches are resolved by foreground / visible-and-enabled and otherwise " +
                    "return an error listing the candidates. timeoutMs default 10000, max 55000; pollIntervalMs default 200, min 50.",
                    SchemaBuilder.Create()
                        .AddEnum("mode", "Kind of file dialog to wait for. Default: 'any'", TheModes)
                        .AddString("title", "Optional title filter (see titleMatch)")
                        .AddEnum("titleMatch", "Match mode for 'title': 'exact' (default, case-sensitive), 'contains' (case-insensitive substring), 'regex' (case-insensitive)",
                            new[] { "exact", "contains", "regex" })
                        .AddInteger("timeoutMs", "Maximum time to wait in milliseconds (default: 10000, max: 55000)")
                        .AddInteger("pollIntervalMs", "Polling interval in milliseconds (default: 200, min: 50)")
                        .Build()),
                InArgs => WaitAsync(InAccessor, InArgs));

            DiagnosticToolRunner.Register(InRegistry,
                new McpToolDefinition(
                    "standard_file_dialog_wait_closed",
                    "[Windows UIA — desktop app being debugged] Wait until the file dialog with the given HWND is closed. Same semantics as ui_wait_for_window_closed with a handle " +
                    "(closed on 'windowDestroyed' or 'handleReused'; an already invalid handle returns closed=true 'alreadyClosed'). Returns text { closed, elapsedMs, handle, " +
                    "initialTitle, reason }; closed=false on timeout. timeoutMs default 10000, max 55000; pollIntervalMs default 200, min 50.",
                    SchemaBuilder.Create()
                        .AddInteger("handle", "HWND of the file dialog to wait for (decimal)", required: true)
                        .AddInteger("timeoutMs", "Maximum time to wait in milliseconds (default: 10000, max: 55000)")
                        .AddInteger("pollIntervalMs", "Polling interval in milliseconds (default: 200, min: 50)")
                        .Build()),
                InArgs => WaitClosedAsync(InAccessor, InArgs));
        }

        // ------------------------------------------------------------------
        // ツールハンドラ
        // ------------------------------------------------------------------

        /// <summary>standard_file_dialog_detect の本体。</summary>
        /// <param name="InAccessor">DTE / UI スレッドアクセサ。</param>
        /// <param name="InArgs">ツール引数（includeUnknown / mode / title / titleMatch）。</param>
        /// <returns>ダイアログ一覧、またはエラー。</returns>
        private static async Task<McpToolResult> DetectAsync(VsServiceAccessor InAccessor, JObject InArgs)
        {
            bool TheIncludeUnknown = InArgs.Value<bool?>("includeUnknown") ?? false;
            string TheMode = InArgs.Value<string>("mode") ?? StandardFileDialogResolver.ModeAny;
            if (!StandardFileDialogResolver.IsKnownModeFilter(TheMode))
                return McpToolResult.Error($"Unknown mode: '{TheMode}'. Expected one of: any, open, save, folder");
            string TheQueryError = TryParseOptionalTitleQuery(InArgs, out WindowTitleQuery TheQuery);
            if (TheQueryError != null)
                return McpToolResult.Error(TheQueryError);

            HashSet<uint> TheProcessIds = await UiWindowTools.GetDebuggedProcessIdsAsync(InAccessor);
            if (TheProcessIds.Count == 0)
                return McpToolResult.Error(DebuggeeWindowResolver.NoDebuggedProcessMessage);

            List<ClassifiedFileDialog> TheDialogs = await FindDialogsAsync(TheProcessIds, TheQuery, TheMode, TheIncludeUnknown);
            return McpToolResult.Success(new
            {
                count = TheDialogs.Count,
                includeUnknown = TheIncludeUnknown,
                mode = TheMode,
                dialogs = TheDialogs.Select(TheDialog => BuildSummary(TheDialog)).ToList(),
            });
        }

        /// <summary>standard_file_dialog_get_info の本体。</summary>
        /// <param name="InAccessor">DTE / UI スレッドアクセサ。</param>
        /// <param name="InArgs">ツール引数（handle）。</param>
        /// <returns>StandardFileDialogInfo、またはエラー。</returns>
        private static async Task<McpToolResult> GetInfoAsync(VsServiceAccessor InAccessor, JObject InArgs)
        {
            ResolvedFileDialog TheResolved = await ResolveByHandleAsync(InAccessor, InArgs);
            if (TheResolved.Error != null)
                return TheResolved.Error;
            return McpToolResult.Success(TheResolved.Info);
        }

        /// <summary>standard_file_dialog_capture の本体。</summary>
        /// <param name="InAccessor">DTE / UI スレッドアクセサ。</param>
        /// <param name="InArgs">ツール引数（handle）。</param>
        /// <returns>text + image、またはエラー。</returns>
        private static async Task<McpToolResult> CaptureAsync(VsServiceAccessor InAccessor, JObject InArgs)
        {
            ResolvedFileDialog TheResolved = await ResolveByHandleAsync(InAccessor, InArgs);
            if (TheResolved.Error != null)
                return TheResolved.Error;

            JObject ThePrefix = new JObject
            {
                ["dialog"] = JObject.FromObject(TheResolved.Info),
            };
            return await UiWindowTools.CaptureTopLevelWindowAsync(InAccessor, TheResolved.Info.Handle, ThePrefix);
        }

        /// <summary>standard_file_dialog_set_filename の本体。</summary>
        /// <param name="InAccessor">DTE / UI スレッドアクセサ。</param>
        /// <param name="InArgs">ツール引数（handle / fileName）。</param>
        /// <returns>設定結果、またはエラー。</returns>
        private static async Task<McpToolResult> SetFileNameAsync(VsServiceAccessor InAccessor, JObject InArgs)
        {
            string TheFileName = InArgs.Value<string>("fileName");
            if (string.IsNullOrEmpty(TheFileName))
                return McpToolResult.Error("Parameter 'fileName' is required.");

            ResolvedFileDialog TheResolved = await ResolveByHandleAsync(InAccessor, InArgs);
            if (TheResolved.Error != null)
                return TheResolved.Error;

            (string TheContextError, UiInteractionContext TheContext) = await CreateInteractionContextAsync(TheResolved);
            if (TheContextError != null)
                return McpToolResult.Error(TheContextError);

            if (TheResolved.Adapter == null || TheResolved.Info.FileNameEditHandle == 0)
                return McpToolResult.Error($"File dialog {TheResolved.Info.Handle} ('{TheResolved.Info.Title}') has no recognized file-name control (dialogType {TheResolved.Info.DialogType}); refusing to type into it.");

            // Extended (Phase 9): 書き込む直前に対象が解決時と同じウィンドウのままかを確認する
            string TheReverifyError = TheContext.ReverifyBeforeAction(TheResolved.Info.DialogType);
            if (TheReverifyError != null)
                return McpToolResult.Error(TheReverifyError);

            try
            {
                string TheReadBack = null;
                string TheMethod = await RunWithTimeoutAsync(() =>
                    StandardFileDialogResolver.TrySetFileName(TheResolved.Structure, new IntPtr(TheResolved.Info.FileNameEditHandle), TheFileName, out TheReadBack));
                if (TheMethod == null)
                    return McpToolResult.Error($"Failed to set the file name on dialog {TheResolved.Info.Handle} (read back: '{TheReadBack}').");

                // Extended (Phase 10): ファイル名の本文は出さず、長さ・種別・実行方式だけを診断へ残す
                string TheAppliedMethod = TheMethod;
                DiagnosticHub.Emit(DiagnosticLevel.Info, DiagnosticCategory.FILE_DIALOG, "fileDialog.setFilename", InData =>
                {
                    InData["dialogHandle"] = TheResolved.Info.Handle;
                    InData["mode"] = TheResolved.Info.Mode;
                    InData["method"] = TheAppliedMethod;
                    InData["hasFileName"] = !string.IsNullOrEmpty(TheFileName);
                    InData["fileNameLength"] = TheFileName.Length;
                    InData["pathKind"] = DiagnosticSanitizer.DescribePathKind(TheFileName);
                    InData["isAbsolute"] = DiagnosticSanitizer.IsAbsolutePath(TheFileName);
                });

                return McpToolResult.Success(new
                {
                    handle = TheResolved.Info.Handle,
                    mode = TheResolved.Info.Mode,
                    fileName = TheReadBack,
                    method = TheMethod,
                });
            }
            catch (TimeoutException TheException)
            {
                return McpToolResult.Error(TheException.Message);
            }
        }

        /// <summary>standard_file_dialog_select の本体。name または path で一覧項目を一意に特定して SelectionItemPattern で選ぶ。</summary>
        /// <param name="InAccessor">DTE / UI スレッドアクセサ。</param>
        /// <param name="InArgs">ツール引数（handle / name / path）。</param>
        /// <returns>選択結果、またはエラー。</returns>
        private static async Task<McpToolResult> SelectAsync(VsServiceAccessor InAccessor, JObject InArgs)
        {
            string TheName = InArgs.Value<string>("name");
            string ThePath = InArgs.Value<string>("path");
            if (string.IsNullOrEmpty(TheName) && string.IsNullOrEmpty(ThePath))
                return McpToolResult.Error("Either 'name' or 'path' must be provided.");

            ResolvedFileDialog TheResolved = await ResolveByHandleAsync(InAccessor, InArgs);
            if (TheResolved.Error != null)
                return TheResolved.Error;

            (string TheContextError, UiInteractionContext TheContext) = await CreateInteractionContextAsync(TheResolved);
            if (TheContextError != null)
                return McpToolResult.Error(TheContextError);

            try
            {
                return await RunWithTimeoutAsync(() =>
                {
                    List<AutomationElement> TheElements = StandardFileDialogResolver.FindItemElements(TheResolved.Structure);
                    List<StandardFileDialogItemInfo> TheItems = StandardFileDialogResolver.ReadItems(TheResolved.Structure, TheResolved.Info.CurrentFolderPath);
                    string TheAvailable = TheItems.Count == 0 ? "(none)" : string.Join(", ", TheItems.Select(TheItem => $"'{TheItem.Name}'"));

                    List<int> TheMatches = new List<int>();
                    for (int TheIndex = 0; TheIndex < TheItems.Count; TheIndex++)
                    {
                        StandardFileDialogItemInfo TheItem = TheItems[TheIndex];
                        bool TheIsMatch = !string.IsNullOrEmpty(ThePath)
                            ? TheItem.Path != null && string.Equals(TheItem.Path.TrimEnd('\\'), ThePath.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)
                            : string.Equals(TheItem.Name, TheName, StringComparison.OrdinalIgnoreCase);
                        if (TheIsMatch)
                            TheMatches.Add(TheIndex);
                    }

                    string TheSelector = !string.IsNullOrEmpty(ThePath) ? $"path '{ThePath}'" : $"name '{TheName}'";
                    if (TheMatches.Count == 0)
                        return McpToolResult.Error($"No item matched {TheSelector} in file dialog {TheResolved.Info.Handle}. Available items: {TheAvailable}");
                    if (TheMatches.Count > 1)
                    {
                        return McpToolResult.Error($"{TheMatches.Count} items matched {TheSelector} in file dialog {TheResolved.Info.Handle}; nothing was selected. Candidates:\n" +
                            JsonConvert.SerializeObject(TheMatches.Select(TheIndex => TheItems[TheIndex]).ToList(), Formatting.Indented));
                    }

                    int TheTarget = TheMatches[0];
                    // Extended (Phase 9): 選択する直前に対象が解決時と同じウィンドウのままかを確認する
                    string TheReverifyError = TheContext.ReverifyBeforeAction(TheResolved.Info.DialogType);
                    if (TheReverifyError != null)
                        return McpToolResult.Error(TheReverifyError);

                    if (TheTarget >= TheElements.Count || !StandardFileDialogResolver.TrySelectItem(TheElements[TheTarget]))
                        return McpToolResult.Error($"Failed to select item '{TheItems[TheTarget].Name}' via SelectionItemPattern.");

                    List<StandardFileDialogItemInfo> TheAfter = StandardFileDialogResolver.ReadItems(TheResolved.Structure, TheResolved.Info.CurrentFolderPath);

                    // Extended (Phase 10): 項目名・パスの本文は出さず、指定の種別と件数だけを診断へ残す
                    int TheItemCount = TheItems.Count;
                    DiagnosticHub.Emit(DiagnosticLevel.Info, DiagnosticCategory.FILE_DIALOG, "fileDialog.select", InData =>
                    {
                        InData["dialogHandle"] = TheResolved.Info.Handle;
                        InData["mode"] = TheResolved.Info.Mode;
                        InData["method"] = "uiaSelectionItem";
                        InData["selectedBy"] = !string.IsNullOrEmpty(ThePath) ? "path" : "name";
                        InData["hasPath"] = !string.IsNullOrEmpty(ThePath);
                        InData["pathKind"] = DiagnosticSanitizer.DescribePathKind(ThePath);
                        InData["isAbsolute"] = DiagnosticSanitizer.IsAbsolutePath(ThePath);
                        InData["nameLength"] = TheName == null ? 0 : TheName.Length;
                        InData["itemCount"] = TheItemCount;
                    });

                    return McpToolResult.Success(new
                    {
                        handle = TheResolved.Info.Handle,
                        mode = TheResolved.Info.Mode,
                        item = TheAfter.FirstOrDefault(TheItem => TheItem.AutomationId == TheItems[TheTarget].AutomationId) ?? TheItems[TheTarget],
                        method = "uiaSelectionItem",
                        selectedItems = TheAfter.Where(TheItem => TheItem.IsSelected == true).ToList(),
                    });
                });
            }
            catch (TimeoutException TheException)
            {
                return McpToolResult.Error(TheException.Message);
            }
        }

        /// <summary>standard_file_dialog_confirm / cancel の本体。Control ID で確定 / キャンセルボタンを押し、閉じたかを短時間確認する。</summary>
        /// <param name="InAccessor">DTE / UI スレッドアクセサ。</param>
        /// <param name="InArgs">ツール引数（handle）。</param>
        /// <param name="InControlId">1（確定）または 2（キャンセル）。</param>
        /// <returns>実行結果、またはエラー。</returns>
        private static async Task<McpToolResult> PressButtonAsync(VsServiceAccessor InAccessor, JObject InArgs, int InControlId)
        {
            ResolvedFileDialog TheResolved = await ResolveByHandleAsync(InAccessor, InArgs);
            if (TheResolved.Error != null)
                return TheResolved.Error;

            (string TheContextError, UiInteractionContext TheContext) = await CreateInteractionContextAsync(TheResolved);
            if (TheContextError != null)
                return McpToolResult.Error(TheContextError);

            DialogChildControl TheButton = StandardFileDialogResolver.FindButton(TheResolved.Structure, InControlId);
            if (TheButton == null)
                return McpToolResult.Error($"File dialog {TheResolved.Info.Handle} has no button with control ID {InControlId}.");
            if (!TheButton.IsEnabled)
                return McpToolResult.Error($"Button id {InControlId} ('{StandardDialogResolver.StripAccelerator(TheButton.Text)}') on file dialog {TheResolved.Info.Handle} is disabled.");

            // Extended (Phase 9): 押す直前に対象が解決時と同じウィンドウのままかを確認する
            string TheReverifyError = TheContext.ReverifyBeforeAction(TheResolved.Info.DialogType);
            if (TheReverifyError != null)
                return McpToolResult.Error(TheReverifyError);

            string TheMethod;
            try
            {
                TheMethod = await RunWithTimeoutAsync(() => StandardFileDialogResolver.TryPressButton(TheResolved.Structure, TheButton));
            }
            catch (TimeoutException TheException)
            {
                return McpToolResult.Error(TheException.Message);
            }
            if (TheMethod == null)
                return McpToolResult.Error($"Failed to press button id {InControlId} on file dialog {TheResolved.Info.Handle}: every execution method was rejected by the target.");

            Stopwatch TheStopwatch = Stopwatch.StartNew();
            bool TheIsClosed = false;
            IntPtr TheDialogHandle = new IntPtr(TheResolved.Info.Handle);
            while (TheStopwatch.ElapsedMilliseconds < CloseWaitMs)
            {
                if (!NativeMethods.IsWindow(TheDialogHandle))
                {
                    TheIsClosed = true;
                    break;
                }
                NativeMethods.GetWindowThreadProcessId(TheDialogHandle, out uint TheCurrentProcessId);
                if (TheCurrentProcessId != TheResolved.Info.ProcessId)
                {
                    TheIsClosed = true;
                    break;
                }
                await Task.Delay(ClosePollMs);
            }

            // Extended (Phase 10): confirm / cancel の区別・実行方式・閉じたかを診断へ残す（表示文字列は出さない）
            bool IsDialogClosed = TheIsClosed;
            string TheAppliedMethod = TheMethod;
            DiagnosticHub.EmitCore(DiagnosticLevel.Info, DiagnosticCategory.FILE_DIALOG,
                InControlId == 1 ? "fileDialog.confirm" : "fileDialog.cancel", null, null,
                TheStopwatch.ElapsedMilliseconds, IsDialogClosed ? "closed" : "stillOpen", null, InData =>
                {
                    InData["dialogHandle"] = TheResolved.Info.Handle;
                    InData["mode"] = TheResolved.Info.Mode;
                    InData["buttonId"] = InControlId;
                    InData["method"] = TheAppliedMethod;
                    InData["dialogClosed"] = IsDialogClosed;
                }, null);

            return McpToolResult.Success(new
            {
                executed = true,
                handle = TheResolved.Info.Handle,
                mode = TheResolved.Info.Mode,
                buttonId = InControlId,
                method = TheMethod,
                dialogClosed = TheIsClosed,
                closeWaitMs = TheStopwatch.ElapsedMilliseconds,
            });
        }

        /// <summary>standard_file_dialog_wait の本体。Phase 4/5 と同じポーリング・候補解決・タイムアウト形式。</summary>
        /// <param name="InAccessor">DTE / UI スレッドアクセサ。</param>
        /// <param name="InArgs">ツール引数（mode / title / titleMatch / timeoutMs / pollIntervalMs）。</param>
        /// <returns>text（found / elapsedMs / resolutionReason / dialog）、またはエラー。</returns>
        private static async Task<McpToolResult> WaitAsync(VsServiceAccessor InAccessor, JObject InArgs)
        {
            string TheMode = InArgs.Value<string>("mode") ?? StandardFileDialogResolver.ModeAny;
            if (!StandardFileDialogResolver.IsKnownModeFilter(TheMode))
                return McpToolResult.Error($"Unknown mode: '{TheMode}'. Expected one of: any, open, save, folder");
            string TheQueryError = TryParseOptionalTitleQuery(InArgs, out WindowTitleQuery TheQuery);
            if (TheQueryError != null)
                return McpToolResult.Error(TheQueryError);
            UiWindowTools.ParseWaitOptions(InArgs, out int TheTimeoutMs, out int ThePollIntervalMs);

            Stopwatch TheStopwatch = Stopwatch.StartNew();
            HashSet<uint> TheProcessIds = await UiWindowTools.GetDebuggedProcessIdsAsync(InAccessor);
            if (TheProcessIds.Count == 0)
                return McpToolResult.Error(DebuggeeWindowResolver.NoDebuggedProcessMessage);

            string TheDescription = $"{TheMode} file dialog" + (TheQuery != null ? " with " + TheQuery.Describe() : string.Empty);
            while (true)
            {
                List<ClassifiedFileDialog> TheDialogs = await FindDialogsAsync(TheProcessIds, TheQuery, TheMode, false);
                if (TheDialogs.Count > 0)
                {
                    CandidateResolution TheResolution = DebuggeeWindowResolver.ResolveCandidates(TheDialogs.Select(TheDialog => TheDialog.Window).ToList());
                    if (!TheResolution.IsResolved)
                    {
                        return McpToolResult.Error(DebuggeeWindowResolver.DescribeAmbiguity(
                            TheResolution, TheDescription, "Pick one and call standard_file_dialog_get_info with its 'handle'."));
                    }

                    ClassifiedFileDialog TheMatched = TheDialogs.First(TheDialog => TheDialog.Window.Handle == TheResolution.Resolved.Handle);
                    return McpToolResult.Success(new
                    {
                        found = true,
                        elapsedMs = TheStopwatch.ElapsedMilliseconds,
                        resolutionReason = TheResolution.Reason,
                        dialog = BuildSummary(TheMatched),
                    });
                }

                if (TheStopwatch.ElapsedMilliseconds >= TheTimeoutMs)
                    break;
                await Task.Delay(ThePollIntervalMs);

                TheProcessIds = await UiWindowTools.GetDebuggedProcessIdsAsync(InAccessor);
                if (TheProcessIds.Count == 0)
                    return McpToolResult.Error("Debugging stopped while waiting for the file dialog.");
            }

            return McpToolResult.Success(new
            {
                found = false,
                elapsedMs = TheStopwatch.ElapsedMilliseconds,
                resolutionReason = (string)null,
                dialog = (object)null,
            });
        }

        /// <summary>standard_file_dialog_wait_closed の本体。Phase 4 の handle 版 close-wait をそのまま使う。</summary>
        /// <param name="InAccessor">DTE / UI スレッドアクセサ。</param>
        /// <param name="InArgs">ツール引数（handle / timeoutMs / pollIntervalMs）。</param>
        /// <returns>text（closed / elapsedMs / handle / initialTitle / reason）、またはエラー。</returns>
        private static async Task<McpToolResult> WaitClosedAsync(VsServiceAccessor InAccessor, JObject InArgs)
        {
            long? TheHandle = InArgs.Value<long?>("handle");
            if (!TheHandle.HasValue)
                return McpToolResult.Error("Parameter 'handle' is required (use the 'handle' value returned by standard_file_dialog_detect)");
            if (!UiWindowTools.IsHandleInRange(TheHandle.Value))
                return McpToolResult.Error($"Window handle {TheHandle.Value} is out of range for a window handle.");

            UiWindowTools.ParseWaitOptions(InArgs, out int TheTimeoutMs, out int ThePollIntervalMs);
            return await UiWindowTools.WaitForHandleClosedAsync(InAccessor, TheHandle.Value, TheTimeoutMs, ThePollIntervalMs);
        }

        // ------------------------------------------------------------------
        // 共通 helper
        // ------------------------------------------------------------------

        /// <summary>分類済み File Dialog。</summary>
        private sealed class ClassifiedFileDialog
        {
            public WindowInfo Window { get; set; }
            public string DialogType { get; set; }
            public string Mode { get; set; }
        }

        /// <summary>handle から解決した File Dialog 一式。Error が非 null なら他は未設定。</summary>
        private sealed class ResolvedFileDialog
        {
            public StandardFileDialogInfo Info { get; set; }
            public IStandardFileDialogAdapter Adapter { get; set; }
            public StandardDialogStructure Structure { get; set; }
            public McpToolResult Error { get; set; }

            /// <summary>Extended (Phase 9): 正規化済みのトップレベル HWND（操作直前の共通再検証で使う）。</summary>
            public IntPtr Window { get; set; }

            /// <summary>Extended (Phase 9): デバッグ中プロセス ID の集合（操作直前の共通再検証で使う）。</summary>
            public HashSet<uint> ProcessIds { get; set; }
        }

        /// <summary>
        /// Extended (Phase 9): 実行系（set_filename / select / confirm / cancel）が解決直後に通す共通の安全境界。
        /// Action 系ツールと同じ HWND / PID / ウィンドウ状態 / モーダル判定を適用する（対象がダイアログ自身であることを示すため
        /// <see cref="UiInteractionContext.TryCreateForDialog"/> を使う。可視かつ有効なダイアログ自身はモーダル候補であっても
        /// interactable と判定される）。Win32 のみで UIA を使わないため STA である必要はない。既存の検証・エラー文はそのまま残す。
        /// </summary>
        /// <param name="InResolved">解決済みの File Dialog。</param>
        /// <returns>Context とエラーメッセージ。正常ならエラーは null。</returns>
        private static Task<(string Error, UiInteractionContext Context)> CreateInteractionContextAsync(ResolvedFileDialog InResolved)
        {
            return Task.Run(() =>
            {
                string TheCreateError = UiInteractionContext.TryCreateForDialog(InResolved.Window, InResolved.ProcessIds, out UiInteractionContext TheCreated);
                return (TheCreateError, TheCreated);
            });
        }

        /// <summary>
        /// handle 引数を Phase 4 の検証に通し、#32770 かつ File Dialog 構造であることを確認してから構造化情報を組み立てる。
        /// get_info / capture / set_filename / select / confirm / cancel の入口で毎回やり直す（古い HWND での誤操作を防ぐ）。
        /// </summary>
        /// <param name="InAccessor">DTE / UI スレッドアクセサ。</param>
        /// <param name="InArgs">ツール引数（handle）。</param>
        /// <returns>解決結果。</returns>
        private static async Task<ResolvedFileDialog> ResolveByHandleAsync(VsServiceAccessor InAccessor, JObject InArgs)
        {
            long? TheHandle = InArgs.Value<long?>("handle");
            if (!TheHandle.HasValue)
                return new ResolvedFileDialog { Error = McpToolResult.Error("Parameter 'handle' is required (use the 'handle' value returned by standard_file_dialog_detect)") };
            if (!UiWindowTools.IsHandleInRange(TheHandle.Value))
                return new ResolvedFileDialog { Error = McpToolResult.Error($"Window handle {TheHandle.Value} is out of range for a window handle.") };

            HashSet<uint> TheProcessIds = await UiWindowTools.GetDebuggedProcessIdsAsync(InAccessor);
            string TheHandleError = DebuggeeWindowResolver.ValidateAndNormalizeWindowHandle(TheHandle.Value, TheProcessIds, out IntPtr TheNormalized);
            if (TheHandleError != null)
                return new ResolvedFileDialog { Error = McpToolResult.Error(TheHandleError) };

            long TheNormalizedHandle = TheNormalized.ToInt64();
            WindowInfo TheWindow = await UiWindowTools.FindWindowInfoAsync(TheProcessIds, TheNormalizedHandle);
            if (TheWindow == null)
                return new ResolvedFileDialog { Error = McpToolResult.Error($"Window handle {TheNormalizedHandle} disappeared before it could be inspected.") };
            if (!StandardDialogResolver.IsDialogClass(TheWindow))
            {
                return new ResolvedFileDialog
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
                    if (!StandardFileDialogResolver.IsFileDialogStructure(TheStructure))
                    {
                        return new ResolvedFileDialog
                        {
                            Error = McpToolResult.Error(
                                $"Window handle {TheNormalizedHandle} ('{TheWindow.Title}') is a #32770 dialog but not a standard file dialog (no IFileDialog view host / address bar / confirm+cancel buttons). Use standard_dialog_get_info for MessageBox / TaskDialog."),
                        };
                    }
                    StandardFileDialogInfo TheInfo = StandardFileDialogResolver.BuildInfo(TheWindow, TheStructure, out IStandardFileDialogAdapter TheAdapter);
                    return new ResolvedFileDialog
                    {
                        Info = TheInfo,
                        Adapter = TheAdapter,
                        Structure = TheStructure,
                        Window = TheNormalized,
                        ProcessIds = TheProcessIds,
                    };
                });
            }
            catch (Exception TheException)
            {
                return new ResolvedFileDialog { Error = McpToolResult.Error($"Failed to inspect file dialog {TheNormalizedHandle}: {TheException.Message}") };
            }
        }

        /// <summary>可視の #32770 のうち File Dialog 構造のものを列挙・分類し、mode と任意のタイトル条件で絞る。</summary>
        /// <param name="InProcessIds">デバッグ中プロセス ID の集合。</param>
        /// <param name="InQuery">タイトル条件（null なら無条件）。</param>
        /// <param name="InModeFilter">any / open / save / folder。</param>
        /// <param name="InIncludeUnknown">unknownFileDialog も含めるか（any のときだけ意味を持つ）。</param>
        /// <returns>分類済み一覧（Z 順）。</returns>
        private static Task<List<ClassifiedFileDialog>> FindDialogsAsync(HashSet<uint> InProcessIds, WindowTitleQuery InQuery, string InModeFilter, bool InIncludeUnknown)
        {
            return Task.Run(() =>
            {
                List<ClassifiedFileDialog> TheDialogs = new List<ClassifiedFileDialog>();
                foreach (WindowInfo TheWindow in DebuggeeWindowEnumerator.EnumerateTopLevelWindows(InProcessIds, false))
                {
                    if (!StandardDialogResolver.IsDialogClass(TheWindow))
                        continue;
                    if (InQuery != null && !DebuggeeWindowResolver.IsMatched(TheWindow, InQuery))
                        continue;

                    string TheType;
                    string TheMode;
                    try
                    {
                        StandardDialogStructure TheStructure = StandardDialogResolver.Inspect(new IntPtr(TheWindow.Handle));
                        if (!StandardFileDialogResolver.IsFileDialogStructure(TheStructure))
                            continue;
                        IStandardFileDialogAdapter TheAdapter = StandardFileDialogResolver.SelectAdapter(TheStructure);
                        TheType = TheAdapter?.DialogType ?? StandardFileDialogResolver.TypeUnknown;
                        TheMode = TheAdapter?.Mode ?? StandardFileDialogResolver.ModeUnknown;
                    }
                    catch
                    {
                        continue; // 列挙中に閉じられたダイアログ
                    }

                    bool TheIsAny = string.Equals(InModeFilter, StandardFileDialogResolver.ModeAny, StringComparison.OrdinalIgnoreCase);
                    bool TheIsAccepted = TheIsAny
                        ? TheMode != StandardFileDialogResolver.ModeUnknown || InIncludeUnknown
                        : string.Equals(TheMode, InModeFilter, StringComparison.OrdinalIgnoreCase);
                    if (!TheIsAccepted)
                        continue;
                    TheDialogs.Add(new ClassifiedFileDialog { Window = TheWindow, DialogType = TheType, Mode = TheMode });
                }
                return TheDialogs;
            });
        }

        /// <summary>任意の title / titleMatch 引数を検索条件にする。title が無ければ null。</summary>
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

        /// <summary>detect / wait の戻り値用の要約（dialogType / mode + ui_list_windows と同じウィンドウ項目）。</summary>
        /// <param name="InDialog">分類済みダイアログ。</param>
        /// <returns>匿名オブジェクト。</returns>
        private static object BuildSummary(ClassifiedFileDialog InDialog)
        {
            WindowInfo TheWindow = InDialog.Window;
            return new
            {
                dialogType = InDialog.DialogType,
                mode = InDialog.Mode,
                handle = TheWindow.Handle,
                handleHex = TheWindow.HandleHex,
                title = TheWindow.Title,
                className = TheWindow.ClassName,
                processId = TheWindow.ProcessId,
                ownerHandle = TheWindow.OwnerHandle,
                isVisible = TheWindow.IsVisible,
                isEnabled = TheWindow.IsEnabled,
                isForeground = TheWindow.IsForeground,
                isModalCandidate = TheWindow.IsModalCandidate,
                modalCandidateReason = TheWindow.ModalCandidateReason,
                bounds = TheWindow.Bounds,
                dpi = TheWindow.Dpi,
                monitor = TheWindow.Monitor,
            };
        }

        /// <summary>UIA / Win32 の処理をバックグラウンドで実行し、応答が無い場合は TimeoutException にする。</summary>
        /// <typeparam name="T">戻り値の型。</typeparam>
        /// <param name="InFunc">実行する処理。</param>
        /// <returns>処理の戻り値。</returns>
        private static async Task<T> RunWithTimeoutAsync<T>(Func<T> InFunc)
        {
            Task<T> TheTask = Task.Run(InFunc);
            Task TheFinished = await Task.WhenAny(TheTask, Task.Delay(TimeSpan.FromSeconds(InspectTimeoutSeconds)));
            if (TheFinished != TheTask)
                throw new TimeoutException($"File dialog inspection timed out after {InspectTimeoutSeconds} seconds. The target application may not be responding.");
            return await TheTask;
        }
    }
}
