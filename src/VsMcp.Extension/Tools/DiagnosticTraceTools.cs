using System;
using System.Diagnostics;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using VsMcp.Extension.McpServer;
using VsMcp.Extension.Services;
using VsMcp.Shared;
using VsMcp.Shared.Protocol;

namespace VsMcp.Extension.Tools
{
    /// <summary>
    /// Extended (Phase 10): 診断基盤そのものを操作する 5 つの Tool（指示書 §28〜§32）。
    /// 既存 upstream の <see cref="DiagnosticsTools"/>（diagnostics_binding_errors）とはファイルを分け、
    /// upstream ファイルの差分を 0 に保つ。5 つとも <see cref="DiagnosticToolRunner"/> を通すので自身も計装される。
    /// </summary>
    public static class DiagnosticTraceTools
    {
        /// <summary>diagnostics_mark の message の最大長。</summary>
        private const int _MAX_MARKER_MESSAGE_LENGTH = 500;

        /// <summary>diagnostics_export の minutes の既定値。</summary>
        private const int _DEFAULT_EXPORT_MINUTES = 30;

        /// <summary>diagnostics_export の minutes の上限（30 日）。</summary>
        private const int _MAX_EXPORT_MINUTES = 43200;

        /// <summary>diagnostics_flush の timeoutMs の既定値。</summary>
        private const int _DEFAULT_FLUSH_TIMEOUT_MS = 2000;

        /// <summary>diagnostics_flush の timeoutMs の上限。</summary>
        private const int _MAX_FLUSH_TIMEOUT_MS = 10000;

        /// <summary>ツールをレジストリへ登録する。VsMcpPackage.RegisterTools から呼ばれる。</summary>
        /// <param name="InRegistry">登録先レジストリ。</param>
        /// <param name="InAccessor">DTE / UI スレッドアクセサ。</param>
        public static void Register(McpToolRegistry InRegistry, VsServiceAccessor InAccessor)
        {
            DiagnosticToolRunner.Register(InRegistry,
                new McpToolDefinition(
                    "diagnostics_get_status",
                    "[Extended diagnostics] Report the state of the Extended diagnostic trace infrastructure: whether it is enabled, the current log level, the " +
                    "current session id, the JSONL file being written, the log / config locations, the pending queue length, the number of dropped events, whether " +
                    "the writer is still healthy, and the effective limits (retentionDays, maxFileSizeMb, maxSessionFiles, maxStringLength). Nothing is captured " +
                    "from the debugged application, so this tool is safe to call at any time. Returns text { enabled, level, sessionId, currentLogFile, " +
                    "logDirectory, configFile, queueLength, droppedEventCount, writerHealthy, ... }. " +
                    "Call it before diagnostics_export: writerHealthy=false means the JSONL may be missing, so an empty export is not proof that nothing happened.",
                    SchemaBuilder.Empty()),
                InArgs => DiagnosticsGetStatusAsync(InArgs));

            DiagnosticToolRunner.Register(InRegistry,
                new McpToolDefinition(
                    "diagnostics_set_level",
                    "[Extended diagnostics] Change how much the Extended diagnostic trace infrastructure records: 'off' disables it completely (the tool wrappers " +
                    "then add no work at all), 'error' / 'warning' / 'info' (default) / 'verbose' / 'trace'. 'trace' logs every poll iteration and every visited UI " +
                    "Automation element, so use it only while reproducing a problem. The change applies immediately to the running Visual Studio; pass persist=true " +
                    "to also write it to the diagnostics.json configuration file so it survives a restart. Returns text { previousLevel, level, persisted, configFile }. " +
                    "Return to 'info' after the reproduction; do not leave 'trace' on during normal work.",
                    SchemaBuilder.Create()
                        .AddEnum("level", "New log level: off, error, warning, info, verbose, trace",
                            new[] { "off", "error", "warning", "info", "verbose", "trace" }, required: true)
                        .AddBoolean("persist", "Also write the level to diagnostics.json (default: false, session only)")
                        .Build()),
                InArgs => DiagnosticsSetLevelAsync(InArgs));

            DiagnosticToolRunner.Register(InRegistry,
                new McpToolDefinition(
                    "diagnostics_mark",
                    "[Extended diagnostics] Put a named marker into the diagnostic log to say 'the problem happened here'. Call it right before and right after " +
                    "reproducing a problem, then export the session with diagnostics_export (sessionId): the marker has a correlationId of its own, so exporting " +
                    "by that id returns only the marker, while the markers' timestamps locate the failure in events.jsonl. The message is sanitized and truncated to " +
                    "500 characters — never put passwords, tokens or file contents in it. Returns text { markerId, timestampUtc, sessionId, correlationId, " +
                    "sequence, recorded }; recorded=false means diagnostics are currently off, so nothing was written.",
                    SchemaBuilder.Create()
                        .AddString("message", "Short note describing what happened (optional, max 500 characters, sanitized)")
                        .Build()),
                InArgs => DiagnosticsMarkAsync(InArgs));

            DiagnosticToolRunner.Register(InRegistry,
                new McpToolDefinition(
                    "diagnostics_export",
                    "[Extended diagnostics] Collect the recorded diagnostic events into a single ZIP under %LOCALAPPDATA%\\Unofficial-VS-MCP-Extended\\Exports so " +
                    "they can be attached to a bug report. By default the last 30 minutes are exported; pass 'sessionId' to export one whole session or " +
                    "'correlationId' to export every event of one tool call. The archive contains manifest.json, events.jsonl, environment.json and README.txt " +
                    "(plus screenshots\\ when includeScreenshots=true and screenshots were captured). User name, machine name and repository paths are not " +
                    "included. Lines that are not valid JSON (a log file truncated by a crash) are skipped and counted. Returns text { exportId, zipPath, " +
                    "eventCount, errorCount, warningCount, startUtc, endUtc, sessionIds, includedScreenshots, skippedLines, bytes }. " +
                    "Prefer 'sessionId' (the whole session, with your diagnostics_mark markers), then 'minutes' for a long session; use 'correlationId' only " +
                    "for one tool call whose id you already know from an earlier export — the failing tool's correlationId is not part of its error text.",
                    SchemaBuilder.Create()
                        .AddInteger("minutes", "How many minutes back to export (default: 30, 1-43200); ignored when sessionId or correlationId is given")
                        .AddString("sessionId", "Export every event of this session id (from diagnostics_get_status)")
                        .AddString("correlationId", "Export every event of this tool call (from diagnostics_mark or a logged event)")
                        .AddBoolean("includeScreenshots", "Also include the saved error screenshots (default: false)")
                        .Build()),
                InArgs => DiagnosticsExportAsync(InArgs));

            DiagnosticToolRunner.Register(InRegistry,
                new McpToolDefinition(
                    "diagnostics_flush",
                    "[Extended diagnostics] Wait until the queued diagnostic events have been written to the JSONL file, so the log can be read from outside " +
                    "Visual Studio. Normal tool calls never flush synchronously; this tool exists for verification scripts. Returns text { flushed, " +
                    "queueLengthBefore, elapsedMs, currentLogFile }; flushed=false means the queue was still not empty when the timeout elapsed.",
                    SchemaBuilder.Create()
                        .AddInteger("timeoutMs", "Maximum time to wait in milliseconds (default: 2000, max: 10000)")
                        .Build()),
                InArgs => DiagnosticsFlushAsync(InArgs));
        }

        /// <summary>diagnostics_get_status の本体。診断基盤の現在値をそのまま返す（推測しない）。</summary>
        /// <param name="InArgs">ツール引数（未使用）。</param>
        /// <returns>text（enabled / level / sessionId / … ）。</returns>
        private static Task<McpToolResult> DiagnosticsGetStatusAsync(JObject InArgs)
        {
            if (!DiagnosticHub.IsInitialized)
            {
                return Task.FromResult(McpToolResult.Error("The Extended diagnostic infrastructure is not initialized in this Visual Studio instance."));
            }

            DiagnosticSettings TheSettings = DiagnosticHub.Settings;
            DiagnosticWriter TheWriter = DiagnosticHub.Writer;
            return Task.FromResult(McpToolResult.Success(new
            {
                enabled = TheSettings.IsEnabled && DiagnosticHub.CurrentLevel != DiagnosticLevel.Off,
                level = DiagnosticConstants.ToText(DiagnosticHub.CurrentLevel),
                sessionId = DiagnosticHub.Session.SessionId,
                currentLogFile = TheWriter == null ? null : TheWriter.CurrentFilePath,
                logDirectory = DiagnosticWriter.LogFolderPath,
                configFile = DiagnosticSettings.GetConfigFilePath(),
                queueLength = TheWriter == null ? 0 : TheWriter.QueueLength,
                droppedEventCount = DiagnosticHub.Session.DroppedEventCount,
                writerHealthy = TheWriter != null && TheWriter.IsHealthy,
                outputPaneAvailable = DiagnosticHub.Pane != null && DiagnosticHub.Pane.IsAvailable,
                retentionDays = TheSettings.RetentionDays,
                maxFileSizeMb = TheSettings.MaxFileSizeMb,
                maxSessionFiles = TheSettings.MaxSessionFiles,
                writeJsonl = TheSettings.IsJsonlWritten,
                writeOutputPane = TheSettings.IsOutputPaneWritten,
                errorDetailDump = TheSettings.IsErrorDetailDumpEnabled,
                captureScreenshotOnError = TheSettings.IsScreenshotCapturedOnError,
                includeUiText = TheSettings.IsUiTextIncluded,
                includeFilePaths = TheSettings.IsFilePathsIncluded,
                maxStringLength = TheSettings.MaxStringLength,
                sequence = DiagnosticHub.Session.CurrentSequence,
                schemaVersion = DiagnosticConstants.SCHEMA_VERSION,
                diagnosticsVersion = DiagnosticConstants.DIAGNOSTICS_VERSION,
            }));
        }

        /// <summary>diagnostics_set_level の本体。水準を変更し、persist=true のときだけ設定ファイルへ書き戻す。</summary>
        /// <param name="InArgs">ツール引数（level / persist）。</param>
        /// <returns>text（previousLevel / level / persisted / configFile）、またはエラー。</returns>
        private static Task<McpToolResult> DiagnosticsSetLevelAsync(JObject InArgs)
        {
            if (!DiagnosticHub.IsInitialized)
            {
                return Task.FromResult(McpToolResult.Error("The Extended diagnostic infrastructure is not initialized in this Visual Studio instance."));
            }

            string TheLevelText = InArgs.Value<string>("level");
            if (string.IsNullOrEmpty(TheLevelText))
            {
                return Task.FromResult(McpToolResult.Error("Parameter 'level' is required (off, error, warning, info, verbose, trace)"));
            }
            DiagnosticLevel TheLevel = DiagnosticConstants.ParseLevel(TheLevelText, DiagnosticLevel.Off);
            if (TheLevel == DiagnosticLevel.Off && !string.Equals(TheLevelText.Trim(), "off", StringComparison.OrdinalIgnoreCase))
            {
                return Task.FromResult(McpToolResult.Error($"Unknown level: '{TheLevelText}'. Expected one of: off, error, warning, info, verbose, trace"));
            }

            bool IsPersistRequested = InArgs.Value<bool?>("persist") ?? false;
            bool IsPersisted = DiagnosticHub.SetLevel(TheLevel, IsPersistRequested, out DiagnosticLevel ThePreviousLevel);

            return Task.FromResult(McpToolResult.Success(new
            {
                previousLevel = DiagnosticConstants.ToText(ThePreviousLevel),
                level = DiagnosticConstants.ToText(TheLevel),
                persisted = IsPersisted,
                configFile = DiagnosticSettings.GetConfigFilePath(),
            }));
        }

        /// <summary>diagnostics_mark の本体。「今問題が起きた」印を 1 件記録し、後で export できる ID を返す。</summary>
        /// <param name="InArgs">ツール引数（message）。</param>
        /// <returns>text（markerId / timestampUtc / sessionId / correlationId / sequence / recorded）。</returns>
        private static Task<McpToolResult> DiagnosticsMarkAsync(JObject InArgs)
        {
            if (!DiagnosticHub.IsInitialized)
            {
                return Task.FromResult(McpToolResult.Error("The Extended diagnostic infrastructure is not initialized in this Visual Studio instance."));
            }

            string TheMessage = InArgs.Value<string>("message") ?? string.Empty;
            if (TheMessage.Length > _MAX_MARKER_MESSAGE_LENGTH)
            {
                TheMessage = TheMessage.Substring(0, _MAX_MARKER_MESSAGE_LENGTH);
            }
            string TheSanitizedMessage = DiagnosticSanitizer.SanitizeText(TheMessage, DiagnosticHub.Settings);
            string TheMarkerId = "m-" + Guid.NewGuid().ToString("N").Substring(0, 8);
            string TheTimestampUtc = DateTime.UtcNow.ToString("o");
            int TheMessageLength = TheMessage.Length;

            // marker の sequence は「この marker が何番の event になったか」なので、emit が払い出した値をそのまま返す
            long TheSequence = DiagnosticHub.EmitCore(DiagnosticLevel.Info, DiagnosticCategory.DIAGNOSTICS, "diagnostics.mark",
                null, null, null, null, TheSanitizedMessage, InData =>
                {
                    InData["markerId"] = TheMarkerId;
                    InData["messageLength"] = TheMessageLength;
                }, null);

            DiagnosticScope TheScope = DiagnosticScope.Current;
            return Task.FromResult(McpToolResult.Success(new
            {
                markerId = TheMarkerId,
                timestampUtc = TheTimestampUtc,
                sessionId = DiagnosticHub.Session.SessionId,
                correlationId = TheScope == null ? DiagnosticConstants.NO_CORRELATION_ID : TheScope.CorrelationId,
                sequence = TheSequence > 0 ? TheSequence : DiagnosticHub.Session.CurrentSequence,
                recorded = TheSequence > 0,
            }));
        }

        /// <summary>diagnostics_export の本体。ZIP の作成は背景 Task で行い、開始と完了を記録する。</summary>
        /// <param name="InArgs">ツール引数（minutes / sessionId / correlationId / includeScreenshots）。</param>
        /// <returns>text（exportId / zipPath / 件数など）、またはエラー。</returns>
        private static async Task<McpToolResult> DiagnosticsExportAsync(JObject InArgs)
        {
            if (!DiagnosticHub.IsInitialized)
            {
                return McpToolResult.Error("The Extended diagnostic infrastructure is not initialized in this Visual Studio instance.");
            }

            int TheMinutes = InArgs.Value<int?>("minutes") ?? _DEFAULT_EXPORT_MINUTES;
            if (TheMinutes < 1 || TheMinutes > _MAX_EXPORT_MINUTES)
            {
                return McpToolResult.Error($"Parameter 'minutes' must be between 1 and {_MAX_EXPORT_MINUTES}");
            }
            string TheSessionId = InArgs.Value<string>("sessionId");
            string TheCorrelationId = InArgs.Value<string>("correlationId");
            bool IsScreenshotsIncluded = InArgs.Value<bool?>("includeScreenshots") ?? false;

            DiagnosticHub.Emit(DiagnosticLevel.Info, DiagnosticCategory.DIAGNOSTICS, "diagnostics.export", "export started", InData =>
            {
                InData["minutes"] = TheMinutes;
                InData["hasSessionId"] = !string.IsNullOrEmpty(TheSessionId);
                InData["hasCorrelationId"] = !string.IsNullOrEmpty(TheCorrelationId);
                InData["includeScreenshots"] = IsScreenshotsIncluded;
            });

            Stopwatch TheStopwatch = Stopwatch.StartNew();
            try
            {
                JObject TheResult = await Task.Run(() => DiagnosticExport.ExportAsync(TheMinutes, TheSessionId, TheCorrelationId, IsScreenshotsIncluded));
                long TheElapsedMs = TheStopwatch.ElapsedMilliseconds;
                DiagnosticHub.EmitCore(DiagnosticLevel.Info, DiagnosticCategory.DIAGNOSTICS, "diagnostics.export", null,
                    null, TheElapsedMs, "success", "export completed", InData =>
                    {
                        InData["exportId"] = TheResult.Value<string>("exportId");
                        InData["eventCount"] = TheResult.Value<int>("eventCount");
                        InData["bytes"] = TheResult.Value<long>("bytes");
                    }, null);
                return McpToolResult.Success(TheResult.ToString(Formatting.Indented));
            }
            catch (Exception TheException)
            {
                return McpToolResult.Error($"diagnostics_export failed: {TheException.Message}");
            }
        }

        /// <summary>diagnostics_flush の本体。キューが空になるまで上限つきで待ってからファイルを Flush する。</summary>
        /// <param name="InArgs">ツール引数（timeoutMs）。</param>
        /// <returns>text（flushed / queueLengthBefore / elapsedMs / currentLogFile）。</returns>
        private static async Task<McpToolResult> DiagnosticsFlushAsync(JObject InArgs)
        {
            if (!DiagnosticHub.IsInitialized || DiagnosticHub.Writer == null)
            {
                return McpToolResult.Error("The Extended diagnostic infrastructure is not initialized in this Visual Studio instance.");
            }

            int TheTimeoutMs = InArgs.Value<int?>("timeoutMs") ?? _DEFAULT_FLUSH_TIMEOUT_MS;
            if (TheTimeoutMs < 1)
            {
                TheTimeoutMs = 1;
            }
            if (TheTimeoutMs > _MAX_FLUSH_TIMEOUT_MS)
            {
                TheTimeoutMs = _MAX_FLUSH_TIMEOUT_MS;
            }

            DiagnosticWriter TheWriter = DiagnosticHub.Writer;
            Stopwatch TheStopwatch = Stopwatch.StartNew();
            int TheQueueLengthBefore = 0;
            bool IsFlushed = await Task.Run(() => TheWriter.Flush(TheTimeoutMs, out TheQueueLengthBefore));

            return McpToolResult.Success(new
            {
                flushed = IsFlushed,
                queueLengthBefore = TheQueueLengthBefore,
                elapsedMs = TheStopwatch.ElapsedMilliseconds,
                currentLogFile = TheWriter.CurrentFilePath,
            });
        }
    }
}
