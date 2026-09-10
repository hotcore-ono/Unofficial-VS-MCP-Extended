using System;
using System.IO;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using VsMcp.Extension.McpServer;
using VsMcp.Shared.Protocol;

namespace VsMcp.Extension.Tools
{
    /// <summary>
    /// Extended (Phase 10): Extended Tool だけを共通に包む計装ラッパー（指示書 §15）。
    /// 各ハンドラへ try/finally を書き足す代わりに、登録時に <see cref="McpToolRegistry.Register"/> をここへ通すことで
    /// tool.start / tool.end と相関スコープを付ける。upstream の Tool 群と <see cref="McpRequestRouter"/> には手を入れない。
    /// 診断が Off / 無効のときはラッパーを素通しし、スコープも作らない。例外は必ずそのまま再スローするので、
    /// Router の総括 catch が出すエラー文（"Tool execution failed: ..."）も変わらない。
    /// </summary>
    internal static class DiagnosticToolRunner
    {
        /// <summary>戻り値の本文をまるごと JObject にしてよい長さ（文字数）。これ以上は逐次読みで必要な項目だけを拾う。</summary>
        private const int _MAX_PARSED_BODY_LENGTH = 256 * 1024;

        /// <summary>
        /// 計装付きで Tool を登録する。既存の <c>InRegistry.Register(definition, handler)</c> をこの呼び出しへ置き換えるだけで、
        /// Tool の schema・戻り値・エラー文は一切変わらない。
        /// </summary>
        /// <param name="InRegistry">登録先レジストリ。</param>
        /// <param name="InDefinition">Tool 定義（名前・説明・schema）。</param>
        /// <param name="InHandler">元のハンドラ。</param>
        public static void Register(McpToolRegistry InRegistry, McpToolDefinition InDefinition, Func<JObject, Task<McpToolResult>> InHandler)
        {
            InRegistry.Register(InDefinition, InArgs => RunAsync(InDefinition.Name, InArgs, InHandler));
        }

        /// <summary>
        /// 相関スコープを開始し、tool.start → ハンドラ → tool.end の順に記録する。
        /// 例外は exception event と tool.end（cancelled / error）を記録したうえで、そのまま再スローする。
        /// </summary>
        /// <param name="InToolName">Tool 名。</param>
        /// <param name="InArgs">Tool 引数。</param>
        /// <param name="InHandler">元のハンドラ。</param>
        /// <returns>元のハンドラの戻り値（変更しない）。</returns>
        private static async Task<McpToolResult> RunAsync(string InToolName, JObject InArgs, Func<JObject, Task<McpToolResult>> InHandler)
        {
            if (!DiagnosticHub.IsEnabled(DiagnosticLevel.Error))
            {
                // Off / enabled=false のときは計装の費用をかけない
                return await InHandler(InArgs);
            }

            DiagnosticScope TheScope = DiagnosticScope.Begin(InToolName);
            try
            {
                DiagnosticHub.Emit(DiagnosticLevel.Info, DiagnosticCategory.TOOL, "tool.start", InData =>
                    InData["args"] = DiagnosticSanitizer.SummarizeArguments(InToolName, InArgs, DiagnosticHub.Settings));

                McpToolResult TheResult = await InHandler(InArgs);

                try
                {
                    // ハンドラが戻ってからの計装は独立した try で包む。ここで何が起きても戻り値は元のまま返す
                    EmitToolCompletion(TheScope, InToolName, InArgs, TheResult);
                }
                catch (Exception)
                {
                    // 計装の失敗で Tool の戻り値を失わない（診断は静かに諦める）
                }
                return TheResult;
            }
            catch (OperationCanceledException TheException)
            {
                try
                {
                    DiagnosticHub.EmitException(DiagnosticLevel.Warning, "exception", TheException,
                        InData => InData["phase"] = "toolHandler");
                    EmitToolEnd(TheScope, InToolName, DiagnosticToolOutcome.CANCELLED, null, false);
                }
                catch (Exception)
                {
                    // 計装が失敗しても元の例外だけを呼び出し元へ伝える
                }
                throw;
            }
            catch (Exception TheException)
            {
                try
                {
                    DiagnosticHub.EmitException(DiagnosticLevel.Error, "exception", TheException,
                        InData => InData["phase"] = "toolHandler");
                    EmitToolEnd(TheScope, InToolName, DiagnosticToolOutcome.ERROR,
                        DiagnosticSanitizer.SanitizeText(TheException.Message, DiagnosticHub.Settings), true);
                    DiagnosticErrorDump.Schedule(TheScope, InToolName, DiagnosticLevel.Error, "tool.exception", ReadTargetWindowHandle(InArgs));
                }
                catch (Exception)
                {
                    // sanitize / dump が失敗しても元の例外を握り潰さない
                }
                throw;
            }
            finally
            {
                DiagnosticScope.End();
            }
        }

        /// <summary>
        /// ハンドラが正常に戻ったあとの計装（結果の区分の判定・interaction.execute.end・tool.end・詳細状態ダンプ）をまとめて行う。
        /// 呼び出し元は必ずこの呼び出しを try で包み、失敗しても Tool の戻り値をそのまま返すこと。
        /// </summary>
        /// <param name="InScope">この呼び出しのスコープ。</param>
        /// <param name="InToolName">Tool 名。</param>
        /// <param name="InArgs">Tool 引数（詳細状態ダンプの対象ウィンドウを読むために使う）。</param>
        /// <param name="InResult">ハンドラの戻り値。</param>
        private static void EmitToolCompletion(DiagnosticScope InScope, string InToolName, JObject InArgs, McpToolResult InResult)
        {
            string TheOutcome = ResolveOutcome(InResult, out string TheErrorMessage, out string TheActualMethod);
            bool IsError = string.Equals(TheOutcome, DiagnosticToolOutcome.ERROR, StringComparison.Ordinal);
            if (TheActualMethod != null)
            {
                // 戻り値が実際に使った操作方式（invokePattern / physicalClick / …）を返す Tool は、それを interaction として残す
                DiagnosticHub.EmitCore(DiagnosticLevel.Info, DiagnosticCategory.INTERACTION, "interaction.execute.end",
                    InToolName, InScope.CorrelationId, InScope.ElapsedMs, TheOutcome, null,
                    InData => InData["actualMethod"] = TheActualMethod, null);
            }
            EmitToolEnd(InScope, InToolName, TheOutcome,
                IsError ? DiagnosticSanitizer.SanitizeText(TheErrorMessage, DiagnosticHub.Settings) : null, IsError);
            if (IsError)
            {
                DiagnosticErrorDump.Schedule(InScope, InToolName, DiagnosticLevel.Error, "tool.error", ReadTargetWindowHandle(InArgs));
            }
        }

        /// <summary>
        /// 引数から詳細状態ダンプの対象ウィンドウを読む。Extended の Tool は HWND を 'windowHandle' か 'handle' で受け取る。
        /// </summary>
        /// <param name="InArgs">Tool 引数。</param>
        /// <returns>対象のトップレベル HWND（10 進）。指定が無ければ 0。</returns>
        private static long ReadTargetWindowHandle(JObject InArgs)
        {
            if (InArgs == null)
            {
                return 0;
            }
            try
            {
                return InArgs.Value<long?>("windowHandle") ?? InArgs.Value<long?>("handle") ?? 0;
            }
            catch (Exception)
            {
                return 0;
            }
        }

        /// <summary>tool.end を記録する（performance の観点でも唯一の所要時間の出どころにする）。</summary>
        /// <param name="InScope">この呼び出しのスコープ。</param>
        /// <param name="InToolName">Tool 名。</param>
        /// <param name="InOutcome">結果の区分。</param>
        /// <param name="InMessage">sanitize 済みのエラー文。無ければ null。</param>
        /// <param name="InIsError">エラーとして記録するか（水準を error にする）。</param>
        private static void EmitToolEnd(DiagnosticScope InScope, string InToolName, string InOutcome, string InMessage, bool InIsError)
        {
            long TheElapsedMs = InScope.ElapsedMs;
            if (InScope.IsWaitStarted)
            {
                // 待機系ツールは wait.start と対になる wait.end をここで出す（reason は tool の outcome と同じ区分）
                DiagnosticHub.EmitCore(DiagnosticLevel.Info, DiagnosticCategory.WAIT, "wait.end", InToolName, InScope.CorrelationId,
                    TheElapsedMs, InOutcome, null, InData =>
                    {
                        InData["timeoutMs"] = InScope.WaitTimeoutMs;
                        InData["pollIntervalMs"] = InScope.WaitPollIntervalMs;
                        InData["reason"] = InOutcome;
                    }, null);
            }
            DiagnosticHub.EmitCore(InIsError ? DiagnosticLevel.Error : DiagnosticLevel.Info, DiagnosticCategory.TOOL, "tool.end",
                InToolName, InScope.CorrelationId, TheElapsedMs, InOutcome, InMessage, null, null);
            DiagnosticHub.EmitCore(DiagnosticLevel.Verbose, DiagnosticCategory.PERFORMANCE, "performance.tool",
                InToolName, InScope.CorrelationId, TheElapsedMs, InOutcome, null, null, null);
        }

        /// <summary>
        /// Tool の戻り値から結果の区分を判定する。IsError は error、本文の timedOut=true は timeout、
        /// found / closed / idle / completed のいずれかが false なら normalFalse、それ以外は success。
        /// 本文が JSON として読めない場合（画像や自由文）は success とみなす。
        /// 本文が大きい場合（ui_window_find_elements の大量結果など）は DOM を作らず逐次読みで必要な項目だけを拾う。
        /// </summary>
        /// <param name="InResult">Tool の戻り値。</param>
        /// <param name="OutErrorMessage">エラー時の本文（先頭の text）。それ以外は null。</param>
        /// <param name="OutActualMethod">本文が返した操作方式（"method"）。無ければ null。</param>
        /// <returns>結果の区分（<see cref="DiagnosticToolOutcome"/>）。</returns>
        private static string ResolveOutcome(McpToolResult InResult, out string OutErrorMessage, out string OutActualMethod)
        {
            OutErrorMessage = null;
            OutActualMethod = null;
            if (InResult == null)
            {
                return DiagnosticToolOutcome.SUCCESS;
            }

            string TheText = ReadFirstText(InResult);
            if (InResult.IsError)
            {
                OutErrorMessage = TheText;
                return DiagnosticToolOutcome.ERROR;
            }

            if (string.IsNullOrEmpty(TheText) || TheText[0] != '{')
            {
                return DiagnosticToolOutcome.SUCCESS;
            }

            try
            {
                bool IsTimedOut;
                bool IsNormalFalse;
                if (TheText.Length < _MAX_PARSED_BODY_LENGTH)
                {
                    ReadOutcomeFields(JObject.Parse(TheText), out OutActualMethod, out IsTimedOut, out IsNormalFalse);
                }
                else
                {
                    ScanOutcomeFields(TheText, out OutActualMethod, out IsTimedOut, out IsNormalFalse);
                }
                if (IsTimedOut)
                {
                    return DiagnosticToolOutcome.TIMEOUT;
                }
                if (IsNormalFalse)
                {
                    return DiagnosticToolOutcome.NORMAL_FALSE;
                }
            }
            catch (Exception)
            {
                // JSON として読めない本文は success 扱い（戻り値は変えない）
            }
            return DiagnosticToolOutcome.SUCCESS;
        }

        /// <summary>解析済みの本文から、結果の区分に使うトップレベルの項目を読む。</summary>
        /// <param name="InBody">解析済みの本文。</param>
        /// <param name="OutActualMethod">本文が返した操作方式（"method"）。無ければ null。</param>
        /// <param name="OutIsTimedOut">timedOut=true があったか。</param>
        /// <param name="OutIsNormalFalse">found / closed / idle / completed のいずれかが false だったか。</param>
        private static void ReadOutcomeFields(JObject InBody, out string OutActualMethod, out bool OutIsTimedOut, out bool OutIsNormalFalse)
        {
            OutActualMethod = InBody.Value<string>("method");
            OutIsTimedOut = InBody.Value<bool?>("timedOut") == true;
            OutIsNormalFalse = InBody.Value<bool?>("found") == false
                || InBody.Value<bool?>("closed") == false
                || InBody.Value<bool?>("idle") == false
                || InBody.Value<bool?>("completed") == false;
        }

        /// <summary>
        /// 大きな本文を DOM にせず逐次読みし、トップレベルの found / closed / idle / completed / timedOut / method だけを拾う。
        /// トップレベルのオブジェクトが閉じた時点で読むのをやめる（入れ子の要素配列は読み飛ばすだけで保持しない）。
        /// </summary>
        /// <param name="InText">Tool の戻り値本文（JSON）。</param>
        /// <param name="OutActualMethod">本文が返した操作方式（"method"）。無ければ null。</param>
        /// <param name="OutIsTimedOut">timedOut=true があったか。</param>
        /// <param name="OutIsNormalFalse">found / closed / idle / completed のいずれかが false だったか。</param>
        private static void ScanOutcomeFields(string InText, out string OutActualMethod, out bool OutIsTimedOut, out bool OutIsNormalFalse)
        {
            OutActualMethod = null;
            OutIsTimedOut = false;
            OutIsNormalFalse = false;
            using (StringReader TheTextReader = new StringReader(InText))
            using (JsonTextReader TheReader = new JsonTextReader(TheTextReader))
            {
                int TheDepth = 0;
                string ThePropertyName = null;
                while (TheReader.Read())
                {
                    if (TheReader.TokenType == JsonToken.StartObject || TheReader.TokenType == JsonToken.StartArray)
                    {
                        TheDepth++;
                        continue;
                    }
                    if (TheReader.TokenType == JsonToken.EndObject || TheReader.TokenType == JsonToken.EndArray)
                    {
                        TheDepth--;
                        if (TheDepth <= 0)
                        {
                            return;
                        }
                        continue;
                    }
                    if (TheReader.TokenType == JsonToken.PropertyName)
                    {
                        ThePropertyName = TheReader.Value as string;
                        continue;
                    }
                    if (TheDepth != 1 || ThePropertyName == null)
                    {
                        continue;
                    }
                    ApplyOutcomeToken(ThePropertyName, TheReader, ref OutActualMethod, ref OutIsTimedOut, ref OutIsNormalFalse);
                    ThePropertyName = null;
                }
            }
        }

        /// <summary>逐次読み中の 1 つの値トークンを、結果の区分の材料へ反映する。</summary>
        /// <param name="InPropertyName">この値のキー名。</param>
        /// <param name="InReader">現在位置が値トークンにある reader。</param>
        /// <param name="InOutActualMethod">操作方式の格納先。</param>
        /// <param name="InOutIsTimedOut">timedOut の格納先。</param>
        /// <param name="InOutIsNormalFalse">normalFalse の格納先。</param>
        private static void ApplyOutcomeToken(string InPropertyName, JsonTextReader InReader, ref string InOutActualMethod,
            ref bool InOutIsTimedOut, ref bool InOutIsNormalFalse)
        {
            if (string.Equals(InPropertyName, "method", StringComparison.Ordinal))
            {
                if (InReader.TokenType == JsonToken.String)
                {
                    InOutActualMethod = InReader.Value as string;
                }
                return;
            }
            if (InReader.TokenType != JsonToken.Boolean)
            {
                return;
            }
            bool IsTrue = (bool)InReader.Value;
            if (string.Equals(InPropertyName, "timedOut", StringComparison.Ordinal))
            {
                InOutIsTimedOut = InOutIsTimedOut || IsTrue;
                return;
            }
            if (string.Equals(InPropertyName, "found", StringComparison.Ordinal)
                || string.Equals(InPropertyName, "closed", StringComparison.Ordinal)
                || string.Equals(InPropertyName, "idle", StringComparison.Ordinal)
                || string.Equals(InPropertyName, "completed", StringComparison.Ordinal))
            {
                InOutIsNormalFalse = InOutIsNormalFalse || !IsTrue;
            }
        }

        /// <summary>戻り値の先頭にある text コンテンツを取り出す。</summary>
        /// <param name="InResult">Tool の戻り値。</param>
        /// <returns>先頭の text。無ければ null。</returns>
        private static string ReadFirstText(McpToolResult InResult)
        {
            if (InResult.Content == null)
            {
                return null;
            }
            foreach (McpContent TheContent in InResult.Content)
            {
                if (string.Equals(TheContent.Type, "text", StringComparison.Ordinal) && TheContent.Text != null)
                {
                    return TheContent.Text;
                }
            }
            return null;
        }
    }
}
