using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using Newtonsoft.Json.Linq;
using VsMcp.Extension.McpServer;
using VsMcp.Extension.Services;

namespace VsMcp.Extension.Tools
{
    /// <summary>
    /// Extended (Phase 10): 診断基盤の唯一の入口。設定・セッション・writer・Output pane を束ね、
    /// 計装点からは <see cref="Emit"/> だけを呼べばよいようにする。
    /// ここの public メソッドはすべて例外を握り潰す。診断の不調で Tool の結果や所要時間を変えないことを最優先にする
    /// （水準が Off または enabled=false のときは data の組み立てすら行わない）。
    /// </summary>
    internal static class DiagnosticHub
    {
        /// <summary>初期化・終了処理の排他。</summary>
        private static readonly object _InitializationLock = new object();

        /// <summary>Shutdown で writer と pane の終了待ちに使える合計時間（ミリ秒）。VS の終了を待たせない上限。</summary>
        private const int _SHUTDOWN_BUDGET_MS = 2000;

        /// <summary>現在の記録水準。diagnostics_set_level で変わる。</summary>
        private static volatile DiagnosticLevel _CurrentLevel = DiagnosticLevel.Off;

        /// <summary>診断が有効か（設定の enabled と初期化の成否をまとめたもの）。</summary>
        private static volatile bool _IsActive;

        /// <summary>診断設定。初期化前は null。</summary>
        public static DiagnosticSettings Settings { get; private set; }

        /// <summary>現在のセッション。初期化前は null。</summary>
        public static DiagnosticSession Session { get; private set; }

        /// <summary>JSONL writer。初期化前は null。</summary>
        public static DiagnosticWriter Writer { get; private set; }

        /// <summary>Output pane。初期化前は null。</summary>
        public static DiagnosticOutputPane Pane { get; private set; }

        /// <summary>DTE / UI スレッドアクセサ（error dump / export が使う）。</summary>
        public static VsServiceAccessor Accessor { get; private set; }

        /// <summary>Tool レジストリ（export の environment.json に載せる Tool 数のため）。</summary>
        public static McpToolRegistry Registry { get; private set; }

        /// <summary>現在の記録水準。</summary>
        public static DiagnosticLevel CurrentLevel { get { return _CurrentLevel; } }

        /// <summary>初期化済みか。</summary>
        public static bool IsInitialized { get { return Session != null; } }

        /// <summary>
        /// 診断基盤を初期化する。設定の読み込み・ディレクトリ作成・writer / pane の起動・session.start の記録までを行う。
        /// 失敗しても例外は投げず、診断を無効化して VS の起動を続行させる。
        /// </summary>
        /// <param name="InAccessor">DTE / UI スレッドアクセサ。</param>
        /// <param name="InRegistry">Tool レジストリ。</param>
        public static void Initialize(VsServiceAccessor InAccessor, McpToolRegistry InRegistry)
        {
            lock (_InitializationLock)
            {
                if (IsInitialized)
                {
                    return;
                }
                try
                {
                    Accessor = InAccessor;
                    Registry = InRegistry;
                    Settings = DiagnosticSettings.Load();
                    Session = new DiagnosticSession();
                    EnsureFolders();

                    Pane = new DiagnosticOutputPane(InAccessor);
                    if (Settings.IsOutputPaneWritten)
                    {
                        Pane.Start();
                    }

                    Writer = new DiagnosticWriter(Settings, Session, InLine => Pane.WriteLine(InLine));
                    Writer.Start();

                    _CurrentLevel = DiagnosticConstants.ParseLevel(Settings.Level, DiagnosticLevel.Info);
                    _IsActive = Settings.IsEnabled && _CurrentLevel != DiagnosticLevel.Off;

                    if (Settings.IsConfigInvalid)
                    {
                        Emit(DiagnosticLevel.Warning, DiagnosticCategory.DIAGNOSTICS, "diagnostics.config.invalid",
                            "diagnostics.json could not be used; safe defaults are in use", InData =>
                            {
                                // 壊れていた（parse）のか読めなかった（io）のかで対処が違うので、理由を分けて残す
                                InData["reason"] = Settings.ConfigFailureKind;
                                InData["quarantined"] = Settings.InvalidConfigPath != null;
                            });
                    }

                    Emit(DiagnosticLevel.Info, DiagnosticCategory.SESSION, "session.start", null, InData => FillEnvironmentSummary(InData));
                }
                catch (Exception)
                {
                    // 診断の初期化に失敗しても VS / MCP server は動かす
                    _IsActive = false;
                }
            }
        }

        /// <summary>
        /// 診断基盤を終了する。session.end を記録してから writer / pane を閉じる。
        /// 終了待ちは writer と pane で合計 2000 ms の予算を分け合い、VS の終了をこれ以上待たせない。
        /// </summary>
        public static void Shutdown()
        {
            lock (_InitializationLock)
            {
                try
                {
                    if (Session != null)
                    {
                        Emit(DiagnosticLevel.Info, DiagnosticCategory.SESSION, "session.end", null, InData =>
                        {
                            InData["uptimeMs"] = (long)(DateTime.UtcNow - Session.StartedUtc).TotalMilliseconds;
                            InData["eventCount"] = Session.CurrentSequence;
                            InData["droppedEventCount"] = Session.DroppedEventCount;
                        });
                    }
                    _IsActive = false;

                    Stopwatch TheStopwatch = Stopwatch.StartNew();
                    Writer?.Shutdown(_SHUTDOWN_BUDGET_MS);
                    int TheRemainingMs = (int)Math.Max(0L, _SHUTDOWN_BUDGET_MS - TheStopwatch.ElapsedMilliseconds);
                    Pane?.Shutdown(TheRemainingMs);
                }
                catch (Exception)
                {
                    // shutdown を長時間止めない・失敗させない
                }
            }
        }

        /// <summary>指定した水準の event を記録するか。計装点が data を組み立てる前に確認するために使う。</summary>
        /// <param name="InLevel">記録しようとしている水準。</param>
        /// <returns>記録するなら true。</returns>
        public static bool IsEnabled(DiagnosticLevel InLevel)
        {
            return _IsActive && InLevel != DiagnosticLevel.Off && InLevel <= _CurrentLevel;
        }

        /// <summary>現在のスコープ（あれば）の相関 ID と Tool 名で event を記録する。</summary>
        /// <param name="InLevel">重要度。</param>
        /// <param name="InCategory">カテゴリ。</param>
        /// <param name="InEventName">event 名。</param>
        /// <param name="InFill">data を組み立てるコールバック。null 可。</param>
        public static void Emit(DiagnosticLevel InLevel, string InCategory, string InEventName, Action<JObject> InFill)
        {
            EmitCore(InLevel, InCategory, InEventName, null, null, null, null, null, InFill, null);
        }

        /// <summary>現在のスコープ（あれば）の相関 ID と Tool 名で、短い説明を添えて event を記録する。</summary>
        /// <param name="InLevel">重要度。</param>
        /// <param name="InCategory">カテゴリ。</param>
        /// <param name="InEventName">event 名。</param>
        /// <param name="InMessage">短い説明（sanitize 済みであること）。</param>
        /// <param name="InFill">data を組み立てるコールバック。null 可。</param>
        public static void Emit(DiagnosticLevel InLevel, string InCategory, string InEventName, string InMessage, Action<JObject> InFill)
        {
            EmitCore(InLevel, InCategory, InEventName, null, null, null, null, InMessage, InFill, null);
        }

        /// <summary>例外を exception カテゴリの event として記録する。</summary>
        /// <param name="InLevel">重要度。</param>
        /// <param name="InEventName">event 名（"exception" / "exception.swallowed"）。</param>
        /// <param name="InException">対象の例外。</param>
        /// <param name="InFill">data を組み立てるコールバック。null 可。</param>
        public static void EmitException(DiagnosticLevel InLevel, string InEventName, Exception InException, Action<JObject> InFill)
        {
            if (!IsEnabled(InLevel))
            {
                return;
            }
            EmitCore(InLevel, DiagnosticCategory.EXCEPTION, InEventName, null, null, null, null, null, InFill,
                DiagnosticExceptionInfo.FromException(InException, Settings));
        }

        /// <summary>
        /// event を組み立ててキューへ入れる唯一の実装。水準に達していなければ即座に戻り、data の組み立ても行わない。
        /// どのような失敗でも例外を外へ出さない。
        /// </summary>
        /// <param name="InLevel">重要度。</param>
        /// <param name="InCategory">カテゴリ。</param>
        /// <param name="InEventName">event 名。</param>
        /// <param name="InToolName">Tool 名。null なら現在のスコープから取る。</param>
        /// <param name="InCorrelationId">相関 ID。null なら現在のスコープから取る。</param>
        /// <param name="InElapsedMs">所要時間（ミリ秒）。無ければ null。</param>
        /// <param name="InResult">結果の区分。無ければ null。</param>
        /// <param name="InMessage">短い説明。無ければ null。</param>
        /// <param name="InFill">data を組み立てるコールバック。null 可。</param>
        /// <param name="InException">例外情報。無ければ null。</param>
        /// <returns>この event へ払い出した sequence。記録しなかった場合と失敗した場合は 0。</returns>
        public static long EmitCore(DiagnosticLevel InLevel, string InCategory, string InEventName, string InToolName,
            string InCorrelationId, long? InElapsedMs, string InResult, string InMessage, Action<JObject> InFill,
            DiagnosticExceptionInfo InException)
        {
            if (!IsEnabled(InLevel))
            {
                return 0;
            }

            try
            {
                DiagnosticScope TheScope = DiagnosticScope.Current;
                JObject TheData = null;
                if (InFill != null)
                {
                    TheData = new JObject();
                    InFill(TheData);
                }

                long TheSequence = Session.NextSequence();
                DiagnosticEvent TheEvent = new DiagnosticEvent
                {
                    TimestampUtc = DateTime.UtcNow.ToString("o"),
                    SessionId = Session.SessionId,
                    CorrelationId = InCorrelationId ?? (TheScope != null ? TheScope.CorrelationId : DiagnosticConstants.NO_CORRELATION_ID),
                    Sequence = TheSequence,
                    Level = DiagnosticConstants.ToText(InLevel),
                    Category = InCategory,
                    EventName = InEventName,
                    Tool = InToolName ?? (TheScope != null ? TheScope.ToolName : null),
                    ElapsedMs = InElapsedMs,
                    Result = InResult,
                    Message = InMessage,
                    Data = TheData,
                    Exception = InException,
                };

                Writer?.Enqueue(TheEvent);
                if (Settings.IsOutputPaneWritten)
                {
                    Pane?.Enqueue(TheEvent);
                }
                return TheSequence;
            }
            catch (Exception)
            {
                // 診断の失敗は Tool へ伝播させない
                return 0;
            }
        }

        /// <summary>
        /// 記録水準を変更する。persist=true のときだけ設定ファイルへ書き戻す。
        /// 変更の記録は「高い方の水準」で出す（下げるときは適用前、上げるときは適用後）。
        /// off へ落とすときに適用後へ回すと、その event 自体が記録されずログが途切れるため。
        /// </summary>
        /// <param name="InLevel">新しい水準。</param>
        /// <param name="InIsPersisted">設定ファイルへ保存するか。</param>
        /// <param name="OutPreviousLevel">変更前の水準。</param>
        /// <returns>設定ファイルへ保存できたら true（persist=false のときは false）。</returns>
        public static bool SetLevel(DiagnosticLevel InLevel, bool InIsPersisted, out DiagnosticLevel OutPreviousLevel)
        {
            OutPreviousLevel = _CurrentLevel;
            bool IsPersisted = false;
            try
            {
                if (Settings != null)
                {
                    Settings.Level = DiagnosticConstants.ToText(InLevel);
                    if (InIsPersisted)
                    {
                        IsPersisted = Settings.TrySave();
                    }
                }

                bool IsLevelLowered = InLevel < OutPreviousLevel;
                if (IsLevelLowered)
                {
                    EmitLevelChanged(OutPreviousLevel, InLevel, IsPersisted);
                }

                _CurrentLevel = InLevel;
                _IsActive = Settings != null && Settings.IsEnabled && InLevel != DiagnosticLevel.Off;

                if (!IsLevelLowered)
                {
                    EmitLevelChanged(OutPreviousLevel, InLevel, IsPersisted);
                }
            }
            catch (Exception)
            {
                // 水準の変更に失敗しても Tool は成功として扱う（status で確認できる）
            }
            return IsPersisted;
        }

        /// <summary>diagnostics.level.changed を 1 件記録する。</summary>
        /// <param name="InPreviousLevel">変更前の水準。</param>
        /// <param name="InLevel">変更後の水準。</param>
        /// <param name="InIsPersisted">設定ファイルへ保存できたか。</param>
        private static void EmitLevelChanged(DiagnosticLevel InPreviousLevel, DiagnosticLevel InLevel, bool InIsPersisted)
        {
            Emit(DiagnosticLevel.Info, DiagnosticCategory.DIAGNOSTICS, "diagnostics.level.changed", null, InData =>
            {
                InData["previousLevel"] = DiagnosticConstants.ToText(InPreviousLevel);
                InData["level"] = DiagnosticConstants.ToText(InLevel);
                InData["persisted"] = InIsPersisted;
            });
        }

        /// <summary>ログ・スクリーンショット・エクスポート・設定の各フォルダーを作る。失敗は握り潰す。</summary>
        private static void EnsureFolders()
        {
            try
            {
                // Extended (Phase 12): Diagnostics\dumps は作らない（ダンプ本文は JSONL の diagnostics.errorDump に載る）
                Directory.CreateDirectory(DiagnosticWriter.LogFolderPath);
                Directory.CreateDirectory(DiagnosticSettings.GetFolder(Path.Combine("Diagnostics", "screenshots")));
                Directory.CreateDirectory(DiagnosticSettings.GetFolder("Exports"));
                Directory.CreateDirectory(DiagnosticSettings.GetFolder("Config"));
            }
            catch (Exception)
            {
                // 作れなくても writer 側で改めて試し、そこでも駄目なら writerHealthy=false になる
            }
        }

        /// <summary>session.start の data（ユーザー名・PC 名・パスは含めない）を埋める。</summary>
        /// <param name="InOutData">書き込み先の data。</param>
        private static void FillEnvironmentSummary(JObject InOutData)
        {
            InOutData["diagnosticsVersion"] = DiagnosticConstants.DIAGNOSTICS_VERSION;
            InOutData["schemaVersion"] = DiagnosticConstants.SCHEMA_VERSION;
            InOutData["extensionVersion"] = GetExtensionVersion();
            InOutData["osVersion"] = Environment.OSVersion.VersionString;
            InOutData["clrVersion"] = Environment.Version.ToString();
            InOutData["is64BitProcess"] = Environment.Is64BitProcess;
            InOutData["processorCount"] = Environment.ProcessorCount;
            InOutData["level"] = DiagnosticConstants.ToText(_CurrentLevel);
            InOutData["queueCapacity"] = Settings.QueueCapacity;
            InOutData["writeJsonl"] = Settings.IsJsonlWritten;
            InOutData["writeOutputPane"] = Settings.IsOutputPaneWritten;
            InOutData["includeUiText"] = Settings.IsUiTextIncluded;
            InOutData["includeFilePaths"] = Settings.IsFilePathsIncluded;
            // Extended (Phase 12): DEBUG 限定の fault injection が有効なときだけ内容を残す（Release ではキー自体が出ない）
            if (DiagnosticTestHooks.IsActive)
            {
                InOutData["testHooks"] = new JObject
                {
                    ["writerDelayMs"] = DiagnosticTestHooks.WriterDelayMs,
                    ["paneFailCount"] = DiagnosticTestHooks.PaneFailCount
                };
            }
        }

        /// <summary>拡張機能のバージョン（アセンブリの情報バージョン）を返す。</summary>
        /// <returns>バージョン文字列。取得できない場合は "unknown"。</returns>
        public static string GetExtensionVersion()
        {
            try
            {
                Assembly TheAssembly = typeof(DiagnosticHub).Assembly;
                AssemblyInformationalVersionAttribute TheAttribute =
                    (AssemblyInformationalVersionAttribute)Attribute.GetCustomAttribute(TheAssembly, typeof(AssemblyInformationalVersionAttribute));
                if (TheAttribute != null && !string.IsNullOrEmpty(TheAttribute.InformationalVersion))
                {
                    return TheAttribute.InformationalVersion;
                }
                return TheAssembly.GetName().Version.ToString();
            }
            catch (Exception)
            {
                return "unknown";
            }
        }
    }
}
