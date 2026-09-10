using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using static VsMcp.Extension.Tools.NativeMethods;

namespace VsMcp.Extension.Tools
{
    /// <summary>
    /// Extended (Phase 10): 問題発生時のログ束を 1 つの ZIP にまとめる（指示書 §32・§33）。
    /// manifest.json / events.jsonl / environment.json / README.txt（指定時は screenshots\）を含み、
    /// ユーザー名・PC 名・リポジトリパスは既定で入れない。JSON として読めない行（強制終了で壊れた最終行）は
    /// 数えるだけで捨てる。
    /// </summary>
    internal static class DiagnosticExport
    {
        /// <summary>manifest に載せる correlationId の最大数。</summary>
        private const int _MAX_CORRELATION_IDS = 1000;

        /// <summary>エクスポート先のサブフォルダー名。</summary>
        private const string _EXPORTS_FOLDER_NAME = "Exports";

        /// <summary>ZIP へ入れる README の本文。</summary>
        private const string _README_TEXT =
            "Unofficial-VS-MCP-Extended diagnostics export\r\n"
            + "\r\n"
            + "このアーカイブは Visual Studio 拡張 Unofficial-VS-MCP-Extended の診断ログです。\r\n"
            + "\r\n"
            + "- manifest.json    : このエクスポートの範囲（期間 / セッション / 件数）\r\n"
            + "- events.jsonl     : 診断イベント本体。1 行 1 JSON（JSON Lines）\r\n"
            + "- environment.json : 実行環境の要約（拡張 / Visual Studio / OS / モニター）\r\n"
            + "- screenshots\\     : includeScreenshots=true を指定した場合のみ\r\n"
            + "\r\n"
            + "プライバシー方針:\r\n"
            + "- ユーザー名・PC 名・リポジトリのパスは含めません。\r\n"
            + "- キー入力の本文・ファイル名・UI のテキストは既定では記録されず、長さや有無だけが残ります。\r\n"
            + "- password / token などのキーに対応する値は '***' に置き換えています。\r\n"
            + "- includeUiText / includeFilePaths を有効にしていた場合は、その期間の UI テキストやパスが含まれます。\r\n"
            + "\r\n"
            + "events.jsonl は追記型のログをそのまま抜き出したものです。解析側は読めない行を無視してください。\r\n";

        /// <summary>エクスポートフォルダーの絶対パス。</summary>
        public static string ExportFolderPath { get { return DiagnosticSettings.GetFolder(_EXPORTS_FOLDER_NAME); } }

        /// <summary>
        /// 条件に合う診断イベントを集めて ZIP を作る。
        /// ログは行単位で ZIP へ流し込み、全行をメモリへ載せない。DTE 由来の値は UI スレッドが要るので先に読み切ってから、
        /// ファイル I/O だけをスレッドプールで実行する。
        /// </summary>
        /// <param name="InMinutes">現在から遡る分数（sessionId / correlationId 指定時は無視される）。</param>
        /// <param name="InSessionId">対象のセッション ID。指定しない場合は null。</param>
        /// <param name="InCorrelationId">対象の相関 ID。指定しない場合は null。</param>
        /// <param name="InIsScreenshotsIncluded">スクリーンショットを同梱するか。</param>
        /// <returns>エクスポート結果（exportId / zipPath / 件数など）。</returns>
        public static async Task<JObject> ExportAsync(int InMinutes, string InSessionId, string InCorrelationId, bool InIsScreenshotsIncluded)
        {
            string TheExportId = "e-" + Guid.NewGuid().ToString("N").Substring(0, 8);
            DateTime TheCutoffUtc = DateTime.UtcNow.AddMinutes(-InMinutes);

            // DTE は UI スレッドでしか触れないため、ZIP 作成を背景へ回す前にここで環境情報を作り切る
            JObject TheEnvironment = await BuildEnvironmentAsync();

            string TheZipPath = Path.Combine(ExportFolderPath,
                "diagnostics-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-" + TheExportId + ".zip");
            ExportStatistics TheStatistics = await Task.Run(() => WriteArchive(TheZipPath, TheExportId, InMinutes, InSessionId,
                InCorrelationId, TheCutoffUtc, InIsScreenshotsIncluded, TheEnvironment));

            return new JObject
            {
                ["exportId"] = TheExportId,
                ["zipPath"] = TheZipPath,
                ["eventCount"] = TheStatistics.EventCount,
                ["errorCount"] = TheStatistics.ErrorCount,
                ["warningCount"] = TheStatistics.WarningCount,
                ["startUtc"] = TheStatistics.StartUtcText,
                ["endUtc"] = TheStatistics.EndUtcText,
                ["sessionIds"] = new JArray(TheStatistics.SessionIds.ToArray()),
                ["includedScreenshots"] = TheStatistics.ScreenshotCount,
                ["skippedLines"] = TheStatistics.SkippedLineCount,
                ["bytes"] = TheStatistics.Bytes,
            };
        }

        /// <summary>ZIP を書き出す（スレッドプール上で実行すること）。</summary>
        /// <param name="InZipPath">出力先の ZIP パス。</param>
        /// <param name="InExportId">このエクスポートの ID。</param>
        /// <param name="InMinutes">要求された遡り分数（manifest に載せる）。</param>
        /// <param name="InSessionId">対象のセッション ID。指定しない場合は null。</param>
        /// <param name="InCorrelationId">対象の相関 ID。指定しない場合は null。</param>
        /// <param name="InCutoffUtc">これ以降の event を対象にする時刻。</param>
        /// <param name="InIsScreenshotsIncluded">スクリーンショットを同梱するか。</param>
        /// <param name="InEnvironment">environment.json の内容（呼び出し前に組み立て済み）。</param>
        /// <returns>集計結果。</returns>
        private static ExportStatistics WriteArchive(string InZipPath, string InExportId, int InMinutes, string InSessionId,
            string InCorrelationId, DateTime InCutoffUtc, bool InIsScreenshotsIncluded, JObject InEnvironment)
        {
            ExportStatistics TheStatistics = new ExportStatistics();
            List<string> TheScreenshotPaths = InIsScreenshotsIncluded
                ? CollectScreenshots(InCorrelationId, InCutoffUtc)
                : new List<string>();

            Directory.CreateDirectory(ExportFolderPath);
            using (FileStream TheFileStream = new FileStream(InZipPath, FileMode.Create, FileAccess.Write, FileShare.None))
            using (ZipArchive TheArchive = new ZipArchive(TheFileStream, ZipArchiveMode.Create))
            {
                ZipArchiveEntry TheEventsEntry = TheArchive.CreateEntry("events.jsonl", CompressionLevel.Optimal);
                using (Stream TheStream = TheEventsEntry.Open())
                using (StreamWriter TheWriter = new StreamWriter(TheStream, new UTF8Encoding(false)))
                {
                    foreach (string TheFilePath in EnumerateLogFiles(InSessionId, InCorrelationId, InCutoffUtc))
                    {
                        CopyMatchingLines(TheFilePath, InSessionId, InCorrelationId, InCutoffUtc, TheWriter, TheStatistics);
                    }
                }

                TheStatistics.ScreenshotCount = WriteScreenshots(TheArchive, TheScreenshotPaths);
                // manifest は集計が終わってから書く（ZIP のエントリ順は読み手に影響しない）
                WriteTextEntry(TheArchive, "manifest.json",
                    BuildManifest(InExportId, InMinutes, InSessionId, InCorrelationId, TheStatistics).ToString(Formatting.Indented));
                WriteTextEntry(TheArchive, "environment.json", InEnvironment.ToString(Formatting.Indented));
                WriteTextEntry(TheArchive, "README.txt", _README_TEXT);
            }

            TheStatistics.Bytes = ReadFileLength(InZipPath);
            return TheStatistics;
        }

        /// <summary>manifest.json の内容を組み立てる。</summary>
        /// <param name="InExportId">このエクスポートの ID。</param>
        /// <param name="InMinutes">要求された遡り分数。</param>
        /// <param name="InSessionId">要求されたセッション ID。</param>
        /// <param name="InCorrelationId">要求された相関 ID。</param>
        /// <param name="InStatistics">集計結果。</param>
        /// <returns>manifest の JSON。</returns>
        private static JObject BuildManifest(string InExportId, int InMinutes, string InSessionId, string InCorrelationId,
            ExportStatistics InStatistics)
        {
            return new JObject
            {
                ["exportId"] = InExportId,
                ["createdUtc"] = DateTime.UtcNow.ToString("o"),
                ["sessionIds"] = new JArray(InStatistics.SessionIds.ToArray()),
                ["correlationIds"] = new JArray(InStatistics.CorrelationIds.ToArray()),
                ["startUtc"] = InStatistics.StartUtcText,
                ["endUtc"] = InStatistics.EndUtcText,
                ["eventCount"] = InStatistics.EventCount,
                ["errorCount"] = InStatistics.ErrorCount,
                ["warningCount"] = InStatistics.WarningCount,
                ["includedScreenshots"] = InStatistics.ScreenshotCount,
                ["diagnosticsVersion"] = DiagnosticConstants.DIAGNOSTICS_VERSION,
                ["schemaVersion"] = DiagnosticConstants.SCHEMA_VERSION,
                ["skippedLines"] = InStatistics.SkippedLineCount,
                ["requestedMinutes"] = InMinutes,
                ["requestedSessionId"] = InSessionId,
                ["requestedCorrelationId"] = InCorrelationId,
            };
        }

        /// <summary>スクリーンショットを ZIP へ入れる。</summary>
        /// <param name="InArchive">対象のアーカイブ。</param>
        /// <param name="InScreenshotPaths">同梱するファイルの絶対パス。</param>
        /// <returns>実際に入れた件数。</returns>
        private static int WriteScreenshots(ZipArchive InArchive, List<string> InScreenshotPaths)
        {
            int TheCount = 0;
            foreach (string TheScreenshotPath in InScreenshotPaths)
            {
                try
                {
                    ZipArchiveEntry TheEntry = InArchive.CreateEntry("screenshots/" + Path.GetFileName(TheScreenshotPath), CompressionLevel.Optimal);
                    using (Stream TheEntryStream = TheEntry.Open())
                    using (FileStream TheSourceStream = new FileStream(TheScreenshotPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                    {
                        TheSourceStream.CopyTo(TheEntryStream);
                    }
                    TheCount++;
                }
                catch (Exception)
                {
                    // 読めないスクリーンショットは飛ばす
                }
            }
            return TheCount;
        }

        /// <summary>できあがった ZIP のバイト数を読む。読めなければ 0。</summary>
        /// <param name="InZipPath">ZIP の絶対パス。</param>
        /// <returns>バイト数。</returns>
        private static long ReadFileLength(string InZipPath)
        {
            try
            {
                return new FileInfo(InZipPath).Length;
            }
            catch (Exception)
            {
                // サイズが読めなくてもエクスポート自体は成功として返す
                return 0;
            }
        }

        /// <summary>ZIP へテキストのエントリを 1 つ書く。</summary>
        /// <param name="InArchive">対象のアーカイブ。</param>
        /// <param name="InEntryName">エントリ名。</param>
        /// <param name="InText">本文。</param>
        private static void WriteTextEntry(ZipArchive InArchive, string InEntryName, string InText)
        {
            ZipArchiveEntry TheEntry = InArchive.CreateEntry(InEntryName, CompressionLevel.Optimal);
            using (Stream TheStream = TheEntry.Open())
            using (StreamWriter TheWriter = new StreamWriter(TheStream, new UTF8Encoding(false)))
            {
                TheWriter.Write(InText);
            }
        }

        /// <summary>
        /// Logs 配下の JSONL を古い順に列挙する。sessionId / correlationId の指定が無いときは、
        /// 最終更新が対象期間より古いファイルを開かずに飛ばす。
        /// </summary>
        /// <param name="InSessionId">対象のセッション ID。指定しない場合は null。</param>
        /// <param name="InCorrelationId">対象の相関 ID。指定しない場合は null。</param>
        /// <param name="InCutoffUtc">これ以降の event を対象にする時刻。</param>
        /// <returns>ログファイルの絶対パス。</returns>
        private static IEnumerable<string> EnumerateLogFiles(string InSessionId, string InCorrelationId, DateTime InCutoffUtc)
        {
            string TheRoot = DiagnosticWriter.LogFolderPath;
            if (!Directory.Exists(TheRoot))
            {
                return new List<string>();
            }
            try
            {
                bool IsTimeFiltered = string.IsNullOrEmpty(InSessionId) && string.IsNullOrEmpty(InCorrelationId);
                return Directory.GetFiles(TheRoot, "*.jsonl", SearchOption.AllDirectories)
                    .Select(ThePath => new FileInfo(ThePath))
                    .Where(TheFile => !IsTimeFiltered || TheFile.LastWriteTimeUtc >= InCutoffUtc)
                    .OrderBy(TheFile => TheFile.LastWriteTimeUtc)
                    .Select(TheFile => TheFile.FullName)
                    .ToList();
            }
            catch (Exception)
            {
                return new List<string>();
            }
        }

        /// <summary>
        /// 書き込み中のファイルも読めるように共有して 1 行ずつ読み、条件に合う行だけを events.jsonl へ直接書く。
        /// </summary>
        /// <param name="InFilePath">読むファイルの絶対パス。</param>
        /// <param name="InSessionId">対象のセッション ID。指定しない場合は null。</param>
        /// <param name="InCorrelationId">対象の相関 ID。指定しない場合は null。</param>
        /// <param name="InCutoffUtc">これ以降の event を対象にする時刻。</param>
        /// <param name="InWriter">events.jsonl の書き出し先。</param>
        /// <param name="InOutStatistics">集計の書き込み先。</param>
        private static void CopyMatchingLines(string InFilePath, string InSessionId, string InCorrelationId, DateTime InCutoffUtc,
            StreamWriter InWriter, ExportStatistics InOutStatistics)
        {
            try
            {
                using (FileStream TheStream = new FileStream(InFilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                using (StreamReader TheReader = new StreamReader(TheStream, new UTF8Encoding(false)))
                {
                    string TheLine;
                    while ((TheLine = TheReader.ReadLine()) != null)
                    {
                        if (TheLine.Length == 0)
                        {
                            continue;
                        }
                        JObject TheEvent = TryParse(TheLine);
                        if (TheEvent == null)
                        {
                            InOutStatistics.SkippedLineCount++;
                            continue;
                        }
                        if (!IsMatch(TheEvent, InSessionId, InCorrelationId, InCutoffUtc))
                        {
                            continue;
                        }
                        InWriter.Write(TheLine);
                        InWriter.Write('\n');
                        InOutStatistics.Add(TheEvent, TryReadTimestamp(TheEvent));
                    }
                }
            }
            catch (Exception)
            {
                // ロック等で読めないファイルは飛ばす
            }
        }

        /// <summary>1 行を JSON として読む。読めなければ null。</summary>
        /// <param name="InLine">JSONL の 1 行。</param>
        /// <returns>読めた JSON。読めなければ null。</returns>
        private static JObject TryParse(string InLine)
        {
            try
            {
                return JObject.Parse(InLine);
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>エクスポート条件に合う event か判定する。</summary>
        /// <param name="InEvent">判定する event。</param>
        /// <param name="InSessionId">対象のセッション ID。null なら条件にしない。</param>
        /// <param name="InCorrelationId">対象の相関 ID。null なら条件にしない。</param>
        /// <param name="InCutoffUtc">これ以降の event を対象にする時刻。</param>
        /// <returns>対象なら true。</returns>
        private static bool IsMatch(JObject InEvent, string InSessionId, string InCorrelationId, DateTime InCutoffUtc)
        {
            if (!string.IsNullOrEmpty(InSessionId))
            {
                return string.Equals(InEvent.Value<string>("sessionId"), InSessionId, StringComparison.Ordinal);
            }
            if (!string.IsNullOrEmpty(InCorrelationId))
            {
                return string.Equals(InEvent.Value<string>("correlationId"), InCorrelationId, StringComparison.Ordinal);
            }
            DateTime? TheTimestamp = TryReadTimestamp(InEvent);
            return TheTimestamp.HasValue && TheTimestamp.Value >= InCutoffUtc;
        }

        /// <summary>event の timestampUtc を読む。読めなければ null。</summary>
        /// <param name="InEvent">対象の event。</param>
        /// <returns>UTC の時刻。読めなければ null。</returns>
        private static DateTime? TryReadTimestamp(JObject InEvent)
        {
            string TheText = InEvent.Value<string>("timestampUtc");
            if (string.IsNullOrEmpty(TheText))
            {
                return null;
            }
            if (DateTime.TryParse(TheText, System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal,
                    out DateTime TheValue))
            {
                return TheValue;
            }
            return null;
        }

        /// <summary>同梱するスクリーンショットを選ぶ（相関 ID 指定時はその分、それ以外は期間内のもの）。</summary>
        /// <param name="InCorrelationId">対象の相関 ID。null なら期間で選ぶ。</param>
        /// <param name="InCutoffUtc">これ以降に作られたものを対象にする時刻。</param>
        /// <returns>同梱するファイルの絶対パス。</returns>
        private static List<string> CollectScreenshots(string InCorrelationId, DateTime InCutoffUtc)
        {
            List<string> TheResult = new List<string>();
            try
            {
                string TheFolder = DiagnosticSettings.GetFolder(Path.Combine("Diagnostics", "screenshots"));
                if (!Directory.Exists(TheFolder))
                {
                    return TheResult;
                }
                foreach (string ThePath in Directory.GetFiles(TheFolder, "*.png"))
                {
                    if (!string.IsNullOrEmpty(InCorrelationId))
                    {
                        if (Path.GetFileName(ThePath).IndexOf(InCorrelationId, StringComparison.Ordinal) >= 0)
                        {
                            TheResult.Add(ThePath);
                        }
                        continue;
                    }
                    if (new FileInfo(ThePath).LastWriteTimeUtc >= InCutoffUtc)
                    {
                        TheResult.Add(ThePath);
                    }
                }
            }
            catch (Exception)
            {
                // 読めない場合は同梱しない
            }
            return TheResult;
        }

        /// <summary>environment.json の内容を組み立てる（ユーザー名・PC 名・パスは入れない）。</summary>
        /// <returns>環境情報。</returns>
        private static async Task<JObject> BuildEnvironmentAsync()
        {
            JObject TheEnvironment = new JObject
            {
                ["extensionVersion"] = DiagnosticHub.GetExtensionVersion(),
                ["assemblyVersion"] = typeof(DiagnosticExport).Assembly.GetName().Version.ToString(),
                ["processArchitecture"] = Environment.Is64BitProcess ? "x64" : "x86",
                ["osVersion"] = Environment.OSVersion.VersionString,
                ["is64BitOperatingSystem"] = Environment.Is64BitOperatingSystem,
                ["dotnetRuntime"] = ".NET Framework " + RuntimeEnvironment.GetSystemVersion(),
                ["clrVersion"] = Environment.Version.ToString(),
                ["mcpServerVersion"] = VsMcp.Shared.Protocol.McpConstants.ServerVersion,
                ["diagnosticsVersion"] = DiagnosticConstants.DIAGNOSTICS_VERSION,
                ["schemaVersion"] = DiagnosticConstants.SCHEMA_VERSION,
                ["toolCount"] = DiagnosticHub.Registry == null ? 0 : DiagnosticHub.Registry.GetAllDefinitions().Count,
                ["dpi"] = ReadSystemDpi(),
            };

            JArray TheMonitors = DescribeMonitors();
            TheEnvironment["monitorCount"] = TheMonitors.Count;
            TheEnvironment["monitors"] = TheMonitors;

            try
            {
                if (DiagnosticHub.Accessor != null)
                {
                    // DTE のプロパティは UI スレッドでのみ読む（既存 UiWindowTools.GetDebuggedProcessIdsAsync と同じ流儀）
                    string[] TheVisualStudioInfo = await DiagnosticHub.Accessor.RunOnUIThreadAsync(
                        () => ReadVisualStudioInfo(DiagnosticHub.Accessor));
                    if (TheVisualStudioInfo != null)
                    {
                        TheEnvironment["vsVersion"] = TheVisualStudioInfo[0];
                        TheEnvironment["vsEdition"] = TheVisualStudioInfo[1];
                    }
                }
            }
            catch (Exception)
            {
                // Visual Studio の情報が取れなくてもエクスポートは続ける
            }
            return TheEnvironment;
        }

        /// <summary>UI スレッド上で DTE のバージョンとエディションを読む。</summary>
        /// <param name="InAccessor">DTE / UI スレッドアクセサ。</param>
        /// <returns>[0]=Version、[1]=Edition。DTE が取れない場合は null。</returns>
        private static string[] ReadVisualStudioInfo(VsMcp.Extension.Services.VsServiceAccessor InAccessor)
        {
            EnvDTE80.DTE2 TheDte = Microsoft.VisualStudio.Shell.ThreadHelper.JoinableTaskFactory
                .Run(() => InAccessor.GetDteAsync());
            if (TheDte == null)
            {
                return null;
            }
            return new string[] { TheDte.Version, TheDte.Edition };
        }

        /// <summary>システム DPI を読む（プライマリモニター相当）。</summary>
        /// <returns>DPI 値。取得できない場合は 96。</returns>
        private static int ReadSystemDpi()
        {
            try
            {
                using (Graphics TheGraphics = Graphics.FromHwnd(IntPtr.Zero))
                {
                    return (int)TheGraphics.DpiX;
                }
            }
            catch (Exception)
            {
                return 96;
            }
        }

        /// <summary>接続されているモニターの一覧（デバイス名 / 矩形 / 作業領域 / プライマリ）を組み立てる。</summary>
        /// <returns>モニターの配列。取得できない場合は空。</returns>
        private static JArray DescribeMonitors()
        {
            JArray TheMonitors = new JArray();
            try
            {
                EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr InMonitor, IntPtr InDeviceContext, ref RECT InRect, IntPtr InData) =>
                {
                    try
                    {
                        MONITORINFOEX TheInfo = new MONITORINFOEX();
                        TheInfo.cbSize = Marshal.SizeOf(typeof(MONITORINFOEX));
                        if (GetMonitorInfoW(InMonitor, ref TheInfo))
                        {
                            TheMonitors.Add(new JObject
                            {
                                ["deviceName"] = TheInfo.szDevice,
                                ["bounds"] = FormatRect(TheInfo.rcMonitor),
                                ["workArea"] = FormatRect(TheInfo.rcWork),
                                ["isPrimary"] = (TheInfo.dwFlags & MONITORINFOF_PRIMARY) != 0,
                            });
                        }
                    }
                    catch (Exception)
                    {
                        // 1 台読めなくても列挙は続ける
                    }
                    return true;
                }, IntPtr.Zero);
            }
            catch (Exception)
            {
                // 列挙できない環境では空のままにする
            }
            return TheMonitors;
        }

        /// <summary>RECT を "x,y,width,height" にする。</summary>
        /// <param name="InRect">対象の矩形。</param>
        /// <returns>矩形文字列。</returns>
        private static string FormatRect(in RECT InRect)
        {
            return $"{InRect.Left},{InRect.Top},{InRect.Right - InRect.Left},{InRect.Bottom - InRect.Top}";
        }

        /// <summary>EnumDisplayMonitors のコールバック。</summary>
        /// <param name="InMonitor">モニターハンドル。</param>
        /// <param name="InDeviceContext">デバイスコンテキスト。</param>
        /// <param name="InRect">モニターの矩形。</param>
        /// <param name="InData">呼び出し側のデータ。</param>
        /// <returns>列挙を続けるなら true。</returns>
        private delegate bool MonitorEnumProc(IntPtr InMonitor, IntPtr InDeviceContext, ref RECT InRect, IntPtr InData);

        /// <summary>接続されているモニターを列挙する（NativeMethods を変更せずに済むよう、ここだけで宣言する）。</summary>
        /// <param name="hdc">デバイスコンテキスト。全モニターなら IntPtr.Zero。</param>
        /// <param name="lprcClip">対象矩形。全モニターなら IntPtr.Zero。</param>
        /// <param name="lpfnEnum">コールバック。</param>
        /// <param name="dwData">コールバックへ渡す値。</param>
        /// <returns>成功したら true。</returns>
        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr lprcClip, MonitorEnumProc lpfnEnum, IntPtr dwData);

        /// <summary>
        /// 行を流し込みながら数える集計値。行そのものは保持せず、manifest と戻り値に必要な数だけを持つ。
        /// </summary>
        private sealed class ExportStatistics
        {
            /// <summary>含めた event に現れたセッション ID。</summary>
            public HashSet<string> SessionIds { get; private set; }

            /// <summary>含めた event に現れた相関 ID（最大 1000 件）。</summary>
            public HashSet<string> CorrelationIds { get; private set; }

            /// <summary>含めた event の件数。</summary>
            public int EventCount { get; private set; }

            /// <summary>そのうち error の件数。</summary>
            public int ErrorCount { get; private set; }

            /// <summary>そのうち warning の件数。</summary>
            public int WarningCount { get; private set; }

            /// <summary>JSON として読めず捨てた行の数。</summary>
            public int SkippedLineCount { get; set; }

            /// <summary>同梱したスクリーンショットの件数。</summary>
            public int ScreenshotCount { get; set; }

            /// <summary>できあがった ZIP のバイト数。</summary>
            public long Bytes { get; set; }

            /// <summary>含めた event の最も古い時刻。</summary>
            private DateTime? _StartUtc;

            /// <summary>含めた event の最も新しい時刻。</summary>
            private DateTime? _EndUtc;

            /// <summary>最も古い時刻の文字列表現。1 件も無ければ null。</summary>
            public string StartUtcText { get { return _StartUtc.HasValue ? _StartUtc.Value.ToString("o") : null; } }

            /// <summary>最も新しい時刻の文字列表現。1 件も無ければ null。</summary>
            public string EndUtcText { get { return _EndUtc.HasValue ? _EndUtc.Value.ToString("o") : null; } }

            /// <summary>空の集計を作る。</summary>
            public ExportStatistics()
            {
                SessionIds = new HashSet<string>(StringComparer.Ordinal);
                CorrelationIds = new HashSet<string>(StringComparer.Ordinal);
            }

            /// <summary>ZIP へ含めた event 1 件を集計へ反映する。</summary>
            /// <param name="InEvent">含めた event。</param>
            /// <param name="InTimestampUtc">その event の時刻。読めなければ null。</param>
            public void Add(JObject InEvent, DateTime? InTimestampUtc)
            {
                EventCount++;
                string TheSessionId = InEvent.Value<string>("sessionId");
                if (TheSessionId != null)
                {
                    SessionIds.Add(TheSessionId);
                }
                string TheCorrelationId = InEvent.Value<string>("correlationId");
                if (TheCorrelationId != null && CorrelationIds.Count < _MAX_CORRELATION_IDS)
                {
                    CorrelationIds.Add(TheCorrelationId);
                }
                string TheLevel = InEvent.Value<string>("level");
                if (string.Equals(TheLevel, "error", StringComparison.Ordinal))
                {
                    ErrorCount++;
                }
                else if (string.Equals(TheLevel, "warning", StringComparison.Ordinal))
                {
                    WarningCount++;
                }

                if (!InTimestampUtc.HasValue)
                {
                    return;
                }
                if (!_StartUtc.HasValue || InTimestampUtc.Value < _StartUtc.Value)
                {
                    _StartUtc = InTimestampUtc.Value;
                }
                if (!_EndUtc.HasValue || InTimestampUtc.Value > _EndUtc.Value)
                {
                    _EndUtc = InTimestampUtc.Value;
                }
            }
        }
    }
}
