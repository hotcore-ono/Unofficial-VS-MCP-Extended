using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using VsMcp.Extension.McpServer;
using VsMcp.Extension.Services;
using VsMcp.Shared;
using VsMcp.Shared.Protocol;

namespace VsMcp.Extension.Tools
{
    /// <summary>
    /// Extended 独自の「任意のデバッグ対象トップレベル HWND を root にした UI Automation / スクリーンショット」ツール群。
    /// upstream の ui_get_tree / ui_snapshot / ui_capture_region はメインウィンドウ固定なので、その本体（UiTools の internal メソッド）を
    /// そのまま再利用し、root だけを Phase 4 の HWND 検証（IsWindow → GA_ROOT → デバッグ対象 PID 再照合）を通した任意ウィンドウに差し替える。
    /// upstream の public Tool schema は変更しない。
    /// </summary>
    public static class UiWindowUiaTools
    {
        /// <summary>ツールをレジストリへ登録する。VsMcpPackage.RegisterTools から呼ばれる。</summary>
        /// <param name="InRegistry">登録先レジストリ。</param>
        /// <param name="InAccessor">DTE / UI スレッドアクセサ。</param>
        public static void Register(McpToolRegistry InRegistry, VsServiceAccessor InAccessor)
        {
            InRegistry.Register(
                new McpToolDefinition(
                    "ui_window_get_tree",
                    "[Windows UIA — desktop app being debugged] Get the raw UI element tree of ANY top-level window of the debugged application (modal dialog, MessageBox, " +
                    "TaskDialog, file dialog, owned window) identified by its HWND from ui_list_windows / standard_dialog_detect / standard_file_dialog_detect. " +
                    "Same output and options as ui_get_tree, which is fixed to the main window. The handle is validated first (must exist, is normalized to its top-level " +
                    "window, and must belong to a debugged process — other applications and Visual Studio itself are refused). Prefer ui_window_snapshot unless you need the unpruned tree.",
                    SchemaBuilder.Create()
                        .AddInteger("windowHandle", "HWND of the window to use as the tree root (decimal)", required: true)
                        .AddInteger("depth", "Maximum depth of the tree (default: 3)")
                        .AddInteger("maxChildren", "Maximum number of child elements to enumerate per node (default: 50)")
                        .AddInteger("maxElements", "Maximum total number of elements in the tree (default: 500)")
                        .Build()),
                InArgs => UiWindowGetTreeAsync(InAccessor, InArgs));

            InRegistry.Register(
                new McpToolDefinition(
                    "ui_window_snapshot",
                    "[Windows UIA — desktop app being debugged] Capture a compact semantic snapshot (pruned UI Automation tree with actionable patterns, state flags, rect and " +
                    "focused element, plus an optional screenshot) of ANY top-level window of the debugged application identified by its HWND — the same output as ui_snapshot, " +
                    "which is fixed to the main window. Use it to inspect modal dialogs, MessageBox / TaskDialog / file dialogs or owned windows. The handle is validated first " +
                    "(exists, normalized to its top-level window, belongs to a debugged process).",
                    SchemaBuilder.Create()
                        .AddInteger("windowHandle", "HWND of the window to snapshot (decimal)", required: true)
                        .AddInteger("depth", "Maximum tree depth (default: 8)")
                        .AddInteger("maxElements", "Maximum total elements in the tree (default: 300)")
                        .AddBoolean("includeScreenshot", "Include a screenshot of the window (default: true)")
                        .AddBoolean("includeOffscreen", "Include elements marked IsOffscreen (default: false)")
                        .AddString("ancestorAutomationId", "Limit the snapshot to the subtree rooted at this AutomationId")
                        .Build()),
                InArgs => UiWindowSnapshotAsync(InAccessor, InArgs));

            InRegistry.Register(
                new McpToolDefinition(
                    "ui_window_capture_region",
                    "[Windows UIA — desktop app being debugged] Capture a screenshot of a region of ANY top-level window of the debugged application identified by its HWND. " +
                    "Identical semantics to ui_capture_region (x/y are relative to the captured window image in physical pixels, the region is clamped to the window, " +
                    "same PNG/JPEG size limits) but the window is chosen by 'windowHandle' instead of being fixed to the main window. The handle is validated first " +
                    "(exists, normalized to its top-level window, belongs to a debugged process).",
                    SchemaBuilder.Create()
                        .AddInteger("windowHandle", "HWND of the window to capture from (decimal)", required: true)
                        .AddInteger("x", "X coordinate of the region (relative to window)", required: true)
                        .AddInteger("y", "Y coordinate of the region (relative to window)", required: true)
                        .AddInteger("width", "Width of the region", required: true)
                        .AddInteger("height", "Height of the region", required: true)
                        .Build()),
                InArgs => UiWindowCaptureRegionAsync(InAccessor, InArgs));
        }

        /// <summary>ui_window_get_tree の本体。HWND を検証してから upstream の tree 構築（UiTools.BuildTreeFromWindowAsync）へ渡す。</summary>
        /// <param name="InAccessor">DTE / UI スレッドアクセサ。</param>
        /// <param name="InArgs">ツール引数。</param>
        /// <returns>ui_get_tree と同形式の結果、またはエラー。</returns>
        private static async Task<McpToolResult> UiWindowGetTreeAsync(VsServiceAccessor InAccessor, JObject InArgs)
        {
            (IntPtr TheWindow, McpToolResult TheError) = await ResolveWindowAsync(InAccessor, InArgs);
            if (TheError != null)
                return TheError;

            int TheMaxDepth = InArgs.Value<int?>("depth") ?? 3;
            int TheMaxChildren = InArgs.Value<int?>("maxChildren") ?? 50;
            int TheMaxElements = InArgs.Value<int?>("maxElements") ?? 500;
            return await UiTools.BuildTreeFromWindowAsync(TheWindow, TheMaxDepth, TheMaxChildren, TheMaxElements);
        }

        /// <summary>ui_window_snapshot の本体。HWND を検証してから upstream のスナップショット構築（UiTools.BuildSnapshotFromWindowAsync）へ渡す。</summary>
        /// <param name="InAccessor">DTE / UI スレッドアクセサ。</param>
        /// <param name="InArgs">ツール引数。</param>
        /// <returns>ui_snapshot と同形式の結果、またはエラー。</returns>
        private static async Task<McpToolResult> UiWindowSnapshotAsync(VsServiceAccessor InAccessor, JObject InArgs)
        {
            (IntPtr TheWindow, McpToolResult TheError) = await ResolveWindowAsync(InAccessor, InArgs);
            if (TheError != null)
                return TheError;

            int TheDepth = InArgs.Value<int?>("depth") ?? 8;
            int TheMaxElements = InArgs.Value<int?>("maxElements") ?? 300;
            bool TheIncludeScreenshot = InArgs.Value<bool?>("includeScreenshot") ?? true;
            bool TheIncludeOffscreen = InArgs.Value<bool?>("includeOffscreen") ?? false;
            string TheAncestorAutomationId = InArgs.Value<string>("ancestorAutomationId");
            return await UiTools.BuildSnapshotFromWindowAsync(TheWindow, TheDepth, TheMaxElements, TheIncludeScreenshot, TheIncludeOffscreen, TheAncestorAutomationId);
        }

        /// <summary>ui_window_capture_region の本体。引数検証は upstream ui_capture_region と同じ順序・同じ文言で行い、HWND 検証後に同じ切り出し処理へ渡す。</summary>
        /// <param name="InAccessor">DTE / UI スレッドアクセサ。</param>
        /// <param name="InArgs">ツール引数。</param>
        /// <returns>image、またはエラー。</returns>
        private static async Task<McpToolResult> UiWindowCaptureRegionAsync(VsServiceAccessor InAccessor, JObject InArgs)
        {
            int TheX = InArgs.Value<int>("x");
            int TheY = InArgs.Value<int>("y");
            int TheWidth = InArgs.Value<int>("width");
            int TheHeight = InArgs.Value<int>("height");
            if (TheWidth <= 0 || TheHeight <= 0)
                return McpToolResult.Error("Width and height must be positive values");

            (IntPtr TheWindow, McpToolResult TheError) = await ResolveWindowAsync(InAccessor, InArgs);
            if (TheError != null)
                return TheError;

            return await UiTools.CaptureRegionFromWindowAsync(TheWindow, TheX, TheY, TheWidth, TheHeight);
        }

        /// <summary>
        /// windowHandle 引数を Phase 4 の検証（範囲 → IsWindow → GA_ROOT 正規化 → デバッグ対象 PID 再照合）に通す。
        /// 最小化中でも UIA ツリーは取れるので IsIconic は確認しない（スクリーンショットは upstream と同じく失敗し得る）。
        /// </summary>
        /// <param name="InAccessor">DTE / UI スレッドアクセサ。</param>
        /// <param name="InArgs">ツール引数（windowHandle）。</param>
        /// <returns>正規化済み HWND と、失敗時のエラー結果（成功時は null）。</returns>
        private static async Task<(IntPtr Window, McpToolResult Error)> ResolveWindowAsync(VsServiceAccessor InAccessor, JObject InArgs)
        {
            long? TheHandle = InArgs.Value<long?>("windowHandle");
            if (!TheHandle.HasValue)
                return (IntPtr.Zero, McpToolResult.Error("Parameter 'windowHandle' is required (use the 'handle' value returned by ui_list_windows)"));
            if (!UiWindowTools.IsHandleInRange(TheHandle.Value))
                return (IntPtr.Zero, McpToolResult.Error($"Window handle {TheHandle.Value} is out of range for a window handle."));

            HashSet<uint> TheProcessIds = await UiWindowTools.GetDebuggedProcessIdsAsync(InAccessor);
            string TheHandleError = DebuggeeWindowResolver.ValidateAndNormalizeWindowHandle(TheHandle.Value, TheProcessIds, out IntPtr TheNormalized);
            if (TheHandleError != null)
                return (IntPtr.Zero, McpToolResult.Error(TheHandleError));
            return (TheNormalized, null);
        }
    }
}
